using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Net;
using System.Net.Sockets;
using System.Text;
using MidiBridge.Models;
using MidiBridge.Services.Interfaces;
using Serilog;

namespace MidiBridge.Services.RtpMidi;

public class RtpMidiService : IRtpMidiService
{
    private const int SYNC_INTERVAL_MS = 10000;
    private const int DEVICE_TIMEOUT_MS = 60000;
    private const int DEVICE_CHECK_INTERVAL_MS = 2000;
    private const int GUARD_INTERVAL_MS = 1000;
    private const int SAMPLE_RATE = 44100;

    private readonly ConcurrentDictionary<string, MidiDevice> _devices = new();
    private readonly ConcurrentDictionary<string, IPEndPoint> _deviceEndpoints = new();
    private readonly ConcurrentDictionary<string, DateTime> _deviceLastActivity = new();
    private readonly ConcurrentDictionary<string, ushort> _deviceSequence = new();
    private readonly ConcurrentDictionary<string, RecoveryJournal> _deviceJournals = new();
    private readonly ConcurrentDictionary<string, RecoveryJournalReceiver> _deviceReceivers = new();
    private readonly ConcurrentDictionary<string, ushort> _deviceExpectedSeq = new();
    private readonly ConcurrentDictionary<string, DateTime> _deviceLastMidiSent = new();
    private readonly ConcurrentDictionary<string, uint> _deviceRemoteSSRC = new();

    private UdpClient? _controlServer;
    private UdpClient? _dataServer;
    private CancellationTokenSource? _cts;
    private bool _isRunning;
    private int _controlPort = 5004;
    private uint _localSSRC;
    private long _startTimeTicks;

    private RtpMidiDiscoveryService? _discoveryService;

    public ObservableCollection<MidiDevice> InputDevices { get; } = new();
    public bool IsRunning => _isRunning;
    public int ControlPort
    {
        get => _controlPort;
        set => _controlPort = value;
    }

    public event EventHandler<MidiDevice>? DeviceAdded;
    public event EventHandler<MidiDevice>? DeviceRemoved;
    public event EventHandler<MidiDevice>? DeviceUpdated;
    public event EventHandler<(MidiDevice Device, byte[] Data)>? MidiDataReceived;
    public event EventHandler<string>? StatusChanged;

    public RtpMidiService()
    {
        _localSSRC = (uint)Random.Shared.Next(1, int.MaxValue);
    }

    public bool Start(int controlPort = 5004)
    {
        if (_isRunning) Stop();

        try
        {
            _controlPort = controlPort;
            _cts = new CancellationTokenSource();
            _startTimeTicks = DateTimeOffset.UtcNow.Ticks;

            _controlServer = new UdpClient(controlPort);
            _dataServer = new UdpClient(controlPort + 1);

            _isRunning = true;

            _discoveryService = new RtpMidiDiscoveryService();
            _discoveryService.SetServiceInfo("MidiBridge", controlPort);
            _discoveryService.DeviceDiscovered += OnDeviceDiscovered;
            _discoveryService.DeviceLost += OnDeviceLost;
            _discoveryService.Start();

            Task.Run(() => ControlLoop(_cts.Token));
            Task.Run(() => DataLoop(_cts.Token));
            Task.Run(() => SyncLoop(_cts.Token));
            Task.Run(() => DeviceTimeoutCheckLoop(_cts.Token));
            Task.Run(() => GuardPacketLoop(_cts.Token));

            OnStatusChanged($"RTP-MIDI 服务已启动: 端口 {controlPort}-{controlPort + 1}");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[RtpMidi] 启动失败");
            OnStatusChanged($"RTP-MIDI 启动失败: {ex.Message}");
            return false;
        }
    }

