using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using MidiBridge.Models;
using MidiBridge.Services.NetworkMidi2;
using NAudio.Midi;
using Serilog;

namespace MidiBridge.Services;

public class MidiDeviceManager : IDisposable
{
    private readonly ConcurrentDictionary<string, MidiDevice> _devices = new();
    private readonly ConcurrentDictionary<int, MidiIn> _localInputs = new();
    private readonly ConcurrentDictionary<int, MidiOut> _localOutputs = new();
    private readonly MidiRouter _router;
    private readonly ConfigService _configService;
    
    private UdpClient? _rtpControlServer;
    private UdpClient? _rtpDataServer;
    private CancellationTokenSource? _cts;
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

    public ObservableCollection<MidiDevice> InputDevices { get; } = new();
    public ObservableCollection<MidiDevice> OutputDevices { get; } = new();
    public ObservableCollection<NetworkMidi2Protocol.DiscoveredDevice> DiscoveredNM2Devices { get; } = new();
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

    public MidiDeviceManager(ConfigService configService)
    {
        _configService = configService;
        _router = new MidiRouter(this, configService);
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

        for (int i = 0; i < MidiIn.NumberOfDevices; i++)
        {
            if (existingLocalInputs.Contains(i)) continue;

            var device = new MidiDevice
            {
                Id = $"local-in-{i}",
                Name = MidiIn.DeviceInfo(i).ProductName,
                Type = MidiDeviceType.LocalInput,
                Protocol = "MIDI 1.0",
                LocalDeviceId = i,
                Status = MidiDeviceStatus.Disconnected
            };

            AddDevice(device);
        }

        for (int i = 0; i < MidiOut.NumberOfDevices; i++)
        {
            if (existingLocalOutputs.Contains(i)) continue;

            var device = new MidiDevice
            {
                Id = $"local-out-{i}",
                Name = MidiOut.DeviceInfo(i).ProductName,
                Type = MidiDeviceType.LocalOutput,
                Protocol = "MIDI 1.0",
                LocalDeviceId = i,
                Status = MidiDeviceStatus.Disconnected
            };

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
            _cts = new CancellationTokenSource();

            _rtpControlServer = new UdpClient(rtpPort);
            _rtpDataServer = new UdpClient(rtpPort + 1);

            _isRunning = true;

            Task.Run(() => RtpControlLoop(_cts.Token));
            Task.Run(() => RtpDataLoop(_cts.Token));

            StartNetworkMidi2(nm2Port);

            OnStatusChanged($"网络服务已启动: RTP-MIDI {rtpPort}-{rtpPort+1}, Network MIDI 2.0 {nm2Port}");
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
        
        _networkMidi2Service.DeviceAdded += (s, device) =>
        {
            AddDeviceToCollections(device);
        };
        _networkMidi2Service.DeviceRemoved += (s, device) =>
        {
            RemoveDeviceFromCollections(device.Id);
        };
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
            try
            {
                System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                {
                    if (!DiscoveredNM2Devices.Any(d => d.Name == device.Name && d.Host == device.Host))
                    {
                        DiscoveredNM2Devices.Add(device);
                        OnStatusChanged($"发现设备: {device.Name} ({device.Host}:{device.Port})");
                    }
                });
            }
            catch { }
        };
        
