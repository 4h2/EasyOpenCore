using System.Text.RegularExpressions;
using EasyOpenCore.Core.Localization;
using EasyOpenCore.Core.Models;
using Microsoft.Win32;

namespace EasyOpenCore.Core.Hardware;

/// <summary>Language-neutral category ids (translated via "category.&lt;id&gt;").</summary>
public static class DeviceCategory
{
    public const string Gpu = "gpu";
    public const string Audio = "audio";
    public const string Network = "network";
    public const string Bluetooth = "bluetooth";
    public const string Storage = "storage";
    public const string Usb = "usb";
    public const string Input = "input";
    public const string Acpi = "acpi";
    public const string Other = "other";
}

/// <summary>Runs every detector and builds the <see cref="HardwareReport"/>.</summary>
public static class HardwareScanner
{
    private static readonly string[] HdmiAudioVendors = ["8086", "10DE", "1002"];

    /// <summary>ACPI _HID → role id (translated via "acpi.role.&lt;id&gt;").</summary>
    private static readonly Dictionary<string, string> AcpiRoles = new(StringComparer.OrdinalIgnoreCase)
    {
        ["PNP0C09"] = "ec",
        ["ACPI0007"] = "cpu",
        ["ACPI0010"] = "cpu-container",
        ["PNP0B00"] = "rtc",
        ["ACPI000E"] = "awac",
        ["PNP0103"] = "hpet",
        ["PNP0A08"] = "pcie-root",
        ["PNP0A03"] = "pci-root",
        ["PNP0C0A"] = "battery",
        ["ACPI0003"] = "ac",
        ["PNP0C0D"] = "lid",
        ["PNP0C0C"] = "power-button",
        ["PNP0C0E"] = "sleep-button",
        ["ACPI0008"] = "als",
        ["PNP0C14"] = "wmi",
        ["PNP0000"] = "pic",
        ["PNP0100"] = "timer",
        ["PNP0303"] = "ps2-keyboard",
        ["INT33A1"] = "pmc",
        ["INTC1026"] = "pmc",
    };

    public static HardwareReport Scan(IProgress<string>? progress = null)
    {
        var report = new HardwareReport
        {
            ToolVersion = typeof(HardwareScanner).Assembly.GetName().Version?.ToString() ?? "",
        };

        Run(report, progress, "scan.step.system", () => report.System = SystemDetector.Detect());
        Run(report, progress, "scan.step.cpu", () => report.Cpu = CpuDetector.Detect());
        Run(report, progress, "scan.step.memory", () => report.Memory = SystemDetector.DetectMemory());
        Run(report, progress, "scan.step.disks", () => report.Storage = StorageDetector.Detect());
        Run(report, progress, "scan.step.devices", () => Classify(report, PnpEnumerator.EnumeratePresent()));
        Run(report, progress, "scan.step.acpi", () => report.AcpiTables = AcpiTableReader.ReadAll());

        progress?.Report(Loc.T("scan.step.done"));
        return report;
    }

    private static void Run(HardwareReport report, IProgress<string>? progress, string step, Action action)
    {
        var name = Loc.T(step);
        progress?.Report(name);
        try { action(); }
        catch (Exception ex) { report.ScanErrors.Add($"{name}: {ex.Message}"); }
    }

    private static void Classify(HardwareReport report, List<PnpDevice> devices)
    {
        report.AllDevices = devices;
        foreach (var d in devices)
        {
            d.Category = DeviceCategory.Other;
            var cc = HardwareIds.ClassCode(d.CompatibleIds);

            if (d.Enumerator == "PCI")
                ClassifyPci(report, d, cc);
            else if (d.Enumerator == "HDAUDIO" && d.HardwareIds.Any(h => h.Contains("FUNC_01", StringComparison.OrdinalIgnoreCase)))
                AddCodec(report, d);
            else if (d.Class.Equals("Bluetooth", StringComparison.OrdinalIgnoreCase) && d.Enumerator == "USB")
                AddNetwork(report, d, NetworkKind.Bluetooth, "USB");
            else if (d.Class.Equals("Net", StringComparison.OrdinalIgnoreCase) && d.Enumerator == "USB")
                AddNetwork(report, d, NetworkKind.Unknown, "USB");

            if (d.Enumerator == "ACPI")
                ClassifyAcpi(report, d);

            if (IsInputDevice(d))
                AddInput(report, d);
        }
    }

