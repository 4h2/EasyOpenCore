using System.Text.Json;
using EasyOpenCore.Core.Plist;

namespace EasyOpenCore.Core.Usb;

/// <summary>macOS "UsbConnector" values (IOUSBHostFamily / USBToolBox shared.USBPhysicalPortTypes).</summary>
public enum UsbConnector
{
    TypeA = 0,
    MiniAB = 1,
    ExpressCard = 2,
    Usb3TypeA = 3,
    Usb3TypeB = 4,
    Usb3MicroB = 5,
    Usb3MicroAB = 6,
    Usb3PowerB = 7,
    TypeCUsb2Only = 8,
    TypeCWithSwitch = 9,
    TypeCWithoutSwitch = 10,
    Internal = 255,
}

public sealed class MappedPort
{
    public int Index { get; set; }
    public UsbPortProtocol Protocol { get; set; }
    public int CompanionPort { get; set; }
    public bool UserConnectable { get; set; } = true;
    public bool TypeC { get; set; }
    /// <summary>A device was seen on this port at some point during discovery.</summary>
    public bool Seen { get; set; }
    public bool Selected { get; set; }
    /// <summary>Connector chosen by the user; null means "use the guess".</summary>
    public UsbConnector? Connector { get; set; }
    public string LastDevice { get; set; } = "";
}

public sealed class MappedController
{
    public string InstanceId { get; set; } = "";
    public string Name { get; set; } = "";
    public bool IsXhci { get; set; }
    public string AcpiPath { get; set; } = "";
    public string Bdf { get; set; } = "";
    public string VendorId { get; set; } = "";
    public string DeviceId { get; set; } = "";
    public List<MappedPort> Ports { get; set; } = [];

    public int SelectedCount => Ports.Count(p => p.Selected);
}

/// <summary>
/// The user's USB map: discovered ports, selection and connector types. Persisted in
/// %AppData%\EasyOpenCore\usbmap.json so discovery can continue across sessions.
/// </summary>
public sealed class UsbMap
{
    /// <summary>macOS enumerates at most 15 ports per controller.</summary>
    public const int PortLimit = 15;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public List<MappedController> Controllers { get; set; } = [];

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EasyOpenCore", "usbmap.json");

