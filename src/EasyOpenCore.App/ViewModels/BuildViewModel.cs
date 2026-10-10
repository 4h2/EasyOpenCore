using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using EasyOpenCore.Core.Compatibility;
using EasyOpenCore.Core.Efi;
using EasyOpenCore.Core.Localization;
using EasyOpenCore.Core.Models;

namespace EasyOpenCore.App.ViewModels;

public sealed record BuildLine(string Title, string Detail);

public sealed class BuildViewModel : INotifyPropertyChanged
{
    private readonly CompatViewModel _compat;
    private HardwareReport? _hardware;
    private string _outputFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "EasyOpenCore-EFI");
    private bool _isBuilding;
    private bool _hasResult;
    private bool? _validationPassed;
    private string _validationOutput = "";
    private string _error = "";

    public BuildViewModel(CompatViewModel compat)
    {
        _compat = compat;
        _compat.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(CompatViewModel.SelectedVersion))
            {
                OnPropertyChanged(nameof(TargetText));
                OnPropertyChanged(nameof(CanBuild));
            }
        };
        Loc.Instance.LanguageChanged += (_, _) => OnPropertyChanged(nameof(TargetText));
    }

    public ObservableCollection<string> Log { get; } = [];
    public ObservableCollection<BuildLine> Kexts { get; } = [];
    public ObservableCollection<BuildLine> ManualSteps { get; } = [];
    public ObservableCollection<string> Ssdts { get; } = [];

    public string TargetText => _compat.SelectedChip is { } chip
        ? Loc.T("build.target", $"macOS {chip.Label.TrimEnd('★', ' ')}", _compat.Smbios)
        : "";

    public string OutputFolder
    {
        get => _outputFolder;
        set { _outputFolder = value; OnPropertyChanged(); }
    }

    public bool DebugBuild { get; set; }

    public bool IsBuilding
    {
        get => _isBuilding;
        private set { _isBuilding = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanBuild)); }
    }

    public bool CanBuild => !_isBuilding && _hardware is not null && _compat.SelectedVersion is not null;

    public bool HasResult
    {
        get => _hasResult;
        private set { _hasResult = value; OnPropertyChanged(); }
    }

    public bool? ValidationPassed
    {
        get => _validationPassed;
        private set { _validationPassed = value; OnPropertyChanged(); OnPropertyChanged(nameof(ValidationText)); }
    }

    public string ValidationText => _validationPassed switch
    {
        true => Loc.T("build.validation.passed"),
        false => Loc.T("build.validation.failed"),
        _ => "",
    };

    public string ValidationOutput
    {
        get => _validationOutput;
        private set { _validationOutput = value; OnPropertyChanged(); }
    }

    public string Error
    {
        get => _error;
        private set { _error = value; OnPropertyChanged(); }
    }

    public string? LastEfiFolder { get; private set; }

    public void SetHardware(HardwareReport hardware)
    {
        _hardware = hardware;
        OnPropertyChanged(nameof(CanBuild));
        OnPropertyChanged(nameof(TargetText));
    }

    public async Task BuildAsync()
    {
        if (!CanBuild || _compat.Report is null || _compat.SelectedVersion is null)
            return;

        IsBuilding = true;
        HasResult = false;
        Error = "";
        Log.Clear(); Kexts.Clear(); ManualSteps.Clear(); Ssdts.Clear();
        ValidationPassed = null;
        ValidationOutput = "";

        try
        {
            using var github = new GitHubClient();
            var progress = new Progress<string>(s => Log.Add(s));
            var result = await new EfiBuilder(github).BuildAsync(_hardware!, _compat.Report, _compat.SelectedVersion, OutputFolder, progress,
                usbMap: Core.Usb.UsbMap.Load(), debugBuild: DebugBuild);

            foreach (var k in result.Kexts)
                Kexts.Add(new BuildLine($"{k.Name} {k.Version}", k.Source));
            foreach (var k in result.MissingKexts)
                ManualSteps.Add(new BuildLine(k.Name, $"{k.Reason} {k.Url}"));
            foreach (var m in result.ManualSsdts)
                ManualSteps.Add(new BuildLine(m.Name, Loc.T(m.ReasonKey)));
            foreach (var s in result.Ssdts)
                Ssdts.Add(s);
            if (result.Audio is { } audio)
            {
                ManualSteps.Add(new BuildLine(Loc.T("build.audio.title", audio.LayoutId),
                    Loc.T("build.audio.detail", string.Join(", ", audio.Alternatives.Where(a => a.Id != audio.LayoutId).Select(a => a.Id)))));
            }

            ValidationPassed = result.ValidationPassed;
            ValidationOutput = result.ValidationOutput;
            LastEfiFolder = result.OutputDirectory;
            Log.Add(Loc.T("build.done", result.OutputDirectory));
            HasResult = true;
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or InvalidOperationException or UnauthorizedAccessException)
        {
            Error = ex.Message;
        }
        finally
        {
            IsBuilding = false;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