    private static void ClassifyPci(HardwareReport report, PnpDevice d, string cc)
    {
        var ven = HardwareIds.Vendor(d.HardwareIds);
        var dev = HardwareIds.Device(d.HardwareIds);
        var controller = new PciController
        {
            Name = d.Name, VendorId = ven, DeviceId = dev, ClassCode = cc,
            PciPath = d.PciPath, AcpiPath = d.AcpiPath, Service = d.Service,
        };

        switch (cc.Length >= 4 ? cc[..4] : cc)
        {
            case "0300" or "0302" or "0380":
                d.Category = DeviceCategory.Gpu;
                report.Gpus.Add(BuildGpu(d, ven, dev));
                break;
            case "0403" or "0401":
                d.Category = DeviceCategory.Audio;
                report.AudioControllers.Add(controller);
                break;
            case "0200":
                AddNetwork(report, d, NetworkKind.Ethernet, "PCIe");
                break;
            case "0280":
                AddNetwork(report, d, NetworkKind.WiFi, "PCIe");
                break;
            case "0101" or "0104" or "0106" or "0107" or "0108":
                d.Category = DeviceCategory.Storage;
                report.StorageControllers.Add(controller);
                break;
            case "0C03":
                d.Category = DeviceCategory.Usb;
                report.UsbControllers.Add(controller);
                break;
            default:
                // Some Intel CNVi Wi-Fi cards report a generic "Network controller" class code
                if (d.Class.Equals("Net", StringComparison.OrdinalIgnoreCase))
                    AddNetwork(report, d, NetworkKind.Unknown, "PCIe");
                break;
        }
    }

    private static GpuInfo BuildGpu(PnpDevice d, string ven, string dev)
    {
        var gpu = new GpuInfo
        {
            Name = d.Name,
            VendorId = ven,
            DeviceId = dev,
            SubsystemId = HardwareIds.Subsystem(d.HardwareIds),
            Revision = HardwareIds.Revision(d.HardwareIds),
            PciPath = d.PciPath,
            AcpiPath = d.AcpiPath,
            DriverVersion = d.DriverVersion,
            VramBytes = ReadVram(d),
        };
        gpu.Kind = ven switch
        {
            "8086" when !d.Name.Contains("Arc", StringComparison.OrdinalIgnoreCase) => GpuKind.Integrated,
            "1002" when Regex.IsMatch(d.Name, @"Radeon\(TM\) Graphics|Radeon Graphics|Vega \d+ Graphics|Radeon \d{3}M", RegexOptions.IgnoreCase)
                => GpuKind.Integrated,
            "8086" or "1002" or "10DE" => GpuKind.Discrete,
            _ => GpuKind.Unknown,
        };
        return gpu;
    }

    /// <summary>Real VRAM from the registry (Win32_VideoController.AdapterRAM is capped at 4 GB).</summary>
    private static ulong ReadVram(PnpDevice d)
    {
        if (string.IsNullOrEmpty(d.DriverKey))
            return 0;
        using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Control\Class\{d.DriverKey}");
        return key?.GetValue("HardwareInformation.qwMemorySize") switch
        {
            long l => (ulong)l,
            byte[] b when b.Length >= 8 => BitConverter.ToUInt64(b),
            byte[] b when b.Length >= 4 => BitConverter.ToUInt32(b),
            int i => (uint)i,
            _ => 0,
        };
    }

