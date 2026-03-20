using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows.Input;
using MidiBridge.Models;
using MidiBridge.Services;
using MidiBridge.Services.Interfaces;
using MidiBridge.Services.NetworkMidi2;
using MidiBridge.Services.RtpMidi;
using Serilog;

namespace MidiBridge.ViewModels;

public class MainViewModel : ViewModelBase
{
    private readonly IMidiDeviceManager _deviceManager;
    private readonly IConfigService _configService;
    private readonly IMidiRouter _router;
    private string _statusMessage = "就绪";
    private MidiDevice? _selectedInputDevice;
    private MidiDevice? _selectedOutputDevice;
    private bool _isC4Pressed;
    private int _rtpPort = 5004;
    private int _nm2Port = 5506;
    private NetworkMidi2Protocol.DiscoveredDevice? _selectedDiscoveredDevice;
    private bool _rtpPortWarning;
    private bool _nm2PortWarning;
    private string _rtpPortWarningMessage = "";
    private string _nm2PortWarningMessage = "";
    private bool _discoveryPanelClosed;

    public ObservableCollection<MidiDevice> InputDevices => _deviceManager.InputDevices;
    public ObservableCollection<MidiDevice> OutputDevices => _deviceManager.OutputDevices;
    public ObservableCollection<MidiRoute> Routes => _router.Routes;
    public ObservableCollection<NetworkMidi2Protocol.DiscoveredDevice> DiscoveredNM2Devices => _deviceManager.DiscoveredNM2Devices;

    public IConfigService ConfigService => _configService;

    public int RtpPort
    {
        get => _rtpPort;
        set
        {
            if (SetProperty(ref _rtpPort, value))
            {
                OnPropertyChanged(nameof(RtpDataPort));
                ValidatePorts();
            }
        }
    }

    public int RtpDataPort => _rtpPort + 1;

    public int NM2Port
    {
        get => _nm2Port;
        set
        {
            if (SetProperty(ref _nm2Port, value))
            {
                ValidatePorts();
            }
        }
    }

    public bool RtpPortWarning
    {
        get => _rtpPortWarning;
        set => SetProperty(ref _rtpPortWarning, value);
    }

    public bool NM2PortWarning
    {
        get => _nm2PortWarning;
        set => SetProperty(ref _nm2PortWarning, value);
    }

    public string RtpPortWarningMessage
    {
        get => _rtpPortWarningMessage;
        set => SetProperty(ref _rtpPortWarningMessage, value);
    }

    public string NM2PortWarningMessage
    {
        get => _nm2PortWarningMessage;
        set => SetProperty(ref _nm2PortWarningMessage, value);
    }

