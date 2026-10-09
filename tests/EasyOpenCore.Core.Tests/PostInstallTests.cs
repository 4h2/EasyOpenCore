using EasyOpenCore.Core.Efi;
using EasyOpenCore.Core.Localization;
using EasyOpenCore.Core.Models;
using EasyOpenCore.Core.Plist;
using EasyOpenCore.Core.PostInstall;
using Xunit;

namespace EasyOpenCore.Core.Tests;

public class PostInstallTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "eoc-postinstall-" + Guid.NewGuid().ToString("N"));
    private readonly GitHubClient _github;

    public PostInstallTests()
    {
        var oc = Path.Combine(_root, "EFI", "OC");
        Directory.CreateDirectory(oc);
        var config = P.Dict(
            ("Misc", P.Dict(
                ("Boot", P.Dict(("ShowPicker", P.Bool(true)), ("LauncherOption", P.Str("Disabled")))),
                ("Security", P.Dict(("ScanPolicy", P.Int(0)), ("SecureBootModel", P.Str("Default")))))),
            ("NVRAM", P.Dict(
                ("Add", P.Dict((EfiSession.AppleNvramGuid, P.Dict(
                    ("boot-args", P.Str("-v keepsyms=1 debug=0x100 npci=0x3000")),
                    ("csr-active-config", P.Data([0, 0, 0, 0])))))),
                ("Delete", P.Dict()))),
            ("Kernel", P.Dict(("Quirks", P.Dict(("DisableRtcChecksum", P.Bool(false)))))),
            ("UEFI", P.Dict(("Quirks", P.Dict()))));
        PlistSerializer.Save(config, Path.Combine(oc, "config.plist"));
        _github = new GitHubClient(Path.Combine(_root, "cache"));
    }

    public void Dispose()
    {
        _github.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    private static async Task Set(EfiSession s, string id, bool on) => await EfiTweaks.Find(id)!.SetAsync(s, on, default);

    [Fact]
    public void Opens_from_parent_efi_or_oc_folder()
    {
        var expected = Path.GetFullPath(Path.Combine(_root, "EFI"));
        Assert.Equal(expected, EfiSession.FindEfi(_root));
        Assert.Equal(expected, EfiSession.FindEfi(Path.Combine(_root, "EFI")));
        Assert.Equal(expected, EfiSession.FindEfi(Path.Combine(_root, "EFI", "OC")));
        Assert.Null(EfiSession.FindEfi(Path.Combine(_root, "cache")));
    }

    [Fact]
    public async Task Boot_arg_tweaks_add_remove_and_replace_values()
    {
        var s = EfiSession.Open(_root, _github);
        Assert.True(EfiTweaks.Find("verbose")!.IsOn(s));
        Assert.True(EfiTweaks.Find("debug_args")!.IsOn(s));
        Assert.False(EfiTweaks.Find("npci")!.IsOn(s));

        await Set(s, "verbose", false);
        await Set(s, "npci", true);
        Assert.Equal(["keepsyms=1", "debug=0x100", "npci=0x2000"], s.BootArgs);

        await Set(s, "npci", false);
        Assert.DoesNotContain(s.BootArgs, a => a.StartsWith("npci"));
        // boot-args only takes effect over an existing NVRAM value when listed in NVRAM → Delete.
        Assert.Contains(s.Config.Dict("NVRAM").Dict("Delete").Array(EfiSession.AppleNvramGuid).Items, n => n is PString { Value: "boot-args" });
    }

    [Fact]
    public async Task Config_tweaks_round_trip()
    {
        var s = EfiSession.Open(_root, _github);
        foreach (var id in new[] { "scan_policy", "secure_boot_off", "sip_off", "rtc_checksum", "launcher_option", "release_usb", "hidpi" })
        {
            var t = EfiTweaks.Find(id)!;
            Assert.False(t.IsOn(s), id);
            await t.SetAsync(s, true, default);
            Assert.True(t.IsOn(s), id);
            await t.SetAsync(s, false, default);
            Assert.False(t.IsOn(s), id);
        }

        await Set(s, "scan_policy", true);
        Assert.Equal(17760515, ((PInteger)s.Config.Dict("Misc").Dict("Security")["ScanPolicy"]).Value);
        await Set(s, "launcher_option", true);
        Assert.Equal("Full", s.Config.Dict("Misc").Dict("Boot").GetString("LauncherOption"));
        Assert.True(((PBool)s.Config.Dict("UEFI").Dict("Quirks")["RequestBootVarRouting"]).Value);
    }

    [Fact]
    public void Chime_needs_an_audio_controller_with_layout_id()
    {
        var s = EfiSession.Open(_root, _github);
        Assert.NotNull(EfiTweaks.Find("chime")!.Unavailable(s));
        s.Config.Dict("DeviceProperties").Dict("Add").Dict("PciRoot(0x0)/Pci(0x8,0x1)/Pci(0x0,0x6)")["layout-id"] = P.Data([11, 0, 0, 0]);
        Assert.Null(EfiTweaks.Find("chime")!.Unavailable(s));
        Assert.Equal("PciRoot(0x0)/Pci(0x8,0x1)/Pci(0x0,0x6)", s.AudioControllerPath);
    }

    [Fact]
    public void Every_tweak_and_problem_is_translated_and_links_resolve()
    {
        foreach (var t in EfiTweaks.All)
        {
            Assert.NotEqual($"tweak.{t.Id}.title", t.Title);
            Assert.NotEqual($"tweak.{t.Id}.detail", t.Detail);
        }
        Assert.True(Troubleshooting.Entries.Count > 90);
        Assert.Equal(Troubleshooting.Entries.Count, Troubleshooting.Entries.Select(e => e.Id).Distinct().Count());
        foreach (var e in Troubleshooting.Entries)
        {
            Assert.Contains(e.Page, Troubleshooting.Pages);
            Assert.NotEqual($"ts.{e.Id}.title", e.Title);
            Assert.NotEqual($"ts.{e.Id}.fix", e.Fix);
            Assert.False(string.IsNullOrWhiteSpace(e.Anchor), e.Id);
            Assert.All(e.Fixes, f => Assert.NotNull(EfiTweaks.Find(f.Tweak)));
        }
    }

    [Fact]
    public void Problems_are_matched_to_hardware_and_search()
    {
        var hw = new HardwareReport();
        hw.Cpu.Vendor = CpuVendor.Amd;
        hw.Cpu.Family = 0x17; // Zen
        hw.System.Chassis = ChassisKind.Laptop;
        var b550 = Troubleshooting.Entries.First(e => e.Id == "k_b550");
        var navi = Troubleshooting.Entries.First(e => e.Id == "k_navi");
        var icelakeLaptop = Troubleshooting.Entries.First(e => e.Id == "k_cd_clock");
        Assert.True(b550.MatchesHardware(hw));
        Assert.False(navi.MatchesHardware(hw));
        // Every tag must match: an AMD laptop is not an Intel laptop.
        Assert.False(icelakeLaptop.MatchesHardware(hw));
        // The FX section is for families 15h/16h only, not Ryzen.
        Assert.False(Troubleshooting.Entries.First(e => e.Id == "k_fx_exception").MatchesHardware(hw));

        Assert.True(Troubleshooting.Matches(Troubleshooting.Entries.First(e => e.Id == "k_exitbs"), "exitbs"));
        Assert.True(Troubleshooting.Matches(Troubleshooting.Entries.First(e => e.Id == "p_amd_sleep"), "wake reason"));
    }

    [Fact]
    public void Troubleshooting_text_is_english_only()
    {
        // The guide is in English, so the section texts are not translated: Portuguese falls back to them.
        var resource = typeof(Loc).Assembly.GetManifestResourceNames().First(n => n.EndsWith("help.pt-BR.json"));
        using var reader = new StreamReader(typeof(Loc).Assembly.GetManifestResourceStream(resource)!);
        Assert.DoesNotContain("\"ts.", reader.ReadToEnd());
    }

    /// <summary>
    /// Every troubleshooting entry and tweak links to a heading id that exists on the live Dortania page.
    /// Opt-in (set EOC_NETWORK_TESTS=1) because it downloads the guide.
    /// </summary>
    [Fact]
    public async Task Guide_links_point_at_existing_headings()
    {
        if (Environment.GetEnvironmentVariable("EOC_NETWORK_TESTS") != "1")
            return;

        var urls = Troubleshooting.Entries.Select(e => e.Url).Concat(EfiTweaks.All.Select(t => t.Url)).Distinct().ToList();
        using var http = new HttpClient();
        var pages = new Dictionary<string, string>();
        var missing = new List<string>();
        foreach (var url in urls)
        {
            var parts = url.Split('#', 2);
            if (!pages.TryGetValue(parts[0], out var html))
                pages[parts[0]] = html = await http.GetStringAsync(parts[0]);
            if (parts.Length == 2 && !html.Contains($"id=\"{parts[1]}\"", StringComparison.Ordinal))
                missing.Add(url);
        }
        Assert.Empty(missing);
    }
}
