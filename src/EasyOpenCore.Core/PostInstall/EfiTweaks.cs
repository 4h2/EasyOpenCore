using EasyOpenCore.Core.Compatibility;
using EasyOpenCore.Core.Efi;
using EasyOpenCore.Core.Localization;
using EasyOpenCore.Core.Models;
using EasyOpenCore.Core.Plist;

namespace EasyOpenCore.Core.PostInstall;

public enum TweakGroup { Cleanup, Appearance, Boot, Security, Fixes, Kexts }

/// <summary>
/// One optional change from the Dortania Post-Install guide (or a fix from its troubleshooting
/// pages) that can be switched on and off on an existing EFI. Texts: tweak.{Id}.title / .detail.
/// </summary>
public sealed class EfiTweak
{
    public required string Id { get; init; }
    public required TweakGroup Group { get; init; }
    public required string Url { get; init; }
    public required Func<EfiSession, bool> IsOn { get; init; }
    public required Func<EfiSession, bool, CancellationToken, Task> SetAsync { get; init; }
    /// <summary>Whether the guide suggests this for the scanned hardware (shown as a badge, never applied automatically).</summary>
    public Func<HardwareReport, bool> Recommended { get; init; } = _ => false;
    /// <summary>Returns a localization key explaining why the tweak can't be used on this EFI, or null.</summary>
    public Func<EfiSession, string?> Unavailable { get; init; } = _ => null;
    /// <summary>Needs a download (OpenCore DEBUG build, OcBinaryData, a kext release).</summary>
    public bool Downloads { get; init; }

    public string Title => Loc.T($"tweak.{Id}.title");
    public string Detail => Loc.T($"tweak.{Id}.detail");
}

public static class EfiTweaks
{
    // Every link points at a heading id that exists on the page (checked by the network test).
    private const string Guide = "https://dortania.github.io/OpenCore-Post-Install/";
    private const string Install = "https://dortania.github.io/OpenCore-Install-Guide/";
    private const string Extended = Install + "troubleshooting/extended/";
    /// <summary>0x10F0103: file system + device lock, APFS/HFS on SATA, SAS, SCSI, NVMe and PCI devices.</summary>
    private const long ScanPolicySecure = 17760515;

    public static IReadOnlyList<EfiTweak> All { get; } = Create();

    public static EfiTweak? Find(string id) => All.FirstOrDefault(t => t.Id == id);

