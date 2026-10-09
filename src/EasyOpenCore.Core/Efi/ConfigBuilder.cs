using EasyOpenCore.Core.Acpi;
using EasyOpenCore.Core.Compatibility;
using EasyOpenCore.Core.Models;
using EasyOpenCore.Core.Plist;

namespace EasyOpenCore.Core.Efi;

public sealed record SmbiosIdentity(string Model, string Serial, string Mlb, string Uuid, byte[] Rom);

public sealed record AudioLayout(string ControllerPath, int LayoutId, IReadOnlyList<(int Id, string Comment)> Alternatives);

/// <summary>Everything ConfigBuilder needs besides the hardware.</summary>
public sealed class ConfigInputs
{
    public required EfiPlan Plan { get; init; }
    public required SsdtResult Ssdts { get; init; }
    public required List<KextBundle> Kexts { get; init; }
    public required SmbiosIdentity Smbios { get; init; }
    public required List<string> Drivers { get; init; }
    public List<string> Tools { get; init; } = [];
    public AudioLayout? Audio { get; init; }
    /// <summary>AMD Vanilla Kernel → Patch entries (already customized with the core count).</summary>
    public List<PNode> KernelPatches { get; init; } = [];
    /// <summary>Apple Secure Boot must be off for OCLP root patches and for macOS older than Catalina.</summary>
    public bool DisableAppleSecureBoot { get; init; }
}

/// <summary>
/// Turns OpenCore's Sample.plist into a config.plist following the Dortania guide:
/// sample entries are cleared, then ACPI, Booter, DeviceProperties, Kernel, Misc, NVRAM,
/// PlatformInfo and UEFI are filled for the detected platform.
/// </summary>
public static class ConfigBuilder
{
    public static PDict Build(PDict sample, HardwareReport hw, CompatibilityReport compat, ConfigInputs input)
    {
        var config = (PDict)sample.Clone();
        foreach (var key in config.Keys.Where(k => k.StartsWith('#')).ToList())
            config.Remove(key);

        var profile = PlatformProfile.For(hw, compat);
        var version = input.Plan.Version.Id;

        Acpi(config.Dict("ACPI"), input.Ssdts);
        Booter(config.Dict("Booter"), profile);
        DeviceProperties(config.Dict("DeviceProperties"), input);
        Kernel(config.Dict("Kernel"), profile, input);
        Misc(config.Dict("Misc"), input);
        Nvram(config.Dict("NVRAM"), input.Plan);
        PlatformInfo(config.Dict("PlatformInfo"), hw, input.Smbios);
        Uefi(config.Dict("UEFI"), profile, input.Drivers, version);
        return config;
    }

    /// <summary>
    /// Creates an entry for a Sample.plist array by cloning its first sample item, so keys added
    /// by newer OpenCore versions (e.g. UEFI → Drivers → HideVerbose in 1.0.8) are always present.
    /// The array is cleared on the first call.
    /// </summary>
    private sealed class EntryFactory(PArray array)
    {
        private readonly PDict _template = array.Items.OfType<PDict>().FirstOrDefault()?.Clone() as PDict ?? new PDict();
        private bool _cleared;

        public void Add(params (string Key, PNode Value)[] values)
        {
            if (!_cleared)
            {
                array.Items.Clear();
                _cleared = true;
            }
            var entry = (PDict)_template.Clone();
            foreach (var (k, v) in values)
                entry[k] = v;
            array.Items.Add(entry);
        }

        public void Clear()
        {
            array.Items.Clear();
            _cleared = true;
        }
    }

    private static void Acpi(PDict acpi, SsdtResult ssdts)
    {
        var add = new EntryFactory(acpi.Array("Add"));
        add.Clear();
        foreach (var t in ssdts.Tables)
            add.Add(("Comment", P.Str(t.Name)), ("Enabled", P.Bool(true)), ("Path", P.Str(t.Name + ".aml")));

        acpi.Array("Delete").Items.Clear();
        var patch = new EntryFactory(acpi.Array("Patch"));
        patch.Clear();
        foreach (var p in ssdts.Patches)
        {
            patch.Add(
                ("Base", P.Str("")), ("BaseSkip", P.Int(0)), ("Comment", P.Str(p.Comment)), ("Count", P.Int(0)),
                ("Enabled", P.Bool(true)), ("Find", P.Data(p.Find)), ("Limit", P.Int(0)), ("Mask", P.Data([])),
                ("OemTableId", P.Data([])), ("Replace", P.Data(p.Replace)), ("ReplaceMask", P.Data([])),
                ("Skip", P.Int(0)), ("TableLength", P.Int(0)), ("TableSignature", P.Data([])));
        }
    }

