using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Data;
using EasyOpenCore.Core.Checks;
using EasyOpenCore.Core.Hardware;
using EasyOpenCore.Core.Localization;
using EasyOpenCore.Core.Models;

namespace EasyOpenCore.App.ViewModels;

public sealed record SummaryCard(string Icon, string Title, string Headline, IReadOnlyList<string> Details);

public sealed record CategoryChip(string Id, string Label);

public sealed class MainViewModel : INotifyPropertyChanged
{
    private const string AllCategories = "all";

    private HardwareReport? _report;
    private bool _isScanning;
    private string _status = Loc.T("status.ready");
    private string _deviceFilter = "";
    private string _selectedCategory = AllCategories;

    public MainViewModel()
    {
        DevicesView = CollectionViewSource.GetDefaultView(Devices);
        DevicesView.Filter = FilterDevice;
        Loc.Instance.LanguageChanged += (_, _) =>
        {
            if (_report is not null)
            {
                Load(_report);
                Status = ScannedStatus(_report);
            }
            else
            {
                Status = Loc.T("status.ready");
            }
        };
    }

    public HardwareReport? Report
    {
        get => _report;
        private set { _report = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasReport)); }
    }

    public bool HasReport => _report is not null;

    public bool IsScanning
    {
        get => _isScanning;
        private set { _isScanning = value; OnPropertyChanged(); }
    }

    public string Status
    {
        get => _status;
        private set { _status = value; OnPropertyChanged(); }
    }

    public string MachineTitle => _report is null ? "" : $"{_report.System.Manufacturer} {_report.System.Model}".Trim();

    public CompatViewModel Compat { get; } = new();

    public ObservableCollection<SummaryCard> Cards { get; } = [];
    public ObservableCollection<Finding> Findings { get; } = [];
    public ObservableCollection<PnpDevice> Devices { get; } = [];
    public ObservableCollection<AcpiDevice> AcpiDevices { get; } = [];
    public ObservableCollection<AcpiTable> AcpiTables { get; } = [];
    public ObservableCollection<CategoryChip> Categories { get; } = [];
    public ICollectionView DevicesView { get; }

    public int BlockerCount => Findings.Count(f => f.Severity == Severity.Blocker);
    public int WarningCount => Findings.Count(f => f.Severity == Severity.Warning);

    public string DeviceFilter
    {
        get => _deviceFilter;
        set { _deviceFilter = value; OnPropertyChanged(); DevicesView.Refresh(); }
    }

    public string SelectedCategory
    {
        get => _selectedCategory;
        set
        {
            _selectedCategory = value ?? AllCategories;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectedCategoryChip));
            DevicesView.Refresh();
        }
    }

    /// <summary>Bound by object for the same reason as <see cref="CompatViewModel.SelectedChip"/>.</summary>
    public CategoryChip? SelectedCategoryChip
    {
        get => Categories.FirstOrDefault(c => c.Id == _selectedCategory);
        set
        {
            if (value is not null)
                SelectedCategory = value.Id;
        }
    }

    public async Task ScanAsync()
    {
        if (IsScanning)
            return;
        IsScanning = true;
        try
        {
            var progress = new Progress<string>(s => Status = Loc.T("status.scanning", s));
            var report = await Task.Run(() => HardwareScanner.Scan(progress));
            Load(report);
            Status = ScannedStatus(report);
        }
        catch (Exception ex)
        {
            Status = Loc.T("status.failed", ex.Message);
        }
        finally
        {
            IsScanning = false;
        }
    }

    private static string ScannedStatus(HardwareReport r) =>
        Loc.T("status.scanned", r.GeneratedAt.ToString("g", Loc.Instance.Culture), r.AllDevices.Count);

    private void Load(HardwareReport r)
    {
        Report = r;
        OnPropertyChanged(nameof(MachineTitle));

        Cards.Clear();
        foreach (var card in BuildCards(r))
            Cards.Add(card);

        Findings.Clear();
        foreach (var f in PreflightChecks.Run(r).OrderByDescending(f => f.Severity))
            Findings.Add(f);
        OnPropertyChanged(nameof(BlockerCount));
        OnPropertyChanged(nameof(WarningCount));

        Devices.Clear();
        foreach (var d in r.AllDevices.OrderBy(d => d.Category).ThenBy(d => d.Name))
            Devices.Add(d);

        var selected = _selectedCategory;
        Categories.Clear();
        Categories.Add(new CategoryChip(AllCategories, Loc.T("category.all")));
        foreach (var c in r.AllDevices.Select(d => d.Category).Distinct().Order())
            Categories.Add(new CategoryChip(c, Loc.T($"category.{c}")));
        SelectedCategory = Categories.Any(c => c.Id == selected) ? selected : AllCategories;

        AcpiDevices.Clear();
        foreach (var a in r.AcpiDevices.Where(a => a.AcpiPath.Length > 0).DistinctBy(a => a.AcpiPath)
                     .OrderBy(a => a.Role.Length == 0).ThenBy(a => a.AcpiPath))
            AcpiDevices.Add(a);

        AcpiTables.Clear();
        foreach (var t in r.AcpiTables)
            AcpiTables.Add(t);

        Compat.Load(r);
    }

    private bool FilterDevice(object o)
    {
        if (o is not PnpDevice d)
            return false;
        if (_selectedCategory != AllCategories && d.Category != _selectedCategory)
            return false;
        if (string.IsNullOrWhiteSpace(_deviceFilter))
            return true;
        var q = _deviceFilter.Trim();
        return d.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
               || d.InstanceId.Contains(q, StringComparison.OrdinalIgnoreCase)
               || d.PciPath.Contains(q, StringComparison.OrdinalIgnoreCase)
               || d.AcpiPath.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<SummaryCard> BuildCards(HardwareReport r)
    {
        var s = r.System;
        string firmware = Loc.T(s.Firmware switch
        {
            FirmwareKind.Uefi => "firmware.uefi",
            FirmwareKind.Bios => "firmware.legacy",
            _ => "firmware.unknown",
        });
        string secureBoot = Loc.T(s.SecureBootEnabled switch
        {
            true => "secureboot.on",
            false => "secureboot.off",
            _ => "secureboot.unknown",
        });
        yield return new("", Loc.T("card.system"), $"{s.Manufacturer} {s.Model}".Trim(),
        [
            Loc.T("card.system.type", Loc.T($"chassis.{s.Chassis}")),
            Loc.T("card.system.board", $"{s.BoardManufacturer} {s.BoardProduct}"),
            Loc.T("card.system.bios", s.BiosVersion, s.BiosDate),
            Loc.T("card.system.firmware", firmware, secureBoot),
        ]);

        var c = r.Cpu;
        yield return new("", Loc.T("card.cpu"), c.Name,
        [
            Loc.T("card.cpu.arch", c.Codename) + (c.IsMobile ? Loc.T("card.cpu.mobile") : ""),
            Loc.T("card.cpu.cores", c.Cores, c.Threads) + (c.IsHybrid ? Loc.T("card.cpu.hybrid") : ""),
            Loc.T("card.cpu.cpuid", c.Family.ToString("X"), c.Model.ToString("X2"), c.Stepping),
            $"SSE4.2: {YesNo(c.Has("SSE4.2"))} · AVX2: {YesNo(c.Has("AVX2"))}",
        ]);

        yield return new("", Loc.T("card.gpu"),
            r.Gpus.Count == 0 ? Loc.T("card.gpu.none") : string.Join(" + ", r.Gpus.Select(g => g.Name)),
            r.Gpus.Select(g => $"{(g.Kind == GpuKind.Integrated ? "iGPU" : "dGPU")} {g.VendorId}:{g.DeviceId} · {FormatBytes(g.VramBytes)} · {g.PciPath}").ToList());

        ulong totalRam = (ulong)r.Memory.Sum(m => (decimal)m.CapacityBytes);
        yield return new("", Loc.T("card.memory"), $"{FormatBytes(totalRam)} {r.Memory.FirstOrDefault()?.Type}",
            r.Memory.Select(m => $"{m.Slot}: {FormatBytes(m.CapacityBytes)} {m.SpeedMts} MT/s {m.Manufacturer}").ToList());

        yield return new("", Loc.T("card.storage"), Loc.T("card.storage.disks", r.Storage.Count),
            r.Storage.Select(d => $"{d.Model} · {d.Bus} · {FormatBytes(d.SizeBytes, 1000)} · {d.PartitionStyle}{(d.IsSystemDisk ? Loc.T("card.system_disk") : "")}")
                .Concat(r.StorageControllers.Select(sc => Loc.T("card.storage.controller", sc.Name, $"{sc.VendorId}:{sc.DeviceId}"))).ToList());

        var mainCodec = r.AudioCodecs.FirstOrDefault(a => !a.IsHdmi);
        yield return new("", Loc.T("card.audio"),
            mainCodec is null ? Loc.T("card.audio.none") : $"{mainCodec.CodecName} ({mainCodec.Name})",
            r.AudioCodecs.Select(a => $"{(a.IsHdmi ? "HDMI" : "Codec")}: {a.VendorId}:{a.DeviceId} {a.CodecName}").ToList());

        yield return new("", Loc.T("card.network"), Loc.T("card.network.count", r.Network.Count),
            r.Network.Select(n => $"{n.Kind}: {n.Name} ({n.VendorId}:{n.DeviceId}, {n.Bus})").ToList());

        yield return new("", Loc.T("card.input"), Loc.T("card.input.usb", r.UsbControllers.Count),
            r.Input.Select(i => $"{Loc.T($"input.{i.Type}")} ({i.Bus}): {i.Name}")
                .Concat(r.UsbControllers.Select(u => $"USB: {u.VendorId}:{u.DeviceId} · {u.PciPath}")).ToList());
    }

    private static string YesNo(bool b) => Loc.T(b ? "yes" : "no");

    private static string FormatBytes(ulong bytes, int unit = 1024)
    {
        if (bytes == 0)
            return "—";
        string[] suffix = ["B", "KB", "MB", "GB", "TB"];
        double v = bytes;
        int i = 0;
        while (v >= unit && i < suffix.Length - 1) { v /= unit; i++; }
        return v.ToString("0.#", Loc.Instance.Culture) + " " + suffix[i];
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