    private void OnDeviceDiscovered(object? sender, RtpMidiDiscoveryService.DiscoveredRtpDevice discovered)
    {
        string deviceId = $"rtp-{discovered.Name}@{discovered.Host}";

        if (_devices.ContainsKey(deviceId)) return;

        var device = new MidiDevice
        {
            Id = deviceId,
            Name = discovered.Name,
            Type = MidiDeviceType.RtpMidi,
            Protocol = "RTP-MIDI",
            Host = discovered.Host,
            Port = discovered.Port,
            ControlPort = discovered.Port - 1,
            Status = MidiDeviceStatus.Connected,
            ConnectedTime = DateTime.Now
        };

        _devices[deviceId] = device;
        _deviceEndpoints[deviceId] = new IPEndPoint(IPAddress.Parse(discovered.Host), discovered.Port);
        _deviceLastActivity[deviceId] = DateTime.Now;
        _deviceSequence[deviceId] = 0;
        _deviceJournals[deviceId] = new RecoveryJournal();
        _deviceReceivers[deviceId] = new RecoveryJournalReceiver();
        _deviceExpectedSeq[deviceId] = 0;
        _deviceLastMidiSent[deviceId] = DateTime.MinValue;

        DeviceAdded?.Invoke(this, device);
        Log.Information("[RtpMidi] mDNS 发现设备: {Name} ({Host}:{Port})", discovered.Name, discovered.Host, discovered.Port);
    }

    private void OnDeviceLost(object? sender, RtpMidiDiscoveryService.DiscoveredRtpDevice lost)
    {
        string deviceId = $"rtp-{lost.Name}@{lost.Host}";

        if (_devices.TryRemove(deviceId, out var device))
        {
            CleanupDevice(device.Id);
            DeviceRemoved?.Invoke(this, device);
        }
    }

    private void CleanupDevice(string deviceId)
    {
        _deviceEndpoints.TryRemove(deviceId, out _);
        _deviceLastActivity.TryRemove(deviceId, out _);
        _deviceSequence.TryRemove(deviceId, out _);
        _deviceJournals.TryRemove(deviceId, out _);
        _deviceReceivers.TryRemove(deviceId, out _);
        _deviceExpectedSeq.TryRemove(deviceId, out _);
        _deviceLastMidiSent.TryRemove(deviceId, out _);
        _deviceRemoteSSRC.TryRemove(deviceId, out _);
    }

    public void Stop()
    {
        if (!_isRunning) return;

        _isRunning = false;
        _cts?.Cancel();

        if (_discoveryService != null)
        {
            _discoveryService.DeviceDiscovered -= OnDeviceDiscovered;
            _discoveryService.DeviceLost -= OnDeviceLost;
            _discoveryService.Dispose();
            _discoveryService = null;
        }

        _controlServer?.Close();
        _dataServer?.Close();
        _controlServer?.Dispose();
        _dataServer?.Dispose();

        _devices.Clear();
        _deviceEndpoints.Clear();
        _deviceLastActivity.Clear();
        _deviceSequence.Clear();
        _deviceJournals.Clear();
        _deviceReceivers.Clear();
        _deviceExpectedSeq.Clear();
        _deviceLastMidiSent.Clear();
        _deviceRemoteSSRC.Clear();

        OnStatusChanged("RTP-MIDI 服务已停止");
    }

    public async Task<bool> ConnectAsync(string host, int port, string name = "MidiBridge")
    {
        if (_dataServer == null) return false;

        try
        {
            var ip = IPAddress.Parse(host);
            var controlEp = new IPEndPoint(ip, port);
            var dataEp = new IPEndPoint(ip, port + 1);

            var invitation = CreateInvitationPacket(name);
            _controlServer?.Send(invitation, invitation.Length, controlEp);

            await Task.Delay(100);

            var device = new MidiDevice
            {
                Id = $"rtp-{name}@{host}",
                Name = name,
                Type = MidiDeviceType.RtpMidi,
                Protocol = "RTP-MIDI",
                Host = host,
                Port = port + 1,
                ControlPort = port,
                Status = MidiDeviceStatus.Connecting,
                ConnectedTime = DateTime.Now
            };

            _devices[device.Id] = device;
            _deviceEndpoints[device.Id] = dataEp;
            _deviceLastActivity[device.Id] = DateTime.Now;
            _deviceSequence[device.Id] = 0;
            _deviceJournals[device.Id] = new RecoveryJournal();
            _deviceReceivers[device.Id] = new RecoveryJournalReceiver();
            _deviceExpectedSeq[device.Id] = 0;
            _deviceLastMidiSent[device.Id] = DateTime.MinValue;

            DeviceAdded?.Invoke(this, device);
            Log.Information("[RtpMidi] 主动连接: {Name} ({Host}:{Port})", name, host, port);

            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[RtpMidi] 连接失败");
            return false;
        }
    }

