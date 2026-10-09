using System.Globalization;
using System.Text.RegularExpressions;

namespace EasyOpenCore.Core.Hardware;

/// <summary>Converts Windows device locations to the formats used by OpenCore.</summary>
public static partial class DevicePaths
{
    /// <summary>"PCIROOT(0)#PCI(1C04)#PCI(0000)" → "PciRoot(0x0)/Pci(0x1C,0x4)/Pci(0x0,0x0)".</summary>
    public static string ToOpenCorePciPath(string windowsPath)
    {
        var parts = new List<string>();
        foreach (var segment in windowsPath.Split('#'))
        {
            var root = PciRootRegex().Match(segment);
            if (root.Success)
            {
                parts.Add($"PciRoot(0x{int.Parse(root.Groups[1].Value, NumberStyles.HexNumber):X})");
                continue;
            }

            var pci = PciRegex().Match(segment);
            if (!pci.Success)
                return ""; // non-PCI segments (USB, etc.) are not part of a DevicePath
            int dev = int.Parse(pci.Groups[1].Value, NumberStyles.HexNumber);
            int fn = int.Parse(pci.Groups[2].Value, NumberStyles.HexNumber);
            parts.Add($"Pci(0x{dev:X},0x{fn:X})");
        }
        return string.Join('/', parts);
    }

    /// <summary>"ACPI(_SB_)#ACPI(PCI0)#ACPI(GFX0)" → "\_SB.PCI0.GFX0".</summary>
    public static string ToAcpiPath(string windowsPath)
    {
        var names = AcpiRegex().Matches(windowsPath).Select(m => m.Groups[1].Value).ToList();
        if (names.Count == 0)
            return "";
        // Strip NameSeg padding underscores (_SB_ → _SB, EC__ → EC), as iasl does.
        return "\\" + string.Join('.', names.Select(n => n.TrimEnd('_')));
    }

    [GeneratedRegex(@"^PCIROOT\(([0-9A-F]+)\)$", RegexOptions.IgnoreCase)]
    private static partial Regex PciRootRegex();

    [GeneratedRegex(@"^PCI\(([0-9A-F]{2})([0-9A-F]{2})\)$", RegexOptions.IgnoreCase)]
    private static partial Regex PciRegex();

    [GeneratedRegex(@"ACPI\(([^)]+)\)", RegexOptions.IgnoreCase)]
    private static partial Regex AcpiRegex();
}

/// <summary>Extracts VEN/DEV/SUBSYS/REV and VID/PID from hardware IDs.</summary>
public static partial class HardwareIds
{
    public static string Vendor(IEnumerable<string> ids) => Find(ids, VenRegex());
    public static string Device(IEnumerable<string> ids) => Find(ids, DevRegex());
    public static string Subsystem(IEnumerable<string> ids) => Find(ids, SubsysRegex());
    public static string Revision(IEnumerable<string> ids) => Find(ids, RevRegex());
    public static string UsbVid(IEnumerable<string> ids) => Find(ids, VidRegex());
    public static string UsbPid(IEnumerable<string> ids) => Find(ids, PidRegex());

    /// <summary>PCI class code (e.g. "0300", "0C0330") from the "CC_xxxx" compatible IDs.</summary>
    public static string ClassCode(IEnumerable<string> compatibleIds) =>
        compatibleIds.Select(id => CcRegex().Match(id))
            .Where(m => m.Success)
            .Select(m => m.Groups[1].Value.ToUpperInvariant())
            .OrderByDescending(v => v.Length)
            .FirstOrDefault() ?? "";

    private static string Find(IEnumerable<string> ids, Regex regex)
    {
        foreach (var id in ids)
        {
            var m = regex.Match(id);
            if (m.Success)
                return m.Groups[1].Value.ToUpperInvariant();
        }
        return "";
    }

    [GeneratedRegex(@"VEN_([0-9A-F]{4})", RegexOptions.IgnoreCase)]
    private static partial Regex VenRegex();

    [GeneratedRegex(@"DEV_([0-9A-F]{4})", RegexOptions.IgnoreCase)]
    private static partial Regex DevRegex();

    [GeneratedRegex(@"SUBSYS_([0-9A-F]{8})", RegexOptions.IgnoreCase)]
    private static partial Regex SubsysRegex();

    [GeneratedRegex(@"REV_([0-9A-F]{2,4})", RegexOptions.IgnoreCase)]
    private static partial Regex RevRegex();

    [GeneratedRegex(@"VID_([0-9A-F]{4})", RegexOptions.IgnoreCase)]
    private static partial Regex VidRegex();

    [GeneratedRegex(@"PID_([0-9A-F]{4})", RegexOptions.IgnoreCase)]
    private static partial Regex PidRegex();

    [GeneratedRegex(@"CC_([0-9A-F]{4,6})$", RegexOptions.IgnoreCase)]
    private static partial Regex CcRegex();
}
