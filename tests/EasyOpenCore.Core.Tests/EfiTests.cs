using EasyOpenCore.Core.Efi;
using EasyOpenCore.Core.Plist;

namespace EasyOpenCore.Core.Tests;

public class EfiTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "eoc-kexts-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private void MakeKext(string relativePath, string id, bool executable = true, params string[] deps)
    {
        var root = Path.Combine(_dir, relativePath);
        Directory.CreateDirectory(Path.Combine(root, "Contents", "MacOS"));
        var name = Path.GetFileNameWithoutExtension(relativePath);
        var info = P.Dict(
            ("CFBundleIdentifier", P.Str(id)),
            ("CFBundleExecutable", P.Str(name)),
            ("CFBundleShortVersionString", P.Str("1.0")),
            ("OSBundleLibraries", P.Dict([.. deps.Select(d => (d, (PNode)P.Str("1.0")))])));
        PlistSerializer.Save(info, Path.Combine(root, "Contents", "Info.plist"));
        if (executable)
            File.WriteAllBytes(Path.Combine(root, "Contents", "MacOS", name), [0]);
    }

    [Fact]
    public void Snapshot_OrdersByDependencyAndDisablesDuplicates()
    {
        MakeKext("Plugin.kext", "com.test.plugin", true, "as.vit9696.Lilu");   // listed first, depends on Lilu
        MakeKext("Lilu.kext", "as.vit9696.Lilu");
        MakeKext("Host.kext", "com.test.host");
        MakeKext("Host.kext/Contents/PlugIns/Input.kext", "com.test.input");
        MakeKext("Other.kext", "com.test.other");
        MakeKext("Other.kext/Contents/PlugIns/Input.kext", "com.test.input");      // duplicate identifier
        MakeKext("Codeless.kext", "com.test.codeless", executable: false);

        var list = KextSnapshot.Build(_dir, ["Plugin.kext", "Lilu.kext", "Host.kext", "Other.kext", "Codeless.kext"]);

        var paths = list.Select(k => k.BundlePath).ToList();
        Assert.True(paths.IndexOf("Lilu.kext") < paths.IndexOf("Plugin.kext"));
        Assert.Contains("Host.kext/Contents/PlugIns/Input.kext", paths);
        Assert.False(list.Single(k => k.BundlePath == "Other.kext/Contents/PlugIns/Input.kext").Enabled);
        Assert.Equal("", list.Single(k => k.Name == "Codeless").ExecutablePath);
    }
}
