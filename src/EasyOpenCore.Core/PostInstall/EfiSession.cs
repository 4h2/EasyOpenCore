using System.Security.Cryptography;
using EasyOpenCore.Core.Efi;
using EasyOpenCore.Core.Localization;
using EasyOpenCore.Core.Models;
using EasyOpenCore.Core.Plist;

namespace EasyOpenCore.Core.PostInstall;

/// <summary>The OpenCorePkg build an EFI was made from: both flavours of the same release.</summary>
public sealed record OpenCorePackage(string Tag, string ReleaseDir, string DebugDir, bool EfiIsDebug)
{
    public string Dir(bool debug) => debug ? DebugDir : ReleaseDir;
    public string OcValidate => Path.Combine(ReleaseDir, "Utilities", "ocvalidate", "ocvalidate.exe");
}

public sealed record TweakChangeResult(string BackupPath, bool ValidationPassed, string ValidationOutput);

/// <summary>
/// An existing EFI folder opened for post-install changes: its config.plist in memory plus
/// helpers to edit boot-args, drivers and kexts, and to fetch matching OpenCore binaries.
/// </summary>
public sealed class EfiSession
{
    public const string AppleNvramGuid = "7C436110-AB2A-4BBB-A880-FE41995C9F82";
    private const string OpenCoreRepo = "acidanthera/OpenCorePkg";
    private const string OcBinaryDataZip = "https://github.com/acidanthera/OcBinaryData/archive/refs/heads/master.zip";

    private OpenCorePackage? _package;

    private EfiSession(string efiDir, PDict config, GitHubClient github, HardwareReport? hardware)
    {
        EfiDir = efiDir;
        Config = config;
        Github = github;
        Hardware = hardware;
    }

    /// <summary>The EFI folder (contains BOOT and OC).</summary>
    public string EfiDir { get; }
    public string OcDir => Path.Combine(EfiDir, "OC");
    public string ConfigPath => Path.Combine(OcDir, "config.plist");
    public PDict Config { get; private set; }
    public GitHubClient Github { get; }
    public HardwareReport? Hardware { get; }
    public IProgress<string>? Progress { get; set; }

    /// <param name="folder">A folder containing EFI/, the EFI folder itself, or its OC folder.</param>
    public static EfiSession Open(string folder, GitHubClient github, HardwareReport? hardware = null)
    {
        var efi = FindEfi(folder) ?? throw new FileNotFoundException(Loc.T("postinstall.error.no_config", folder));
        var config = PlistSerializer.Load(Path.Combine(efi, "OC", "config.plist")).AsDict;
        return new EfiSession(efi, config, github, hardware);
    }

