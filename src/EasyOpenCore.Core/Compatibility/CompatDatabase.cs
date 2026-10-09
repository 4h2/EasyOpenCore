using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using EasyOpenCore.Core.Models;

namespace EasyOpenCore.Core.Compatibility;

/// <summary>Ordered from worst to best, so levels can be compared with &lt; / &gt;.</summary>
public enum SupportLevel { Unsupported, Unknown, Limited, Patched, Supported }

public sealed class MacOsVersion
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public int Darwin { get; set; }
    public string? Note { get; set; }
    public string DisplayName => $"macOS {Id} {Name}";
}

public sealed class VersionRange
{
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    public SupportLevel Level { get; set; }
    public string? Note { get; set; }
}

public sealed class KextRef
{
    public string Name { get; set; } = "";
    public string? From { get; set; }
    public string? To { get; set; }
}

public sealed class SmbiosModel
{
    public string Id { get; set; } = "";
    public string Kind { get; set; } = "";
    public string From { get; set; } = "";
    public string To { get; set; } = "";
}

public sealed class CpuSmbiosCandidates
{
    public List<string> Desktop { get; set; } = [];
    public List<string> DesktopDgpu { get; set; } = [];
    public List<string> Laptop { get; set; } = [];
    public List<string> LaptopH { get; set; } = [];
}

public sealed class FormFactorList
{
    public List<string> Desktop { get; set; } = [];
    public List<string> Laptop { get; set; } = [];
}

public sealed class CpuRule
{
    public string Id { get; set; } = "";
    public CpuVendor Vendor { get; set; }
    public List<string> Codenames { get; set; } = [];
    public List<VersionRange> Ranges { get; set; } = [];
    public CpuSmbiosCandidates Smbios { get; set; } = new();
    public FormFactorList Ssdts { get; set; } = new();
    public List<KextRef> Kexts { get; set; } = [];
}

/// <summary>Rule for a GPU or any other device. First matching rule (file order) wins.</summary>
public sealed class DeviceRule
{
    public string Id { get; set; } = "";
    public string Component { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Vendor { get; set; }
    public List<string>? DeviceIds { get; set; }
    public List<List<string>>? DeviceIdRanges { get; set; }
    public string? NameRegex { get; set; }
    public GpuKind? Kind { get; set; }
    public List<VersionRange> Ranges { get; set; } = [];
    public List<KextRef> Kexts { get; set; } = [];
    public List<string> BootArgs { get; set; } = [];
    /// <summary>Variant ("desktop", "desktopHeadless", "laptop") → DeviceProperties.</summary>
    public Dictionary<string, Dictionary<string, string>> Properties { get; set; } = [];

    public bool Matches(string component, string vendor, string device, string name, GpuKind? kind = null)
    {
        if (!Component.Equals(component, StringComparison.OrdinalIgnoreCase))
            return false;
        if (Vendor is not null && !Vendor.Equals(vendor, StringComparison.OrdinalIgnoreCase))
            return false;
        if (Kind is not null && Kind != kind)
            return false;
        if (DeviceIds is not null && !DeviceIds.Contains(device, StringComparer.OrdinalIgnoreCase))
            return false;
        if (DeviceIdRanges is not null && !InRanges(device))
            return false;
        if (NameRegex is not null && !System.Text.RegularExpressions.Regex.IsMatch(name, NameRegex,
                System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            return false;
        return true;
    }

    private bool InRanges(string device)
    {
        if (!int.TryParse(device, System.Globalization.NumberStyles.HexNumber, null, out var id))
            return false;
        return DeviceIdRanges!.Any(r => r.Count == 2
            && id >= Convert.ToInt32(r[0], 16) && id <= Convert.ToInt32(r[1], 16));
    }
}

public sealed class KextInfo
{
    public string Name { get; set; } = "";
    public string Repo { get; set; } = "";
    public int Order { get; set; }
}

/// <summary>Compatibility database embedded as JSON (Data/*.json).</summary>
public sealed class CompatDatabase
{
    private static readonly Lazy<CompatDatabase> Shared = new(Load);

    public static CompatDatabase Instance => Shared.Value;

    public List<MacOsVersion> Versions { get; private init; } = [];
    public List<SmbiosModel> SmbiosModels { get; private init; } = [];
    public List<CpuRule> Cpus { get; private init; } = [];
    public List<DeviceRule> Gpus { get; private init; } = [];
    public List<DeviceRule> Devices { get; private init; } = [];
    public Dictionary<string, KextInfo> Kexts { get; private init; } = [];

    public int IndexOf(string versionId) => Versions.FindIndex(v => v.Id == versionId);

    /// <summary>True when <paramref name="version"/> is inside [from, to] (empty bounds are open).</summary>
    public bool InRange(string version, string? from, string? to)
    {
        int v = IndexOf(version);
        int lo = string.IsNullOrEmpty(from) ? 0 : IndexOf(from);
        int hi = string.IsNullOrEmpty(to) ? Versions.Count - 1 : IndexOf(to);
        return v >= lo && v <= hi;
    }

    public (SupportLevel Level, string? Note) Evaluate(IEnumerable<VersionRange> ranges, string version)
    {
        foreach (var r in ranges)
            if (InRange(version, r.From, r.To))
                return (r.Level, r.Note);
        return (SupportLevel.Unsupported, null);
    }

    public SmbiosModel? Smbios(string id) => SmbiosModels.FirstOrDefault(s => s.Id == id);

    private static CompatDatabase Load() => new()
    {
        Versions = Read<List<MacOsVersion>>("macos"),
        SmbiosModels = Read<List<SmbiosModel>>("smbios"),
        Cpus = Read<List<CpuRule>>("cpus"),
        Gpus = Read<List<DeviceRule>>("gpus").Select(g => { g.Component = "gpu"; return g; }).ToList(),
        Devices = Read<List<DeviceRule>>("devices"),
        Kexts = Read<List<KextInfo>>("kexts").ToDictionary(k => k.Name),
    };

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        Converters = { new JsonStringEnumConverter() },
    };

    private static T Read<T>(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"EasyOpenCore.Data.{name}.json")
                           ?? throw new InvalidOperationException($"Missing embedded data file {name}.json");
        return JsonSerializer.Deserialize<T>(stream, Options) ?? throw new InvalidOperationException($"Invalid {name}.json");
    }
}