    private static List<EfiTweak> Create() =>
    [
        // ---- Cleanup: "Fixing Resolution and Verbose" ----
        BootArg("verbose", "-v", TweakGroup.Cleanup, Guide + "cosmetic/verbose.html#macos-decluttering"),
        new()
        {
            Id = "debug_args", Group = TweakGroup.Cleanup, Url = Install + "config.plist/coffee-lake.html#add-4",
            IsOn = s => s.HasBootArg("keepsyms=1") && s.HasBootArg("debug=0x100"),
            SetAsync = (s, on, _) =>
            {
                s.SetBootArg("keepsyms=1", on);
                s.SetBootArg("debug=0x100", on);
                return Task.CompletedTask;
            },
        },
        new()
        {
            Id = "apple_debug", Group = TweakGroup.Cleanup, Url = Guide + "cosmetic/verbose.html#macos-decluttering",
            IsOn = s => Bool(s.Config, "Misc", "Debug", "AppleDebug"),
            SetAsync = (s, on, _) =>
            {
                var debug = s.Config.Dict("Misc").Dict("Debug");
                debug["AppleDebug"] = P.Bool(on);
                debug["ApplePanic"] = P.Bool(on);
                return Task.CompletedTask;
            },
        },
        new()
        {
            Id = "debug_log", Group = TweakGroup.Cleanup, Url = Install + "troubleshooting/debug.html#file-swaps", Downloads = true,
            IsOn = s => (Int(s.Config, "Misc", "Debug", "Target") & 0x40) != 0,
            SetAsync = async (s, on, ct) =>
            {
                // "OpenCore Debugging": DEBUG files + Target 67; "Disabling all logging": RELEASE files + Target 0.
                await s.SwapFlavourAsync(on, ct);
                var debug = s.Config.Dict("Misc").Dict("Debug");
                debug["Target"] = P.Int(on ? 67 : 0);
                debug["AppleDebug"] = P.Bool(on);
                debug["ApplePanic"] = P.Bool(on);
                if (on)
                {
                    debug["DisableWatchDog"] = P.Bool(true);
                    debug["DisplayLevel"] = P.Int(2147483714);
                }
            },
        },

        // ---- Appearance: "Add GUI and Boot-chime" ----
        new()
        {
            Id = "gui", Group = TweakGroup.Appearance, Url = Guide + "cosmetic/gui.html#setting-up-opencore-s-gui", Downloads = true,
            IsOn = s => s.Config.Dict("Misc").Dict("Boot").GetString("PickerMode") == "External" && s.HasDriver("OpenCanopy.efi"),
            SetAsync = async (s, on, ct) =>
            {
                var boot = s.Config.Dict("Misc").Dict("Boot");
                var resources = Path.Combine(s.OcDir, "Resources");
                string[] folders = ["Font", "Image", "Label"];
                if (on)
                {
                    var source = await s.OcBinaryResourcesAsync(ct);
                    foreach (var f in folders)
                        CopyDirectory(Path.Combine(source, f), Path.Combine(resources, f));
                    await s.AddDriverAsync("OpenCanopy.efi", ct);
                    boot["PickerMode"] = P.Str("External");
                    boot["PickerAttributes"] = P.Int(17);
                    boot["PickerVariant"] = P.Str(@"Acidanthera\GoldenGate");
                }
                else
                {
                    s.RemoveDriver("OpenCanopy.efi");
                    foreach (var f in folders.Select(f => Path.Combine(resources, f)).Where(Directory.Exists))
                        Directory.Delete(f, recursive: true);
                    boot["PickerMode"] = P.Str("Builtin");
                    boot["PickerVariant"] = P.Str("Auto");
                }
            },
        },
        new()
        {
            Id = "chime", Group = TweakGroup.Appearance, Url = Guide + "cosmetic/gui.html#setting-up-boot-chime-with-audiodxe", Downloads = true,
            Unavailable = s => s.AudioControllerPath is null ? "tweak.chime.unavailable" : null,
            IsOn = s => Bool(s.Config, "UEFI", "Audio", "AudioSupport") && s.HasDriver("AudioDxe.efi"),
            SetAsync = async (s, on, ct) =>
            {
                var audio = s.Config.Dict("UEFI").Dict("Audio");
                const string chime = "OCEFIAudio_VoiceOver_Boot.mp3";
                var target = Path.Combine(s.OcDir, "Resources", "Audio", chime);
                if (on)
                {
                    var source = await s.OcBinaryResourcesAsync(ct);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(Path.Combine(source, "Audio", chime), target, overwrite: true);
                    await s.AddDriverAsync("AudioDxe.efi", ct);
                    audio["AudioSupport"] = P.Bool(true);
                    audio["AudioDevice"] = P.Str(s.AudioControllerPath!);
                    audio["AudioCodec"] = P.Int(0);
                    audio["AudioOutMask"] = P.Int(-1);
                    audio["PlayChime"] = P.Str("Enabled");
                    audio["MaximumGain"] = P.Int(-15);
                    audio["MinimumAssistGain"] = P.Int(-30);
                    audio["MinimumAudibleGain"] = P.Int(-55);
                    s.AppleNvram["SystemAudioVolume"] = P.Data([0x46]);
                }
                else
                {
                    s.RemoveDriver("AudioDxe.efi");
                    if (File.Exists(target))
                        File.Delete(target);
                    var folder = Path.GetDirectoryName(target)!;
                    if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any())
                        Directory.Delete(folder);
                    audio["AudioSupport"] = P.Bool(false);
                    audio["PlayChime"] = P.Str("Auto");
                }
            },
        },
        new()
        {
            Id = "hidpi", Group = TweakGroup.Appearance, Url = Guide + "cosmetic/verbose.html#macos-decluttering",
            IsOn = s => Int(s.Config, "UEFI", "Output", "UIScale") == 2,
            SetAsync = (s, on, _) =>
            {
                var output = s.Config.Dict("UEFI").Dict("Output");
                output["UIScale"] = P.Int(on ? 2 : 0);
                if (on)
                    output["Resolution"] = P.Str("Max");
                return Task.CompletedTask;
            },
        },
        Flag("hide_aux", TweakGroup.Appearance, Install + "config.plist/coffee-lake.html#boot", "Misc", "Boot", "HideAuxiliary"),

        // ---- Boot / multiboot ----
        new()
        {
            Id = "launcher_option", Group = TweakGroup.Boot, Url = Guide + "multiboot/bootstrap.html#prerequisites",
            IsOn = s => (s.Config.Dict("Misc").Dict("Boot").GetString("LauncherOption") ?? "Disabled") != "Disabled",
            SetAsync = (s, on, _) =>
            {
                // Insyde firmware (common on laptops) needs the "Short" variant.
                bool insyde = s.Hardware?.System.BiosVendor.Contains("Insyde", StringComparison.OrdinalIgnoreCase) == true;
                s.Config.Dict("Misc").Dict("Boot")["LauncherOption"] = P.Str(on ? insyde ? "Short" : "Full" : "Disabled");
                if (on)
                    s.Config.Dict("UEFI").Dict("Quirks")["RequestBootVarRouting"] = P.Bool(true);
                return Task.CompletedTask;
            },
        },
        Flag("advise_features", TweakGroup.Boot, Extended + "post-issues.html#you-can-t-change-the-startup-disk-to-the-selected-disk-error",
            "PlatformInfo", "Generic", "AdviseFeatures"),