    public static string? FindEfi(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder))
            return null;
        foreach (var candidate in new[] { Path.Combine(folder, "EFI"), folder, Path.GetDirectoryName(folder) ?? "" })
            if (candidate.Length > 0 && File.Exists(Path.Combine(candidate, "OC", "config.plist")))
                return Path.GetFullPath(candidate);
        return null;
    }

    public void Reload() => Config = PlistSerializer.Load(ConfigPath).AsDict;

    // ---- config helpers -------------------------------------------------------------

    public PDict AppleNvram => Config.Dict("NVRAM").Dict("Add").Dict(AppleNvramGuid);

    public List<string> BootArgs
    {
        get => (AppleNvram.GetString("boot-args") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        set
        {
            AppleNvram["boot-args"] = P.Str(string.Join(' ', value));
            EnsureNvramDeleted(AppleNvramGuid, "boot-args");
        }
    }

    public bool HasBootArg(string arg) => BootArgs.Contains(arg);

    public void SetBootArg(string arg, bool on)
    {
        // Args like "npci=0x2000" replace any other value of the same name.
        string name = arg.Split('=')[0];
        var args = BootArgs.Where(a => a.Split('=')[0] != name).ToList();
        if (on)
            args.Add(arg);
        BootArgs = args;
    }

    /// <summary>NVRAM → Delete makes OpenCore overwrite a variable that already exists in NVRAM.</summary>
    public void EnsureNvramDeleted(string guid, string key)
    {
        var list = Config.Dict("NVRAM").Dict("Delete").Array(guid).Items;
        if (!list.OfType<PString>().Any(s => s.Value == key))
            list.Add(P.Str(key));
    }

    public PArray Drivers => Config.Dict("UEFI").Array("Drivers");

    public bool HasDriver(string file) =>
        Drivers.Items.OfType<PDict>().Any(d => d.GetString("Path") == file && d.TryGet("Enabled") is PBool { Value: true });

    /// <summary>Copies a driver from the OpenCore package and adds its UEFI → Drivers entry.</summary>
    public async Task AddDriverAsync(string file, CancellationToken ct)
    {
        var pkg = await PackageAsync(ct);
        File.Copy(Path.Combine(pkg.Dir(pkg.EfiIsDebug), "X64", "EFI", "OC", "Drivers", file), Path.Combine(OcDir, "Drivers", file), overwrite: true);
        if (Drivers.Items.OfType<PDict>().FirstOrDefault(d => d.GetString("Path") == file) is { } existing)
        {
            existing["Enabled"] = P.Bool(true);
            return;
        }
        var entry = Drivers.Items.OfType<PDict>().FirstOrDefault()?.Clone() as PDict ?? new PDict();
        entry["Arguments"] = P.Str("");
        entry["Comment"] = P.Str(file);
        entry["Enabled"] = P.Bool(true);
        entry["LoadEarly"] = P.Bool(false);
        entry["Path"] = P.Str(file);
        Drivers.Items.Add(entry);
    }

    public void RemoveDriver(string file)
    {
        Drivers.Items.RemoveAll(d => (d as PDict)?.GetString("Path") == file);
        var path = Path.Combine(OcDir, "Drivers", file);
        if (File.Exists(path))
            File.Delete(path);
    }

    /// <summary>The PCI path that carries AppleALC's layout-id, i.e. the onboard audio controller.</summary>
    public string? AudioControllerPath =>
        Config.Dict("DeviceProperties").Dict("Add").Items
            .FirstOrDefault(i => i.Value is PDict d && d.ContainsKey("layout-id")).Key;

    // ---- downloads ----------------------------------------------------------------

    /// <summary>
    /// Finds the OpenCore release this EFI came from by hashing OpenCore.efi against the
    /// cached packages (then the latest release), so swapped binaries always match.
    /// </summary>
    public async Task<OpenCorePackage> PackageAsync(CancellationToken ct = default)
    {
        if (_package is not null)
            return _package;

        var hash = Sha(Path.Combine(OcDir, "OpenCore.efi"));
        var root = Path.Combine(Github.CacheRoot, "downloads", OpenCoreRepo.Replace('/', '_'));
        var tags = Directory.Exists(root) ? Directory.GetDirectories(root).Select(Path.GetFileName).OfType<string>().ToList() : [];
        foreach (var tag in tags)
            if (await MatchAsync(tag, hash, ct) is { } cached)
                return _package = cached;

        var latest = await Github.GetLatestReleaseAsync(OpenCoreRepo, ct);
        if (!tags.Contains(latest.TagName) && await MatchAsync(latest.TagName, hash, ct) is { } fresh)
            return _package = fresh;
        throw new InvalidOperationException(Loc.T("postinstall.error.unknown_oc"));
    }

    private async Task<OpenCorePackage?> MatchAsync(string tag, string hash, CancellationToken ct)
    {
        string? release = null, debug = null;
        foreach (var flavour in new[] { "RELEASE", "DEBUG" })
        {
            var url = $"https://github.com/{OpenCoreRepo}/releases/download/{tag}/OpenCore-{tag}-{flavour}.zip";
            var dir = Github.Extract(await Github.DownloadAsync(url, $"{OpenCoreRepo.Replace('/', '_')}/{tag}", ct));
            if (flavour == "RELEASE") release = dir; else debug = dir;
        }
        bool isRelease = Sha(Path.Combine(release!, "X64", "EFI", "OC", "OpenCore.efi")) == hash;
        bool isDebug = Sha(Path.Combine(debug!, "X64", "EFI", "OC", "OpenCore.efi")) == hash;
        return isRelease || isDebug ? new OpenCorePackage(tag, release!, debug!, isDebug) : null;
    }

    /// <summary>OcBinaryData's Resources folder (fonts, picker icons, audio).</summary>
    public async Task<string> OcBinaryResourcesAsync(CancellationToken ct)
    {
        var dir = Github.Extract(await Github.DownloadAsync(OcBinaryDataZip, "OcBinaryData-archive", ct));
        return Directory.GetDirectories(dir, "Resources", SearchOption.AllDirectories).First();
    }

    /// <summary>Switches BOOTx64.efi, OpenCore.efi, OpenRuntime.efi (and OpenCanopy.efi) to the DEBUG or RELEASE build.</summary>
    public async Task SwapFlavourAsync(bool debug, CancellationToken ct)
    {
        var pkg = await PackageAsync(ct);
        var src = Path.Combine(pkg.Dir(debug), "X64", "EFI");
        void Swap(string relative)
        {
            var target = Path.Combine(EfiDir, relative);
            if (File.Exists(target))
                File.Copy(Path.Combine(src, relative), target, overwrite: true);
        }
        Swap(Path.Combine("BOOT", "BOOTx64.efi"));
        Swap(Path.Combine("OC", "OpenCore.efi"));
        foreach (var driver in new[] { "OpenRuntime.efi", "OpenCanopy.efi", "AudioDxe.efi", "ResetNvramEntry.efi" })
            Swap(Path.Combine("OC", "Drivers", driver));
        _package = pkg with { EfiIsDebug = debug };
    }

    // ---- apply ----------------------------------------------------------------------

    /// <summary>Backs up config.plist, applies the changes in order, saves and runs ocvalidate.</summary>
    public async Task<TweakChangeResult> ApplyAsync(IReadOnlyList<(EfiTweak Tweak, bool On)> changes, CancellationToken ct = default)
    {
        var backups = Path.Combine(Path.GetDirectoryName(EfiDir)!, "EasyOpenCore-backups");
        Directory.CreateDirectory(backups);
        var backup = Path.Combine(backups, $"config-{DateTime.Now:yyyyMMdd-HHmmss}.plist");
        File.Copy(ConfigPath, backup, overwrite: true);

        foreach (var (tweak, on) in changes)
        {
            Progress?.Report(Loc.T(on ? "postinstall.applying" : "postinstall.reverting", tweak.Title));
            var before = (PDict)Config.Clone();
            try
            {
                await tweak.SetAsync(this, on, ct);
            }
            catch
            {
                // Earlier tweaks already changed files (e.g. swapped to DEBUG binaries), so their
                // config edits are kept; only the failed tweak's half-done edit is dropped.
                Config = before;
                PlistSerializer.Save(Config, ConfigPath);
                throw;
            }
        }
        PlistSerializer.Save(Config, ConfigPath);

        Progress?.Report(Loc.T("build.step.validate"));
        var pkg = await PackageAsync(ct);
        var (passed, output) = await EfiBuilder.ValidateAsync(pkg.OcValidate, ConfigPath, ct);
        return new TweakChangeResult(backup, passed, output);
    }

    private static string Sha(string file) =>
        File.Exists(file) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))) : "";
}
