using EasyOpenCore.Core.Acpi;
using EasyOpenCore.Core.Compatibility;
using EasyOpenCore.Core.Models;

namespace EasyOpenCore.Core.Tests;

public class AmlTests
{
    [Fact]
    public void EncodesEisaIdLikeIasl() // EisaId ("PNP0C09") == 0x090CD041
    {
        var o = new List<byte>();
        new AmlEisaId("PNP0C09").Emit(o);
        Assert.Equal(new byte[] { 0x0C, 0x41, 0xD0, 0x0C, 0x09 }, o);
    }

    [Fact]
    public void EncodesMultiSegmentRootPath()
    {
        var bytes = AmlEncoding.NameString(@"\_SB.PCI0.LPCB.EC0");
        Assert.Equal("\\/\u0004_SB_PCI0LPCBEC0_", System.Text.Encoding.ASCII.GetString(bytes));
    }

    [Theory]
    [InlineData(10, new byte[] { 11 })]
    [InlineData(62, new byte[] { 63 })]
    [InlineData(63, new byte[] { 0x41, 0x04 })]          // 63 + 2 = 65 = 0x041
    [InlineData(5000, new byte[] { 0x8B, 0x38, 0x01 })]   // 5000 + 3 = 5003 = 0x0138B
    public void EncodesPkgLength(int bodyLength, byte[] expectedPrefix)
    {
        var encoded = AmlEncoding.WithPkgLength(new List<byte>(new byte[bodyLength]));
        Assert.Equal(expectedPrefix, encoded[..expectedPrefix.Length]);
        Assert.Equal(bodyLength + expectedPrefix.Length, encoded.Length);
    }

    [Fact]
    public void TableChecksumAndLengthAreValid()
    {
        var aml = new AmlTable("Test", new AmlScope(@"\_SB", new AmlName("TEST", new AmlInt(0x1234)))).Build();
        Assert.Equal("SSDT", System.Text.Encoding.ASCII.GetString(aml, 0, 4));
        Assert.Equal(aml.Length, BitConverter.ToInt32(aml, 4));
        Assert.Equal(0, aml.Aggregate(0, (sum, b) => (sum + b) & 0xFF));
    }

    /// <summary>
    /// Generates every SSDT the factory knows for a synthetic laptop/desktop and writes them to
    /// %TEMP%\eoc-aml so they can be checked with "iasl -d" (done when changing the encoder).
    /// </summary>
    [Fact]
    public void GeneratesAllTables()
    {
        var hw = new HardwareReport
        {
            System = new SystemInfo { Chassis = ChassisKind.Laptop },
            Cpu = new CpuInfo { Vendor = CpuVendor.Intel, Codename = "Coffee Lake", Threads = 8, Features = ["SSE4.2", "AVX2"] },
            Gpus =
            [
                new GpuInfo { VendorId = "8086", DeviceId = "3EA0", Kind = GpuKind.Integrated, PciPath = "PciRoot(0x0)/Pci(0x2,0x0)", AcpiPath = @"\_SB.PCI0.GFX0" },
                new GpuInfo { VendorId = "10DE", DeviceId = "1F91", Kind = GpuKind.Discrete, AcpiPath = @"\_SB.PCI0.PEG0.PEGP" },
            ],
            UsbControllers = [new PciController { ClassCode = "0C0330", AcpiPath = @"\_SB.PCI0.XHC" }],
            AcpiDevices =
            [
                new AcpiDevice { Role = "cpu", AcpiPath = @"\_PR.CPU0" },
                new AcpiDevice { Role = "ec", AcpiPath = @"\_SB.PCI0.LPCB.H_EC" },
                new AcpiDevice { Role = "rtc", AcpiPath = @"\_SB.PCI0.LPCB.RTC" },
            ],
        };
        var compat = CompatibilityEngine.Evaluate(hw);
        var plan = new EfiPlan();
        foreach (var name in new[] { "SSDT-PLUG", "SSDT-PLUG-ALT", "SSDT-CPUR", "SSDT-EC-USBX", "SSDT-AWAC", "SSDT-PNLF", "SSDT-RHUB", "SSDT-PMC", "SSDT-XOSI", "SSDT-dGPU-Off", "SSDT-GPIO" })
            plan.Ssdts.Add(new PlanItem(name, ""));

        var result = SsdtFactory.Build(hw, compat, plan);

        Assert.Equal(10, result.Tables.Count);
        Assert.Contains(result.Manual, m => m.Name == "SSDT-GPIO");
        Assert.Contains(result.Tables, t => t.Name == "SSDT-RTC0"); // no STAS in the (empty) DSDT
        Assert.Single(result.Patches);

        var dir = Path.Combine(Path.GetTempPath(), "eoc-aml");
        Directory.CreateDirectory(dir);
        foreach (var t in result.Tables)
        {
            Assert.Equal(0, t.Aml.Aggregate(0, (sum, b) => (sum + b) & 0xFF));
            File.WriteAllBytes(Path.Combine(dir, t.Name + ".aml"), t.Aml);
            File.WriteAllText(Path.Combine(dir, t.Name + ".generated.dsl"), t.Asl);
        }
    }
}
