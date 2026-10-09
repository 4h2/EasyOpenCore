using System.Diagnostics;
using EasyOpenCore.Core.Acpi;
using EasyOpenCore.Core.Compatibility;
using EasyOpenCore.Core.Localization;
using EasyOpenCore.Core.Models;
using EasyOpenCore.Core.Plist;

namespace EasyOpenCore.Core.Efi;

public sealed record BuiltKext(string Name, string Version, string Source);

public sealed record MissingKext(string Name, string Reason, string Url);

public sealed class EfiBuildResult
{
    public string OutputDirectory { get; init; } = "";
    public string OpenCoreVersion { get; set; } = "";
    public string Smbios { get; set; } = "";
    public List<BuiltKext> Kexts { get; } = [];
    public List<MissingKext> MissingKexts { get; } = [];
    public List<string> Ssdts { get; } = [];
    public List<ManualSsdt> ManualSsdts { get; } = [];
    public AudioLayout? Audio { get; set; }
    public string ValidationOutput { get; set; } = "";
    public bool ValidationPassed { get; set; }
    public List<string> Notes { get; } = [];
}

/// <summary>
/// Assembles a complete EFI folder: OpenCore binaries, drivers, kexts (from GitHub releases),
/// generated SSDTs and a config.plist, then validates it with ocvalidate.
/// </summary>
public sealed class EfiBuilder(GitHubClient github)
{
    private const string OpenCoreRepo = "acidanthera/OpenCorePkg";
    private const string HfsPlusUrl = "https://github.com/acidanthera/OcBinaryData/raw/master/Drivers/HfsPlus.efi";
    private const string KextGuideUrl = "https://dortania.github.io/OpenCore-Install-Guide/ktext.html";

    public async Task<EfiBuildResult> BuildAsync(HardwareReport hw, CompatibilityReport compat, string macosVersion,
        string outputDirectory, IProgress<string>? progress = null, CancellationToken ct = default, Usb.UsbMap? usbMap = null)
    {
        var db = CompatDatabase.Instance;
        var plan = EfiPlanner.Plan(hw, compat, macosVersion);
        var result = new EfiBuildResult { OutputDirectory = outputDirectory, Smbios = plan.Smbios };
        void Step(string key, params object?[] args) => progress?.Report(Loc.T($"build.step.{key}", args));

        var efi = Path.Combine(outputDirectory, "EFI");
        if (Directory.Exists(efi))
            throw new IOException(Loc.T("build.error.exists", efi));
        var oc = Path.Combine(efi, "OC");

        // 1. OpenCore
        Step("opencore");
        var release = await github.GetLatestReleaseAsync(OpenCoreRepo, ct);
        var asset = release.Assets.First(a => a.Name.EndsWith("-RELEASE.zip", StringComparison.OrdinalIgnoreCase));
        var ocPkg = github.Extract(await github.DownloadAsync(asset.Url, $"{OpenCoreRepo.Replace('/', '_')}/{release.TagName}", ct));
        result.OpenCoreVersion = release.TagName;

        CopyDirectory(Path.Combine(ocPkg, "X64", "EFI", "BOOT"), Path.Combine(efi, "BOOT"));
        foreach (var dir in new[] { "ACPI", "Drivers", "Kexts", "Tools", "Resources" })
            Directory.CreateDirectory(Path.Combine(oc, dir));
        File.Copy(Path.Combine(ocPkg, "X64", "EFI", "OC", "OpenCore.efi"), Path.Combine(oc, "OpenCore.efi"));

        var drivers = new List<string> { "OpenRuntime.efi", "ResetNvramEntry.efi" };
        foreach (var d in drivers)
            File.Copy(Path.Combine(ocPkg, "X64", "EFI", "OC", "Drivers", d), Path.Combine(oc, "Drivers", d));
        try
        {
            File.Copy(await github.DownloadAsync(HfsPlusUrl, "OcBinaryData", ct), Path.Combine(oc, "Drivers", "HfsPlus.efi"));
            drivers.Insert(0, "HfsPlus.efi");
        }
        catch (HttpRequestException)
        {
            // OpenCore ships an open-source HFS+ driver; slower, but fine as a fallback.
            File.Copy(Path.Combine(ocPkg, "X64", "EFI", "OC", "Drivers", "OpenHfsPlus.efi"), Path.Combine(oc, "Drivers", "OpenHfsPlus.efi"));
            drivers.Insert(0, "OpenHfsPlus.efi");
        }
        File.Copy(Path.Combine(ocPkg, "X64", "EFI", "OC", "Tools", "OpenShell.efi"), Path.Combine(oc, "Tools", "OpenShell.efi"));

        // 2. Kexts
        var resolver = new KextResolver(github, db);
        var topLevel = new List<string>();
        foreach (var k in plan.Kexts)
        {
            if (k.Name == "UTBMap" && usbMap?.HasSelection == true)
            {
                usbMap.WriteKext(Path.Combine(oc, "Kexts"));
                topLevel.Add("UTBMap.kext");
                result.Kexts.Add(new("UTBMap", "", Loc.T("build.kext.usbmap")));
                continue;
            }
            if (k.Name == "UTBMap")
            {
                result.MissingKexts.Add(new(k.Name, Loc.T("build.kext.utbmap"), "https://github.com/USBToolBox/tool/releases"));
                continue;
            }
            Step("kext", k.Name);
            ResolvedKext? resolved = null;
            try
            {
                resolved = await resolver.ResolveAsync(k.Name, macosVersion, ct);
            }
            catch (HttpRequestException ex)
            {
                result.MissingKexts.Add(new(k.Name, Loc.T("build.kext.download_failed", ex.Message), RepoUrl(db, k.Name)));
                continue;
            }
            if (resolved is null)
            {
                result.MissingKexts.Add(new(k.Name, Loc.T("build.kext.manual"), RepoUrl(db, k.Name)));
                continue;
            }
            CopyDirectory(resolved.Directory, Path.Combine(oc, "Kexts", k.Name + ".kext"));
            topLevel.Add(k.Name + ".kext");
            result.Kexts.Add(new(k.Name, "", resolved.Source));
        }

        var snapshot = KextSnapshot.Build(Path.Combine(oc, "Kexts"), topLevel);
        for (int i = 0; i < result.Kexts.Count; i++)
        {
            var bundle = snapshot.FirstOrDefault(b => b.BundlePath == result.Kexts[i].Name + ".kext");
            result.Kexts[i] = result.Kexts[i] with { Version = bundle?.Version ?? "" };
        }

        // 3. SSDTs
        Step("ssdt");
        var ssdts = SsdtFactory.Build(hw, compat, plan);
        var sources = Path.Combine(outputDirectory, "ACPI-Sources");
        Directory.CreateDirectory(sources);
        foreach (var t in ssdts.Tables)
        {
            await File.WriteAllBytesAsync(Path.Combine(oc, "ACPI", t.Name + ".aml"), t.Aml, ct);
            await File.WriteAllTextAsync(Path.Combine(sources, t.Name + ".dsl"), t.Asl, ct);
            result.Ssdts.Add(t.Name);
        }
        result.ManualSsdts.AddRange(ssdts.Manual);

        // 4. config.plist
        Step("smbios", plan.Smbios);
        var identity = await SmbiosGenerator.GenerateAsync(
            Path.Combine(ocPkg, "Utilities", "macserial", "macserial.exe"), plan.Smbios, ct);

        Step("audio");
        result.Audio = await AppleAlcLayouts.PickAsync(github, hw, ct);

        var kernelPatches = new List<PNode>();
        if (hw.Cpu.Vendor == CpuVendor.Amd)
        {
            Step("amd");
            kernelPatches = await AmdVanilla.LoadAsync(github, hw.Cpu.Cores, ct);
        }

        Step("config");
        var sample = PlistSerializer.Load(Path.Combine(ocPkg, "Docs", "Sample.plist")).AsDict;
        var config = ConfigBuilder.Build(sample, hw, compat, new ConfigInputs
        {
            Plan = plan,
            Ssdts = ssdts,
            Kexts = snapshot,
            Smbios = identity,
            Drivers = drivers,
            Tools = ["OpenShell.efi"],
            Audio = result.Audio,
            KernelPatches = kernelPatches,
            DisableAppleSecureBoot = db.IndexOf(macosVersion) < db.IndexOf("10.15")
                                     || compat.Components.Any(c => c.Cells[macosVersion].Note is "@note.oclp_gpu" or "@note.oclp_wifi"
                                                                   && c.Cells[macosVersion].Level == SupportLevel.Patched),
        });
        var configPath = Path.Combine(oc, "config.plist");
        PlistSerializer.Save(config, configPath);

        // 5. Validate
        Step("validate");
        (result.ValidationPassed, result.ValidationOutput) =
            await ValidateAsync(Path.Combine(ocPkg, "Utilities", "ocvalidate", "ocvalidate.exe"), configPath, ct);

        result.Notes.AddRange(plan.Warnings);
        await File.WriteAllTextAsync(Path.Combine(outputDirectory, "EasyOpenCore-summary.txt"), Summary(result, plan), ct);
        return result;
    }

