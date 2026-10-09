using EasyOpenCore.Core.Plist;

namespace EasyOpenCore.Core.Efi;

/// <summary>A .kext bundle (or a plugin inside one) as it will appear under EFI/OC/Kexts.</summary>
public sealed class KextBundle
{
    /// <summary>Path relative to EFI/OC/Kexts, e.g. "VoodooPS2Controller.kext/Contents/PlugIns/VoodooInput.kext".</summary>
    public string BundlePath { get; init; } = "";
    public string Identifier { get; init; } = "";
    public string Version { get; init; } = "";
    /// <summary>"Contents/MacOS/X", or empty for codeless (plist-only) kexts.</summary>
    public string ExecutablePath { get; init; } = "";
    public List<string> Dependencies { get; init; } = [];
    public string MaxKernel { get; set; } = "";
    public bool Enabled { get; set; } = true;

    public string Name => Path.GetFileNameWithoutExtension(BundlePath.Split('/')[^1]);

    public static KextBundle Read(string bundleDirectory, string bundlePath)
    {
        var info = PlistSerializer.Load(Path.Combine(bundleDirectory, "Contents", "Info.plist")).AsDict;
        var exe = info.GetString("CFBundleExecutable");
        bool hasExe = exe is not null && File.Exists(Path.Combine(bundleDirectory, "Contents", "MacOS", exe));
        return new KextBundle
        {
            BundlePath = bundlePath,
            Identifier = info.GetString("CFBundleIdentifier") ?? "",
            Version = info.GetString("CFBundleShortVersionString") ?? info.GetString("CFBundleVersion") ?? "",
            ExecutablePath = hasExe ? $"Contents/MacOS/{exe}" : "",
            Dependencies = (info.TryGet("OSBundleLibraries") as PDict)?.Keys.ToList() ?? [],
        };
    }
}

/// <summary>
/// Builds the Kernel → Add list the way ProperTree's "OC Snapshot" does: every kext and its
/// PlugIns, duplicates (same bundle identifier) disabled, ordered so dependencies load first.
/// </summary>
public static class KextSnapshot
{
    /// <param name="kextsDirectory">EFI/OC/Kexts.</param>
    /// <param name="topLevelOrder">Top-level kext folder names in preferred load order.</param>
    public static List<KextBundle> Build(string kextsDirectory, IReadOnlyList<string> topLevelOrder)
    {
        var bundles = new List<KextBundle>();
        foreach (var name in topLevelOrder)
        {
            var dir = Path.Combine(kextsDirectory, name);
            if (Directory.Exists(dir))
                Collect(dir, name, bundles);
        }

        // Same identifier twice (e.g. VoodooInput inside both VoodooPS2 and VoodooI2C): keep the first.
        var seen = new HashSet<string>();
        foreach (var b in bundles)
            if (b.Identifier.Length > 0 && !seen.Add(b.Identifier))
                b.Enabled = false;

        // The Broadcom 4360 injector must not load on Big Sur and newer (Darwin 20+).
        foreach (var b in bundles.Where(b => b.Name == "AirPortBrcm4360_Injector"))
            b.MaxKernel = "19.9.9";

        return SortByDependencies(bundles);
    }

    private static void Collect(string dir, string bundlePath, List<KextBundle> into)
    {
        if (!File.Exists(Path.Combine(dir, "Contents", "Info.plist")))
            return;
        into.Add(KextBundle.Read(dir, bundlePath));

        var plugins = Path.Combine(dir, "Contents", "PlugIns");
        if (!Directory.Exists(plugins))
            return;
        foreach (var plugin in Directory.GetDirectories(plugins, "*.kext").OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            Collect(plugin, $"{bundlePath}/Contents/PlugIns/{Path.GetFileName(plugin)}", into);
    }

    /// <summary>Stable topological sort: a kext moves after the kexts it links against.</summary>
    private static List<KextBundle> SortByDependencies(List<KextBundle> bundles)
    {
        var byId = bundles.Where(b => b.Enabled && b.Identifier.Length > 0)
            .GroupBy(b => b.Identifier).ToDictionary(g => g.Key, g => g.First());
        var result = new List<KextBundle>();
        var placed = new HashSet<KextBundle>();
        var visiting = new HashSet<KextBundle>();

        void Place(KextBundle b)
        {
            if (placed.Contains(b) || !visiting.Add(b))
                return; // already placed, or a dependency cycle (keep the original order)
            foreach (var dep in b.Dependencies)
                if (byId.TryGetValue(dep, out var target) && target != b)
                    Place(target);
            visiting.Remove(b);
            if (placed.Add(b))
                result.Add(b);
        }

        foreach (var b in bundles)
            Place(b);
        return result;
    }
}
