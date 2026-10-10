using EasyOpenCore.Core.Compatibility;
using EasyOpenCore.Core.Models;

namespace EasyOpenCore.Core.Tests;

public class CompatibilityEngineTests
{
    private static HardwareReport Desktop(string codename, CpuVendor vendor = CpuVendor.Intel, params GpuInfo[] gpus) => new()
    {
        System = new SystemInfo { Chassis = ChassisKind.Desktop, BoardProduct = "Z390 AORUS PRO" },
        Cpu = new CpuInfo { Name = "Test CPU", Vendor = vendor, Codename = codename, Features = ["SSE4.2", "AVX2"] },
        Gpus = [.. gpus],
        AcpiDevices =
        [
            new AcpiDevice { Role = "cpu", AcpiPath = @"\_SB.PR00" },
            new AcpiDevice { Role = "ec", AcpiPath = @"\_SB.PCI0.LPCB.EC0" },
            new AcpiDevice { Role = "awac", AcpiPath = @"\_SB.AWAC" },
        ],
    };

    private static GpuInfo Gpu(string vendor, string device, GpuKind kind, string path = "PciRoot(0x0)/Pci(0x2,0x0)") =>
        new() { Name = $"{vendor}:{device}", VendorId = vendor, DeviceId = device, Kind = kind, PciPath = path };

    [Fact]
    public void CoffeeLakeWithPolaris_IsFullySupportedThroughTahoe()
    {
        var hw = Desktop("Coffee Lake", CpuVendor.Intel,
            Gpu("8086", "3E92", GpuKind.Integrated),
            Gpu("1002", "67DF", GpuKind.Discrete, "PciRoot(0x0)/Pci(0x1,0x0)/Pci(0x0,0x0)"));

        var compat = CompatibilityEngine.Evaluate(hw);

        Assert.Equal("26", compat.RecommendedVersion);
        Assert.All(compat.Verdicts.Where(v => v.Version.Id != "10.13"), v => Assert.Equal(SupportLevel.Supported, v.Level));
    }

    [Fact]
    public void CoffeeLakePlan_UsesHeadlessIgpuAndNewerSmbiosForTahoe()
    {
        var hw = Desktop("Coffee Lake", CpuVendor.Intel,
            Gpu("8086", "3E92", GpuKind.Integrated),
            Gpu("1002", "67DF", GpuKind.Discrete));
        var compat = CompatibilityEngine.Evaluate(hw);

        var sequoia = EfiPlanner.Plan(hw, compat, "15");
        var tahoe = EfiPlanner.Plan(hw, compat, "26");

        Assert.Equal("iMac19,1", sequoia.Smbios);
        Assert.Equal("iMac20,1", tahoe.Smbios); // iMac19,1 stops at Sequoia
        Assert.Contains(sequoia.DeviceProperties, p => p.Key == "AAPL,ig-platform-id" && p.Value == "0300923E");
        Assert.Contains(sequoia.Ssdts, s => s.Name == "SSDT-AWAC");
        Assert.Contains(sequoia.Ssdts, s => s.Name == "SSDT-PMC"); // Z390
        Assert.Contains(tahoe.BootArgs, b => b.Name == "-lilubetaall");
    }

    [Fact]
    public void KextOrder_StartsWithLiluAndVirtualSmc()
    {
        var hw = Desktop("Coffee Lake", CpuVendor.Intel, Gpu("8086", "3E92", GpuKind.Integrated));
        var plan = EfiPlanner.Plan(hw, CompatibilityEngine.Evaluate(hw), "15");

        Assert.Equal("Lilu", plan.Kexts[0].Name);
        Assert.Equal("VirtualSMC", plan.Kexts[1].Name);
        Assert.Equal(plan.Kexts.Count, plan.Kexts.Select(k => k.Name).Distinct().Count());
    }

    [Fact]
    public void AlderLakeWithRtx_HasNoUsableGraphics()
    {
        var hw = Desktop("Alder Lake", CpuVendor.Intel,
            Gpu("8086", "4680", GpuKind.Integrated),
            Gpu("10DE", "2484", GpuKind.Discrete));

        var compat = CompatibilityEngine.Evaluate(hw);

        Assert.Null(compat.RecommendedVersion);
        Assert.All(compat.Verdicts, v => Assert.Equal(SupportLevel.Unsupported, v.Level));
    }

