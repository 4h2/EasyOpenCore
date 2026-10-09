using EasyOpenCore.Core.Models;

namespace EasyOpenCore.Core.Hardware;

public static class StorageDetector
{
    private const string StorageScope = @"root\Microsoft\Windows\Storage";

    public static List<StorageDisk> Detect()
    {
        var disks = new Dictionary<string, StorageDisk>();

        foreach (var pd in Wmi.Query("SELECT DeviceId, FriendlyName, BusType, MediaType, Size, FirmwareVersion FROM MSFT_PhysicalDisk", StorageScope))
        {
            disks[Wmi.Str(pd, "DeviceId")] = new StorageDisk
            {
                Model = Wmi.Str(pd, "FriendlyName"),
                Bus = Wmi.Int(pd, "BusType") switch
                {
                    1 => "SCSI", 3 => "ATA", 6 => "Fibre", 7 => "USB", 8 => "RAID", 10 => "SAS", 11 => "SATA",
                    12 => "SD", 13 => "MMC", 15 => "File", 16 => "Storage Spaces", 17 => "NVMe", _ => "Unknown",
                },
                Media = Wmi.Int(pd, "MediaType") switch { 3 => "HDD", 4 => "SSD", 5 => "SCM", _ => "" },
                SizeBytes = Wmi.ULong(pd, "Size"),
                Firmware = Wmi.Str(pd, "FirmwareVersion"),
            };
        }

        foreach (var d in Wmi.Query("SELECT Number, PartitionStyle, IsSystem, IsBoot FROM MSFT_Disk", StorageScope))
        {
            if (!disks.TryGetValue(Wmi.Str(d, "Number"), out var disk))
                continue;
            disk.PartitionStyle = Wmi.Int(d, "PartitionStyle") switch { 1 => "MBR", 2 => "GPT", _ => "RAW" };
            disk.IsSystemDisk = Wmi.Get(d, "IsSystem") is true || Wmi.Get(d, "IsBoot") is true;
        }

        return disks.Values.ToList();
    }
}
