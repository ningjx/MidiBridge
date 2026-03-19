using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using MidiBridge.Models;
using MidiBridge.Services.Interfaces;
using MidiBridge.Services.NetworkMidi2;
using Serilog;

namespace MidiBridge.Services;

/// <summary>
/// MIDI 设备管理器实现，作为设备协调器整合各服务。
/// </summary>
public class MidiDeviceManager : IMidiDeviceManager
{
    private readonly ConcurrentDictionary<string, MidiDevice> _devices = new();
    private readonly MidiRouter _router;
    private readonly IConfigService _configService;
    private readonly ILocalMidiService _localMidiService;
    private readonly IRtpMidiService _rtpMidiService;

    private bool _isRunning;
    private int _rtpPort = 5004;
    private int _nm2Port = 5506;

    private NetworkMidi2Service? _networkMidi2Service;
    private MdnsDiscoveryService? _mdnsDiscoveryService;

    public event EventHandler<MidiDevice>? DeviceAdded;
    public event EventHandler<MidiDevice>? DeviceRemoved;
    public event EventHandler<MidiDevice>? DeviceUpdated;
    public event EventHandler<string>? StatusChanged;
    public event EventHandler<(MidiDevice Device, byte[] Data)>? MidiDataReceived;

    public SafeObservableCollection<MidiDevice> InputDevices { get; } = new();
    public SafeObservableCollection<MidiDevice> OutputDevices { get; } = new();
    public SafeObservableCollection<NetworkMidi2Protocol.DiscoveredDevice> DiscoveredNM2Devices { get; } = new();

    ObservableCollection<MidiDevice> IMidiDeviceManager.InputDevices => InputDevices;
    ObservableCollection<MidiDevice> IMidiDeviceManager.OutputDevices => OutputDevices;
    ObservableCollection<NetworkMidi2Protocol.DiscoveredDevice> IMidiDeviceManager.DiscoveredNM2Devices => DiscoveredNM2Devices;

    IMidiRouter IMidiDeviceManager.Router => _router;
    public MidiRouter Router => _router;

    public bool IsRunning => _isRunning;

    public int RtpPort
    {
        get => _rtpPort;
        set => _rtpPort = value;
    }

    public int NM2Port
    {
        get => _nm2Port;
        set => _nm2Port = value;
    }

    public MidiDeviceManager(
        IConfigService configService,
        ILocalMidiService localMidiService,
        IRtpMidiService rtpMidiService)
    {
        _configService = configService ?? throw new ArgumentNullException(nameof(configService));
        _localMidiService = localMidiService ?? throw new ArgumentNullException(nameof(localMidiService));
        _rtpMidiService = rtpMidiService ?? throw new ArgumentNullException(nameof(rtpMidiService));

        _router = new MidiRouter(this, configService);

        SetupLocalMidiServiceEvents();
        SetupRtpMidiServiceEvents();
    }

    private void SetupLocalMidiServiceEvents()
    {
        _localMidiService.MidiDataReceived += (s, args) =>
        {
            MidiDataReceived?.Invoke(this, args);
            _router.RouteMessage(args.Device, args.Data);
        };
    }

    private void SetupRtpMidiServiceEvents()
    {
        _rtpMidiService.DeviceAdded += (s, device) => AddDeviceToCollections(device);
        _rtpMidiService.DeviceRemoved += (s, device) => RemoveDeviceFromCollections(device.Id);
        _rtpMidiService.MidiDataReceived += (s, args) =>
        {
            MidiDataReceived?.Invoke(this, args);
            _router.RouteMessage(args.Device, args.Data);
        };
        _rtpMidiService.StatusChanged += (s, msg) => OnStatusChanged(msg);
    }

    public void ScanLocalDevices()
    {
        var existingLocalInputs = _devices.Values
            .Where(d => d.Type == MidiDeviceType.LocalInput)
            .Select(d => d.LocalDeviceId)
            .ToHashSet();

        var existingLocalOutputs = _devices.Values
            .Where(d => d.Type == MidiDeviceType.LocalOutput)
            .Select(d => d.LocalDeviceId)
            .ToHashSet();

        foreach (var device in _localMidiService.ScanDevices())
        {
            if (device.Type == MidiDeviceType.LocalInput && existingLocalInputs.Contains(device.LocalDeviceId))
                continue;
            if (device.Type == MidiDeviceType.LocalOutput && existingLocalOutputs.Contains(device.LocalDeviceId))
                continue;

            AddDevice(device);
        }

        OnStatusChanged($"本地设备扫描完成: {InputDevices.Count(d => d.Type == MidiDeviceType.LocalInput)} 输入, {OutputDevices.Count(d => d.Type == MidiDeviceType.LocalOutput)} 输出");
    }