    private static async Task<(bool, string)> ValidateAsync(string ocvalidate, string config, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(ocvalidate, [config])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync(ct);
        var stderr = p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        return (p.ExitCode == 0, ((await stdout) + (await stderr)).Trim());
    }

    private static string RepoUrl(CompatDatabase db, string kext) =>
        db.Kexts.GetValueOrDefault(kext) is { Repo.Length: > 0 } k ? $"https://github.com/{k.Repo}/releases" : KextGuideUrl;

    private static string Summary(EfiBuildResult r, EfiPlan plan)
    {
        var lines = new List<string>
        {
            $"EasyOpenCore — {plan.Version.DisplayName}",
            $"OpenCore {r.OpenCoreVersion} · SMBIOS {r.Smbios}",
            "",
            "Kexts:",
        };
        lines.AddRange(r.Kexts.Select(k => $"  {k.Name} {k.Version}  [{k.Source}]"));
        if (r.MissingKexts.Count > 0)
        {
            lines.Add("");
            lines.Add("Kexts to add manually:");
            lines.AddRange(r.MissingKexts.Select(k => $"  {k.Name}: {k.Reason} {k.Url}"));
        }
        lines.Add("");
        lines.Add("SSDTs: " + string.Join(", ", r.Ssdts));
        lines.AddRange(r.ManualSsdts.Select(m => $"  Manual: {m.Name} — {Loc.T(m.ReasonKey)}"));
        if (r.Audio is { } a)
        {
            lines.Add("");
            lines.Add($"Audio: layout-id {a.LayoutId} on {a.ControllerPath}");
            lines.Add("  Other layouts to try (boot-arg alcid=N): " + string.Join(", ", a.Alternatives.Select(x => x.Id)));
        }
        lines.Add("");
        lines.Add("ocvalidate:");
        lines.Add(r.ValidationOutput);
        lines.AddRange(r.Notes.Select(n => "Note: " + n));
        return string.Join(Environment.NewLine, lines) + Environment.NewLine;
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
        foreach (var dir in Directory.GetDirectories(source))
            CopyDirectory(dir, Path.Combine(target, Path.GetFileName(dir)));
    }
}