    public NetworkMidi2Protocol.DiscoveredDevice? SelectedDiscoveredDevice
    {
        get => _selectedDiscoveredDevice;
        set => SetProperty(ref _selectedDiscoveredDevice, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }

    public bool IsNetworkRunning => _deviceManager.IsRunning;

    public bool ShowNM2DevicesPanel => _deviceManager.IsRunning && _deviceManager.DiscoveredNM2Devices.Count > 0 && !_discoveryPanelClosed;

    public string NetworkButtonText => _deviceManager.IsRunning ? "停止服务" : "启动服务";

    public ICommand ScanCommand { get; }
    public ICommand ToggleNetworkCommand { get; }
    public ICommand ConnectDeviceCommand { get; }
    public ICommand DisconnectDeviceCommand { get; }
    public ICommand CreateRouteCommand { get; }
    public ICommand RemoveRouteCommand { get; }
    public ICommand ClearRoutesCommand { get; }
    public ICommand C4DownCommand { get; }
    public ICommand C4UpCommand { get; }
    public ICommand RefreshNM2DiscoveryCommand { get; }
    public ICommand InviteNM2DeviceCommand { get; }
    public ICommand CloseDiscoveryPanelCommand { get; }
    public ICommand ToggleDeviceEnabledCommand { get; }

    public void ToggleRouteEnabled(string routeId)
    {
        var route = _router.Routes.FirstOrDefault(r => r.Id == routeId);
        if (route != null)
        {
            _router.EnableRoute(routeId, !route.IsEnabled);
        }
    }

    /// <summary>
    /// 构造函数（用于设计器）。
    /// </summary>
    public MainViewModel()
    {
        var configService = new ConfigService();
        var localMidiService = new LocalMidiService();
        var rtpMidiService = new RtpMidiService();
        var networkMidi2Service = new NetworkMidi2Service();
        var mdnsDiscoveryService = new MdnsDiscoveryService();
        
        _configService = configService;
        _deviceManager = new MidiDeviceManager(configService, localMidiService, rtpMidiService, networkMidi2Service, mdnsDiscoveryService);
        _router = _deviceManager.Router;
        
        _rtpPort = _configService.Config.Network.RtpPort;
        _nm2Port = _configService.Config.Network.NM2Port;

        _deviceManager.StatusChanged += (_, msg) => StatusMessage = msg;
        _deviceManager.DeviceAdded += OnDeviceAdded;
        _deviceManager.DeviceRemoved += OnDeviceRemoved;
        _deviceManager.DeviceUpdated += OnDeviceUpdated;
        _deviceManager.DiscoveredNM2Devices.CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Add)
            {
                _discoveryPanelClosed = false;
            }
            OnPropertyChanged(nameof(ShowNM2DevicesPanel));
        };

