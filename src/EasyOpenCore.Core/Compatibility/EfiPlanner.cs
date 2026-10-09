using System.Text.RegularExpressions;
using EasyOpenCore.Core.Localization;
using EasyOpenCore.Core.Models;

namespace EasyOpenCore.Core.Compatibility;

/// <summary>A planned kext/SSDT/boot-arg with a localized explanation.</summary>
public sealed record PlanItem(string Name, string Reason, string Source = "");

public sealed record DeviceProperty(string PciPath, string Key, string Value, string Device);

/// <summary>What the EFI will contain for one target macOS version.</summary>
public sealed class EfiPlan
{
    public MacOsVersion Version { get; init; } = new();
    public string Smbios { get; set; } = "";
    public string SmbiosReason { get; set; } = "";
    public List<PlanItem> Kexts { get; } = [];
    public List<PlanItem> Ssdts { get; } = [];
    public List<PlanItem> BootArgs { get; } = [];
    public List<DeviceProperty> DeviceProperties { get; } = [];
    public List<string> Warnings { get; } = [];
}

public static class EfiPlanner
{
    public static EfiPlan Plan(HardwareReport hw, CompatibilityReport compat, string versionId, CompatDatabase? db = null)
    {
        db ??= CompatDatabase.Instance;
        var version = db.Versions.First(v => v.Id == versionId);
        var plan = new EfiPlan { Version = version };
        bool laptop = compat.IsLaptop;
        var cpu = compat.Components.First(c => c.Component == "cpu");
        var cpuRule = cpu.CpuRule;

        bool Works(ComponentResult c) => c.Cells[versionId].Level >= SupportLevel.Limited;
        var workingGpus = compat.Components.Where(c => c.Component == "gpu" && Works(c)).ToList();
        bool hasWorkingDgpu = workingGpus.Any(g => g.Gpu?.Kind == GpuKind.Discrete);

        PickSmbios(hw, db, plan, cpuRule, laptop, hasWorkingDgpu);
        PlanKexts(hw, compat, db, plan, cpuRule, laptop, Works);
        PlanSsdts(hw, compat, plan, cpuRule, laptop);
        PlanGraphics(compat, plan, laptop, hasWorkingDgpu, Works);

        if (versionId == db.Versions[^1].Id)
            plan.BootArgs.Add(new("-lilubetaall", Loc.T("plan.bootarg.lilubeta")));

        foreach (var c in compat.Components.Where(c => c.Cells[versionId].Level == SupportLevel.Patched))
            plan.Warnings.Add($"{c.Name}: {Loc.T(c.Cells[versionId].Note ?? "@note.patched")}");

        return plan;
    }

    private static void PickSmbios(HardwareReport hw, CompatDatabase db, EfiPlan plan, CpuRule? rule, bool laptop, bool dgpu)
    {
        var s = rule?.Smbios ?? new CpuSmbiosCandidates();
        bool hSeries = Regex.IsMatch(hw.Cpu.Name, @"\d{4,5}H", RegexOptions.IgnoreCase);
        var candidates = laptop
            ? (hSeries && s.LaptopH.Count > 0 ? s.LaptopH : s.Laptop)
            : (dgpu && s.DesktopDgpu.Count > 0 ? s.DesktopDgpu : s.Desktop);

        string kind = laptop ? "laptop" : "desktop";
        var fits = candidates.Select(db.Smbios).Where(m => m is not null && db.InRange(plan.Version.Id, m.From, m.To)).ToList();

        if (fits.Count > 0)
        {
            plan.Smbios = fits[0]!.Id;
            plan.SmbiosReason = fits[0]!.Id == candidates[0]
                ? Loc.T("plan.smbios.best", hw.Cpu.Codename)
                : Loc.T("plan.smbios.newer", candidates[0], plan.Version.DisplayName);
            return;
        }

        // No CPU-specific candidate: fall back to the oldest model of the same form factor that runs this version.
        var fallback = db.SmbiosModels.FirstOrDefault(m => m.Kind == kind && db.InRange(plan.Version.Id, m.From, m.To));
        plan.Smbios = fallback?.Id ?? "";
        plan.SmbiosReason = Loc.T("plan.smbios.fallback");
    }

