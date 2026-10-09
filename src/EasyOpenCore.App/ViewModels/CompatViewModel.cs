using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using EasyOpenCore.Core.Compatibility;
using EasyOpenCore.Core.Localization;
using EasyOpenCore.Core.Models;

namespace EasyOpenCore.App.ViewModels;

public sealed record MatrixCell(SupportLevel Level, string Tooltip);

public sealed record MatrixRow(string Label, string Name, string Detail, bool IsVerdict, IReadOnlyList<MatrixCell> Cells);

public sealed record VersionChip(string Id, string Label, SupportLevel Level, bool IsRecommended);

public sealed class CompatViewModel : INotifyPropertyChanged
{
    private HardwareReport? _hardware;
    private CompatibilityReport? _report;
    private string? _selectedVersion;
    private string _smbios = "";
    private string _smbiosReason = "";
    private string _recommendation = "";

    public ObservableCollection<VersionChip> Versions { get; } = [];
    public ObservableCollection<MatrixRow> Rows { get; } = [];
    public ObservableCollection<PlanItem> Kexts { get; } = [];
    public ObservableCollection<PlanItem> Ssdts { get; } = [];
    public ObservableCollection<PlanItem> BootArgs { get; } = [];
    public ObservableCollection<DeviceProperty> DeviceProperties { get; } = [];
    public ObservableCollection<string> Warnings { get; } = [];
    public ObservableCollection<string> NotWorking { get; } = [];
    public ObservableCollection<string> AcpiFacts { get; } = [];

    public int VersionCount => Versions.Count;

    public CompatibilityReport? Report => _report;

    public string Recommendation
    {
        get => _recommendation;
        private set { _recommendation = value; OnPropertyChanged(); }
    }

    public string Smbios
    {
        get => _smbios;
        private set { _smbios = value; OnPropertyChanged(); }
    }

    public string SmbiosReason
    {
        get => _smbiosReason;
        private set { _smbiosReason = value; OnPropertyChanged(); }
    }

    public string? SelectedVersion
    {
        get => _selectedVersion;
        set
        {
            if (value is null || value == _selectedVersion)
                return;
            _selectedVersion = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectedChip));
            BuildPlan();
        }
    }

    /// <summary>
    /// Selection bound by object instead of SelectedValue: the chips are recreated on every
    /// load, and a SelectedValue binding loses the selection when its items are replaced.
    /// </summary>
    public VersionChip? SelectedChip
    {
        get => Versions.FirstOrDefault(v => v.Id == _selectedVersion);
        set => SelectedVersion = value?.Id;
    }

    public void Load(HardwareReport hardware)
    {
        _hardware = hardware;
        _report = CompatibilityEngine.Evaluate(hardware);
        var r = _report;

        Versions.Clear();
        foreach (var v in r.Verdicts)
        {
            bool isRecommended = v.Version.Id == r.RecommendedVersion;
            Versions.Add(new VersionChip(v.Version.Id, $"{v.Version.Id} {v.Version.Name}{(isRecommended ? " ★" : "")}", v.Level, isRecommended));
        }
        OnPropertyChanged(nameof(VersionCount));

        Rows.Clear();
        Rows.Add(new MatrixRow(Loc.T("compat.overall"), Loc.T("compat.overall_detail"), "", true,
            r.Verdicts.Select(v => new MatrixCell(v.Level, VerdictTooltip(v))).ToList()));
        foreach (var c in r.Components)
        {
            Rows.Add(new MatrixRow(Loc.T($"component.{c.Component}"), c.Name, c.Detail, false,
                r.Versions.Select(v => new MatrixCell(c.Cells[v.Id].Level, CellTooltip(c, v))).ToList()));
        }

        AcpiFacts.Clear();
        AcpiFacts.Add(Loc.T("compat.acpi.ec", Or(r.Acpi.EcPath)));
        AcpiFacts.Add(Loc.T("compat.acpi.cpu", Or(r.Acpi.CpuPath)));
        AcpiFacts.Add(Loc.T("compat.acpi.pci", Or(r.Acpi.PciRootPath)));
        AcpiFacts.Add(Loc.T("compat.acpi.lpc", Or(r.Acpi.LpcPath)));
        AcpiFacts.Add(Loc.T("compat.acpi.clock", r.Acpi.HasAwac ? "AWAC" : r.Acpi.HasRtc ? "RTC" : "—"));

        var recommended = r.Verdicts.FirstOrDefault(v => v.Version.Id == r.RecommendedVersion);
        Recommendation = recommended is null
            ? Loc.T("compat.recommended_none")
            : Loc.T("compat.recommended", recommended.Version.DisplayName, Loc.T($"level.{recommended.Level}"));

        // Keep the user's choice across rescans/language changes.
        var target = Versions.Any(v => v.Id == _selectedVersion) ? _selectedVersion : r.RecommendedVersion ?? Versions.LastOrDefault()?.Id;
        _selectedVersion = null;
        SelectedVersion = target;
    }

    private void BuildPlan()
    {
        Kexts.Clear(); Ssdts.Clear(); BootArgs.Clear(); DeviceProperties.Clear(); Warnings.Clear(); NotWorking.Clear();
        if (_hardware is null || _report is null || _selectedVersion is null)
            return;

        var plan = EfiPlanner.Plan(_hardware, _report, _selectedVersion);
        Smbios = string.IsNullOrEmpty(plan.Smbios) ? "—" : plan.Smbios;
        SmbiosReason = plan.SmbiosReason;
        foreach (var k in plan.Kexts) Kexts.Add(k);
        foreach (var s in plan.Ssdts) Ssdts.Add(s);
        foreach (var b in plan.BootArgs) BootArgs.Add(b);
        foreach (var p in plan.DeviceProperties) DeviceProperties.Add(p);
        foreach (var w in plan.Warnings) Warnings.Add(w);

        var verdict = _report.Verdicts.First(v => v.Version.Id == _selectedVersion);
        if (verdict.Version.Note is { } note)
            Warnings.Insert(0, Loc.T(note));
        foreach (var c in verdict.NotWorking)
            NotWorking.Add($"{Loc.T($"component.{c.Component}")}: {c.Name} — {Loc.T(c.Cells[_selectedVersion].Note ?? $"@level.{c.Cells[_selectedVersion].Level}")}");
    }

    private static string CellTooltip(ComponentResult c, MacOsVersion v)
    {
        var cell = c.Cells[v.Id];
        var text = $"{c.Name}\n{v.DisplayName}: {Loc.T($"level.{cell.Level}")}";
        return cell.Note is null ? text : $"{text}\n{Loc.T(cell.Note)}";
    }

    private static string VerdictTooltip(VersionVerdict v)
    {
        var text = $"{v.Version.DisplayName}: {Loc.T($"level.{v.Level}")}";
        return v.NotWorking.Count == 0
            ? text
            : $"{text}\n{Loc.T("compat.not_working")}: {string.Join(", ", v.NotWorking.Select(c => c.Name))}";
    }

    private static string Or(string s) => s.Length > 0 ? s : "—";

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