    public bool Start(int rtpPort = 5004, int nm2Port = 5506)
    {
        if (_isRunning) Stop();

        try
        {
            _rtpPort = rtpPort;
            _nm2Port = nm2Port;

            _rtpMidiService.Start(rtpPort);
            StartNetworkMidi2(nm2Port);

            _isRunning = true;
            OnStatusChanged($"网络服务已启动: RTP-MIDI {rtpPort}-{rtpPort + 1}, Network MIDI 2.0 {nm2Port}");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[MidiDeviceManager] 启动失败");
            OnStatusChanged($"启动失败: {ex.Message}");
            return false;
        }
    }

    private void StartNetworkMidi2(int port)
    {
        _networkMidi2Service = new NetworkMidi2Service();
        _networkMidi2Service.SetServiceInfo("MidiBridge", Guid.NewGuid().ToString("N").Substring(0, 16));

        _networkMidi2Service.DeviceAdded += (s, device) => AddDeviceToCollections(device);
        _networkMidi2Service.DeviceRemoved += (s, device) => RemoveDeviceFromCollections(device.Id);
        _networkMidi2Service.MidiDataReceived += (s, args) =>
        {
            MidiDataReceived?.Invoke(this, args);
            _router.RouteMessage(args.Device, args.Data);
        };
        _networkMidi2Service.StatusChanged += (s, msg) => OnStatusChanged(msg);

        _networkMidi2Service.Start(port);

        _mdnsDiscoveryService = new MdnsDiscoveryService();
        _mdnsDiscoveryService.SetServiceInfo("MidiBridge", port, Guid.NewGuid().ToString("N").Substring(0, 16));

        _mdnsDiscoveryService.DeviceDiscovered += (s, device) =>
        {
            DispatcherService.RunOnUIThread(() =>
            {
                if (!DiscoveredNM2Devices.Any(d => d.Name == device.Name && d.Host == device.Host))
                {
                    DiscoveredNM2Devices.AddSafe(device);
                    OnStatusChanged($"发现设备: {device.Name} ({device.Host}:{device.Port})");
                }
            });
        };

        _mdnsDiscoveryService.DeviceLost += (s, device) =>
        {
            DispatcherService.RunOnUIThread(() =>
            {
                var existing = DiscoveredNM2Devices.FirstOrDefault(d => d.Name == device.Name && d.Host == device.Host);
                if (existing != null)
                {
                    DiscoveredNM2Devices.RemoveSafe(existing);
                }
            });
        };

        _mdnsDiscoveryService.Start();
    }

    public void Stop()
    {
        Log.Information("[MidiDeviceManager] Stop() 被调用");
        _isRunning = false;

        _rtpMidiService.Stop();

        _networkMidi2Service?.Stop();
        _networkMidi2Service?.Dispose();

        _mdnsDiscoveryService?.Stop();
        _mdnsDiscoveryService?.Dispose();

        RemoveNetworkDevices();

        DiscoveredNM2Devices.ClearSafe();

        OnStatusChanged("网络服务已停止");
    }

    private void RemoveNetworkDevices()
    {
        var networkDevices = _devices.Values.Where(d => d.IsNetwork).ToList();

        foreach (var device in networkDevices)
        {
            InputDevices.RemoveSafe(device);
            OutputDevices.RemoveSafe(device);
        }

        foreach (var device in networkDevices)
        {
            if (_devices.TryRemove(device.Id, out _))
            {
                _router.OnDeviceDisconnected(device);
                device.Dispose();
            }
        }
    }

    public bool ConnectDevice(string deviceId)
    {
        if (!_devices.TryGetValue(deviceId, out var device)) return false;
        if (!device.IsEnabled) return false;

        if (device.Type == MidiDeviceType.LocalInput || device.Type == MidiDeviceType.LocalOutput)
        {
            if (_localMidiService.Connect(device))
            {
                DeviceUpdated?.Invoke(this, device);
                return true;
            }
        }

        return false;
    }

    public void DisconnectDevice(string deviceId)
    {
        if (!_devices.TryGetValue(deviceId, out var device)) return;

        if (device.Type == MidiDeviceType.LocalInput || device.Type == MidiDeviceType.LocalOutput)
        {
            _localMidiService.Disconnect(device);
            DeviceUpdated?.Invoke(this, device);
        }
    }

    public void SendMidiMessage(MidiDevice target, byte[] data)
    {
        if (target.Type == MidiDeviceType.LocalOutput)
        {
            _localMidiService.SendMessage(target, data);
        }
        else if (target.Type == MidiDeviceType.RtpMidi)
        {
            _rtpMidiService.SendMessage(target, data);
        }
        else if (target.Type == MidiDeviceType.NetworkMidi2)
        {
            var sessionId = GetNM2SessionId(target);
            _networkMidi2Service?.SendMidiData(sessionId, data);
            target.SentMessages++;
            target.LastActivity = DateTime.Now;
            target.PulseTransmit();
        }
    }

    public void SendMidiBuffer(MidiDevice target, byte[] buffer)
    {
        if (target.Type == MidiDeviceType.LocalOutput)
        {
            _localMidiService.SendBuffer(target, buffer);
        }
    }

