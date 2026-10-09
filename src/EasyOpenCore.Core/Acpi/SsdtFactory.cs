using System.Text;
using EasyOpenCore.Core.Compatibility;
using EasyOpenCore.Core.Models;

namespace EasyOpenCore.Core.Acpi;

public sealed record GeneratedSsdt(string Name, byte[] Aml, string Asl, string Comment);

/// <summary>An SSDT the user must create by hand (e.g. it needs macOS tools or per-device pin data).</summary>
public sealed record ManualSsdt(string Name, string ReasonKey);

/// <summary>ACPI patch (config.plist → ACPI → Patch) needed by a generated SSDT.</summary>
public sealed record AcpiPatch(string Comment, byte[] Find, byte[] Replace);

public sealed class SsdtResult
{
    public List<GeneratedSsdt> Tables { get; } = [];
    public List<ManualSsdt> Manual { get; } = [];
    public List<AcpiPatch> Patches { get; } = [];
}

/// <summary>
/// Builds the SSDTs chosen by <see cref="EfiPlanner"/> using the machine's real ACPI paths.
/// Table contents follow the Dortania "Getting Started With ACPI" prebuilt SSDTs and the
/// OpenCorePkg AcpiSamples, adapted to the detected device paths.
/// </summary>
public static class SsdtFactory
{
    private static AmlCall Darwin() => new("_OSI", new AmlString("Darwin"));

    /// <summary>Method (_STA) returning <paramref name="onMac"/> on macOS and <paramref name="other"/> elsewhere.</summary>
    private static AmlMethod Sta(ulong onMac, ulong other) =>
        new("_STA", 0, new AmlIf(Darwin(), [new AmlReturn(new AmlInt(onMac))], [new AmlReturn(new AmlInt(other))]));

    /// <summary>The usual _DSM shape: answer the function-0 query, then return the property package.</summary>
    private static AmlMethod Dsm(params AmlExpr[] properties) =>
        new("_DSM", 4,
            new AmlIf(new AmlLNot(new AmlArg(2)), [new AmlReturn(new AmlBuffer([0x03]))]),
            new AmlReturn(new AmlPackage(properties)));

    public static SsdtResult Build(HardwareReport hw, CompatibilityReport compat, EfiPlan plan)
    {
        var result = new SsdtResult();
        var acpi = compat.Acpi;
        var dsdt = hw.AcpiTables.FirstOrDefault(t => t.Signature == "DSDT")?.Data ?? [];
        string lpc = acpi.LpcPath.Length > 0 ? acpi.LpcPath : @"\_SB";

        foreach (var item in plan.Ssdts)
        {
            switch (item.Name)
            {
                case "SSDT-PLUG" when acpi.CpuPath.Length > 0:
                    result.Tables.Add(Plug(acpi.CpuPath, item.Reason));
                    break;
                case "SSDT-PLUG-ALT":
                    result.Tables.Add(ProcessorObjects("SSDT-PLUG-ALT", "CpuPlugA", Math.Max(hw.Cpu.Threads, 1), withPlugin: true, item.Reason));
                    break;
                case "SSDT-CPUR":
                    result.Tables.Add(ProcessorObjects("SSDT-CPUR", "CPUR", Math.Max(hw.Cpu.Threads, 1), withPlugin: false, item.Reason));
                    break;
                case "SSDT-EC" or "SSDT-EC-USBX":
                    result.Tables.Add(Ec(item.Name, lpc, acpi, compat.IsLaptop, usbx: item.Name == "SSDT-EC-USBX", item.Reason));
                    break;
                case "SSDT-AWAC" when ContainsName(dsdt, "STAS"):
                    result.Tables.Add(Awac(item.Reason));
                    break;
                case "SSDT-AWAC" or "SSDT-RTC0":
                    // AWAC without the STAS switch: macOS still needs a legacy RTC device.
                    result.Tables.Add(Rtc0(lpc, item.Reason));
                    break;
                case "SSDT-PNLF":
                    result.Tables.Add(Pnlf(hw.Cpu, item.Reason));
                    break;
                case "SSDT-RHUB" when FirstXhciPath(hw) is { } xhc:
                    result.Tables.Add(Rhub(xhc + ".RHUB", item.Reason));
                    break;
                case "SSDT-PMC":
                    result.Tables.Add(Pmc(lpc, item.Reason));
                    break;
                case "SSDT-XOSI":
                    result.Tables.Add(Xosi(item.Reason));
                    result.Patches.Add(new("_OSI to XOSI (SSDT-XOSI)", Encoding.ASCII.GetBytes("_OSI"), Encoding.ASCII.GetBytes("XOSI")));
                    break;
                case "SSDT-dGPU-Off" when DiscreteGpuPath(hw) is { } gpu:
                    result.Tables.Add(DgpuOff(gpu, item.Reason));
                    break;
                default:
                    result.Manual.Add(new(item.Name, $"ssdt.manual.{item.Name}"));
                    break;
            }
        }
        return result;
    }

    private static GeneratedSsdt Make(string name, AmlTable table, string comment) =>
        new(name, table.Build(), table.ToAsl(), comment);

