using EasyOpenCore.Core.Models;

namespace EasyOpenCore.Core.Compatibility;

public sealed record CompatCell(SupportLevel Level, string? Note);

/// <summary>One hardware component evaluated against every macOS version.</summary>
public sealed class ComponentResult
{
    /// <summary>Component id: cpu, gpu, audio, ethernet, wifi, bluetooth, nvme, sata, input-ps2...</summary>
    public string Component { get; init; } = "";
    public string Name { get; init; } = "";
    public string Detail { get; init; } = "";
    /// <summary>CPU and GPU decide whether a version is usable at all.</summary>
    public bool Critical { get; init; }
    public CpuRule? CpuRule { get; init; }
    public DeviceRule? Rule { get; init; }
    public GpuInfo? Gpu { get; init; }
    public Dictionary<string, CompatCell> Cells { get; } = [];
}

public sealed class VersionVerdict
{
    public MacOsVersion Version { get; init; } = new();
    public SupportLevel Level { get; init; }
    /// <summary>Non-critical components that will not work on this version.</summary>
    public List<ComponentResult> NotWorking { get; init; } = [];
}

public sealed class CompatibilityReport
{
    public List<MacOsVersion> Versions { get; init; } = [];
    public List<ComponentResult> Components { get; init; } = [];
    public List<VersionVerdict> Verdicts { get; init; } = [];
    public string? RecommendedVersion { get; init; }
    public AcpiInfo Acpi { get; init; } = new();
    public bool IsLaptop { get; init; }
}

public static class CompatibilityEngine
{
    public static CompatibilityReport Evaluate(HardwareReport hw, CompatDatabase? db = null)
    {
        db ??= CompatDatabase.Instance;
        bool laptop = hw.System.Chassis == ChassisKind.Laptop;
        var components = new List<ComponentResult>();

        // CPU
        var cpuRule = db.Cpus.FirstOrDefault(c => c.Vendor == hw.Cpu.Vendor && c.Codenames.Contains(hw.Cpu.Codename));
        var cpu = new ComponentResult
        {
            Component = "cpu", Name = hw.Cpu.Name, Detail = hw.Cpu.Codename, Critical = true, CpuRule = cpuRule,
        };
        foreach (var v in db.Versions)
            cpu.Cells[v.Id] = EvaluateCpu(db, hw.Cpu, cpuRule, v.Id);
        components.Add(cpu);

        // GPUs
        foreach (var gpu in hw.Gpus)
        {
            var rule = db.Gpus.FirstOrDefault(r => r.Matches("gpu", gpu.VendorId, gpu.DeviceId, gpu.Name, gpu.Kind));
            components.Add(Build(db, "gpu", gpu.Name, $"{gpu.VendorId}:{gpu.DeviceId}", rule, critical: true, gpu));
        }

        // Audio: analog codecs only (HDMI codecs follow the GPU)
        foreach (var codec in hw.AudioCodecs.Where(c => !c.IsHdmi))
        {
            var rule = Match(db, "audio", codec.VendorId, codec.DeviceId, codec.Name);
            components.Add(Build(db, "audio", string.IsNullOrEmpty(codec.CodecName) ? codec.Name : codec.CodecName,
                $"{codec.VendorId}:{codec.DeviceId}", rule));
        }

        // Network: internal PCIe NICs + USB Bluetooth (USB Ethernet dongles are skipped)
        foreach (var nic in hw.Network.Where(n => n.Bus == "PCIe" || n.Kind == NetworkKind.Bluetooth)
                     .DistinctBy(n => (n.Kind, n.VendorId, n.DeviceId)))
        {
            string component = nic.Kind switch
            {
                NetworkKind.WiFi => "wifi",
                NetworkKind.Bluetooth => "bluetooth",
                _ => "ethernet",
            };
            var rule = Match(db, component, nic.VendorId, nic.DeviceId, nic.Name);
            components.Add(Build(db, component, nic.Name, $"{nic.VendorId}:{nic.DeviceId}", rule));
        }

        // Storage
        foreach (var disk in hw.Storage.Where(d => d.Bus is "NVMe" or "SATA" or "ATA"))
        {
            string component = disk.Bus == "NVMe" ? "nvme" : "sata";
            var rule = Match(db, component, "", "", disk.Model);
            components.Add(Build(db, component, disk.Model, $"{disk.Bus} · {disk.SizeBytes / 1_000_000_000} GB", rule));
        }

        // Input
        foreach (var bus in hw.Input.Select(i => i.Bus).Distinct())
        {
            string? component = bus switch
            {
                InputBus.Ps2 => "input-ps2",
                InputBus.I2c => "input-i2c",
                InputBus.Smbus => "input-smbus",
                _ => null,
            };
            if (component is null)
                continue;
            var devices = hw.Input.Where(i => i.Bus == bus).Select(i => i.Name).Distinct();
            var rule = Match(db, component, "", "", "");
            components.Add(Build(db, component, string.Join(" / ", devices), rule?.Name ?? "", rule));
        }

        var verdicts = db.Versions.Select(v => Verdict(v, components, laptop)).ToList();

        return new CompatibilityReport
        {
            Versions = db.Versions,
            Components = components,
            Verdicts = verdicts,
            RecommendedVersion = Recommend(verdicts),
            Acpi = AcpiAnalyzer.Analyze(hw),
            IsLaptop = laptop,
        };
    }