    private static void Booter(PDict booter, PlatformProfile p)
    {
        booter.Array("MmioWhitelist").Items.Clear();
        booter.Array("Patch").Items.Clear();
        var q = booter.Dict("Quirks");
        q["AvoidRuntimeDefrag"] = P.Bool(true);
        q["DevirtualiseMmio"] = P.Bool(p.ModernMemoryMap);
        q["EnableSafeModeSlide"] = P.Bool(true);
        q["EnableWriteUnprotector"] = P.Bool(!p.ModernMemoryMap);
        q["ProtectUefiServices"] = P.Bool(p.ProtectUefiServices);
        q["ProvideCustomSlide"] = P.Bool(true);
        q["RebuildAppleMemoryMap"] = P.Bool(p.ModernMemoryMap);
        q["SetupVirtualMap"] = P.Bool(p.SetupVirtualMap);
        q["SyncRuntimePermissions"] = P.Bool(p.ModernMemoryMap);
    }

    private static void DeviceProperties(PDict dp, ConfigInputs input)
    {
        var add = dp.Dict("Add");
        add.Clear();
        dp.Dict("Delete").Clear();

        foreach (var group in input.Plan.DeviceProperties.GroupBy(d => d.PciPath))
        {
            var dev = add.Dict(group.Key);
            foreach (var prop in group)
                dev[prop.Key] = P.Hex(prop.Value);
        }

        if (input.Audio is { } audio)
            add.Dict(audio.ControllerPath)["layout-id"] = P.Data(BitConverter.GetBytes(audio.LayoutId));
    }

    private static void Kernel(PDict kernel, PlatformProfile p, ConfigInputs input)
    {
        var add = new EntryFactory(kernel.Array("Add"));
        add.Clear();
        foreach (var k in input.Kexts)
        {
            add.Add(
                ("Arch", P.Str("x86_64")),
                ("BundlePath", P.Str(k.BundlePath)),
                ("Comment", P.Str($"{k.Name} {k.Version}".Trim())),
                ("Enabled", P.Bool(k.Enabled)),
                ("ExecutablePath", P.Str(k.ExecutablePath)),
                ("MaxKernel", P.Str(k.MaxKernel)),
                ("MinKernel", P.Str("")),
                ("PlistPath", P.Str("Contents/Info.plist")));
        }

        kernel.Array("Block").Items.Clear();
        kernel.Array("Force").Items.Clear();
        var patches = kernel.Array("Patch");
        patches.Items.Clear();
        patches.Items.AddRange(input.KernelPatches);

        var emulate = kernel.Dict("Emulate");
        emulate["DummyPowerManagement"] = P.Bool(p.Amd);

        var q = kernel.Dict("Quirks");
        q["AppleCpuPmCfgLock"] = P.Bool(p.LegacyPowerManagement);
        q["AppleXcpmCfgLock"] = P.Bool(p.Intel && !p.LegacyPowerManagement);
        q["DisableIoMapper"] = P.Bool(p.Intel);
        q["DisableLinkeditJettison"] = P.Bool(true);
        q["PanicNoKextDump"] = P.Bool(true);
        q["PowerTimeoutKernelPanic"] = P.Bool(true);
        q["ProvideCurrentCpuInfo"] = P.Bool(p.Amd || p.Hybrid);
        q["XhciPortLimit"] = P.Bool(false);

        kernel.Dict("Scheme")["KernelArch"] = P.Str("x86_64");
    }

    private static void Misc(PDict misc, ConfigInputs input)
    {
        var debug = misc.Dict("Debug");
        debug["AppleDebug"] = P.Bool(true);
        debug["ApplePanic"] = P.Bool(true);
        debug["DisableWatchDog"] = P.Bool(true);

        var security = misc.Dict("Security");
        security["AllowSetDefault"] = P.Bool(true);
        security["ScanPolicy"] = P.Int(0);
        security["Vault"] = P.Str("Optional");
        security["SecureBootModel"] = P.Str(input.DisableAppleSecureBoot ? "Disabled" : "Default");

        misc.Array("Entries").Items.Clear();
        var tools = new EntryFactory(misc.Array("Tools"));
        tools.Clear();
        foreach (var tool in input.Tools)
        {
            tools.Add(
                ("Arguments", P.Str("")), ("Auxiliary", P.Bool(true)), ("Comment", P.Str(tool)),
                ("Enabled", P.Bool(true)), ("Flavour", P.Str("Auto")), ("FullNvramAccess", P.Bool(false)),
                ("Name", P.Str(Path.GetFileNameWithoutExtension(tool))), ("Path", P.Str(tool)),
                ("RealPath", P.Bool(false)), ("TextMode", P.Bool(false)));
        }
    }

    private const string AppleNvramGuid = "7C436110-AB2A-4BBB-A880-FE41995C9F82";

