using EasyOpenCore.Core.Compatibility;

namespace EasyOpenCore.Core.Efi;

public sealed record ResolvedKext(string Name, string Directory, string Source);

/// <summary>Finds a kext bundle inside the latest GitHub release of its repository.</summary>
public sealed class KextResolver(GitHubClient github, CompatDatabase db)
{
    /// <summary>AirportItlwm ships one build per macOS release; asset names use these tokens.</summary>
    private static readonly Dictionary<string, string[]> ItlwmTokens = new()
    {
        ["10.13"] = ["HighSierra"],
        ["10.14"] = ["Mojave"],
        ["10.15"] = ["Catalina"],
        ["11"] = ["BigSur"],
        ["12"] = ["Monterey"],
        ["13"] = ["Ventura"],
        ["14"] = ["Sonoma14.4", "Sonoma"],
    };

    public async Task<ResolvedKext?> ResolveAsync(string name, string macosVersion, CancellationToken ct = default)
    {
        if (!db.Kexts.TryGetValue(name, out var info) || string.IsNullOrEmpty(info.Repo))
            return null;

        var release = await github.GetLatestReleaseAsync(info.Repo, ct);
        foreach (var asset in RankAssets(name, release.Assets, macosVersion))
        {
            var zip = await github.DownloadAsync(asset.Url, $"{info.Repo.Replace('/', '_')}/{release.TagName}", ct);
            var folder = github.Extract(zip);
            var bundle = FindBundle(folder, name);
            if (bundle is not null)
                return new ResolvedKext(name, bundle, $"{info.Repo} {release.TagName} ({asset.Name})");
        }
        return null;
    }

    private static IEnumerable<GitHubAsset> RankAssets(string kext, List<GitHubAsset> assets, string macos)
    {
        var zips = assets.Where(a => a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                                     && !a.Name.Contains("DEBUG", StringComparison.OrdinalIgnoreCase)
                                     && !a.Name.Contains("RESEARCH", StringComparison.OrdinalIgnoreCase)
                                     && !a.Name.Contains(".app", StringComparison.OrdinalIgnoreCase)
                                     && !a.Name.Contains("dSYM", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (kext == "AirportItlwm")
        {
            var tokens = ItlwmTokens.GetValueOrDefault(macos) ?? [];
            return tokens.SelectMany(t => zips.Where(a => a.Name.StartsWith("AirportItlwm", StringComparison.OrdinalIgnoreCase)
                                                          && a.Name.Contains(t, StringComparison.OrdinalIgnoreCase)))
                .Distinct();
        }
        if (kext == "itlwm")
            return zips.Where(a => a.Name.StartsWith("itlwm", StringComparison.OrdinalIgnoreCase));

        // Assets named after the kext first, then everything else (multi-kext packages like VirtualSMC).
        return zips.OrderByDescending(a => a.Name.Contains(kext, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(a => a.Name.Contains("RELEASE", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Shallowest "&lt;name&gt;.kext" folder that is not inside another kext or a macOS metadata folder.</summary>
    private static string? FindBundle(string root, string name) =>
        Directory.EnumerateDirectories(root, name + ".kext", SearchOption.AllDirectories)
            .Where(d => !d.Contains("__MACOSX", StringComparison.Ordinal))
            .Where(d => !Path.GetRelativePath(root, d).Split(Path.DirectorySeparatorChar)[..^1]
                .Any(p => p.EndsWith(".kext", StringComparison.OrdinalIgnoreCase)))
            .OrderBy(d => d.Count(c => c == Path.DirectorySeparatorChar))
            .FirstOrDefault();
}