    [Fact]
    public void NvidiaKepler_IsMatchedByDeviceIdRange()
    {
        var hw = Desktop("Haswell", CpuVendor.Intel, Gpu("10DE", "1187", GpuKind.Discrete)); // GTX 760
        var gpu = CompatibilityEngine.Evaluate(hw).Components.Single(c => c.Component == "gpu");

        Assert.Equal("nvidia-kepler", gpu.Rule?.Id);
        Assert.Equal(SupportLevel.Supported, gpu.Cells["11"].Level);
        Assert.Equal(SupportLevel.Patched, gpu.Cells["12"].Level);
    }

    [Fact]
    public void LaptopCometLake_PicksMacBookProAndIgnoresDgpu()
    {
        var hw = Desktop("Comet Lake", CpuVendor.Intel,
            Gpu("8086", "9B41", GpuKind.Integrated),
            Gpu("10DE", "1F91", GpuKind.Discrete)); // GTX 1650 Mobile: unsupported
        hw.System.Chassis = ChassisKind.Laptop;
        hw.Cpu.Name = "Intel(R) Core(TM) i5-10210U";

        var compat = CompatibilityEngine.Evaluate(hw);
        var plan = EfiPlanner.Plan(hw, compat, "15");

        Assert.Equal(SupportLevel.Supported, compat.Verdicts.Single(v => v.Version.Id == "15").Level);
        Assert.Equal("MacBookPro16,3", plan.Smbios);
        Assert.Contains(plan.Ssdts, s => s.Name == "SSDT-dGPU-Off");
        Assert.Contains(plan.Kexts, k => k.Name == "SMCBatteryManager");
        Assert.Contains(plan.DeviceProperties, p => p.Key == "device-id" && p.Value == "9B3E0000");
    }

    [Fact]
    public void AmdApuLaptop_FollowsNootedRedPrerequisites()
    {
        var hw = Desktop("Zen 2", CpuVendor.Amd, Gpu("1002", "164C", GpuKind.Integrated));
        hw.System.Chassis = ChassisKind.Laptop;
        hw.Cpu.Name = "AMD Ryzen 5 5500U with Radeon Graphics";

        var plan = EfiPlanner.Plan(hw, CompatibilityEngine.Evaluate(hw), "15");

        Assert.Equal("MacBookPro16,2", plan.Smbios);
        Assert.Contains(plan.Kexts, k => k.Name == "NootedRed");
        Assert.DoesNotContain(plan.Kexts, k => k.Name == "WhateverGreen");
        Assert.Contains(plan.Kexts, k => k.Name == "ForgedInvariant");
        Assert.Contains(plan.Ssdts, s => s.Name == "SSDT-XOSI");
        Assert.Contains(plan.Warnings, w => w.Contains("512"));
    }

    [Fact]
    public void CpuWithoutAvx2_GetsCryptexFixupOnVentura()
    {
        var hw = Desktop("Ivy Bridge", CpuVendor.Intel, Gpu("1002", "67DF", GpuKind.Discrete));
        hw.Cpu.Features = ["SSE4.2", "AVX"];
        var compat = CompatibilityEngine.Evaluate(hw);

        Assert.Contains(EfiPlanner.Plan(hw, compat, "13").Kexts, k => k.Name == "CryptexFixup");
        Assert.DoesNotContain(EfiPlanner.Plan(hw, compat, "12").Kexts, k => k.Name == "CryptexFixup");
    }

    [Fact]
    public void Database_RangesReferenceKnownVersionsAndKexts()
    {
        var db = CompatDatabase.Instance;
        var ranges = db.Cpus.SelectMany(c => c.Ranges)
            .Concat(db.Gpus.SelectMany(g => g.Ranges))
            .Concat(db.Devices.SelectMany(d => d.Ranges));
        Assert.All(ranges, r =>
        {
            Assert.True(db.IndexOf(r.From) >= 0, $"unknown version {r.From}");
            Assert.True(db.IndexOf(r.To) >= 0, $"unknown version {r.To}");
        });

        var kexts = db.Cpus.SelectMany(c => c.Kexts).Concat(db.Gpus.SelectMany(g => g.Kexts)).Concat(db.Devices.SelectMany(d => d.Kexts));
        Assert.All(kexts, k => Assert.True(db.Kexts.ContainsKey(k.Name), $"kext {k.Name} missing from kexts.json"));

        var smbios = db.Cpus.SelectMany(c => c.Smbios.Desktop.Concat(c.Smbios.DesktopDgpu).Concat(c.Smbios.Laptop).Concat(c.Smbios.LaptopH));
        Assert.All(smbios, s => Assert.NotNull(db.Smbios(s)));
    }
}
