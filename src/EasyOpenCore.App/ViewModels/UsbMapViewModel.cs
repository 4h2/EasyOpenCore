using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using EasyOpenCore.Core.Localization;
using EasyOpenCore.Core.Usb;

namespace EasyOpenCore.App.ViewModels;

public sealed record ConnectorOption(UsbConnector Value, string Label);

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class PortViewModel(MappedPort port, MappedController controller, Action changed) : Observable
{
    public int Index => port.Index;

    public string Title => Loc.T("usb.port", port.Index) + " · " + port.Protocol switch
    {
        UsbPortProtocol.Usb3 => "USB 3",
        UsbPortProtocol.Usb2 => "USB 2",
        _ => "?",
    } + (port.TypeC ? " · Type-C" : "");

    public string Detail => string.Join(" · ", new[]
    {
        port.CompanionPort > 0 ? Loc.T("usb.companion", port.CompanionPort) : null,
        port.UserConnectable ? null : Loc.T("usb.internal"),
        Connected ? Loc.T("usb.connected", CurrentDevice) : port.LastDevice.Length > 0 ? Loc.T("usb.last", port.LastDevice) : null,
    }.Where(s => s is not null));

    public bool Selected
    {
        get => port.Selected;
        set { port.Selected = value; OnPropertyChanged(); changed(); }
    }

    public UsbConnector Connector
    {
        get => UsbMap.EffectiveConnector(port, controller);
        set { port.Connector = value; OnPropertyChanged(); changed(); }
    }

    public bool Seen => port.Seen;

    private string? _currentDevice;

    public string? CurrentDevice
    {
        get => _currentDevice;
        set
        {
            if (_currentDevice == value)
                return;
            _currentDevice = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Connected));
            OnPropertyChanged(nameof(Detail));
            OnPropertyChanged(nameof(Seen));
            OnPropertyChanged(nameof(Selected));
        }
    }

    public bool Connected => _currentDevice is not null;
}

public sealed class ControllerViewModel : Observable
{
    private readonly MappedController _controller;

    public ControllerViewModel(MappedController controller, Action changed)
    {
        _controller = controller;
        Ports = new(controller.Ports.Select(p => new PortViewModel(p, controller, () => { changed(); Refresh(); })));
    }

    public string InstanceId => _controller.InstanceId;
    public string Title => _controller.Name;
    public string Subtitle => string.Join(" · ", new[] { _controller.AcpiPath, _controller.Bdf.Length > 0 ? "PCI " + _controller.Bdf : "" }.Where(s => s.Length > 0));
    public ObservableCollection<PortViewModel> Ports { get; }

    public bool OverLimit => _controller.SelectedCount > UsbMap.PortLimit;

    public string CountText => Loc.T(OverLimit ? "usb.over_limit" : "usb.count", _controller.SelectedCount, UsbMap.PortLimit);

    public void Refresh()
    {
        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(OverLimit));
    }
}

/// <summary>Live USB port discovery and selection (USBToolBox workflow) with automatic persistence.</summary>
public sealed class UsbMapViewModel : Observable
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1.5) };
    private UsbMap _map = UsbMap.Load();
    private bool _discovering;
    private bool _busy;
    private string _message = "";

    public UsbMapViewModel()
    {
        _timer.Tick += async (_, _) => await PollAsync();
        Connectors = new(Enum.GetValues<UsbConnector>().Select(c => new ConnectorOption(c, Loc.T($"connector.{c}"))));
        Loc.Instance.LanguageChanged += (_, _) =>
        {
            // Connector labels first, then the port rows that bind to them.
            Connectors.Clear();
            foreach (var c in Enum.GetValues<UsbConnector>())
                Connectors.Add(new ConnectorOption(c, Loc.T($"connector.{c}")));
            Rebuild();
        };
    }

    public UsbMap Map => _map;
    public ObservableCollection<ControllerViewModel> Controllers { get; } = [];
    public ObservableCollection<ConnectorOption> Connectors { get; }

    public bool Discovering
    {
        get => _discovering;
        private set { _discovering = value; OnPropertyChanged(); OnPropertyChanged(nameof(DiscoverText)); }
    }

    public string DiscoverText => Loc.T(_discovering ? "usb.stop" : "usb.start");

    public string Message
    {
        get => _message;
        set { _message = value; OnPropertyChanged(); }
    }

    public async Task LoadAsync()
    {
        Rebuild();
        await PollAsync();
    }

    public void ToggleDiscovery()
    {
        Discovering = !Discovering;
        if (Discovering)
            _timer.Start();
        else
            _timer.Stop();
    }

    public void Reset()
    {
        _map = new UsbMap();
        _map.Save();
        Rebuild();
        _ = PollAsync();
    }

    private async Task PollAsync()
    {
        if (_busy)
            return;
        _busy = true;
        try
        {
            var live = await Task.Run(() => UsbTopology.Read());
            bool structureChanged = _map.Update(live);
            if (structureChanged)
            {
                _map.Save();
                Rebuild();
            }

            foreach (var c in live)
            {
                var vm = Controllers.FirstOrDefault(x => x.InstanceId == c.InstanceId);
                if (vm is null)
                    continue;
                foreach (var p in c.Ports)
                    if (vm.Ports.FirstOrDefault(x => x.Index == p.Index) is { } port)
                        port.CurrentDevice = p.DeviceName;
                vm.Refresh();
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Message = ex.Message;
        }
        finally
        {
            _busy = false;
        }
    }

    private void Rebuild()
    {
        var current = Controllers.SelectMany(c => c.Ports.Select(p => (c.InstanceId, p.Index, p.CurrentDevice))).ToList();
        Controllers.Clear();
        foreach (var c in _map.Controllers.Where(c => c.Ports.Count > 0))
        {
            var vm = new ControllerViewModel(c, () => _map.Save());
            foreach (var p in vm.Ports)
                p.CurrentDevice = current.FirstOrDefault(x => x.InstanceId == c.InstanceId && x.Index == p.Index).CurrentDevice;
            Controllers.Add(vm);
        }
        OnPropertyChanged(nameof(DiscoverText));
    }
}
