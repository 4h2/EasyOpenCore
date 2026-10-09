using System.Diagnostics;
using EasyOpenCore.Core.Hardware;

namespace EasyOpenCore.Core.Usb;

public sealed record UsbVolume(char DriveLetter, string FileSystem, string Label, ulong SizeBytes);

public sealed record UsbDisk(int Number, string Model, ulong SizeBytes, IReadOnlyList<UsbVolume> Volumes)
{
    public string Display => $"{Model} — {SizeBytes / 1_000_000_000.0:0.#} GB"
                             + (Volumes.Count > 0 ? $" ({string.Join(", ", Volumes.Select(v => $"{v.DriveLetter}: {v.FileSystem}"))})" : "");
}

/// <summary>
/// Prepares an OpenCore USB installer on Windows: optional format (FAT32, GPT), then copies
/// EFI/ and the recovery image into com.apple.recovery.boot/ (Dortania "Making the installer in Windows").
/// </summary>
public static class UsbDriveWriter
{
    private const string StorageScope = @"root\Microsoft\Windows\Storage";
    public const string RecoveryFolder = "com.apple.recovery.boot";

    /// <summary>USB disks only, never the disk Windows boots from.</summary>
    public static List<UsbDisk> ListUsbDisks()
    {
        var disks = new List<UsbDisk>();
        foreach (var d in Wmi.Query("SELECT Number, FriendlyName, Size, BusType, IsBoot, IsSystem FROM MSFT_Disk", StorageScope))
        {
            if (Wmi.Int(d, "BusType") != 7 || Wmi.ULong(d, "Size") == 0 || Wmi.Get(d, "IsBoot") is true || Wmi.Get(d, "IsSystem") is true)
                continue;
            int number = Wmi.Int(d, "Number");
            disks.Add(new UsbDisk(number, Wmi.Str(d, "FriendlyName"), Wmi.ULong(d, "Size"), VolumesOf(number)));
        }
        return disks;
    }

    private static List<UsbVolume> VolumesOf(int diskNumber)
    {
        var volumes = new List<UsbVolume>();
        foreach (var p in Wmi.Query($"SELECT DriveLetter FROM MSFT_Partition WHERE DiskNumber = {diskNumber}", StorageScope))
        {
            if (Wmi.Get(p, "DriveLetter") is not char letter || letter == '\0')
                continue;
            var drive = new DriveInfo(letter + ":\\");
            volumes.Add(drive.IsReady
                ? new UsbVolume(letter, drive.DriveFormat, drive.VolumeLabel, (ulong)drive.TotalSize)
                : new UsbVolume(letter, "", "", 0));
        }
        return volumes;
    }

    /// <summary>
    /// Erases the disk and creates one FAT32 partition labeled OPENCORE (at most 16 GB, since Windows
    /// cannot format larger FAT32 volumes). Runs an elevated PowerShell script (UAC prompt), which
    /// re-checks that the target is a non-boot USB disk before touching it.
    /// </summary>
    /// <returns>The new drive letter.</returns>
    public static async Task<char> FormatAsync(int diskNumber, CancellationToken ct = default)
    {
        var resultFile = Path.Combine(Path.GetTempPath(), $"eoc-format-{Guid.NewGuid():N}.txt");
        var script = Path.Combine(Path.GetTempPath(), $"eoc-format-{Guid.NewGuid():N}.ps1");
        await File.WriteAllTextAsync(script, $$"""
            $ErrorActionPreference = 'Stop'
            $disk = Get-Disk -Number {{diskNumber}}
            if ($disk.BusType -ne 'USB' -or $disk.IsBoot -or $disk.IsSystem) { exit 2 }
            $disk | Set-Disk -IsReadOnly $false -ErrorAction SilentlyContinue
            $disk | Set-Disk -IsOffline $false -ErrorAction SilentlyContinue
            Clear-Disk -Number {{diskNumber}} -RemoveData -RemoveOEM -Confirm:$false
            Initialize-Disk -Number {{diskNumber}} -PartitionStyle GPT
            $size = [Math]::Min(16GB, (Get-Disk -Number {{diskNumber}}).LargestFreeExtent)
            $part = New-Partition -DiskNumber {{diskNumber}} -Size $size -AssignDriveLetter
            Format-Volume -Partition $part -FileSystem FAT32 -NewFileSystemLabel 'OPENCORE' -Confirm:$false | Out-Null
            (Get-Partition -DiskNumber {{diskNumber}} -PartitionNumber $part.PartitionNumber).DriveLetter | Set-Content -Path '{{resultFile}}'
            """, ct);

        try
        {
            using var process = Process.Start(new ProcessStartInfo("powershell.exe",
                ["-NoProfile", "-ExecutionPolicy", "Bypass", "-WindowStyle", "Hidden", "-File", script])
            {
                UseShellExecute = true,
                Verb = "runas",
            }) ?? throw new InvalidOperationException("Could not start PowerShell.");
            await process.WaitForExitAsync(ct);

            if (process.ExitCode == 2)
                throw new InvalidOperationException("The selected disk is not a removable USB disk.");
            if (process.ExitCode != 0 || !File.Exists(resultFile))
                throw new InvalidOperationException($"Formatting failed (exit code {process.ExitCode}).");
            var letter = (await File.ReadAllTextAsync(resultFile, ct)).Trim();
            return letter.Length > 0 ? letter[0] : throw new InvalidOperationException("No drive letter was assigned.");
        }
        finally
        {
            File.Delete(script);
            File.Delete(resultFile);
        }
    }

    /// <summary>Copies EFI/ (and the recovery files, when given) to the root of <paramref name="targetRoot"/>.</summary>
    public static async Task CopyAsync(string efiFolder, string targetRoot, IEnumerable<string>? recoveryFiles = null,
        bool overwrite = false, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (Path.GetPathRoot(targetRoot) == targetRoot)
        {
            var drive = new DriveInfo(targetRoot);
            if (!drive.DriveFormat.Equals("FAT32", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"{targetRoot} is {drive.DriveFormat}; UEFI firmware needs FAT32.");
        }

        var efiTarget = Path.Combine(targetRoot, "EFI");
        if (Directory.Exists(efiTarget))
        {
            if (!overwrite)
                throw new IOException($"{efiTarget} already exists.");
            Directory.Delete(efiTarget, recursive: true);
        }

        await Task.Run(() => CopyDirectory(efiFolder, efiTarget, progress), ct);

        foreach (var file in recoveryFiles ?? [])
        {
            var dir = Path.Combine(targetRoot, RecoveryFolder);
            Directory.CreateDirectory(dir);
            progress?.Report(Path.GetFileName(file));
            await using var src = File.OpenRead(file);
            await using var dst = File.Create(Path.Combine(dir, Path.GetFileName(file)));
            await src.CopyToAsync(dst, ct);
        }
    }

    private static void CopyDirectory(string source, string target, IProgress<string>? progress)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
        foreach (var dir in Directory.GetDirectories(source))
        {
            progress?.Report(Path.GetFileName(dir));
            CopyDirectory(dir, Path.Combine(target, Path.GetFileName(dir)), progress);
        }
    }
}