        ScanCommand = new RelayCommand(_ => ScanDevices());
        ToggleNetworkCommand = new RelayCommand(_ => ToggleNetwork());
        ConnectDeviceCommand = new RelayCommand<MidiDevice>(d => ConnectDevice(d));
        DisconnectDeviceCommand = new RelayCommand<MidiDevice>(d => DisconnectDevice(d));
        CreateRouteCommand = new RelayCommand(_ => CreateRoute(), _ => _selectedInputDevice != null && _selectedOutputDevice != null);
        RemoveRouteCommand = new RelayCommand<MidiRoute>(r => RemoveRoute(r));
        ClearRoutesCommand = new RelayCommand(_ => ClearAllRoutes());
        C4DownCommand = new RelayCommand(_ => SendC4On(), _ => HasConnectedOutput());
        C4UpCommand = new RelayCommand(_ => SendC4Off(), _ => _isC4Pressed);
        RefreshNM2DiscoveryCommand = new RelayCommand(_ => RefreshNM2Discovery(), _ => _deviceManager.IsRunning);
        InviteNM2DeviceCommand = new RelayCommand(_ => InviteNM2Device(), _ => _selectedDiscoveredDevice != null && _deviceManager.IsRunning);
        CloseDiscoveryPanelCommand = new RelayCommand(_ =>
        {
            _discoveryPanelClosed = true;
            OnPropertyChanged(nameof(ShowNM2DevicesPanel));
        });
        ToggleDeviceEnabledCommand = new RelayCommand<string>(id => ToggleDeviceEnabled(id));
    }

    /// <summary>
    /// 构造函数（用于依赖注入）。
    /// </summary>
    /// <param name="configService">配置服务。</param>
    /// <param name="deviceManager">设备管理器。</param>
    public MainViewModel(IConfigService configService, IMidiDeviceManager deviceManager)
    {
        _configService = configService ?? throw new ArgumentNullException(nameof(configService));
        _deviceManager = deviceManager ?? throw new ArgumentNullException(nameof(deviceManager));
        _router = _deviceManager.Router;

        _rtpPort = _configService.Config.Network.RtpPort;
        _nm2Port = _configService.Config.Network.NM2Port;

        _deviceManager.StatusChanged += (_, msg) => StatusMessage = msg;
        _deviceManager.DeviceAdded += OnDeviceAdded;
        _deviceManager.DeviceRemoved += OnDeviceRemoved;
        _deviceManager.DeviceUpdated += OnDeviceUpdated;
        _deviceManager.DiscoveredNM2Devices.CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Add)
            {
                _discoveryPanelClosed = false;
            }
            OnPropertyChanged(nameof(ShowNM2DevicesPanel));
        };

        ScanCommand = new RelayCommand(_ => ScanDevices());
        ToggleNetworkCommand = new RelayCommand(_ => ToggleNetwork());
        ConnectDeviceCommand = new RelayCommand<MidiDevice>(d => ConnectDevice(d));
        DisconnectDeviceCommand = new RelayCommand<MidiDevice>(d => DisconnectDevice(d));
        CreateRouteCommand = new RelayCommand(_ => CreateRoute(), _ => _selectedInputDevice != null && _selectedOutputDevice != null);
        RemoveRouteCommand = new RelayCommand<MidiRoute>(r => RemoveRoute(r));
        ClearRoutesCommand = new RelayCommand(_ => ClearAllRoutes());
        C4DownCommand = new RelayCommand(_ => SendC4On(), _ => HasConnectedOutput());
        C4UpCommand = new RelayCommand(_ => SendC4Off(), _ => _isC4Pressed);
        RefreshNM2DiscoveryCommand = new RelayCommand(_ => RefreshNM2Discovery(), _ => _deviceManager.IsRunning);
        InviteNM2DeviceCommand = new RelayCommand(_ => InviteNM2Device(), _ => _selectedDiscoveredDevice != null && _deviceManager.IsRunning);
        CloseDiscoveryPanelCommand = new RelayCommand(_ =>
        {
            _discoveryPanelClosed = true;
            OnPropertyChanged(nameof(ShowNM2DevicesPanel));
        });
        ToggleDeviceEnabledCommand = new RelayCommand<string>(id => ToggleDeviceEnabled(id));
    }

    public void Initialize()
    {
        _deviceManager.ScanLocalDevices();
        _router.TryRestoreAllRoutes();
        ValidatePorts();

        if (_configService.Config.Network.AutoStart)
        {
            TryAutoStartService();
        }
    }

    private void TryAutoStartService()
    {
        if (RtpPortWarning || NM2PortWarning)
        {
            StatusMessage = "端口被占用，自动启动失败";
            Log.Warning("[MainViewModel] 自动启动失败：端口冲突");
            return;
        }

        if (_deviceManager.Start(_rtpPort, _nm2Port))
        {
            StatusMessage = $"自动启动服务 (RTP: {_rtpPort}-{RtpDataPort}, NM2: {_nm2Port})";
            OnPropertyChanged(nameof(IsNetworkRunning));
            OnPropertyChanged(nameof(NetworkButtonText));
        }
    }

    private void ValidatePorts()
    {
        var (_, _, rtpError, nm2Error) = PortChecker.CheckPorts(_rtpPort, _nm2Port);
        
        RtpPortWarning = !string.IsNullOrEmpty(rtpError);
        RtpPortWarningMessage = RtpPortWarning ? rtpError : "";
        
        NM2PortWarning = !string.IsNullOrEmpty(nm2Error);
        NM2PortWarningMessage = NM2PortWarning ? nm2Error : "";
    }

    private void OnDeviceAdded(object? sender, MidiDevice device)
    {
        StatusMessage = $"设备添加: {device.DisplayName}";
    }

    private void OnDeviceRemoved(object? sender, MidiDevice device)
    {
        StatusMessage = $"设备移除: {device.DisplayName}";
    }

    private void OnDeviceUpdated(object? sender, MidiDevice device)
    {
        OnPropertyChanged(nameof(InputDevices));
        OnPropertyChanged(nameof(OutputDevices));
    }

    public void SelectInputDevice(string deviceId)
    {
        var device = InputDevices.FirstOrDefault(d => d.Id == deviceId);
        if (device == null) return;

        if (_selectedInputDevice != null)
            _selectedInputDevice.IsSelected = false;

        if (_selectedInputDevice == device)
        {
            _selectedInputDevice = null;
            StatusMessage = "已取消选择输入设备";
        }
        else
        {
            _selectedInputDevice = device;
            device.IsSelected = true;

            if (device.Status == MidiDeviceStatus.Disconnected)
            {
                _deviceManager.ConnectDevice(device.Id);
            }

            if (_selectedOutputDevice != null)
            {
                CreateRoute();
            }
            else
            {
                StatusMessage = $"已选择输入: {device.Name}，请选择输出设备";
            }
        }
    }

    public void SelectOutputDevice(string deviceId)
    {
        var device = OutputDevices.FirstOrDefault(d => d.Id == deviceId);
        if (device == null) return;

        if (_selectedOutputDevice != null)
            _selectedOutputDevice.IsSelected = false;

        if (_selectedOutputDevice == device)
        {
            _selectedOutputDevice = null;
            StatusMessage = "已取消选择输出设备";
        }
        else
        {
            _selectedOutputDevice = device;
            device.IsSelected = true;

            if (device.Status == MidiDeviceStatus.Disconnected)
            {
                _deviceManager.ConnectDevice(device.Id);
            }

            if (_selectedInputDevice != null)
            {
                CreateRoute();
            }
            else
            {
                StatusMessage = $"已选择输出: {device.Name}，请选择输入设备";
            }
        }
    }

    private void ScanDevices()
    {
        _deviceManager.ScanLocalDevices();
        StatusMessage = "本地设备扫描完成";
    }

    private void ToggleNetwork()
    {
        if (_deviceManager.IsRunning)
        {
            _deviceManager.Stop();
            _discoveryPanelClosed = false;
            StatusMessage = "网络服务已停止";
        }
        else
        {
            ValidatePorts();

            if (RtpPortWarning || NM2PortWarning)
            {
                StatusMessage = "端口被占用，无法启动服务";
                return;
            }

            if (_deviceManager.Start(_rtpPort, _nm2Port))
            {
                _discoveryPanelClosed = false;
                _deviceManager.ScanLocalDevices();
                StatusMessage = $"网络服务已启动 (RTP-MIDI: {_rtpPort}-{RtpDataPort}, Network MIDI 2.0: {_nm2Port})";
            }
        }

        OnPropertyChanged(nameof(IsNetworkRunning));
        OnPropertyChanged(nameof(NetworkButtonText));
        OnPropertyChanged(nameof(ShowNM2DevicesPanel));
    }

    private void ConnectDevice(MidiDevice? device)
    {
        if (device == null) return;

        if (_deviceManager.ConnectDevice(device.Id))
        {
            StatusMessage = $"已连接: {device.Name}";
        }
        else
        {
            StatusMessage = $"连接失败: {device.Name}";
        }
    }

    private void DisconnectDevice(MidiDevice? device)
    {
        if (device == null) return;

        _deviceManager.DisconnectDevice(device.Id);
        StatusMessage = $"已断开: {device.Name}";
    }

    private void CreateRoute()
    {
        if (_selectedInputDevice == null || _selectedOutputDevice == null) return;

        if (_router.HasRoute(_selectedInputDevice, _selectedOutputDevice))
        {
            StatusMessage = "路由已存在";
            ClearSelection();
            return;
        }

        var route = _router.CreateRoute(_selectedInputDevice, _selectedOutputDevice);
        if (route != null)
        {
            StatusMessage = $"路由创建: {route.DisplayName}";
            OnPropertyChanged(nameof(Routes));
        }

        ClearSelection();
    }

    private void RemoveRoute(MidiRoute? route)
    {
        if (route == null) return;

        _router.RemoveRoute(route.Id);
        StatusMessage = $"路由删除: {route.DisplayName}";
        OnPropertyChanged(nameof(Routes));
    }

    private void ClearAllRoutes()
    {
        _router.ClearAllRoutes();
        StatusMessage = "已清除所有路由";
        OnPropertyChanged(nameof(Routes));
    }

    private void ClearSelection()
    {
        if (_selectedInputDevice != null)
        {
            _selectedInputDevice.IsSelected = false;
            _selectedInputDevice = null;
        }
        if (_selectedOutputDevice != null)
        {
            _selectedOutputDevice.IsSelected = false;
            _selectedOutputDevice = null;
        }
    }

    private bool HasConnectedOutput()
    {
        return OutputDevices.Any(d => d.Status == MidiDeviceStatus.Connected || d.Status == MidiDeviceStatus.Active);
    }

    private void SendC4On()
    {
        if (_isC4Pressed) return;
        _isC4Pressed = true;

        var targets = OutputDevices.Where(d => d.Status == MidiDeviceStatus.Connected || d.Status == MidiDeviceStatus.Active);
        foreach (var target in targets)
        {
            byte[] noteOn = { (byte)0x90, 60, 100 };
            _deviceManager.SendMidiMessage(target, noteOn);
        }
    }

    private void SendC4Off()
    {
        if (!_isC4Pressed) return;
        _isC4Pressed = false;

        var targets = OutputDevices.Where(d => d.Status == MidiDeviceStatus.Connected || d.Status == MidiDeviceStatus.Active);
        foreach (var target in targets)
        {
            byte[] noteOff = { (byte)0x80, 60, 0 };
            _deviceManager.SendMidiMessage(target, noteOff);
        }
    }

    private void RefreshNM2Discovery()
    {
        _deviceManager.RefreshNM2Discovery();
        StatusMessage = "正在搜索 Network MIDI 2.0 设备...";
    }

    private async void InviteNM2Device()
    {
        if (_selectedDiscoveredDevice == null) return;

        var device = _selectedDiscoveredDevice;
        StatusMessage = $"正在连接 {device.Name}...";

        var success = await _deviceManager.InviteNM2Device(device.Host, device.Port, device.Name);

        if (success)
        {
            StatusMessage = $"已连接到 {device.Name}";
            var connected = _deviceManager.DiscoveredNM2Devices.FirstOrDefault(d => 
                d.Name == device.Name && d.Host == device.Host);
            if (connected != null)
            {
                _deviceManager.DiscoveredNM2Devices.Remove(connected);
            }
        }
        else
        {
            StatusMessage = $"连接 {device.Name} 失败";
        }

        SelectedDiscoveredDevice = null;
    }

    public void SaveConfig(double left, double top, double width, double height, bool isMaximized)
    {
        _configService.UpdateWindowPosition(left, top, width, height, isMaximized);
        _configService.UpdateNetworkSettings(_rtpPort, _nm2Port, _deviceManager.IsRunning);
        _configService.Save();
        Log.Information("[MainViewModel] 配置已保存: RTP={RtpPort}, NM2={NM2Port}, 自动启动={AutoStart}",
            _rtpPort, _nm2Port, _deviceManager.IsRunning);
    }

    public void SaveDeviceOrder()
    {
        _deviceManager.SaveDeviceOrder();
    }

    private void ToggleDeviceEnabled(string? deviceId)
    {
        if (string.IsNullOrEmpty(deviceId)) return;

        var inputDevice = _deviceManager.InputDevices.FirstOrDefault(d => d.Id == deviceId);
        var outputDevice = _deviceManager.OutputDevices.FirstOrDefault(d => d.Id == deviceId);
        var device = inputDevice ?? outputDevice;

        if (device != null)
        {
            _deviceManager.SetDeviceEnabled(deviceId, !device.IsEnabled);
        }
    }

    public void Cleanup()
    {
        Log.Information("[MainViewModel] Cleanup() 被调用");
        if (_isC4Pressed) SendC4Off();
        _deviceManager.Dispose();
        Log.Information("[MainViewModel] Cleanup() 完成");
    }
}

public record DeviceInfo(int Id, string Name)
{
    public override string ToString() => Name;
}