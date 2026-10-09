using EasyOpenCore.Core.Compatibility;
using EasyOpenCore.Core.Localization;
using EasyOpenCore.Core.Models;

namespace EasyOpenCore.Core.PostInstall;

public sealed class TroubleFix
{
    public string Tweak { get; set; } = "";
    /// <summary>The tweak state that fixes the problem (e.g. ScanPolicy off).</summary>
    public bool On { get; set; } = true;
}

/// <summary>
/// One problem from the Dortania troubleshooting pages (Data/troubleshooting.json).
/// Texts: ts.{Id}.title (the symptom), ts.{Id}.cause and ts.{Id}.fix.
/// </summary>
public sealed class TroubleEntry
{
    private const string ExtendedBase = "https://dortania.github.io/OpenCore-Install-Guide/troubleshooting/extended/";

    public string Id { get; set; } = "";
    /// <summary>opencore, kernel, installer, postinstall or multiboot.</summary>
    public string Stage { get; set; } = "";
    /// <summary>Hardware the problem is specific to (amd, intel, laptop, intel_igpu, amd_dgpu, nvme); empty = everyone.</summary>
    public List<string> Tags { get; set; } = [];
    public string Url { get; set; } = "";
    public List<TroubleFix> Fixes { get; set; } = [];
    /// <summary>Terminal commands quoted by the guide, shown with a copy button.</summary>
    public List<string> Commands { get; set; } = [];

    public string Title => Loc.T($"ts.{Id}.title");
    public string Cause => Loc.T($"ts.{Id}.cause");
    public string Fix => Loc.T($"ts.{Id}.fix");
    public string FullUrl => Url.StartsWith("https://", StringComparison.Ordinal) ? Url : ExtendedBase + Url;

    /// <summary>True when the entry names hardware the scanned machine has.</summary>
    public bool MatchesHardware(HardwareReport hw) => Tags.Any(t => Troubleshooting.HasTag(hw, t));
}

public static class Troubleshooting
{
    public static readonly string[] Stages = ["opencore", "kernel", "installer", "postinstall", "multiboot"];

    public static IReadOnlyList<TroubleEntry> Entries { get; } = CompatDatabase.Read<List<TroubleEntry>>("troubleshooting");

    public static bool HasTag(HardwareReport hw, string tag) => tag switch
    {
        "amd" => hw.Cpu.Vendor == CpuVendor.Amd,
        "intel" => hw.Cpu.Vendor == CpuVendor.Intel,
        "laptop" => hw.System.Chassis == ChassisKind.Laptop,
        "desktop" => hw.System.Chassis != ChassisKind.Laptop,
        "intel_igpu" => hw.Gpus.Any(g => g.Kind == GpuKind.Integrated && g.VendorId == "8086"),
        "amd_dgpu" => hw.Gpus.Any(g => g.Kind == GpuKind.Discrete && g.VendorId == "1002"),
        "nvme" => hw.Storage.Any(d => d.Bus.Contains("NVMe", StringComparison.OrdinalIgnoreCase)),
        _ => false,
    };

    /// <summary>Case-insensitive search over the localized title, cause and fix (and the raw commands).</summary>
    public static bool Matches(TroubleEntry e, string query) =>
        string.IsNullOrWhiteSpace(query)
        || query.Split(' ', StringSplitOptions.RemoveEmptyEntries).All(word =>
            e.Title.Contains(word, StringComparison.OrdinalIgnoreCase)
            || e.Cause.Contains(word, StringComparison.OrdinalIgnoreCase)
            || e.Fix.Contains(word, StringComparison.OrdinalIgnoreCase)
            || e.Commands.Any(c => c.Contains(word, StringComparison.OrdinalIgnoreCase)));
}
