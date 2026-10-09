using System.Collections.ObjectModel;
using EasyOpenCore.Core.Localization;
using EasyOpenCore.Core.Models;
using EasyOpenCore.Core.PostInstall;

namespace EasyOpenCore.App.ViewModels;

/// <summary>A "turn on/off in my EFI" button under a problem.</summary>
public sealed record TroubleFixViewModel(string TweakId, bool On, string Label);

public sealed class TroubleViewModel(TroubleEntry entry, bool relevant) : Observable
{
    private bool _expanded;
    private string _message = "";

    public string Id => entry.Id;
    public string Title => entry.Title;
    public string Cause => entry.Cause;
    public bool HasCause => Cause.Length > 0;
    public string Fix => entry.Fix;
    public string Url => entry.Url;
    public string StageLabel => Loc.T($"trouble.page.{entry.Page}");
    public bool Relevant => relevant;
    public IReadOnlyList<string> Commands => entry.Commands;
    public string CommandsText => string.Join(Environment.NewLine, entry.Commands);
    public bool HasCommands => entry.Commands.Count > 0;

    public IReadOnlyList<TroubleFixViewModel> Fixes { get; } = entry.Fixes
        .Select(f => (f, t: EfiTweaks.Find(f.Tweak)!))
        .Select(x => new TroubleFixViewModel(x.f.Tweak, x.f.On, Loc.T(x.f.On ? "trouble.turn_on" : "trouble.turn_off", x.t.Title)))
        .ToList();

    public bool IsExpanded
    {
        get => _expanded;
        set { _expanded = value; OnPropertyChanged(); }
    }

    /// <summary>Result of the last fix applied from this card.</summary>
    public string Message
    {
        get => _message;
        set { _message = value; OnPropertyChanged(); }
    }
}

/// <summary>The Troubleshooting tab: searchable list of the Dortania troubleshooting entries.</summary>
public sealed class TroubleshootingViewModel : Observable
{
    private const string AllStages = "all";

    private HardwareReport? _hardware;
    private string _query = "";
    private string _stage = AllStages;
    private bool _onlyRelevant;

    public TroubleshootingViewModel()
    {
        Loc.Instance.LanguageChanged += (_, _) => Rebuild();
        Rebuild();
    }

    public ObservableCollection<CategoryChip> Stages { get; } = [];
    public ObservableCollection<TroubleViewModel> Items { get; } = [];

    public string Query
    {
        get => _query;
        set { _query = value; OnPropertyChanged(); Filter(); }
    }

    public bool OnlyRelevant
    {
        get => _onlyRelevant;
        set { _onlyRelevant = value; OnPropertyChanged(); Filter(); }
    }

    /// <summary>Bound by object for the same reason as <see cref="CompatViewModel.SelectedChip"/>.</summary>
    public CategoryChip? SelectedStage
    {
        get => Stages.FirstOrDefault(s => s.Id == _stage);
        set
        {
            if (value is null || value.Id == _stage)
                return;
            _stage = value.Id;
            OnPropertyChanged();
            Filter();
        }
    }

    public string CountText => Loc.T("trouble.count", Items.Count);
    public bool IsEmpty => Items.Count == 0;

    public void SetHardware(HardwareReport hardware)
    {
        _hardware = hardware;
        Filter();
    }

    private void Rebuild()
    {
        Stages.Clear();
        Stages.Add(new CategoryChip(AllStages, Loc.T("trouble.stage.all")));
        foreach (var s in Troubleshooting.Pages)
            Stages.Add(new CategoryChip(s, Loc.T($"trouble.page.{s}")));
        OnPropertyChanged(nameof(SelectedStage));
        Filter();
    }

    private void Filter()
    {
        var expanded = Items.Where(i => i.IsExpanded).Select(i => i.Id).ToHashSet();
        Items.Clear();
        var entries = Troubleshooting.Entries
            .Where(e => _stage == AllStages || e.Page == _stage)
            .Where(e => Troubleshooting.Matches(e, _query))
            .Select(e => new TroubleViewModel(e, _hardware is not null && e.MatchesHardware(_hardware)))
            .Where(vm => !_onlyRelevant || vm.Relevant)
            // Hardware-specific problems first; otherwise keep the guide's order.
            .OrderByDescending(vm => vm.Relevant);
        foreach (var vm in entries)
        {
            vm.IsExpanded = expanded.Contains(vm.Id) || (!string.IsNullOrWhiteSpace(_query) && Items.Count < 3);
            Items.Add(vm);
        }
        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(IsEmpty));
    }
}