    private byte[] CreateInvitationPacket(string name)
    {
        var nameBytes = Encoding.ASCII.GetBytes(name);
        var packet = new byte[16 + nameBytes.Length];

        packet[0] = 0xFF;
        packet[1] = 0xFF;
        packet[2] = (byte)'I';
        packet[3] = (byte)'N';

        WriteBigEndianUInt32(packet, 4, 2);
        WriteBigEndianUInt32(packet, 8, _localSSRC);
        packet[12] = (byte)((nameBytes.Length >> 8) & 0xFF);
        packet[13] = (byte)(nameBytes.Length & 0xFF);

        Buffer.BlockCopy(nameBytes, 0, packet, 16, nameBytes.Length);

        return packet;
    }

    public void SendMessage(MidiDevice device, byte[] data)
    {
        if (!_deviceEndpoints.TryGetValue(device.Id, out var endpoint)) return;
        if (_dataServer == null) return;
        if (!_deviceJournals.TryGetValue(device.Id, out var journal)) return;
        if (!_deviceSequence.TryGetValue(device.Id, out var currentSeq)) currentSeq = 0;

        try
        {
            ushort seq = (ushort)(currentSeq + 1);
            _deviceSequence[device.Id] = seq;

            uint timestamp = GetRtpTimestamp();

            journal.UpdateFromMidiCommand(data, seq, timestamp);

            uint checkpointSeq = (uint)Math.Max(0, (int)seq - 10);
            var recoveryJournalData = journal.Encode(checkpointSeq, timestamp);

            var packet = CreateRtpMidiPacket(seq, timestamp, _localSSRC, data, recoveryJournalData);
            _dataServer.Send(packet, packet.Length, endpoint);

            _deviceLastMidiSent[device.Id] = DateTime.Now;

            device.SentMessages++;
            device.LastActivity = DateTime.Now;
            device.PulseTransmit();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[RtpMidi] 发送消息失败");
        }
    }

    private uint GetRtpTimestamp()
    {
        long elapsedTicks = DateTimeOffset.UtcNow.Ticks - _startTimeTicks;
        double elapsedSeconds = elapsedTicks / (double)TimeSpan.TicksPerSecond;
        return (uint)(elapsedSeconds * SAMPLE_RATE);
    }