        // ---- Security ----
        new()
        {
            Id = "scan_policy", Group = TweakGroup.Security, Url = Guide + "universal/security/scanpolicy.html",
            IsOn = s => Int(s.Config, "Misc", "Security", "ScanPolicy") != 0,
            SetAsync = (s, on, _) =>
            {
                s.Config.Dict("Misc").Dict("Security")["ScanPolicy"] = P.Int(on ? ScanPolicySecure : 0);
                return Task.CompletedTask;
            },
        },
        new()
        {
            Id = "secure_boot_off", Group = TweakGroup.Security, Url = Guide + "universal/security/applesecureboot.html#securebootmodel",
            IsOn = s => s.Config.Dict("Misc").Dict("Security").GetString("SecureBootModel") == "Disabled",
            SetAsync = (s, on, _) =>
            {
                s.Config.Dict("Misc").Dict("Security")["SecureBootModel"] = P.Str(on ? "Disabled" : "Default");
                return Task.CompletedTask;
            },
        },
        new()
        {
            Id = "sip_off", Group = TweakGroup.Security, Url = Extended + "post-issues.html#disabling-sip",
            IsOn = s => s.AppleNvram.TryGet("csr-active-config") is PData d && d.Value.Any(b => b != 0),
            SetAsync = (s, on, _) =>
            {
                s.AppleNvram["csr-active-config"] = P.Data(on ? [0x03, 0, 0, 0] : [0, 0, 0, 0]);
                s.EnsureNvramDeleted(EfiSession.AppleNvramGuid, "csr-active-config");
                return Task.CompletedTask;
            },
        },

        // ---- Fixes from the troubleshooting pages ----
        Flag("rtc_checksum", TweakGroup.Fixes, Guide + "misc/rtc.html", "Kernel", "Quirks", "DisableRtcChecksum"),
        Flag("jumpstart_hotplug", TweakGroup.Fixes, Extended + "kernel-issues.html#stuck-on-eb-ld-ofs-err-0xe-when-booting-preboot-volume",
            "UEFI", "APFS", "JumpstartHotPlug"),
        Flag("release_usb", TweakGroup.Fixes, Extended + "kernel-issues.html#usb-issues", "UEFI", "Quirks", "ReleaseUsbOwnership"),
        BootArg("npci", "npci=0x2000", TweakGroup.Fixes, Extended + "kernel-issues.html#stuck-on-rtc-pci-configuration-begins-previous-shutdown-hpet-hid-legacy"),
        BootArg("igfxonln", "igfxonln=1", TweakGroup.Fixes, Extended + "post-issues.html#coffee-lake-systems-failing-to-wake"),
        BootArg("igfxblr", "-igfxblr", TweakGroup.Fixes, Extended + "userspace-issues.html#black-screen-after-ioconsoleusers-gioscreenlock-on-laptops-and-aios"),
        BootArg("agdpmod", "agdpmod=pikera", TweakGroup.Fixes, Extended + "kernel-issues.html#black-screen-after-ioconsoleusers-gioscreenlock-on-navi"),

