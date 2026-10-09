using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using EasyOpenCore.Core.Efi;
using EasyOpenCore.Core.Localization;
using EasyOpenCore.Core.Models;
using EasyOpenCore.Core.PostInstall;

namespace EasyOpenCore.App.ViewModels;

/// <summary>One Post-Install tweak: its state in the opened EFI and the state the user wants.</summary>
public sealed class TweakViewModel(EfiTweak tweak, bool current, bool recommended, string? unavailable, Action changed) : Observable
{
    private bool _wanted = current;

    public EfiTweak Tweak => tweak;
    public string Id => tweak.Id;
    public string Title => tweak.Title;
    public string Detail => tweak.Detail;
    public string Url => tweak.Url;
    public bool Current { get; private set; } = current;
    public bool Recommended => recommended;
    public bool Downloads => tweak.Downloads;
    public string Unavailable => unavailable is null ? "" : Loc.T(unavailable);
    public bool IsAvailable => unavailable is null;

    public bool Wanted
    {
        get => _wanted;
        set
        {
            if (_wanted == value)
                return;
            _wanted = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Changed));
            changed();
        }
    }

    public bool Changed => _wanted != Current;
}

public sealed record TweakGroupViewModel(string Title, IReadOnlyList<TweakViewModel> Items);

/// <summary>The Post-Install tab: opens an existing EFI and toggles the Dortania post-install tweaks on it.</summary>
public sealed class PostInstallViewModel : Observable
{
    private readonly BuildViewModel _build;
    private HardwareReport? _hardware;
    private EfiSession? _session;
    private GitHubClient? _github;
    private string _folder = "";
    private bool _busy;
    private string _status = "";
    private bool? _validationPassed;
    private string _validationOutput = "";

    public PostInstallViewModel(BuildViewModel build)
    {
        _build = build;
        build.PropertyChanged += (_, e) =>
        {
            // A freshly built EFI is the natural one to edit next.
            if (e.PropertyName == nameof(BuildViewModel.HasResult) && build.LastEfiFolder is { } built && !_busy)
                Open(built);
        };
        Loc.Instance.LanguageChanged += (_, _) => Rebuild();
    }

    public ObservableCollection<TweakGroupViewModel> Groups { get; } = [];

    public string Folder
    {
        get => _folder;
        set { _folder = value; OnPropertyChanged(); }
    }

    public bool IsLoaded => _session is not null;
    public string LoadedText => _session is null ? Loc.T("postinstall.no_efi") : Loc.T("postinstall.loaded", _session.EfiDir);

    public bool Busy
    {
        get => _busy;
        private set { _busy = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanApply)); OnPropertyChanged(nameof(IsIdle)); }
    }

    public bool IsIdle => !_busy;

    public string Status
    {
        get => _status;
        private set { _status = value; OnPropertyChanged(); }
    }

    public bool? ValidationPassed
    {
        get => _validationPassed;
        private set { _validationPassed = value; OnPropertyChanged(); OnPropertyChanged(nameof(ValidationText)); }
    }

    public string ValidationText => _validationPassed switch
    {
        true => Loc.T("postinstall.validation.passed"),
        false => Loc.T("postinstall.validation.failed"),
        _ => "",
    };

    public string ValidationOutput
    {
        get => _validationOutput;
        private set { _validationOutput = value; OnPropertyChanged(); }
    }

    private IEnumerable<TweakViewModel> All => Groups.SelectMany(g => g.Items);

    public int PendingCount => All.Count(t => t.Changed);
    public string PendingText => PendingCount == 0 ? Loc.T("postinstall.none_pending") : Loc.T("postinstall.pending", PendingCount);
    public bool CanApply => !_busy && PendingCount > 0;

    public void SetHardware(HardwareReport hardware)
    {
        _hardware = hardware;
        if (_session is not null)
            Open(_session.EfiDir);
        else if (EfiSession.FindEfi(_build.OutputFolder) is not null)
            Open(_build.OutputFolder);
    }

    public void UseLastBuilt()
    {
        if (_build.LastEfiFolder is { } folder)
            Open(folder);
        else
            Status = Loc.T("usb.no_efi");
    }

    public void Open(string folder)
    {
        Folder = folder;
        ValidationPassed = null;
        ValidationOutput = "";
        try
        {
            _github ??= new GitHubClient();
            _session = EfiSession.Open(folder, _github, _hardware);
            Status = "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Xml.XmlException)
        {
            _session = null;
            Status = ex.Message;
        }
        Rebuild();
    }

    /// <summary>Recreates the rows from the opened config, keeping changes the user hasn't applied yet.</summary>
    private void Rebuild()
    {
        var wanted = All.Where(t => t.Changed).ToDictionary(t => t.Id, t => t.Wanted);
        Groups.Clear();
        if (_session is { } s)
        {
            foreach (var group in EfiTweaks.All.GroupBy(t => t.Group))
            {
                var items = group.Select(t =>
                {
                    var vm = new TweakViewModel(t, t.IsOn(s), _hardware is not null && t.Recommended(_hardware), t.Unavailable(s), Refresh);
                    if (wanted.TryGetValue(t.Id, out var w))
                        vm.Wanted = w;
                    return vm;
                }).ToList();
                Groups.Add(new TweakGroupViewModel(Loc.T($"postinstall.group.{group.Key.ToString().ToLowerInvariant()}"), items));
            }
        }
        OnPropertyChanged(nameof(IsLoaded));
        OnPropertyChanged(nameof(LoadedText));
        OnPropertyChanged(nameof(ValidationText));
        Refresh();
    }

    private void Refresh()
    {
        OnPropertyChanged(nameof(PendingCount));
        OnPropertyChanged(nameof(PendingText));
        OnPropertyChanged(nameof(CanApply));
    }

    public void Discard()
    {
        foreach (var t in All)
            t.Wanted = t.Current;
    }

    public Task ApplyAsync() => RunAsync(All.Where(t => t.Changed).Select(t => (t.Tweak, t.Wanted)).ToList());

    /// <summary>Used by the Troubleshooting tab: applies one fix right away.</summary>
    public async Task<string> ApplyFixAsync(string tweakId, bool on)
    {
        if (_session is null)
            return Loc.T("trouble.no_efi");
        var tweak = EfiTweaks.Find(tweakId)!;
        if (tweak.IsOn(_session) == on)
            return Loc.T("trouble.already", tweak.Title);
        await RunAsync([(tweak, on)]);
        return Status;
    }

    private async Task RunAsync(List<(EfiTweak Tweak, bool On)> changes)
    {
        if (_session is null || changes.Count == 0 || _busy)
            return;
        Busy = true;
        ValidationPassed = null;
        ValidationOutput = "";
        _session.Progress = new Progress<string>(s => Status = s);
        try
        {
            var result = await _session.ApplyAsync(changes);
            ValidationPassed = result.ValidationPassed;
            ValidationOutput = result.ValidationOutput;
            Status = Loc.T("postinstall.done", result.BackupPath);
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or InvalidOperationException
                                       or UnauthorizedAccessException or InvalidDataException)
        {
            Status = ex.Message;
        }
        finally
        {
            // Reload from disk so the rows show what the EFI really contains now.
            _session.Reload();
            var pending = All.Where(t => t.Changed && !changes.Any(c => c.Tweak.Id == t.Id)).ToDictionary(t => t.Id, t => t.Wanted);
            foreach (var t in All)
                t.Wanted = t.Current;
            Rebuild();
            foreach (var t in All.Where(t => pending.ContainsKey(t.Id)))
                t.Wanted = pending[t.Id];
            Busy = false;
        }
    }
}
