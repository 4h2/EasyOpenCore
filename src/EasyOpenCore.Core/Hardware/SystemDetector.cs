using EasyOpenCore.Core.Hardware.Native;
using EasyOpenCore.Core.Models;
using Microsoft.Win32;

namespace EasyOpenCore.Core.Hardware;

public static class SystemDetector
{
    public static SystemInfo Detect()
    {
        var info = new SystemInfo();

        foreach (var cs in Wmi.Query("SELECT Manufacturer, Model, TotalPhysicalMemory, PCSystemType FROM Win32_ComputerSystem"))
        {
            info.Manufacturer = Wmi.Str(cs, "Manufacturer");
            info.Model = Wmi.Str(cs, "Model");
            info.TotalMemoryBytes = Wmi.ULong(cs, "TotalPhysicalMemory");
            if (Wmi.Int(cs, "PCSystemType") == 2)
                info.Chassis = ChassisKind.Laptop;
        }

        foreach (var enc in Wmi.Query("SELECT ChassisTypes FROM Win32_SystemEnclosure"))
        {
            if (Wmi.Get(enc, "ChassisTypes") is ushort[] { Length: > 0 } types && info.Chassis == ChassisKind.Unknown)
                info.Chassis = MapChassis(types[0]);
        }

        foreach (var bb in Wmi.Query("SELECT Manufacturer, Product FROM Win32_BaseBoard"))
        {
            info.BoardManufacturer = Wmi.Str(bb, "Manufacturer");
            info.BoardProduct = Wmi.Str(bb, "Product");
        }

        foreach (var bios in Wmi.Query("SELECT Manufacturer, SMBIOSBIOSVersion, ReleaseDate FROM Win32_BIOS"))
        {
            info.BiosVendor = Wmi.Str(bios, "Manufacturer");
            info.BiosVersion = Wmi.Str(bios, "SMBIOSBIOSVersion");
            var date = Wmi.Str(bios, "ReleaseDate");
            info.BiosDate = date.Length >= 8 ? $"{date[..4]}-{date[4..6]}-{date[6..8]}" : date;
        }

        foreach (var os in Wmi.Query("SELECT Caption, BuildNumber FROM Win32_OperatingSystem"))
        {
            info.OsName = Wmi.Str(os, "Caption");
            info.OsBuild = Wmi.Str(os, "BuildNumber");
        }

        if (Kernel32.GetFirmwareType(out var fw))
            info.Firmware = fw switch { 1 => FirmwareKind.Bios, 2 => FirmwareKind.Uefi, _ => FirmwareKind.Unknown };

        if (Registry.GetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\SecureBoot\State",
                "UEFISecureBootEnabled", null) is int sb)
            info.SecureBootEnabled = sb == 1;

        return info;
    }

    public static List<MemoryModule> DetectMemory()
    {
        var list = new List<MemoryModule>();
        foreach (var m in Wmi.Query("SELECT DeviceLocator, Capacity, Speed, ConfiguredClockSpeed, SMBIOSMemoryType, Manufacturer, PartNumber FROM Win32_PhysicalMemory"))
        {
            var configured = (uint)Wmi.Int(m, "ConfiguredClockSpeed");
            list.Add(new MemoryModule
            {
                Slot = Wmi.Str(m, "DeviceLocator"),
                CapacityBytes = Wmi.ULong(m, "Capacity"),
                SpeedMts = configured > 0 ? configured : (uint)Wmi.Int(m, "Speed"),
                Type = Wmi.Int(m, "SMBIOSMemoryType") switch
                {
                    20 => "DDR", 21 => "DDR2", 24 => "DDR3", 26 => "DDR4", 27 => "LPDDR", 28 => "LPDDR2",
                    29 => "LPDDR3", 30 => "LPDDR4", 34 => "DDR5", 35 => "LPDDR5", _ => "Unknown",
                },
                Manufacturer = Wmi.Str(m, "Manufacturer"),
                PartNumber = Wmi.Str(m, "PartNumber"),
            });
        }
        return list;
    }

    // SMBIOS values (DMTF DSP0134, "System Enclosure or Chassis Types" table).
    private static ChassisKind MapChassis(ushort type) => type switch
    {
        3 or 4 or 5 or 6 or 7 or 15 or 16 or 24 => ChassisKind.Desktop,
        8 or 9 or 10 or 11 or 12 or 14 or 18 or 21 or 30 or 31 or 32 => ChassisKind.Laptop,
        13 => ChassisKind.AllInOne,
        35 or 36 => ChassisKind.MiniPc,
        17 or 23 or 28 => ChassisKind.Server,
        _ => ChassisKind.Unknown,
    };
}