    public static UsbMap Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<UsbMap>(File.ReadAllText(path)) ?? new() : new();
        }
        catch (JsonException)
        {
            return new();
        }
    }

    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
    }

    /// <summary>
    /// Merges a live snapshot: new controllers/ports are added, ports with a device become "seen"
    /// (and selected the first time). Returns true when something changed.
    /// </summary>
    public bool Update(IEnumerable<UsbControllerInfo> live)
    {
        bool changed = false;
        foreach (var c in live)
        {
            var mc = Controllers.FirstOrDefault(x => x.InstanceId == c.InstanceId);
            if (mc is null)
            {
                mc = new MappedController { InstanceId = c.InstanceId };
                Controllers.Add(mc);
                changed = true;
            }
            mc.Name = c.Name;
            mc.IsXhci = c.IsXhci;
            mc.AcpiPath = c.AcpiPath;
            mc.Bdf = c.Bdf;
            mc.VendorId = c.VendorId;
            mc.DeviceId = c.DeviceId;

            foreach (var p in c.Ports)
            {
                var mp = mc.Ports.FirstOrDefault(x => x.Index == p.Index);
                if (mp is null)
                {
                    mp = new MappedPort { Index = p.Index };
                    mc.Ports.Add(mp);
                    changed = true;
                }
                mp.Protocol = p.Protocol;
                mp.CompanionPort = p.CompanionPort;
                mp.UserConnectable = p.UserConnectable;
                mp.TypeC = p.TypeC;
                if (p.DeviceName is not null && !mp.Seen)
                {
                    mp.Seen = true;
                    mp.Selected = true;
                    changed = true;
                }
                if (p.DeviceName is not null)
                    mp.LastDevice = p.DeviceName;
            }
            mc.Ports.Sort((a, b) => a.Index.CompareTo(b.Index));
        }
        return changed;
    }

    /// <summary>USBToolBox's guess_ports(): Type-C, internal, USB 3 Type-A pairs, else USB 2 Type-A.</summary>
    public static UsbConnector Guess(MappedPort port, MappedController controller)
    {
        var companion = controller.Ports.FirstOrDefault(p => p.Index == port.CompanionPort && port.CompanionPort > 0);
        if (port.TypeC || companion?.TypeC == true)
            return UsbConnector.TypeCWithSwitch;
        if (!port.UserConnectable)
            return UsbConnector.Internal;
        if (companion is not null && port.Protocol != companion.Protocol)
            return UsbConnector.Usb3TypeA;
        if (port.Protocol == UsbPortProtocol.Usb3 && companion is null)
            return UsbConnector.Internal;
        return UsbConnector.TypeA;
    }

    public static UsbConnector EffectiveConnector(MappedPort port, MappedController controller) =>
        port.Connector ?? Guess(port, controller);

    public bool HasSelection => Controllers.Any(c => c.SelectedCount > 0);

    // ---------- UTBMap.kext ----------

    /// <summary>Writes UTBMap.kext (USBToolBox map) into <paramref name="kextsDirectory"/>.</summary>
    public string WriteKext(string kextsDirectory)
    {
        var kext = Path.Combine(kextsDirectory, "UTBMap.kext");
        if (Directory.Exists(kext))
            Directory.Delete(kext, recursive: true);
        Directory.CreateDirectory(Path.Combine(kext, "Contents"));
        PlistSerializer.Save(BuildInfoPlist(), Path.Combine(kext, "Contents", "Info.plist"));
        return kext;
    }

    /// <summary>Same structure as USBToolBox/tool base.py build_kext() (non-native mode).</summary>
    public PDict BuildInfoPlist()
    {
        var personalities = new PDict();
        foreach (var c in Controllers.Where(c => c.SelectedCount > 0))
        {
            var (name, match) = Matching(c);
            var personality = P.Dict(
                ("CFBundleIdentifier", P.Str("com.dhinakg.USBToolBox.kext")),
                ("IOClass", P.Str("USBToolBox")),
                ("IOProviderClass", P.Str("IOPCIDevice")),
                ("IOMatchCategory", P.Str("USBToolBox")));
            foreach (var (k, v) in match.Items)
                personality[k] = v;

            var ports = new PDict();
            var counters = new Dictionary<string, int>();
            int highest = 0;
            foreach (var p in c.Ports.Where(p => p.Selected).OrderBy(p => p.Index))
            {
                string prefix = c.IsXhci && p.Protocol == UsbPortProtocol.Usb3 ? "SS"
                    : c.IsXhci && p.Protocol == UsbPortProtocol.Usb2 ? "HS"
                    : "PRT";
                int n = counters[prefix] = counters.GetValueOrDefault(prefix) + 1;
                ports[prefix + n.ToString().PadLeft(4 - prefix.Length, '0')] = P.Dict(
                    ("port", P.Data(BitConverter.GetBytes((uint)p.Index))),
                    ("UsbConnector", P.Int((int)EffectiveConnector(p, c))));
                highest = Math.Max(highest, p.Index);
            }
            personality["IOProviderMergeProperties"] = P.Dict(
                ("ports", ports),
                ("port-count", P.Data(BitConverter.GetBytes((uint)highest))));
            personalities[name] = personality;
        }

        return P.Dict(
            ("CFBundleDevelopmentRegion", P.Str("English")),
            ("CFBundleGetInfoString", P.Str("v1.1")),
            ("CFBundleIdentifier", P.Str("com.dhinakg.USBToolBox.map")),
            ("CFBundleInfoDictionaryVersion", P.Str("6.0")),
            ("CFBundleName", P.Str("UTBMap")),
            ("CFBundlePackageType", P.Str("KEXT")),
            ("CFBundleShortVersionString", P.Str("1.1")),
            ("CFBundleSignature", P.Str("????")),
            ("CFBundleVersion", P.Str("1.1")),
            ("IOKitPersonalities", personalities),
            ("OSBundleLibraries", P.Dict(("com.dhinakg.USBToolBox.kext", P.Str("1.0.0")))),
            ("OSBundleRequired", P.Str("Root")));
    }

    /// <summary>USBToolBox choose_matching_key(): unique ACPI name, else bus:device:function, else PCI id.</summary>
    private (string Name, PDict Match) Matching(MappedController c)
    {
        string acpiName = c.AcpiPath.Split('.')[^1];
        bool uniqueAcpi = acpiName.Length > 0
                          && Controllers.Count(x => x.AcpiPath.Split('.')[^1] == acpiName) == 1;
        if (uniqueAcpi)
            return (acpiName, P.Dict(("IONameMatch", P.Str(acpiName))));
        if (c.Bdf.Length > 0)
            return (c.Bdf, P.Dict(("IOPropertyMatch", P.Dict(("pcidebug", P.Str(c.Bdf))))));
        return (c.Name, P.Dict(("IOPCIPrimaryMatch", P.Str($"0x{c.DeviceId.ToLowerInvariant()}{c.VendorId.ToLowerInvariant()}"))));
    }
}
