using EasyOpenCore.Core.Hardware;

namespace EasyOpenCore.Core.Tests;

public class DevicePathsTests
{
    [Theory]
    [InlineData("PCIROOT(0)#PCI(0200)", "PciRoot(0x0)/Pci(0x2,0x0)")]
    [InlineData("PCIROOT(0)#PCI(1C04)#PCI(0000)", "PciRoot(0x0)/Pci(0x1C,0x4)/Pci(0x0,0x0)")]
    [InlineData("PCIROOT(0)#PCI(0801)#PCI(0003)", "PciRoot(0x0)/Pci(0x8,0x1)/Pci(0x0,0x3)")]
    [InlineData("PCIROOT(0)#PCI(1400)#USBROOT(0)#USB(1)", "")]
    public void ConvertsWindowsLocationToOpenCorePath(string windows, string expected) =>
        Assert.Equal(expected, DevicePaths.ToOpenCorePciPath(windows));

    [Theory]
    [InlineData("ACPI(_SB_)#ACPI(PCI0)#ACPI(GFX0)", @"\_SB.PCI0.GFX0")]
    [InlineData("ACPI(_SB_)#ACPI(PCI0)#ACPI(LPCB)#ACPI(EC__)", @"\_SB.PCI0.LPCB.EC")]
    public void ConvertsWindowsLocationToAcpiPath(string windows, string expected) =>
        Assert.Equal(expected, DevicePaths.ToAcpiPath(windows));

    [Fact]
    public void ParsesPciAndClassIds()
    {
        string[] hw = [@"PCI\VEN_8086&DEV_3E92&SUBSYS_86941043&REV_02"];
        string[] compat = [@"PCI\VEN_8086&CC_030000", @"PCI\VEN_8086&CC_0300"];

        Assert.Equal("8086", HardwareIds.Vendor(hw));
        Assert.Equal("3E92", HardwareIds.Device(hw));
        Assert.Equal("86941043", HardwareIds.Subsystem(hw));
        Assert.Equal("030000", HardwareIds.ClassCode(compat));
    }
}
