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
/// One section of the Dortania troubleshooting pages (Data/troubleshooting.json), linked by the
/// section's real anchor id. Texts (English only, like the guide): ts.{Id}.title is the section
/// heading, ts.{Id}.cause is present only when the guide states one, ts.{Id}.fix.
/// </summary>
public sealed class TroubleEntry
{
    private const string ExtendedBase = "https://dortania.github.io/OpenCore-Install-Guide/troubleshooting/extended/";

    public string Id { get; set; } = "";
    /// <summary>The guide page: opencore, kernel, userspace, post or misc.</summary>
    public string Page { get; set; } = "";
    /// <summary>The heading id on that page.</summary>
    public string Anchor { get; set; } = "";
    /// <summary>Hardware the section is specific to (amd, amd_15h_16h, intel, laptop, intel_igpu, amd_dgpu); all must match; empty = everyone.</summary>
    public List<string> Tags { get; set; } = [];
    public List<TroubleFix> Fixes { get; set; } = [];
    /// <summary>Terminal commands quoted by the guide, shown with a copy button.</summary>
    public List<string> Commands { get; set; } = [];

    public string Title => Loc.T($"ts.{Id}.title");
    public string Cause => Loc.Has($"ts.{Id}.cause") ? Loc.T($"ts.{Id}.cause") : "";
    public string Fix => Loc.T($"ts.{Id}.fix");
    public string Url => $"{ExtendedBase}{Page}-issues.html#{Anchor}";

    /// <summary>True when the section is specific to hardware the scanned machine has.</summary>
    public bool MatchesHardware(HardwareReport hw) => Tags.Count > 0 && Tags.All(t => Troubleshooting.HasTag(hw, t));
}

public static class Troubleshooting
{
    /// <summary>The five "extended" troubleshooting pages, in the guide's order.</summary>
    public static readonly string[] Pages = ["opencore", "kernel", "userspace", "post", "misc"];

    public static IReadOnlyList<TroubleEntry> Entries { get; } = CompatDatabase.Read<List<TroubleEntry>>("troubleshooting");

    public static bool HasTag(HardwareReport hw, string tag) => tag switch
    {
        "amd" => hw.Cpu.Vendor == CpuVendor.Amd,
        "intel" => hw.Cpu.Vendor == CpuVendor.Intel,
        // AMD FX and other Bulldozer-era CPUs (families 15h and 16h), named by two of the guide's sections.
        "amd_15h_16h" => hw.Cpu.Vendor == CpuVendor.Amd && hw.Cpu.Family is 0x15 or 0x16,
        "laptop" => hw.System.Chassis == ChassisKind.Laptop,
        "intel_igpu" => hw.Gpus.Any(g => g.Kind == GpuKind.Integrated && g.VendorId == "8086"),
        "amd_dgpu" => hw.Gpus.Any(g => g.Kind == GpuKind.Discrete && g.VendorId == "1002"),
        _ => false,
    };

    /// <summary>Case-insensitive search over the title, cause, fix and commands.</summary>
    public static bool Matches(TroubleEntry e, string query) =>
        string.IsNullOrWhiteSpace(query)
        || query.Split(' ', StringSplitOptions.RemoveEmptyEntries).All(word =>
            e.Title.Contains(word, StringComparison.OrdinalIgnoreCase)
            || e.Cause.Contains(word, StringComparison.OrdinalIgnoreCase)
            || e.Fix.Contains(word, StringComparison.OrdinalIgnoreCase)
            || e.Commands.Any(c => c.Contains(word, StringComparison.OrdinalIgnoreCase)));
}