    private async void ControlLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _controlServer != null)
        {
            try
            {
                var result = await _controlServer.ReceiveAsync();
                ProcessControlPacket(result.Buffer, result.RemoteEndPoint);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException ex)
            {
                if (ct.IsCancellationRequested) break;
                if (ex.SocketErrorCode == SocketError.ConnectionReset ||
                    ex.SocketErrorCode == SocketError.ConnectionRefused)
                {
                    continue;
                }
                break;
            }
            catch (Exception ex)
            {
                if (!ct.IsCancellationRequested)
                    Log.Error(ex, "[RtpMidi] 控制端口循环错误");
                break;
            }
        }
    }

    private async void DataLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _dataServer != null)
        {
            try
            {
                var result = await _dataServer.ReceiveAsync();
                ProcessDataPacket(result.Buffer, result.RemoteEndPoint);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException ex)
            {
                if (ct.IsCancellationRequested) break;
                if (ex.SocketErrorCode == SocketError.ConnectionReset ||
                    ex.SocketErrorCode == SocketError.ConnectionRefused)
                {
                    continue;
                }
                break;
            }
            catch (Exception ex)
            {
                if (!ct.IsCancellationRequested)
                    Log.Error(ex, "[RtpMidi] 数据端口循环错误");
                break;
            }
        }
    }

    private async void SyncLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(SYNC_INTERVAL_MS, ct);

                var deviceList = _devices.Values.ToList();
                Log.Debug("[RTP] SyncLoop: {Count} 个设备", deviceList.Count);

                foreach (var device in deviceList)
                {
                    if (_deviceEndpoints.TryGetValue(device.Id, out var endpoint))
                    {
                        Log.Debug("[RTP] SyncLoop: 发送 CK 到 {Name}", device.Name);
                        SendSyncPacket(endpoint);
                    }
                    else
                    {
                        Log.Debug("[RTP] SyncLoop: 设备 {Name} 没有端点", device.Name);
                    }
                }
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch { }
        }
    }

    private async void DeviceTimeoutCheckLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(DEVICE_CHECK_INTERVAL_MS, ct);

                var now = DateTime.Now;
                var timeoutDevices = _deviceLastActivity
                    .Where(kvp => (now - kvp.Value).TotalMilliseconds > DEVICE_TIMEOUT_MS)
                    .Select(kvp => kvp.Key)
                    .ToList();

                foreach (var deviceId in timeoutDevices)
                {
                    if (_devices.TryRemove(deviceId, out var device))
                    {
                        if (_deviceReceivers.TryGetValue(deviceId, out var receiver))
                        {
                            foreach (var cmd in receiver.GenerateAllNotesOff())
                            {
                                MidiDataReceived?.Invoke(this, (device, cmd));
                            }
                        }
                        CleanupDevice(deviceId);
                        DeviceRemoved?.Invoke(this, device);
                        Log.Warning("[RTP] 设备超时断开: {Name}", device.Name);
                    }
                }
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch { }
        }
    }

    private async void GuardPacketLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(GUARD_INTERVAL_MS, ct);

                var now = DateTime.Now;
                foreach (var device in _devices.Values.ToList())
                {
                    if (_deviceEndpoints.TryGetValue(device.Id, out var endpoint) &&
                        _deviceJournals.TryGetValue(device.Id, out var journal) &&
                        _deviceSequence.TryGetValue(device.Id, out var seq) &&
                        _deviceLastMidiSent.TryGetValue(device.Id, out var lastSent))
                    {
                        var elapsed = (now - lastSent).TotalMilliseconds;
                        if (elapsed >= GUARD_INTERVAL_MS)
                        {
                            SendGuardPacket(device, endpoint, journal, seq);
                        }
                    }
                }
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch { }
        }
    }

    private void SendGuardPacket(MidiDevice device, IPEndPoint endpoint, RecoveryJournal journal, ushort lastSeq)
    {
        if (_dataServer == null) return;

        try
        {
            ushort seq = (ushort)(lastSeq + 1);
            _deviceSequence[device.Id] = seq;

            uint timestamp = GetRtpTimestamp();
            uint checkpointSeq = (uint)Math.Max(0, (int)seq - 10);
            var recoveryJournalData = journal.Encode(checkpointSeq, timestamp);

            var packet = CreateRtpMidiPacket(seq, timestamp, _localSSRC, Array.Empty<byte>(), recoveryJournalData);
            _dataServer.Send(packet, packet.Length, endpoint);

            _deviceLastMidiSent[device.Id] = DateTime.Now;
            Log.Debug("[RTP] 发送 Guard Packet 到 {Endpoint}", endpoint);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[RTP] 发送 Guard Packet 失败");
        }
    }

    private void ProcessControlPacket(byte[] data, IPEndPoint remoteEP)
    {
        if (data.Length < 4) return;

        string command = Encoding.ASCII.GetString(data, 2, 2);

        switch (command)
        {
            case "IN":
                HandleInvitation(data, remoteEP);
                break;
            case "OK":
                HandleInvitationAccept(data, remoteEP);
                break;
            case "BY":
                HandleBye(remoteEP);
                break;
            case "CK":
                HandleSyncPacket(data, remoteEP, isDataPort: false);
                break;
        }
    }

    private void ProcessDataPacket(byte[] data, IPEndPoint remoteEP)
    {
        if (data.Length < 4) return;

        string command = Encoding.ASCII.GetString(data, 2, 2);

        if (command == "CK")
        {
            HandleSyncPacket(data, remoteEP, isDataPort: true);
        }
        else if (data.Length >= 13)
        {
            ProcessMidiData(data, remoteEP);
        }
    }

    private void HandleInvitation(byte[] data, IPEndPoint remoteEP)
    {
        if (data.Length < 16) return;

        uint initiatorSSRC = ((uint)data[4] << 24) | ((uint)data[5] << 16) | ((uint)data[6] << 8) | data[7];

        int nameLength = (data[12] << 8) | data[13];
        string name = nameLength > 0 && data.Length >= 16 + nameLength
            ? Encoding.ASCII.GetString(data, 16, nameLength)
            : "RTP-MIDI Device";

        string host = remoteEP.Address.ToString();
        string deviceId = $"rtp-{name}@{host}";

        if (_devices.TryGetValue(deviceId, out var existingDevice))
        {
            _deviceEndpoints[deviceId] = new IPEndPoint(remoteEP.Address, remoteEP.Port + 1);
            _deviceLastActivity[deviceId] = DateTime.Now;
            _deviceRemoteSSRC[deviceId] = initiatorSSRC;
            existingDevice.Status = MidiDeviceStatus.Connected;
            existingDevice.LastActivity = DateTime.Now;
            DeviceUpdated?.Invoke(this, existingDevice);
            SendInvitationAccept(remoteEP, initiatorSSRC);
            Log.Debug("[RTP] 设备重连: {Name} ({Host}:{Port})", name, host, remoteEP.Port);
            return;
        }

        var device = new MidiDevice
        {
            Id = deviceId,
            Name = name,
            Type = MidiDeviceType.RtpMidi,
            Protocol = "RTP-MIDI",
            Host = host,
            Port = remoteEP.Port + 1,
            ControlPort = remoteEP.Port,
            Status = MidiDeviceStatus.Connected,
            ConnectedTime = DateTime.Now
        };

        _devices[deviceId] = device;
        _deviceEndpoints[deviceId] = new IPEndPoint(remoteEP.Address, remoteEP.Port + 1);
        _deviceLastActivity[deviceId] = DateTime.Now;
        _deviceJournals[deviceId] = new RecoveryJournal();
        _deviceReceivers[deviceId] = new RecoveryJournalReceiver();
        _deviceExpectedSeq[deviceId] = 0;
        _deviceLastMidiSent[deviceId] = DateTime.MinValue;
        _deviceRemoteSSRC[deviceId] = initiatorSSRC;

        DeviceAdded?.Invoke(this, device);
        SendInvitationAccept(remoteEP, initiatorSSRC);

        Log.Information("[RtpMidi] 设备连接: {Name} ({Host})", name, host);
    }

    private void HandleInvitationAccept(byte[] data, IPEndPoint remoteEP)
    {
        string host = remoteEP.Address.ToString();
        var device = _devices.Values.FirstOrDefault(d => d.Host == host);

        if (device != null)
        {
            device.Status = MidiDeviceStatus.Connected;
            _deviceLastActivity[device.Id] = DateTime.Now;
            DeviceUpdated?.Invoke(this, device);
            Log.Information("[RtpMidi] 连接已确认: {Name}", device.Name);
        }
    }

    private void HandleBye(IPEndPoint remoteEP)
    {
        string host = remoteEP.Address.ToString();
        var device = _devices.Values.FirstOrDefault(d => d.Host == host);

        if (device != null)
        {
            if (_deviceReceivers.TryGetValue(device.Id, out var receiver))
            {
                foreach (var cmd in receiver.GenerateAllNotesOff())
                {
                    MidiDataReceived?.Invoke(this, (device, cmd));
                }
            }

            _devices.TryRemove(device.Id, out _);
            CleanupDevice(device.Id);
            DeviceRemoved?.Invoke(this, device);
        }
    }

    private void HandleSyncPacket(byte[] data, IPEndPoint remoteEP, bool isDataPort)
    {
        string host = remoteEP.Address.ToString();
        var device = _devices.Values.FirstOrDefault(d => d.Host == host);

        if (device != null)
        {
            _deviceLastActivity[device.Id] = DateTime.Now;
            device.LastActivity = DateTime.Now;
            DeviceUpdated?.Invoke(this, device);
        }

        SendSyncResponse(data, remoteEP, isDataPort);
    }

    private void ProcessMidiData(byte[] data, IPEndPoint remoteEP)
    {
        string host = remoteEP.Address.ToString();
        var device = _devices.Values.FirstOrDefault(d => d.Host == host);
        if (device == null) return;

        if (!_deviceReceivers.TryGetValue(device.Id, out var receiver)) return;
        if (!_deviceExpectedSeq.TryGetValue(device.Id, out var expectedSeq)) expectedSeq = 0;

        int offset = 12;

        if (offset >= data.Length) return;
        byte midiHeader = data[offset++];
        bool bFlag = (midiHeader & 0x80) != 0;
        bool zFlag = (midiHeader & 0x20) != 0;

        int midiLength = midiHeader & 0x0F;

        if (midiLength == 15 && offset < data.Length)
        {
            int lenByte = data[offset++];
            if (lenByte < 255)
                midiLength = lenByte;
            else if (offset + 2 <= data.Length)
            {
                midiLength = (data[offset] << 8) | data[offset + 1];
                offset += 2;
            }
        }

        if (zFlag && offset + 3 <= data.Length)
        {
            offset += 3;
        }

        if (offset + midiLength > data.Length) return;

        ushort seqNum = (ushort)((data[2] << 8) | data[3]);

        if (seqNum != expectedSeq && bFlag)
        {
            Log.Debug("[RTP] 包丢失: 期望 {Expected}, 收到 {Received}", expectedSeq, seqNum);

            int journalOffset = offset + midiLength;
            if (journalOffset < data.Length)
            {
                int journalLength = data.Length - journalOffset;
                byte[] journalData = new byte[journalLength];
                Buffer.BlockCopy(data, journalOffset, journalData, 0, journalLength);

                foreach (var cmd in receiver.ProcessRecoveryJournal(journalData, seqNum, expectedSeq))
                {
                    MidiDataReceived?.Invoke(this, (device, cmd));
                }
            }
        }

        if (midiLength > 0)
        {
            byte[] midiData = new byte[midiLength];
            Buffer.BlockCopy(data, offset, midiData, 0, midiLength);

            receiver.UpdateFromReceivedMidi(midiData, seqNum);
            MidiDataReceived?.Invoke(this, (device, midiData));
        }

        _deviceExpectedSeq[device.Id] = (ushort)(seqNum + 1);

        device.ReceivedMessages++;
        device.LastActivity = DateTime.Now;
        device.Status = MidiDeviceStatus.Active;
        device.PulseTransmit();
        _deviceLastActivity[device.Id] = DateTime.Now;
        DeviceUpdated?.Invoke(this, device);
    }

    private void SendSyncPacket(IPEndPoint endpoint)
    {
        try
        {
            var packet = new byte[12];
            packet[0] = 0xFF;
            packet[1] = 0xFF;
            packet[2] = (byte)'C';
            packet[3] = (byte)'K';
            WriteBigEndianUInt32(packet, 4, _localSSRC);

            var count = BitConverter.GetBytes(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            if (BitConverter.IsLittleEndian) Array.Reverse(count);
            Buffer.BlockCopy(count, 0, packet, 8, 4);

            var controlEP = new IPEndPoint(endpoint.Address, endpoint.Port - 1);
            _controlServer?.Send(packet, packet.Length, controlEP);
            Log.Debug("[RTP] 发送 CK 到 {RemoteEP}", controlEP);
        }
        catch { }
    }

    private void SendInvitationAccept(IPEndPoint remoteEP, uint initiatorSSRC)
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
            WriteBigEndianUInt32(packet, 8, initiatorSSRC);
            packet[12] = (byte)((nameBytes.Length >> 8) & 0xFF);
            packet[13] = (byte)(nameBytes.Length & 0xFF);

            Buffer.BlockCopy(nameBytes, 0, packet, 16, nameBytes.Length);

            _controlServer?.Send(packet, packet.Length, remoteEP);
            Log.Debug("[RTP] 发送 OK 到 {RemoteEP}", remoteEP);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[RTP] 发送邀请响应失败: {RemoteEP}", remoteEP);
        }
    }