    public void SendMidiShortMessage(MidiDevice target, int message)
    {
        if (target.Type == MidiDeviceType.LocalOutput)
        {
            _localMidiService.SendShortMessage(target, message);
        }
    }

    public async Task<bool> InviteNM2Device(string host, int port, string? name = null)
    {
        if (_networkMidi2Service == null) return false;
        return await _networkMidi2Service.InviteDevice(host, port, name);
    }

    public void EndNM2Session(string sessionId)
    {
        _networkMidi2Service?.EndSession(sessionId);
    }

    public void RefreshNM2Discovery()
    {
        _mdnsDiscoveryService?.QueryServices();
    }

    public void MoveDevice(string deviceId, int newIndex, bool isInput)
    {
        var devices = isInput ? InputDevices : OutputDevices;
        var device = devices.FirstOrDefault(d => d.Id == deviceId);
        if (device == null) return;

        int oldIndex = devices.IndexOf(device);
        if (oldIndex < 0 || oldIndex == newIndex) return;

        devices.MoveSafe(oldIndex, newIndex);
        SaveDeviceOrder();
    }

    public void SaveDeviceOrder()
    {
        var inputOrder = InputDevices.Select(d => d.Id).ToList();
        var outputOrder = OutputDevices.Select(d => d.Id).ToList();
        _configService.SaveDeviceOrder(inputOrder, outputOrder);
    }

    public void SetDeviceEnabled(string deviceId, bool enabled)
    {
        if (!_devices.TryGetValue(deviceId, out var device)) return;

        device.IsEnabled = enabled;
        _configService.SetDeviceEnabled(deviceId, enabled);

        if (!enabled && (device.Status == MidiDeviceStatus.Connected || device.Status == MidiDeviceStatus.Active))
        {
            DisconnectDevice(deviceId);
        }
        else if (enabled)
        {
            _router.TryRestoreRoutesForDevice(device);
        }
    }

    private void AddDevice(MidiDevice device)
    {
        device.IsEnabled = _configService.IsDeviceEnabled(device.Id);
        _devices[device.Id] = device;

        if (device.IsInput && !InputDevices.Any(d => d.Id == device.Id))
        {
            InsertDeviceSorted(InputDevices, device, _configService.GetInputDeviceOrder());
        }
        else if (device.IsOutput && !OutputDevices.Any(d => d.Id == device.Id))
        {
            InsertDeviceSorted(OutputDevices, device, _configService.GetOutputDeviceOrder());
        }

        DeviceAdded?.Invoke(this, device);

        if (device.IsNetwork && device.IsEnabled)
        {
            _router.TryRestoreRoutesForDevice(device);
        }
    }

    private void AddDeviceToCollections(MidiDevice device)
    {
        device.IsEnabled = _configService.IsDeviceEnabled(device.Id);
        _devices[device.Id] = device;

        if (device.IsInput && !InputDevices.Any(d => d.Id == device.Id))
        {
            InsertDeviceSorted(InputDevices, device, _configService.GetInputDeviceOrder());
        }
        else if (device.IsOutput && !OutputDevices.Any(d => d.Id == device.Id))
        {
            InsertDeviceSorted(OutputDevices, device, _configService.GetOutputDeviceOrder());
        }

        if (device.IsEnabled)
        {
            _router.TryRestoreRoutesForDevice(device);
        }
    }

    private void InsertDeviceSorted(ObservableCollection<MidiDevice> devices, MidiDevice device, List<string> order)
    {
        int savedIndex = order.IndexOf(device.Id);
        if (savedIndex >= 0)
        {
            int insertIndex = 0;
            for (int i = 0; i < savedIndex && insertIndex < devices.Count; i++)
            {
                if (order.IndexOf(devices[insertIndex].Id) < savedIndex)
                {
                    insertIndex++;
                }
            }
            while (insertIndex < devices.Count && order.IndexOf(devices[insertIndex].Id) < savedIndex && order.IndexOf(devices[insertIndex].Id) >= 0)
            {
                insertIndex++;
            }
            devices.Insert(insertIndex, device);
        }
        else
        {
            devices.Add(device);
        }
    }

    private void RemoveDeviceFromCollections(string deviceId)
    {
        if (_devices.TryRemove(deviceId, out var device))
        {
            InputDevices.RemoveSafe(device);
            OutputDevices.RemoveSafe(device);

            _router.OnDeviceDisconnected(device);
            device.Dispose();
        }
    }

    private static string GetNM2SessionId(MidiDevice device)
    {
        return $"nm2-{device.Host}-{device.Port}";
    }

    private void OnStatusChanged(string message) => StatusChanged?.Invoke(this, message);

    public void Dispose()
    {
        Log.Information("[MidiDeviceManager] Dispose() 被调用");
        Stop();
        _localMidiService.Dispose();
        _rtpMidiService.Dispose();
        Log.Information("[MidiDeviceManager] Dispose() 完成");
    }
}