    private static void PlanKexts(HardwareReport hw, CompatibilityReport compat, CompatDatabase db, EfiPlan plan,
        CpuRule? cpuRule, bool laptop, Func<ComponentResult, bool> works)
    {
        var picked = new Dictionary<string, string>();
        void Add(string name, string reason) => picked.TryAdd(name, reason);
        bool Applies(KextRef k) => db.InRange(plan.Version.Id, k.From, k.To);

        Add("Lilu", Loc.T("plan.kext.lilu"));
        Add("VirtualSMC", Loc.T("plan.kext.virtualsmc"));
        Add("WhateverGreen", Loc.T("plan.kext.whatevergreen"));

        if (hw.Cpu.Vendor == CpuVendor.Intel)
        {
            Add("SMCProcessor", Loc.T("plan.kext.smcprocessor"));
            if (!laptop)
                Add("SMCSuperIO", Loc.T("plan.kext.smcsuperio"));
        }
        if (laptop)
        {
            Add("SMCBatteryManager", Loc.T("plan.kext.battery"));
            Add("ECEnabler", Loc.T("plan.kext.ecenabler"));
            Add("BrightnessKeys", Loc.T("plan.kext.brightness"));
        }

        foreach (var k in cpuRule?.Kexts.Where(Applies) ?? [])
            Add(k.Name, Loc.T("plan.kext.for", hw.Cpu.Codename));

        if (!hw.Cpu.Has("AVX2") && db.IndexOf(plan.Version.Id) >= db.IndexOf("13"))
            Add("CryptexFixup", Loc.T("plan.kext.cryptexfixup"));

        foreach (var c in compat.Components.Where(works))
            foreach (var k in c.Rule?.Kexts.Where(Applies) ?? [])
                Add(k.Name, Loc.T("plan.kext.for", c.Name));

        if (plan.Smbios == "MacPro7,1")
            Add("RestrictEvents", Loc.T("plan.kext.restrictevents"));

        // AppleMCEReporter panics on AMD CPUs, but only loads with Mac Pro / iMac Pro SMBIOS.
        if (hw.Cpu.Vendor == CpuVendor.Amd && plan.Smbios is "MacPro6,1" or "MacPro7,1" or "iMacPro1,1"
            && db.IndexOf(plan.Version.Id) >= db.IndexOf("12"))
            Add("AppleMCEReporterDisabler", Loc.T("plan.kext.mce"));

        Add("USBToolBox", Loc.T("plan.kext.usbtoolbox"));
        Add("UTBMap", Loc.T("plan.kext.utbmap"));

        foreach (var (name, reason) in picked.OrderBy(p => db.Kexts.GetValueOrDefault(p.Key)?.Order ?? 999))
            plan.Kexts.Add(new(name, reason, db.Kexts.GetValueOrDefault(name)?.Repo ?? ""));
    }

    private static void PlanSsdts(HardwareReport hw, CompatibilityReport compat, EfiPlan plan, CpuRule? rule, bool laptop)
    {
        var acpi = compat.Acpi;
        var board = hw.System.BoardProduct;

        foreach (var name in (laptop ? rule?.Ssdts.Laptop : rule?.Ssdts.Desktop) ?? [])
        {
            switch (name)
            {
                // SSDT-PMC is only needed on true 300-series chipsets (not Z370).
                case "SSDT-PMC" when !Regex.IsMatch(board, "B360|B365|H310|H370|Q370|Z390", RegexOptions.IgnoreCase):
                    continue;
                case "SSDT-PLUG" or "SSDT-PLUG-ALT":
                    plan.Ssdts.Add(new(name, Loc.T("plan.ssdt.plug", Or(acpi.CpuPath))));
                    break;
                case "SSDT-EC" or "SSDT-EC-USBX":
                    plan.Ssdts.Add(new(name, acpi.EcPath.Length == 0
                        ? Loc.T("plan.ssdt.ec_none")
                        : Loc.T(laptop ? "plan.ssdt.ec_laptop" : "plan.ssdt.ec_desktop", acpi.EcPath)));
                    break;
                default:
                    plan.Ssdts.Add(new(name, Loc.T($"plan.ssdt.{name}")));
                    break;
            }
        }

        if (acpi.HasAwac)
            plan.Ssdts.Add(new("SSDT-AWAC", Loc.T("plan.ssdt.awac")));
        else if (!acpi.HasRtc)
            plan.Ssdts.Add(new("SSDT-RTC0", Loc.T("plan.ssdt.rtc0")));

        if (hw.Cpu.Vendor == CpuVendor.Amd && !laptop && Regex.IsMatch(board, "B550|A520", RegexOptions.IgnoreCase))
            plan.Ssdts.Add(new("SSDT-CPUR", Loc.T("plan.ssdt.cpur", board)));

        if (hw.Input.Any(i => i.Bus == InputBus.I2c))
        {
            plan.Ssdts.Add(new("SSDT-GPIO", Loc.T("plan.ssdt.gpio")));
            plan.Ssdts.Add(new("SSDT-XOSI", Loc.T("plan.ssdt.xosi")));
        }

        if (laptop && compat.Components.Any(c => c.Gpu?.Kind == GpuKind.Discrete && c.Cells[plan.Version.Id].Level < SupportLevel.Limited))
            plan.Ssdts.Add(new("SSDT-dGPU-Off", Loc.T("plan.ssdt.dgpu_off")));
    }

    private static void PlanGraphics(CompatibilityReport compat, EfiPlan plan, bool laptop, bool hasWorkingDgpu,
        Func<ComponentResult, bool> works)
    {
        plan.BootArgs.Insert(0, new("-v keepsyms=1 debug=0x100", Loc.T("plan.bootarg.debug")));

        foreach (var gpu in compat.Components.Where(c => c.Component == "gpu"))
        {
            if (!works(gpu))
            {
                if (!laptop && gpu.Gpu?.Kind == GpuKind.Discrete)
                    plan.BootArgs.Add(new("-wegnoegpu", Loc.T("plan.bootarg.wegnoegpu", gpu.Name)));
                continue;
            }

            foreach (var arg in gpu.Rule?.BootArgs ?? [])
                plan.BootArgs.Add(new(arg, Loc.T("plan.bootarg.for", gpu.Name)));

            if (gpu.Gpu is not { Kind: GpuKind.Integrated, PciPath.Length: > 0 } igpu || gpu.Rule is null)
                continue;

            string variant = laptop ? "laptop" : hasWorkingDgpu ? "desktopHeadless" : "desktop";
            if (!gpu.Rule.Properties.TryGetValue(variant, out var props) && !gpu.Rule.Properties.TryGetValue(laptop ? "laptop" : "desktop", out props))
                continue;
            foreach (var (key, value) in props)
                plan.DeviceProperties.Add(new(igpu.PciPath, key, value, gpu.Name));
        }
    }

    private static string Or(string path) => path.Length > 0 ? path : "?";
}
