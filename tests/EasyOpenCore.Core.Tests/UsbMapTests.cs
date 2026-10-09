using EasyOpenCore.Core.Plist;
using EasyOpenCore.Core.Usb;

namespace EasyOpenCore.Core.Tests;

public class UsbMapTests
{
    /// <summary>Mirrors this laptop's XHC0: a Type-C pair (1/5), a USB 3 Type-A pair (2/6), internal and plain USB 2 ports.</summary>
    private static UsbControllerInfo Xhc0(bool deviceOnPort2 = false) => new()
    {
        InstanceId = @"PCI\VEN_1022&DEV_1639\4&1",
        Name = "AMD xHCI",
        IsXhci = true,
        AcpiPath = @"\_SB.PCI0.GP17.XHC0",
        Bdf = "4:0:3",
        VendorId = "1022",
        DeviceId = "1639",
        Ports =
        [
            new UsbPortInfo { Index = 1, Protocol = UsbPortProtocol.Usb2, TypeC = true, CompanionPort = 5, DeviceName = "Hub" },
            new UsbPortInfo { Index = 2, Protocol = UsbPortProtocol.Usb2, CompanionPort = 6, DeviceName = deviceOnPort2 ? "Stick" : null },
            new UsbPortInfo { Index = 3, Protocol = UsbPortProtocol.Usb2, UserConnectable = false, DeviceName = "Camera" },
            new UsbPortInfo { Index = 4, Protocol = UsbPortProtocol.Usb2 },
            new UsbPortInfo { Index = 5, Protocol = UsbPortProtocol.Usb3, TypeC = true, CompanionPort = 1 },
            new UsbPortInfo { Index = 6, Protocol = UsbPortProtocol.Usb3, CompanionPort = 2 },
        ],
    };

    [Fact]
    public void GuessesConnectorsLikeUsbToolBox()
    {
        var map = new UsbMap();
        map.Update([Xhc0()]);
        var c = map.Controllers[0];
        UsbConnector Of(int i) => UsbMap.EffectiveConnector(c.Ports.Single(p => p.Index == i), c);

        Assert.Equal(UsbConnector.TypeCWithSwitch, Of(1));
        Assert.Equal(UsbConnector.Usb3TypeA, Of(2));
        Assert.Equal(UsbConnector.Internal, Of(3));
        Assert.Equal(UsbConnector.TypeA, Of(4));
        Assert.Equal(UsbConnector.TypeCWithSwitch, Of(5));
        Assert.Equal(UsbConnector.Usb3TypeA, Of(6));
    }

    [Fact]
    public void DiscoveryMarksPortsSeenAndKeepsUserChoices()
    {
        var map = new UsbMap();
        map.Update([Xhc0()]);
        var c = map.Controllers[0];
        Assert.Equal([1, 3], c.Ports.Where(p => p.Selected).Select(p => p.Index));

        c.Ports.Single(p => p.Index == 4).Connector = UsbConnector.TypeA; // explicit user choice equal to enum default
        c.Ports.Single(p => p.Index == 1).Selected = false;               // user deselects
        Assert.True(map.Update([Xhc0(deviceOnPort2: true)]));

        Assert.True(c.Ports.Single(p => p.Index == 2).Selected);
        Assert.False(c.Ports.Single(p => p.Index == 1).Selected);         // already seen, user choice kept
        Assert.Equal(UsbConnector.TypeA, c.Ports.Single(p => p.Index == 4).Connector);
    }

    [Fact]
    public void BuildsUtbMapInUsbToolBoxFormat()
    {
        var map = new UsbMap();
        map.Update([Xhc0(deviceOnPort2: true)]);
        map.Controllers[0].Ports.Single(p => p.Index == 6).Selected = true;

        var personalities = map.BuildInfoPlist()["IOKitPersonalities"].AsDict;
        var xhc0 = personalities["XHC0"].AsDict;

        Assert.Equal("XHC0", xhc0.GetString("IONameMatch"));
        Assert.Equal("USBToolBox", xhc0.GetString("IOClass"));
        var merge = xhc0["IOProviderMergeProperties"].AsDict;
        var ports = merge["ports"].AsDict;
        Assert.Equal(["HS01", "HS02", "HS03", "SS01"], ports.Keys);
        Assert.Equal(new byte[] { 6, 0, 0, 0 }, ((PData)ports["SS01"].AsDict["port"]).Value);
        Assert.Equal(255, ((PInteger)ports["HS03"].AsDict["UsbConnector"]).Value);
        Assert.Equal(new byte[] { 6, 0, 0, 0 }, ((PData)merge["port-count"]).Value);
    }

    [Fact]
    public void FallsBackToPcidebugWhenAcpiNamesRepeat()
    {
        var a = Xhc0();
        var b = Xhc0();
        var map = new UsbMap();
        map.Update([a, new UsbControllerInfo { InstanceId = "other", IsXhci = true, AcpiPath = @"\_SB.PCI1.XHC0", Bdf = "5:0:0", Ports = b.Ports }]);

        var personalities = map.BuildInfoPlist()["IOKitPersonalities"].AsDict;
        Assert.Equal(["4:0:3", "5:0:0"], personalities.Keys);
        Assert.Equal("4:0:3", personalities["4:0:3"].AsDict["IOPropertyMatch"].AsDict.GetString("pcidebug"));
    }
}
