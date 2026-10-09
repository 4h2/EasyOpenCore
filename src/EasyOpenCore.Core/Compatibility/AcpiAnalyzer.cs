using EasyOpenCore.Core.Models;

namespace EasyOpenCore.Core.Compatibility;

/// <summary>ACPI facts used to pick and customize SSDTs (paths as reported by Windows).</summary>
public sealed class AcpiInfo
{
    public string EcPath { get; init; } = "";
    public string CpuPath { get; init; } = "";
    public string PciRootPath { get; init; } = "";
    public string LpcPath { get; init; } = "";
    public bool HasAwac { get; init; }
    public bool HasRtc { get; init; }
    public bool HasHpet { get; init; }

    /// <summary>macOS only attaches AppleACPIEC to a device literally named "EC".</summary>
    public bool EcNamedEc => EcPath.EndsWith(".EC", StringComparison.OrdinalIgnoreCase);
}

public static class AcpiAnalyzer
{
    public static AcpiInfo Analyze(HardwareReport hw)
    {
        string First(string role) => hw.AcpiDevices
            .Where(a => a.Role == role && a.AcpiPath.Length > 0)
            .Select(a => a.AcpiPath)
            .OrderBy(p => p, StringComparer.Ordinal)
            .FirstOrDefault() ?? "";

        var ec = First("ec");
        var rtc = First("rtc");
        string lpcSource = ec.Length > 0 ? ec : rtc;

        return new AcpiInfo
        {
            EcPath = ec,
            CpuPath = First("cpu"),
            PciRootPath = First("pcie-root") is { Length: > 0 } pcie ? pcie : First("pci-root"),
            LpcPath = lpcSource.Contains('.') ? lpcSource[..lpcSource.LastIndexOf('.')] : "",
            HasAwac = hw.AcpiDevices.Any(a => a.Role == "awac"),
            HasRtc = rtc.Length > 0,
            HasHpet = hw.AcpiDevices.Any(a => a.Role == "hpet"),
        };
    }
}