    private static void Nvram(PDict nvram, EfiPlan plan)
    {
        var apple = nvram.Dict("Add").Dict(AppleNvramGuid);
        foreach (var key in apple.Keys.Where(k => k.StartsWith('#')).ToList())
            apple.Remove(key);
        apple["boot-args"] = P.Str(string.Join(' ', plan.BootArgs.Select(b => b.Name)));
        apple["csr-active-config"] = P.Data([0, 0, 0, 0]);
        apple["prev-lang:kbd"] = P.Data("en-US:0"u8.ToArray());
        nvram["WriteFlash"] = P.Bool(true);
    }

    private static void PlatformInfo(PDict info, HardwareReport hw, SmbiosIdentity id)
    {
        var generic = info.Dict("Generic");
        generic["MLB"] = P.Str(id.Mlb);
        generic["ROM"] = P.Data(id.Rom);
        generic["SystemProductName"] = P.Str(id.Model);
        generic["SystemSerialNumber"] = P.Str(id.Serial);
        generic["SystemUUID"] = P.Str(id.Uuid);
        // Dell firmware breaks with "Create"; Dortania recommends "Custom" there.
        info["UpdateSMBIOSMode"] = P.Str(hw.System.Manufacturer.Contains("Dell", StringComparison.OrdinalIgnoreCase) ? "Custom" : "Create");
    }

    private static void Uefi(PDict uefi, PlatformProfile p, List<string> drivers, string version)
    {
        var list = new EntryFactory(uefi.Array("Drivers"));
        list.Clear();
        foreach (var d in drivers)
        {
            list.Add(
                ("Arguments", P.Str("")), ("Comment", P.Str(d)), ("Enabled", P.Bool(true)),
                ("LoadEarly", P.Bool(false)), ("Path", P.Str(d)));
        }

        var q = uefi.Dict("Quirks");
        q["IgnoreInvalidFlexRatio"] = P.Bool(p.IgnoreInvalidFlexRatio);
        q["RequestBootVarRouting"] = P.Bool(true);
        q["UnblockFsConnect"] = P.Bool(p.HpFirmware);
        uefi.Array("ReservedMemory").Items.Clear();

        // APFS driver version/date checks reject macOS releases older than Big Sur.
        if (CompatDatabase.Instance.IndexOf(version) < CompatDatabase.Instance.IndexOf("11"))
        {
            var apfs = uefi.Dict("APFS");
            apfs["MinDate"] = P.Int(-1);
            apfs["MinVersion"] = P.Int(-1);
        }
    }
}

/// <summary>Platform switches derived from the CPU generation (Dortania per-platform config pages).</summary>
public sealed class PlatformProfile
{
    public bool Intel { get; init; }
    public bool Amd { get; init; }
    public bool Hybrid { get; init; }
    /// <summary>Sandy/Ivy Bridge and older use AppleIntelCPUPowerManagement (AppleCpuPmCfgLock).</summary>
    public bool LegacyPowerManagement { get; init; }
    /// <summary>Coffee Lake+ and AMD: DevirtualiseMmio + RebuildAppleMemoryMap + SyncRuntimePermissions instead of EnableWriteUnprotector.</summary>
    public bool ModernMemoryMap { get; init; }
    public bool ProtectUefiServices { get; init; }
    public bool SetupVirtualMap { get; init; } = true;
    public bool IgnoreInvalidFlexRatio { get; init; }
    public bool HpFirmware { get; init; }

    public static PlatformProfile For(HardwareReport hw, CompatibilityReport compat)
    {
        var cpu = hw.Cpu;
        string arch = compat.Components.First(c => c.Component == "cpu").CpuRule?.Id ?? "";
        bool intel = cpu.Vendor == CpuVendor.Intel;
        bool legacyPm = arch is "intel-penryn" or "intel-nehalem" or "intel-sandy" or "intel-ivy" or "intel-hedt-legacy";
        bool modern = cpu.Vendor == CpuVendor.Amd
                      || arch is "intel-coffeelake" or "intel-cometlake" or "intel-icelake" or "intel-rocketlake"
                          or "intel-alderlake" or "intel-tigerlake" or "intel-modern-unsupported";
        bool z390 = hw.System.BoardProduct.Contains("Z390", StringComparison.OrdinalIgnoreCase);

        return new PlatformProfile
        {
            Intel = intel,
            Amd = cpu.Vendor == CpuVendor.Amd,
            Hybrid = cpu.IsHybrid,
            LegacyPowerManagement = legacyPm,
            ModernMemoryMap = modern,
            ProtectUefiServices = z390 || arch is "intel-cometlake" or "intel-icelake" or "intel-rocketlake" or "intel-alderlake" or "intel-tigerlake",
            SetupVirtualMap = arch != "intel-icelake",
            IgnoreInvalidFlexRatio = intel && (legacyPm || arch is "intel-haswell" or "intel-broadwell" or "intel-hedt"),
            HpFirmware = hw.System.Manufacturer.Contains("HP", StringComparison.OrdinalIgnoreCase)
                         || hw.System.Manufacturer.Contains("Hewlett", StringComparison.OrdinalIgnoreCase),
        };
    }
}