    // ---- CPU ----

    private static GeneratedSsdt Plug(string cpuPath, string comment) => Make("SSDT-PLUG", new AmlTable("CpuPlug",
        new AmlExternal(cpuPath, ExternalType.ProcessorObj),
        new AmlScope(cpuPath,
            new AmlIf(Darwin(), [Dsm(new AmlString("plugin-type"), new AmlInt(1))]))), comment);

    /// <summary>
    /// Declares legacy Processor objects (macOS cannot attach to ACPI0007 Device-style CPUs).
    /// Used by SSDT-PLUG-ALT (Alder Lake+) and SSDT-CPUR (AMD B550/A520).
    /// </summary>
    private static GeneratedSsdt ProcessorObjects(string name, string tableId, int threads, bool withPlugin, string comment)
    {
        var cpus = new List<AmlNode>();
        for (int i = 0; i < Math.Min(threads, 255); i++)
        {
            var body = withPlugin && i == 0
                ? new AmlNode[] { Dsm(new AmlString("plugin-type"), new AmlInt(1)) }
                : [];
            cpus.Add(new AmlProcessor($"CP{i:X2}", (byte)i, 0x00000510, 0x06, body));
        }
        return Make(name, new AmlTable(tableId, new AmlScope(@"\_SB", new AmlIf(Darwin(), [.. cpus]))), comment);
    }

    // ---- Embedded controller / USB power ----

    private static GeneratedSsdt Ec(string name, string lpc, AcpiInfo acpi, bool laptop, bool usbx, string comment)
    {
        var body = new List<AmlNode>();
        if (lpc.Contains('.'))
            body.Add(new AmlExternal(lpc, ExternalType.DeviceObj));

        // Desktops: hide the real EC from macOS (it breaks AppleACPIEC), as in Dortania's desktop SSDT-EC.
        bool disableRealEc = !laptop && acpi.EcPath.Length > 0 && !acpi.EcNamedEc;
        if (disableRealEc)
        {
            body.Add(new AmlExternal(acpi.EcPath, ExternalType.DeviceObj));
            body.Add(new AmlScope(acpi.EcPath, Sta(0x00, 0x0F)));
        }

        if (usbx)
        {
            body.Add(new AmlScope(@"\_SB",
                new AmlDevice("USBX",
                    new AmlName("_ADR", new AmlInt(0)),
                    Dsm(new AmlString("kUSBSleepPowerSupply"), new AmlInt(0x13EC),
                        new AmlString("kUSBSleepPortCurrentLimit"), new AmlInt(0x0834),
                        new AmlString("kUSBWakePowerSupply"), new AmlInt(0x13EC),
                        new AmlString("kUSBWakePortCurrentLimit"), new AmlInt(0x0834)),
                    Sta(0x0F, 0x00))));
        }

        // A device literally named "EC" already exists: adding another would clash.
        if (!acpi.EcNamedEc)
        {
            body.Add(new AmlScope(lpc,
                new AmlDevice("EC",
                    new AmlName("_HID", new AmlString("ACID0001")),
                    Sta(0x0F, 0x00))));
        }

        return Make(name, new AmlTable(usbx ? "SsdtEC" : "SsdtECnu", [.. body]), comment);
    }

    // ---- Clock ----

    /// <summary>Acidanthera's SSDT-AWAC-DISABLE: flips the firmware's STAS switch to expose the legacy RTC.</summary>
    private static GeneratedSsdt Awac(string comment) => Make("SSDT-AWAC", new AmlTable("AWACOFF",
        new AmlExternal("STAS", ExternalType.IntObj),
        new AmlScope(@"\",
            new AmlIf(Darwin(), [new AmlStore(new AmlInt(1), new AmlNameRef("STAS"))]))), comment);

    private static GeneratedSsdt Rtc0(string lpc, string comment)
    {
        // IO (Decode16, 0x0070, 0x0070, 0x01, 0x08) + IRQNoFlags () {8} + EndTag
        byte[] crs = [0x47, 0x01, 0x70, 0x00, 0x70, 0x00, 0x01, 0x08, 0x22, 0x00, 0x01, 0x79, 0x00];
        return Make("SSDT-RTC0", new AmlTable("RtcAwac",
            new AmlExternal(lpc, ExternalType.DeviceObj),
            new AmlScope(lpc,
                new AmlDevice("RTC0",
                    new AmlName("_HID", new AmlEisaId("PNP0B00")),
                    new AmlName("_CRS", new AmlBuffer(crs,
                        "ResourceTemplate () { IO (Decode16, 0x0070, 0x0070, 0x01, 0x08) IRQNoFlags () {8} }")),
                    Sta(0x0F, 0x00)))), comment);
    }

    // ---- Graphics / backlight ----

