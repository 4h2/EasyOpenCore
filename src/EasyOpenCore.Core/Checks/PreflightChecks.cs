using EasyOpenCore.Core.Hardware;
using EasyOpenCore.Core.Localization;
using EasyOpenCore.Core.Models;

namespace EasyOpenCore.Core.Checks;

public enum Severity { Info, Warning, Blocker }

/// <summary>A check result. <see cref="Area"/> is a category id; texts are already localized.</summary>
public sealed record Finding(Severity Severity, string Area, string Title, string Detail);

/// <summary>
/// Firmware/hardware prerequisites that do not depend on the macOS version.
/// Per-version rules live in the compatibility engine.
/// </summary>
public static class PreflightChecks
{
    public static List<Finding> Run(HardwareReport r)
    {
        var f = new List<Finding>();

        void Add(Severity severity, string area, string key, params object?[] args) =>
            f.Add(new(severity, area, Loc.T($"check.{key}.title", args), Loc.T($"check.{key}.detail", args)));

        if (r.Cpu.Vendor == CpuVendor.Unknown)
            Add(Severity.Blocker, "cpu", "cpu_vendor");
        if (!r.Cpu.Has("SSE4.2"))
            Add(Severity.Blocker, "cpu", "no_sse42");
        else if (!r.Cpu.Has("AVX2"))
            Add(Severity.Warning, "cpu", "no_avx2");
        if (r.Cpu.IsHybrid)
            Add(Severity.Info, "cpu", "hybrid");
        if (r.Cpu.Vendor == CpuVendor.Amd && r.System.Chassis == ChassisKind.Laptop)
            Add(Severity.Warning, "cpu", "amd_laptop");

        if (r.System.Firmware == FirmwareKind.Bios)
            Add(Severity.Warning, "firmware", "legacy_bios");
        if (r.System.SecureBootEnabled == true)
            Add(Severity.Warning, "firmware", "secure_boot");

        foreach (var c in r.StorageControllers.Where(c => c.ClassCode.StartsWith("0104")))
            Add(Severity.Blocker, DeviceCategory.Storage, "raid", c.Name);
        if (r.AllDevices.Any(d => d.Service.Equals("iaStorVD", StringComparison.OrdinalIgnoreCase)))
            Add(Severity.Blocker, DeviceCategory.Storage, "vmd");
        foreach (var d in r.Storage.Where(d => d.IsSystemDisk && d.PartitionStyle == "MBR"))
            Add(Severity.Warning, DeviceCategory.Storage, "mbr", d.Model);

        if (r.AllDevices.Any(d => d.Enumerator == "INTELAUDIO"))
            Add(Severity.Warning, DeviceCategory.Audio, "intel_sst");

        if (r.Input.Any(i => i.Bus == InputBus.I2c))
            Add(Severity.Info, DeviceCategory.Input, "i2c_touchpad");

        if (r.ScanErrors.Count > 0)
            Add(Severity.Info, "scanner", "scan_errors", string.Join("; ", r.ScanErrors));

        return f;
    }
}