        _mdnsDiscoveryService.DeviceLost += (s, device) =>
        {
            try
            {
                System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                {
                    var existing = DiscoveredNM2Devices.FirstOrDefault(d => d.Name == device.Name && d.Host == device.Host);
                    if (existing != null)
                    {
                        DiscoveredNM2Devices.Remove(existing);
                    }
                });
            }
            catch { }
        };
        
        _mdnsDiscoveryService.Start();
    }

    public void Stop()
    {
        Log.Information("[MidiDeviceManager] Stop() 被调用");
        _isRunning = false;
        _cts?.Cancel();

        Log.Debug("[MidiDeviceManager] 正在关闭 RTP 服务");
        _rtpControlServer?.Close();
        _rtpDataServer?.Close();
        _rtpControlServer?.Dispose();
        _rtpDataServer?.Dispose();

        Log.Debug("[MidiDeviceManager] 正在停止 Network MIDI 2.0 服务");
        _networkMidi2Service?.Stop();
        _networkMidi2Service?.Dispose();
        
        Log.Debug("[MidiDeviceManager] 正在停止 mDNS 发现服务");
        _mdnsDiscoveryService?.Stop();
        _mdnsDiscoveryService?.Dispose();

        Log.Debug("[MidiDeviceManager] 正在移除网络设备（保留路由）");
        var networkDevices = _devices.Values.Where(d => d.IsNetwork).ToList();
        
        try
        {
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                foreach (var device in networkDevices)
                {
                    InputDevices.Remove(device);
                    OutputDevices.Remove(device);
                }
            });
        }
        catch { }

        foreach (var device in networkDevices)
        {
            try
            {
                if (_devices.TryRemove(device.Id, out _))
                {
                    _router.OnDeviceDisconnected(device);
                    device.Dispose();
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[MidiDeviceManager] 移除设备失败 {DeviceId}", device.Id);
            }
        }

        try
        {
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                DiscoveredNM2Devices.Clear();
            });
        }
        catch (Exception ex)
        {
            Log.Debug("[MidiDeviceManager] Stop 期间 Dispatcher.Invoke 失败: {Message}", ex.Message);
        }

        Log.Information("[MidiDeviceManager] Stop() 完成");
        OnStatusChanged("网络服务已停止");
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

    public bool ConnectDevice(string deviceId)
    {
        if (!_devices.TryGetValue(deviceId, out var device)) return false;

        if (!device.IsEnabled)
        {
            Log.Debug("[MidiDeviceManager] 设备 {DeviceId} 已禁用，跳过连接", deviceId);
            return false;
        }

        try
        {
            if (device.Type == MidiDeviceType.LocalInput && device.LocalDeviceId.HasValue)
            {
                var midiIn = new MidiIn(device.LocalDeviceId.Value);
                midiIn.MessageReceived += (s, e) => OnLocalMidiMessage(device, e);
                midiIn.Start();
                _localInputs[device.LocalDeviceId.Value] = midiIn;
                device.Status = MidiDeviceStatus.Connected;
                device.ConnectedTime = DateTime.Now;
                DeviceUpdated?.Invoke(this, device);
                return true;
            }
            else if (device.Type == MidiDeviceType.LocalOutput && device.LocalDeviceId.HasValue)
            {
                var midiOut = new MidiOut(device.LocalDeviceId.Value);
                _localOutputs[device.LocalDeviceId.Value] = midiOut;
                device.Status = MidiDeviceStatus.Connected;
                device.ConnectedTime = DateTime.Now;
                DeviceUpdated?.Invoke(this, device);
                return true;
            }
        }
        catch (Exception ex)
        {
            device.Status = MidiDeviceStatus.Error;
            device.ErrorMessage = ex.Message;
            DeviceUpdated?.Invoke(this, device);
        }

        return false;
    }

    public void DisconnectDevice(string deviceId)
    {
        if (!_devices.TryGetValue(deviceId, out var device)) return;

        if (device.Type == MidiDeviceType.LocalInput && device.LocalDeviceId.HasValue)
        {
            if (_localInputs.TryRemove(device.LocalDeviceId.Value, out var midiIn))
            {
                midiIn.Stop();
                midiIn.Dispose();
            }
        }
        else if (device.Type == MidiDeviceType.LocalOutput && device.LocalDeviceId.HasValue)
        {
            if (_localOutputs.TryRemove(device.LocalDeviceId.Value, out var midiOut))
            {
                midiOut.Dispose();
            }
        }

        device.Status = MidiDeviceStatus.Disconnected;
        DeviceUpdated?.Invoke(this, device);
    }

    public void SendMidiMessage(MidiDevice target, byte[] data)
    {
        if (target.Type == MidiDeviceType.LocalOutput && target.LocalDeviceId.HasValue)
        {
            if (_localOutputs.TryGetValue(target.LocalDeviceId.Value, out var midiOut))
            {
                if (data.Length >= 3)
                {
                    // 3字节短消息
                    int message = (data[2] << 16) | (data[1] << 8) | data[0];
                    midiOut.Send(message);
                    target.SentMessages++;
                    target.LastActivity = DateTime.Now;
                    target.PulseTransmit();
                }
                else if (data.Length == 2)
                {
                    // 2字节消息 (Program Change, Channel Aftertouch)
                    int message = (data[1] << 8) | data[0];
                    midiOut.Send(message);
                    target.SentMessages++;
                    target.LastActivity = DateTime.Now;
                    target.PulseTransmit();
                }
                else if (data.Length == 1)
                {
                    // 1字节消息 (System Realtime: Timing Clock, Start, Stop, etc.)
                    midiOut.Send(data[0]);
                    target.SentMessages++;
                    target.LastActivity = DateTime.Now;
                    target.PulseTransmit();
                }
            }
        }
    }

    public void SendMidiBuffer(MidiDevice target, byte[] buffer)
    {
        if (target.Type == MidiDeviceType.LocalOutput && target.LocalDeviceId.HasValue)
        {
            if (_localOutputs.TryGetValue(target.LocalDeviceId.Value, out var midiOut))
            {
                // 用于发送 SysEx 等变长消息
                midiOut.SendBuffer(buffer);
                target.SentMessages++;
                target.LastActivity = DateTime.Now;
                target.PulseTransmit();
            }
        }
    }

    public void SendMidiShortMessage(MidiDevice target, int message)
    {
        if (target.Type == MidiDeviceType.LocalOutput && target.LocalDeviceId.HasValue)
        {
            if (_localOutputs.TryGetValue(target.LocalDeviceId.Value, out var midiOut))
            {
                midiOut.Send(message);
                target.SentMessages++;
                target.LastActivity = DateTime.Now;
                target.PulseTransmit();
            }
        }
    }

    private void OnLocalMidiMessage(MidiDevice device, NAudio.Midi.MidiInMessageEventArgs e)
    {
        device.ReceivedMessages++;
        device.LastActivity = DateTime.Now;
        device.Status = MidiDeviceStatus.Active;
        device.PulseTransmit();
        DeviceUpdated?.Invoke(this, device);

        if (e.MidiEvent != null)
        {
            byte[] data;
            
            // 检查是否是 SysEx 消息
            if (e.MidiEvent is NAudio.Midi.SysexEvent sysexEvent)
            {
                // SysEx 是变长消息，需要从原始缓冲区获取
                // NAudio 的 SysexEvent 没有 GetData 方法，需要用 RawData
                data = Array.Empty<byte>();
            }
            else
            {
                // 短消息 (1-3 字节)
                int msg = e.MidiEvent.GetAsShortMessage();
                if (msg == 0) return;
                
                byte status = (byte)(msg & 0xFF);
                byte data1 = (byte)((msg >> 8) & 0xFF);
                byte data2 = (byte)((msg >> 16) & 0xFF);
                
                // 根据消息类型确定数据长度
                if (status >= 0xF8)
                {
                    // 系统实时消息 (1字节)
                    data = new byte[] { status };
                }
                else if (status >= 0xF0 || (status & 0xF0) == 0xC0 || (status & 0xF0) == 0xD0)
                {
                    // 系统消息或 Program Change / Channel Aftertouch (2字节)
                    data = new byte[] { status, data1 };
                }
                else
                {
                    // 普通通道消息 (3字节)
                    data = new byte[] { status, data1, data2 };
                }
            }

            if (data.Length > 0)
            {
                MidiDataReceived?.Invoke(this, (device, data));
                _router.RouteMessage(device, data);
            }
        }
    }

    private async void RtpControlLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _rtpControlServer != null)
        {
            try
            {
                var result = await _rtpControlServer.ReceiveAsync();
                ProcessRtpControlPacket(result.Buffer, result.RemoteEndPoint);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { break; }
            catch (Exception ex)
            {
                if (!ct.IsCancellationRequested)
                    Log.Error(ex, "[RTP] 控制端口循环错误");
                break;
            }
        }
    }

    private async void RtpDataLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _rtpDataServer != null)
        {
            try
            {
                var result = await _rtpDataServer.ReceiveAsync();
                ProcessRtpDataPacket(result.Buffer, result.RemoteEndPoint);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { break; }
            catch (Exception ex)
            {
                if (!ct.IsCancellationRequested)
                    Log.Error(ex, "[RTP] 数据端口循环错误");
                break;
            }
        }
    }

    private void ProcessRtpControlPacket(byte[] data, IPEndPoint remoteEP)
    {
        if (data.Length < 4) return;

        string command = Encoding.ASCII.GetString(data, 2, 2);
        string host = remoteEP.Address.ToString();

        switch (command)
        {
            case "IN":
                HandleRtpInvitation(data, remoteEP);
                break;
            case "OK":
                {
                    var device = _devices.Values.FirstOrDefault(d => d.Type == MidiDeviceType.RtpMidi && d.Host == host);
                    if (device != null)
                    {
                        UpdateDeviceStatus(device.Id, MidiDeviceStatus.Connected);
                    }
                }
                break;
            case "BY":
                {
                    var device = _devices.Values.FirstOrDefault(d => d.Type == MidiDeviceType.RtpMidi && d.Host == host);
                    if (device != null)
                    {
                        RemoveDevice(device.Id);
                    }
                }
                break;
            case "CK":
                SendRtpSyncResponse(data, remoteEP);
                break;
        }
    }

    private void ProcessRtpDataPacket(byte[] data, IPEndPoint remoteEP)
    {
        if (data.Length < 4) return;

        string command = Encoding.ASCII.GetString(data, 2, 2);

        if (command == "CK")
        {
            SendRtpSyncResponse(data, remoteEP, isDataPort: true);
        }
        else if (data.Length >= 13)
        {
            ProcessRtpMidiData(data, remoteEP);
        }
    }

    private void HandleRtpInvitation(byte[] data, IPEndPoint remoteEP)
    {
        if (data.Length < 16) return;

        int nameLength = (data[12] << 8) | data[13];
        string name = nameLength > 0 && data.Length >= 16 + nameLength
            ? Encoding.ASCII.GetString(data, 16, nameLength)
            : "RTP-MIDI Device";

        int controlPort = remoteEP.Port;
        int dataPort = controlPort + 1;
        string host = remoteEP.Address.ToString();
        
        string safeName = string.Join("_", name.Split(Path.GetInvalidFileNameChars()));
        string deviceId = $"rtp-{safeName}@{host}";

        var device = new MidiDevice
        {
            Id = deviceId,
            Name = name,
            Type = MidiDeviceType.RtpMidi,
            Protocol = "RTP-MIDI",
            Host = host,
            Port = dataPort,
            ControlPort = controlPort,
            Status = MidiDeviceStatus.Connected,
            ConnectedTime = DateTime.Now
        };

        AddDevice(device);
        SendRtpInvitationAccept(remoteEP);
    }

    private void SendRtpInvitationAccept(IPEndPoint remoteEP)
    {
        try
        {
            var nameBytes = Encoding.ASCII.GetBytes("MidiBridge");
            var packet = new byte[16 + nameBytes.Length];

            packet[0] = 0xFF;
            packet[1] = 0xFF;
            packet[2] = (byte)'O';
            packet[3] = (byte)'K';

            WriteBigEndianUInt32(packet, 4, 2);
            WriteBigEndianUInt32(packet, 8, (uint)Random.Shared.Next());
            packet[12] = (byte)((nameBytes.Length >> 8) & 0xFF);
            packet[13] = (byte)(nameBytes.Length & 0xFF);

            Buffer.BlockCopy(nameBytes, 0, packet, 16, nameBytes.Length);

            _rtpControlServer?.Send(packet, packet.Length, remoteEP);
        }
        catch { }
    }

    private void SendRtpSyncResponse(byte[] data, IPEndPoint remoteEP, bool isDataPort = false)
    {
        try
        {
            var packet = new byte[12];
            packet[0] = 0xFF;
            packet[1] = 0xFF;
            packet[2] = (byte)'C';
            packet[3] = (byte)'K';
            WriteBigEndianUInt32(packet, 4, (uint)Random.Shared.Next());
            var count = BitConverter.GetBytes(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            if (BitConverter.IsLittleEndian) Array.Reverse(count);
            Buffer.BlockCopy(count, 0, packet, 8, 4);
            
            if (isDataPort)
            {
                _rtpDataServer?.Send(packet, packet.Length, remoteEP);
            }
            else
            {
                _rtpControlServer?.Send(packet, packet.Length, remoteEP);
            }
        }
        catch { }
    }

    private void ProcessRtpMidiData(byte[] data, IPEndPoint remoteEP)
    {
        string host = remoteEP.Address.ToString();
        var device = _devices.Values.FirstOrDefault(d => d.Type == MidiDeviceType.RtpMidi && d.Host == host);
        if (device == null) return;

        int midiOffset = 12;
        byte midiHeader = data[midiOffset];
        int midiLength = midiHeader & 0x0F;
        midiOffset++;

        if (midiOffset + midiLength <= data.Length)
        {
            byte[] midiData = new byte[midiLength];
            Buffer.BlockCopy(data, midiOffset, midiData, 0, midiLength);

            device.ReceivedMessages++;
            device.LastActivity = DateTime.Now;
            device.Status = MidiDeviceStatus.Active;
            device.PulseTransmit();
            DeviceUpdated?.Invoke(this, device);

            MidiDataReceived?.Invoke(this, (device, midiData));
            _router.RouteMessage(device, midiData);
        }
    }

    private void AddDevice(MidiDevice device)
    {
        device.IsEnabled = _configService.IsDeviceEnabled(device.Id);
        
        _devices[device.Id] = device;

        try
        {
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                if (device.IsInput && !InputDevices.Any(d => d.Id == device.Id))
                {
                    InsertDeviceSorted(InputDevices, device, _configService.GetInputDeviceOrder());
                }
                else if (device.IsOutput && !OutputDevices.Any(d => d.Id == device.Id))
                {
                    InsertDeviceSorted(OutputDevices, device, _configService.GetOutputDeviceOrder());
                }
            });
        }
        catch { }

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

        try
        {
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                if (device.IsInput && !InputDevices.Any(d => d.Id == device.Id))
                {
                    InsertDeviceSorted(InputDevices, device, _configService.GetInputDeviceOrder());
                }
                else if (device.IsOutput && !OutputDevices.Any(d => d.Id == device.Id))
                {
                    InsertDeviceSorted(OutputDevices, device, _configService.GetOutputDeviceOrder());
                }
            });
        }
        catch { }

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

    public void MoveDevice(string deviceId, int newIndex, bool isInput)
    {
        var devices = isInput ? InputDevices : OutputDevices;
        var device = devices.FirstOrDefault(d => d.Id == deviceId);
        if (device == null) return;

        int oldIndex = devices.IndexOf(device);
        if (oldIndex < 0 || oldIndex == newIndex) return;

        devices.Move(oldIndex, newIndex);
        SaveDeviceOrder();
    }

    public void SaveDeviceOrder()
    {
        var inputOrder = InputDevices.Select(d => d.Id).ToList();
        var outputOrder = OutputDevices.Select(d => d.Id).ToList();
        _configService.SaveDeviceOrder(inputOrder, outputOrder);
        Log.Debug("[MidiDeviceManager] 设备排序已保存");
    }

    public void SetDeviceEnabled(string deviceId, bool enabled)
    {
        if (!_devices.TryGetValue(deviceId, out var device)) return;

        device.IsEnabled = enabled;
        _configService.SetDeviceEnabled(deviceId, enabled);

        if (!enabled)
        {
            if (device.Status == MidiDeviceStatus.Connected || device.Status == MidiDeviceStatus.Active)
            {
                DisconnectDevice(deviceId);
            }
        }
        else
        {
            _router.TryRestoreRoutesForDevice(device);
        }

        Log.Information("[MidiDeviceManager] 设备 {DeviceId} {Status}", deviceId, enabled ? "已启用" : "已禁用");
    }

    private void RemoveDeviceFromCollections(string deviceId)
    {
        if (_devices.TryRemove(deviceId, out var device))
        {
            try
            {
                System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                {
                    InputDevices.Remove(device);
                    OutputDevices.Remove(device);
                });
            }
            catch { }

            _router.OnDeviceDisconnected(device);

            device.Dispose();
        }
    }

    private void RemoveDevice(string deviceId)
    {
        if (_devices.TryRemove(deviceId, out var device))
        {
            try
            {
                System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                {
                    InputDevices.Remove(device);
                    OutputDevices.Remove(device);
                });
            }
            catch { }

            _router.OnDeviceDisconnected(device);

            device.Dispose();
            DeviceRemoved?.Invoke(this, device);
        }
    }

    private void UpdateDeviceStatus(string deviceId, MidiDeviceStatus status)
    {
        if (_devices.TryGetValue(deviceId, out var device))
        {
            device.Status = status;
            DeviceUpdated?.Invoke(this, device);
        }
    }

    private void WriteBigEndianUInt32(byte[] data, int offset, uint value)
    {
        data[offset] = (byte)((value >> 24) & 0xFF);
        data[offset + 1] = (byte)((value >> 16) & 0xFF);
        data[offset + 2] = (byte)((value >> 8) & 0xFF);
        data[offset + 3] = (byte)(value & 0xFF);
    }

    private void OnStatusChanged(string message) => StatusChanged?.Invoke(this, message);

    public void Dispose()
    {
        Log.Information("[MidiDeviceManager] Dispose() 被调用");
        Stop();

        Log.Debug("[MidiDeviceManager] 正在释放本地 MIDI 设备");
        foreach (var midiIn in _localInputs.Values)
            midiIn.Dispose();
        foreach (var midiOut in _localOutputs.Values)
            midiOut.Dispose();

        _localInputs.Clear();
        _localOutputs.Clear();
        _cts?.Dispose();
        Log.Information("[MidiDeviceManager] Dispose() 完成");
    }
}