    private static void AddCodec(HardwareReport report, PnpDevice d)
    {
        d.Category = DeviceCategory.Audio;
        var ven = HardwareIds.Vendor(d.HardwareIds);
        var dev = HardwareIds.Device(d.HardwareIds);
        report.AudioCodecs.Add(new AudioCodec
        {
            Name = d.Name,
            VendorId = ven,
            DeviceId = dev,
            Subsystem = HardwareIds.Subsystem(d.HardwareIds),
            IsHdmi = HdmiAudioVendors.Contains(ven),
            CodecName = ven switch
            {
                "10EC" => $"ALC{dev.TrimStart('0')}",
                "14F1" => $"Conexant CX{dev}",
                "111D" => $"IDT 92HD (0x{dev})",
                "1013" => $"Cirrus Logic CS{dev}",
                "1106" => $"VIA VT{dev}",
                _ => "",
            },
        });
    }

    private static void AddNetwork(HardwareReport report, PnpDevice d, NetworkKind kind, string bus)
    {
        if (kind == NetworkKind.Unknown)
            kind = Regex.IsMatch(d.Name, @"Wi-?Fi|Wireless|802\.11|WLAN", RegexOptions.IgnoreCase) ? NetworkKind.WiFi : NetworkKind.Ethernet;

        d.Category = kind == NetworkKind.Bluetooth ? DeviceCategory.Bluetooth : DeviceCategory.Network;
        report.Network.Add(new NetworkAdapter
        {
            Name = d.Name,
            Kind = kind,
            Bus = bus,
            VendorId = bus == "USB" ? HardwareIds.UsbVid(d.HardwareIds) : HardwareIds.Vendor(d.HardwareIds),
            DeviceId = bus == "USB" ? HardwareIds.UsbPid(d.HardwareIds) : HardwareIds.Device(d.HardwareIds),
            PciPath = d.PciPath,
        });
    }

    private static void ClassifyAcpi(HardwareReport report, PnpDevice d)
    {
        var hid = d.InstanceId.Split('\\').ElementAtOrDefault(1) ?? "";
        if (d.Category == DeviceCategory.Other)
            d.Category = DeviceCategory.Acpi;
        report.AcpiDevices.Add(new AcpiDevice
        {
            Name = d.Name,
            HardwareId = hid,
            Role = AcpiRoles.GetValueOrDefault(hid)
                   ?? (hid.StartsWith("GenuineIntel", StringComparison.OrdinalIgnoreCase)
                       || hid.StartsWith("AuthenticAMD", StringComparison.OrdinalIgnoreCase) ? "cpu" : ""),
            AcpiPath = d.AcpiPath,
        });
    }

    private static bool IsInputDevice(PnpDevice d) =>
        d.Service.Equals("i8042prt", StringComparison.OrdinalIgnoreCase)
        || d.Service.Equals("hidi2c", StringComparison.OrdinalIgnoreCase)
        || d.Service.Equals("SynTP", StringComparison.OrdinalIgnoreCase);

    private static void AddInput(HardwareReport report, PnpDevice d)
    {
        d.Category = DeviceCategory.Input;
        var hid = d.HardwareIds.FirstOrDefault() ?? d.InstanceId;
        bool touchpad = Regex.IsMatch(hid + d.Name, @"ELAN|SYN|MSFT0001|ALPS|ATML|touch\s?pad|precision", RegexOptions.IgnoreCase);
        report.Input.Add(new InputDevice
        {
            Name = d.Name,
            Type = d.Class.Equals("Keyboard", StringComparison.OrdinalIgnoreCase) ? "keyboard" : touchpad ? "touchpad" : "mouse",
            Bus = d.Service.ToLowerInvariant() switch
            {
                "i8042prt" => InputBus.Ps2,
                "hidi2c" => InputBus.I2c,
                "syntp" => InputBus.Smbus,
                _ => InputBus.Unknown,
            },
            HardwareId = hid,
            AcpiPath = d.AcpiPath,
        });
    }
}