    private static CompatCell EvaluateCpu(CompatDatabase db, CpuInfo cpu, CpuRule? rule, string version)
    {
        if (rule is not null)
        {
            var (level, note) = db.Evaluate(rule.Ranges, version);
            return new CompatCell(level, note);
        }

        // Unknown microarchitecture: fall back to instruction-set requirements.
        int idx = db.IndexOf(version);
        if (cpu.Vendor == CpuVendor.Unknown)
            return new(SupportLevel.Unsupported, "@note.cpu_vendor");
        if (!cpu.Has("SSE4.2") && idx >= db.IndexOf("10.14"))
            return new(SupportLevel.Unsupported, "@note.no_sse42");
        if (!cpu.Has("AVX2") && idx >= db.IndexOf("13"))
            return new(SupportLevel.Patched, "@note.cryptexfixup");
        return new(SupportLevel.Unknown, "@note.unknown_cpu");
    }

    private static DeviceRule? Match(CompatDatabase db, string component, string vendor, string device, string name) =>
        db.Devices.FirstOrDefault(r => r.Matches(component, vendor, device, name));

    private static ComponentResult Build(CompatDatabase db, string component, string name, string detail,
        DeviceRule? rule, bool critical = false, GpuInfo? gpu = null)
    {
        var result = new ComponentResult
        {
            Component = component, Name = name, Detail = detail, Critical = critical, Rule = rule, Gpu = gpu,
        };
        foreach (var v in db.Versions)
        {
            if (rule is null)
            {
                result.Cells[v.Id] = new CompatCell(SupportLevel.Unknown, "@note.no_rule");
                continue;
            }
            var (level, note) = db.Evaluate(rule.Ranges, v.Id);
            result.Cells[v.Id] = new CompatCell(level, note);
        }
        return result;
    }

    private static VersionVerdict Verdict(MacOsVersion v, List<ComponentResult> components, bool laptop)
    {
        var cpu = components.First(c => c.Component == "cpu").Cells[v.Id].Level;

        // On laptops the internal display is wired to the iGPU; a dGPU rarely helps.
        var gpus = components.Where(c => c.Component == "gpu").ToList();
        if (laptop && gpus.Any(g => g.Gpu?.Kind == GpuKind.Integrated))
            gpus = gpus.Where(g => g.Gpu?.Kind == GpuKind.Integrated).ToList();
        var gpu = gpus.Count == 0 ? SupportLevel.Unsupported : gpus.Max(g => g.Cells[v.Id].Level);

        var level = cpu < gpu ? cpu : gpu;

        var notWorking = components
            .Where(c => !c.Critical && c.Cells[v.Id].Level <= SupportLevel.Unknown)
            .ToList();

        // Every internal disk is a known-bad NVMe: still installable on another drive, but flag it.
        var disks = components.Where(c => c.Component is "nvme" or "sata").ToList();
        if (disks.Count > 0 && disks.All(d => d.Cells[v.Id].Level == SupportLevel.Unsupported) && level > SupportLevel.Limited)
            level = SupportLevel.Limited;

        return new VersionVerdict { Version = v, Level = level, NotWorking = notWorking };
    }

    /// <summary>Best overall level first, then fewest broken components, then the newest version.</summary>
    private static string? Recommend(List<VersionVerdict> verdicts)
    {
        var usable = verdicts.Where(v => v.Level >= SupportLevel.Limited).ToList();
        if (usable.Count == 0)
            return null;
        var best = usable.Max(v => v.Level);
        return usable.Where(v => v.Level == best)
            .OrderBy(v => v.NotWorking.Count)
            .ThenByDescending(v => verdicts.IndexOf(v))
            .First().Version.Id;
    }
}