        // ---- Extra kexts ----
        Kext("CpuTscSync", Extended + "kernel-issues.html#macos-frozen-right-before-login", _ => false),
        // "Fixing Sleep" asks for NVMeFix on NVMe drives; the battery page uses ECEnabler instead of ACPI patches.
        Kext("NVMeFix", Guide + "universal/sleep.html#fixing-nvme", hw => hw.Storage.Any(d => d.Bus.Contains("NVMe", StringComparison.OrdinalIgnoreCase))),
        Kext("ECEnabler", Guide + "laptop-specific/battery.html", hw => hw.System.Chassis == ChassisKind.Laptop),
        Kext("RestrictEvents", Extended + "post-issues.html#memory-modules-misconfigured-on-macpro7-1", _ => false),
    ];

    // ---- factories --------------------------------------------------------------------

    private static EfiTweak BootArg(string id, string arg, TweakGroup group, string url, Func<HardwareReport, bool>? recommended = null) => new()
    {
        Id = id, Group = group, Url = url,
        Recommended = recommended ?? (_ => false),
        IsOn = s => s.HasBootArg(arg),
        SetAsync = (s, on, _) =>
        {
            s.SetBootArg(arg, on);
            return Task.CompletedTask;
        },
    };

    private static EfiTweak Flag(string id, TweakGroup group, string url, string section, string dict, string key) => new()
    {
        Id = id, Group = group, Url = url,
        IsOn = s => Bool(s.Config, section, dict, key),
        SetAsync = (s, on, _) =>
        {
            s.Config.Dict(section).Dict(dict)[key] = P.Bool(on);
            return Task.CompletedTask;
        },
    };

    private static EfiTweak Kext(string name, string url, Func<HardwareReport, bool> recommended) => new()
    {
        Id = "kext_" + name, Group = TweakGroup.Kexts, Url = url, Downloads = true,
        Recommended = recommended,
        IsOn = s => Directory.Exists(Path.Combine(s.OcDir, "Kexts", name + ".kext"))
                    && KernelAdd(s).Any(e => e.GetString("BundlePath") == name + ".kext" && e.TryGet("Enabled") is PBool { Value: true }),
        SetAsync = async (s, on, ct) =>
        {
            var target = Path.Combine(s.OcDir, "Kexts", name + ".kext");
            if (on)
            {
                var resolved = await new KextResolver(s.Github, CompatDatabase.Instance).ResolveAsync(name, "", ct)
                               ?? throw new InvalidOperationException(Loc.T("build.kext.manual"));
                if (Directory.Exists(target))
                    Directory.Delete(target, recursive: true);
                CopyDirectory(resolved.Directory, target);
                RebuildKernelAdd(s, name + ".kext");
            }
            else
            {
                s.Config.Dict("Kernel").Array("Add").Items.RemoveAll(e =>
                    (e as PDict)?.GetString("BundlePath") is { } p && (p == name + ".kext" || p.StartsWith(name + ".kext/", StringComparison.Ordinal)));
                if (Directory.Exists(target))
                    Directory.Delete(target, recursive: true);
            }
        },
    };

    // ---- helpers ------------------------------------------------------------------------

    private static IEnumerable<PDict> KernelAdd(EfiSession s) => s.Config.Dict("Kernel").Array("Add").Items.OfType<PDict>();

    /// <summary>
    /// Re-runs the OC Snapshot ordering with the new kext placed by its usual load order,
    /// keeping the existing entries (and whatever the user changed in them) as they are.
    /// </summary>
    private static void RebuildKernelAdd(EfiSession s, string newBundle)
    {
        var add = s.Config.Dict("Kernel").Array("Add").Items;
        var existing = add.OfType<PDict>().Where(e => e.GetString("BundlePath") is not null)
            .GroupBy(e => e.GetString("BundlePath")!).ToDictionary(g => g.Key, g => g.First());

        var db = CompatDatabase.Instance;
        int OrderOf(string bundle) => db.Kexts.GetValueOrDefault(Path.GetFileNameWithoutExtension(bundle))?.Order ?? 500;
        var topLevel = existing.Keys.Where(k => !k.Contains('/')).Where(k => k != newBundle).ToList();
        int index = topLevel.FindIndex(k => OrderOf(k) > OrderOf(newBundle));
        topLevel.Insert(index < 0 ? topLevel.Count : index, newBundle);

        var template = add.OfType<PDict>().FirstOrDefault()?.Clone() as PDict ?? new PDict();
        var rebuilt = new List<PNode>();
        foreach (var b in KextSnapshot.Build(Path.Combine(s.OcDir, "Kexts"), topLevel))
        {
            if (existing.Remove(b.BundlePath, out var kept))
            {
                rebuilt.Add(kept);
                continue;
            }
            var entry = (PDict)template.Clone();
            entry["Arch"] = P.Str("x86_64");
            entry["BundlePath"] = P.Str(b.BundlePath);
            entry["Comment"] = P.Str($"{b.Name} {b.Version}".Trim());
            entry["Enabled"] = P.Bool(b.Enabled);
            entry["ExecutablePath"] = P.Str(b.ExecutablePath);
            entry["MaxKernel"] = P.Str(b.MaxKernel);
            entry["MinKernel"] = P.Str("");
            entry["PlistPath"] = P.Str("Contents/Info.plist");
            rebuilt.Add(entry);
        }
        // Entries for kexts that are not on disk stay at the end, untouched (ocvalidate will flag them).
        rebuilt.AddRange(existing.Values);
        add.Clear();
        add.AddRange(rebuilt);
    }

    private static bool Bool(PDict config, string section, string dict, string key) =>
        (config.TryGet(section) as PDict)?.TryGet(dict) is PDict d && d.TryGet(key) is PBool { Value: true };

    private static long Int(PDict config, string section, string dict, string key) =>
        (config.TryGet(section) as PDict)?.TryGet(dict) is PDict d && d.TryGet(key) is PInteger i ? i.Value : 0;

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
        foreach (var dir in Directory.GetDirectories(source))
            CopyDirectory(dir, Path.Combine(target, Path.GetFileName(dir)));
    }
}