private void SendSyncResponse(byte[] data, IPEndPoint remoteEP, bool isDataPort = false)
    {
        try
        {
            if (data.Length >= 8)
            {
                uint receivedSSRC = ReadBigEndianUInt32(data, 4);
                if (receivedSSRC == _localSSRC)
                {
                    return;
                }
            }

            var packet = new byte[12];
            packet[0] = 0xFF;
            packet[1] = 0xFF;
            packet[2] = (byte)'C';
            packet[3] = (byte)'K';
            WriteBigEndianUInt32(packet, 4, _localSSRC);

            var count = BitConverter.GetBytes(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            if (BitConverter.IsLittleEndian) Array.Reverse(count);
            Buffer.BlockCopy(count, 0, packet, 8, 4);

            if (isDataPort && _dataServer != null)
            {
                _dataServer.Send(packet, packet.Length, remoteEP);
            }
            else if (_controlServer != null)
            {
                _controlServer.Send(packet, packet.Length, remoteEP);
            }
        }
        catch { }
    }

    private byte[] CreateRtpMidiPacket(ushort sequenceNumber, uint timestamp, uint ssrc, byte[] midiData, byte[]? recoveryJournal = null)
    {
        bool hasJournal = recoveryJournal != null && recoveryJournal.Length > 0;
        int midiListLen = CalculateMidiListLength(midiData);
        int journalLen = hasJournal ? recoveryJournal!.Length : 0;
        int packetLen = 12 + midiListLen + journalLen;
        var packet = new byte[packetLen];

        int offset = 0;

        packet[offset++] = 0x80;
        packet[offset++] = 0x61;

        packet[offset++] = (byte)((sequenceNumber >> 8) & 0xFF);
        packet[offset++] = (byte)(sequenceNumber & 0xFF);

        WriteBigEndianUInt32(packet, offset, timestamp);
        offset += 4;
        WriteBigEndianUInt32(packet, offset, ssrc);
        offset += 4;

        offset = WriteMidiCommandSection(packet, offset, midiData, hasJournal);

        if (hasJournal)
        {
            Buffer.BlockCopy(recoveryJournal!, 0, packet, offset, recoveryJournal!.Length);
        }

        return packet;
    }

    private int CalculateMidiListLength(byte[] midiData)
    {
        if (midiData == null || midiData.Length == 0) return 1;

        if (midiData.Length <= 15) return 1 + midiData.Length;

        return 1 + 1 + midiData.Length;
    }

    private int WriteMidiCommandSection(byte[] packet, int offset, byte[] midiData, bool hasJournal)
    {
        byte header = 0;

        if (hasJournal)
            header |= 0x80;

        if (midiData == null || midiData.Length == 0)
        {
            packet[offset++] = header;
            return offset;
        }

        int len = midiData.Length;

        if (len <= 15)
        {
            header |= (byte)len;
            packet[offset++] = header;
            Buffer.BlockCopy(midiData, 0, packet, offset, len);
            return offset + len;
        }

        header |= 0x0F;
        packet[offset++] = header;

        if (len < 255)
        {
            packet[offset++] = (byte)len;
        }
        else
        {
            packet[offset++] = 0xFF;
            packet[offset++] = (byte)((len >> 8) & 0xFF);
            packet[offset++] = (byte)(len & 0xFF);
        }

        Buffer.BlockCopy(midiData, 0, packet, offset, len);
        return offset + len;
    }

    private void WriteBigEndianUInt32(byte[] data, int offset, uint value)
    {
        data[offset] = (byte)((value >> 24) & 0xFF);
        data[offset + 1] = (byte)((value >> 16) & 0xFF);
        data[offset + 2] = (byte)((value >> 8) & 0xFF);
        data[offset + 3] = (byte)(value & 0xFF);
    }

    private uint ReadBigEndianUInt32(byte[] data, int offset)
    {
        return (uint)((data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3]);
    }

    private void OnStatusChanged(string message) => StatusChanged?.Invoke(this, message);

    public void Dispose()
    {
        Stop();
        _cts?.Dispose();
    }
}