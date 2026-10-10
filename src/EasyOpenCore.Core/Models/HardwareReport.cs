namespace EasyOpenCore.Core.Models;

/// <summary>Full hardware snapshot; input for the compatibility engine and EFI generation.</summary>
public sealed class HardwareReport
{
    public DateTime GeneratedAt { get; set; } = DateTime.Now;
    public string ToolVersion { get; set; } = "";
    public SystemInfo System { get; set; } = new();
    public CpuInfo Cpu { get; set; } = new();
    public List<GpuInfo> Gpus { get; set; } = [];
    public List<MemoryModule> Memory { get; set; } = [];
    public List<StorageDisk> Storage { get; set; } = [];
    public List<PciController> StorageControllers { get; set; } = [];
    public List<AudioCodec> AudioCodecs { get; set; } = [];
    public List<PciController> AudioControllers { get; set; } = [];
    public List<NetworkAdapter> Network { get; set; } = [];
    public List<PciController> UsbControllers { get; set; } = [];
    public List<InputDevice> Input { get; set; } = [];
    public List<AcpiDevice> AcpiDevices { get; set; } = [];
    public List<AcpiTable> AcpiTables { get; set; } = [];
    public List<PnpDevice> AllDevices { get; set; } = [];
    public List<string> ScanErrors { get; set; } = [];
}

public enum ChassisKind { Unknown, Desktop, Laptop, AllInOne, MiniPc, Server }

public enum FirmwareKind { Unknown, Bios, Uefi }

public sealed class SystemInfo
{
    public string Manufacturer { get; set; } = "";
    public string Model { get; set; } = "";
    /// <summary>Marketing name (Win32_ComputerSystemProduct.Version), e.g. "ThinkPad E14 Gen 3"; Lenovo's Model is only the machine type.</summary>
    public string Family { get; set; } = "";
    public ChassisKind Chassis { get; set; }
    public string BoardManufacturer { get; set; } = "";
    public string BoardProduct { get; set; } = "";
    public string BiosVendor { get; set; } = "";
    public string BiosVersion { get; set; } = "";
    public string BiosDate { get; set; } = "";
    public FirmwareKind Firmware { get; set; }
    public bool? SecureBootEnabled { get; set; }
    public string OsName { get; set; } = "";
    public string OsBuild { get; set; } = "";
    public ulong TotalMemoryBytes { get; set; }

    /// <summary>The guide warns that ThinkPads can be semi-bricked by an NVRAM reset from OpenCore.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsThinkPad => $"{Family} {Model}".Contains("ThinkPad", StringComparison.OrdinalIgnoreCase);
}

public enum CpuVendor { Unknown, Intel, Amd }

public sealed class CpuInfo
{
    public string Name { get; set; } = "";
    public CpuVendor Vendor { get; set; }
    public string VendorString { get; set; } = "";
    public int Family { get; set; }
    public int Model { get; set; }
    public int Stepping { get; set; }
    /// <summary>Microarchitecture (e.g. "Coffee Lake", "Zen 3").</summary>
    public string Codename { get; set; } = "";
    public bool IsMobile { get; set; }
    public int Cores { get; set; }
    public int Threads { get; set; }
    public bool IsHybrid { get; set; }
    public List<string> Features { get; set; } = [];
    public bool Has(string feature) => Features.Contains(feature, StringComparer.OrdinalIgnoreCase);
}

public enum GpuKind { Unknown, Integrated, Discrete }

public sealed class GpuInfo
{
    public string Name { get; set; } = "";
    public string VendorId { get; set; } = "";
    public string DeviceId { get; set; } = "";
    public string SubsystemId { get; set; } = "";
    public string Revision { get; set; } = "";
    public GpuKind Kind { get; set; }
    public string PciPath { get; set; } = "";
    public string AcpiPath { get; set; } = "";
    public ulong VramBytes { get; set; }
    public string DriverVersion { get; set; } = "";
}

public sealed class MemoryModule
{
    public string Slot { get; set; } = "";
    public ulong CapacityBytes { get; set; }
    public uint SpeedMts { get; set; }
    public string Type { get; set; } = "";
    public string Manufacturer { get; set; } = "";
    public string PartNumber { get; set; } = "";
}

public sealed class StorageDisk
{
    public string Model { get; set; } = "";
    public string Bus { get; set; } = "";
    public string Media { get; set; } = "";
    public ulong SizeBytes { get; set; }
    public string PartitionStyle { get; set; } = "";
    public bool IsSystemDisk { get; set; }
    public string Firmware { get; set; } = "";
}

/// <summary>Generic PCI controller (SATA/NVMe/USB/HDA...).</summary>
public sealed class PciController
{
    public string Name { get; set; } = "";
    public string VendorId { get; set; } = "";
    public string DeviceId { get; set; } = "";
    public string ClassCode { get; set; } = "";
    public string PciPath { get; set; } = "";
    public string AcpiPath { get; set; } = "";
    public string Service { get; set; } = "";
}

public sealed class AudioCodec
{
    public string Name { get; set; } = "";
    public string VendorId { get; set; } = "";
    public string DeviceId { get; set; } = "";
    public string Subsystem { get; set; } = "";
    /// <summary>GPU HDMI/DP codec (not the main analog codec).</summary>
    public bool IsHdmi { get; set; }
    /// <summary>Marketing name (e.g. ALC897) when known.</summary>
    public string CodecName { get; set; } = "";
}

public enum NetworkKind { Unknown, Ethernet, WiFi, Bluetooth }

public sealed class NetworkAdapter
{
    public string Name { get; set; } = "";
    public NetworkKind Kind { get; set; }
    public string Bus { get; set; } = "";
    public string VendorId { get; set; } = "";
    public string DeviceId { get; set; } = "";
    public string PciPath { get; set; } = "";
}

public enum InputBus { Unknown, Ps2, I2c, Usb, Smbus }

public sealed class InputDevice
{
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    public InputBus Bus { get; set; }
    public string HardwareId { get; set; } = "";
    public string AcpiPath { get; set; } = "";
}

public sealed class AcpiDevice
{
    public string Name { get; set; } = "";
    public string HardwareId { get; set; } = "";
    /// <summary>Role id relevant for hackintosh (ec, rtc, awac, hpet, cpu...), or empty.</summary>
    public string Role { get; set; } = "";
    public string AcpiPath { get; set; } = "";
}

public sealed class AcpiTable
{
    public string Signature { get; set; } = "";
    public string OemId { get; set; } = "";
    public string OemTableId { get; set; } = "";
    public int Length { get; set; }
    /// <summary>Raw table (.aml). Not serialized to JSON.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public byte[] Data { get; set; } = [];
}

public sealed class PnpDevice
{
    public string InstanceId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Class { get; set; } = "";
    public string Manufacturer { get; set; } = "";
    public string Service { get; set; } = "";
    public string DriverVersion { get; set; } = "";
    /// <summary>Driver key under Control\Class ("{guid}\0000").</summary>
    public string DriverKey { get; set; } = "";
    public List<string> HardwareIds { get; set; } = [];
    public List<string> CompatibleIds { get; set; } = [];
    public string PciPath { get; set; } = "";
    public string AcpiPath { get; set; } = "";
    public string Category { get; set; } = "";

    public string Enumerator => InstanceId.Split('\\', 2)[0].ToUpperInvariant();
}
