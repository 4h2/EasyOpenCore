using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using EasyOpenCore.Core.Efi;
using EasyOpenCore.Core.Localization;
using EasyOpenCore.Core.Recovery;
using EasyOpenCore.Core.Usb;

namespace EasyOpenCore.App.ViewModels;

/// <summary>Creates the bootable USB installer: optional format, EFI copy and macOS recovery download.</summary>
public sealed class UsbInstallerViewModel : Observable
{
    private readonly BuildViewModel build;
    private readonly CompatViewModel compat;

    public UsbInstallerViewModel(BuildViewModel build, CompatViewModel compat)
    {
        this.build = build;
        this.compat = compat;
        build.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(BuildViewModel.HasResult))
                OnPropertyChanged(nameof(CanCreate));
        };
    }

    private UsbDisk? _selectedDisk;
    private bool _format;
    private bool _includeRecovery = true;
    private bool _busy;
    private double _progress;
    private string _status = "";

    public ObservableCollection<UsbDisk> Disks { get; } = [];

    public UsbDisk? SelectedDisk
    {
        get => _selectedDisk;
        set { _selectedDisk = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanCreate)); }
    }

    public bool Format
    {
        get => _format;
        set { _format = value; OnPropertyChanged(); }
    }

    public bool IncludeRecovery
    {
        get => _includeRecovery;
        set { _includeRecovery = value; OnPropertyChanged(); }
    }

    public bool Busy
    {
        get => _busy;
        private set { _busy = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanCreate)); }
    }

    public bool CanCreate => !_busy && _selectedDisk is not null && build.LastEfiFolder is not null;

    public double Progress
    {
        get => _progress;
        private set { _progress = value; OnPropertyChanged(); }
    }

    public string Status
    {
        get => _status;
        private set { _status = value; OnPropertyChanged(); }
    }

    public void RefreshDisks()
    {
        var selected = _selectedDisk?.Number;
        Disks.Clear();
        foreach (var d in UsbDriveWriter.ListUsbDisks())
            Disks.Add(d);
        SelectedDisk = Disks.FirstOrDefault(d => d.Number == selected) ?? Disks.FirstOrDefault();
        if (Disks.Count == 0)
            Status = Loc.T("installer.no_disks");
        OnPropertyChanged(nameof(CanCreate));
    }

    /// <param name="confirm">Asks the user a yes/no question (format warning, overwrite existing EFI).</param>
    public async Task CreateAsync(Func<string, bool> confirm)
    {
        if (!CanCreate || _selectedDisk is null || build.LastEfiFolder is null || compat.SelectedVersion is null)
            return;
        var disk = _selectedDisk;

        if (_format && !confirm(Loc.T("installer.confirm_format", disk.Display)))
            return;

        Busy = true;
        Progress = 0;
        try
        {
            char letter;
            if (_format)
            {
                Status = Loc.T("installer.formatting");
                letter = await UsbDriveWriter.FormatAsync(disk.Number);
            }
            else
            {
                var fat32 = disk.Volumes.FirstOrDefault(v => v.FileSystem.Equals("FAT32", StringComparison.OrdinalIgnoreCase));
                if (fat32 is null)
                {
                    Status = Loc.T("installer.needs_fat32");
                    return;
                }
                letter = fat32.DriveLetter;
            }

            var recoveryFiles = new List<string>();
            if (_includeRecovery)
                recoveryFiles.AddRange(await DownloadRecoveryAsync());

            var root = letter + ":\\";
            bool overwrite = false;
            if (Directory.Exists(Path.Combine(root, "EFI")))
            {
                overwrite = confirm(Loc.T("installer.confirm_overwrite", root));
                if (!overwrite)
                    return;
            }

            Status = Loc.T("installer.copying", root);
            await UsbDriveWriter.CopyAsync(Path.Combine(build.LastEfiFolder, "EFI"), root, recoveryFiles, overwrite);
            Progress = 100;
            Status = Loc.T("installer.done", root);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or HttpRequestException
                                       or InvalidDataException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            Status = ex.Message;
        }
        finally
        {
            Busy = false;
            RefreshDisks();
        }
    }

    private async Task<string[]> DownloadRecoveryAsync()
    {
        var version = compat.Report!.Versions.First(v => v.Id == compat.SelectedVersion);
        using var github = new GitHubClient();
        var macrecovery = Directory.EnumerateDirectories(github.CacheRoot, "macrecovery", SearchOption.AllDirectories)
            .OrderByDescending(Directory.GetLastWriteTimeUtc)
            .First();

        using var downloader = new RecoveryDownloader(macrecovery);
        var board = downloader.BoardFor(version.Name)
                    ?? throw new InvalidOperationException(Loc.T("installer.no_board", version.DisplayName));
        Status = Loc.T("installer.asking_apple", version.DisplayName);
        var info = await downloader.GetImageInfoAsync(board);

        var target = Path.Combine(github.CacheRoot, "recovery", version.Id);
        var progress = new Progress<DownloadProgress>(p =>
        {
            if (p.Total > 0)
                Progress = 100.0 * p.Received / p.Total;
            Status = Loc.T("installer.downloading", p.File, p.Received / 1_000_000, p.Total / 1_000_000);
        });
        var (dmg, chunklist) = await downloader.DownloadAsync(info, target, progress);
        return [dmg, chunklist];
    }
}