    /// <summary>Backlight device; _UID selects WhateverGreen's brightness profile for the iGPU generation.</summary>
    private static GeneratedSsdt Pnlf(CpuInfo cpu, string comment)
    {
        ulong uid = cpu.Codename switch
        {
            "Sandy Bridge" or "Ivy Bridge" => 14,
            "Haswell" or "Broadwell" => 15,
            "Skylake" or "Kaby Lake" => 16,
            _ => 19, // Coffee Lake and newer (and AMD with NootedRed)
        };
        return Make("SSDT-PNLF", new AmlTable("PNLF",
            new AmlScope(@"\_SB",
                new AmlIf(Darwin(),
                [
                    new AmlDevice("PNLF",
                        new AmlName("_HID", new AmlEisaId("APP0002")),
                        new AmlName("_CID", new AmlString("backlight")),
                        new AmlName("_UID", new AmlInt(uid)),
                        new AmlName("_STA", new AmlInt(0x0B))),
                ]))), comment);
    }

    private static GeneratedSsdt DgpuOff(string gpuPath, string comment)
    {
        string off = gpuPath + "._OFF";
        return Make("SSDT-dGPU-Off", new AmlTable("DGPUOFF",
            new AmlExternal(off, ExternalType.MethodObj),
            new AmlScope(@"\_SB",
                new AmlDevice("RMD1",
                    new AmlName("_HID", new AmlString("RMD10000")),
                    new AmlMethod("_INI", 0,
                        new AmlIf(Darwin(),
                        [
                            new AmlIf(new AmlCondRefOf(off), [new AmlStatementCall(off)]),
                        ])),
                    Sta(0x0F, 0x00)))), comment);
    }

    // ---- Chipset ----

    private static GeneratedSsdt Rhub(string rhubPath, string comment) => Make("SSDT-RHUB", new AmlTable("RhubOff",
        new AmlExternal(rhubPath, ExternalType.DeviceObj),
        new AmlScope(rhubPath, Sta(0x00, 0x0F))), comment);

    private static GeneratedSsdt Pmc(string lpc, string comment)
    {
        // Memory32Fixed (ReadWrite, 0xFE000000, 0x00010000) + EndTag
        byte[] crs = [0x86, 0x09, 0x00, 0x01, 0x00, 0x00, 0x00, 0xFE, 0x00, 0x00, 0x01, 0x00, 0x79, 0x00];
        return Make("SSDT-PMC", new AmlTable("PMCR",
            new AmlExternal(lpc, ExternalType.DeviceObj),
            new AmlScope(lpc,
                new AmlDevice("PMCR",
                    new AmlName("_HID", new AmlEisaId("APP9876")),
                    new AmlName("_CRS", new AmlBuffer(crs, "ResourceTemplate () { Memory32Fixed (ReadWrite, 0xFE000000, 0x00010000) }")),
                    Sta(0x0B, 0x00)))), comment);
    }

    /// <summary>Makes _OSI report Windows versions on macOS so the firmware enables I2C devices.</summary>
    private static GeneratedSsdt Xosi(string comment)
    {
        string[] windows =
        [
            "Windows 2001", "Windows 2001.1", "Windows 2001 SP1", "Windows 2001 SP2", "Windows 2006", "Windows 2006.1",
            "Windows 2006 SP1", "Windows 2009", "Windows 2012", "Windows 2013", "Windows 2015", "Windows 2016",
            "Windows 2017", "Windows 2017.2", "Windows 2018", "Windows 2018.2", "Windows 2019", "Windows 2020",
            "Windows 2021", "Windows 2022",
        ];
        var list = new AmlPackage([.. windows.Select(w => (AmlExpr)new AmlString(w))]);
        return Make("SSDT-XOSI", new AmlTable("XOSI",
            new AmlMethod("XOSI", 1,
                new AmlIf(Darwin(),
                    [new AmlReturn(new AmlLNotEqual(new AmlMatch(list, MatchOp.MEQ, new AmlArg(0), MatchOp.MTR, new AmlInt(0), new AmlInt(0)), new AmlOnes()))],
                    [new AmlReturn(new AmlCall("_OSI", new AmlArg(0)))]))), comment);
    }

    // ---- helpers ----

    /// <summary>True when a 4-character ACPI name appears in an AML table.</summary>
    public static bool ContainsName(byte[] aml, string name)
    {
        var needle = Encoding.ASCII.GetBytes(name);
        return aml.AsSpan().IndexOf(needle) >= 0;
    }

    private static string? FirstXhciPath(HardwareReport hw) =>
        hw.UsbControllers.FirstOrDefault(u => u.ClassCode.StartsWith("0C0330") && u.AcpiPath.Length > 0)?.AcpiPath
        ?? hw.UsbControllers.FirstOrDefault(u => u.AcpiPath.Length > 0)?.AcpiPath;

    private static string? DiscreteGpuPath(HardwareReport hw) =>
        hw.Gpus.FirstOrDefault(g => g.Kind == GpuKind.Discrete && g.AcpiPath.Length > 0)?.AcpiPath;
}

/// <summary>A method call used as a statement (its return value is discarded).</summary>
public sealed class AmlStatementCall(string path, params AmlExpr[] args) : AmlNode
{
    private readonly AmlCall _call = new(path, args);
    public override void Emit(List<byte> o) => _call.Emit(o);
    public override void Asl(AslWriter w) => w.Line(_call.AslExpr());
}
