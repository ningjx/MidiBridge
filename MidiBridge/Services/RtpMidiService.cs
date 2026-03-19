using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Net;
using System.Net.Sockets;
using System.Text;
using MidiBridge.Models;
using MidiBridge.Services.Interfaces;
using Serilog;

namespace MidiBridge.Services;

/// <summary>
/// RTP-MIDI 服务实现，负责 RTP-MIDI 协议的处理。
/// </summary>
public class RtpMidiService : IRtpMidiService
{
    private readonly ConcurrentDictionary<string, MidiDevice> _devices = new();
    private readonly ConcurrentDictionary<string, IPEndPoint> _deviceEndpoints = new();
    
    private UdpClient? _controlServer;
    private UdpClient? _dataServer;
    private CancellationTokenSource? _cts;
    private bool _isRunning;
    private int _controlPort = 5004;

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

    /// <summary>
    /// 启动 RTP-MIDI 服务。
    /// </summary>
    public bool Start(int controlPort = 5004)
    {
        if (_isRunning) Stop();

        try
        {
            _controlPort = controlPort;
            _cts = new CancellationTokenSource();

            _controlServer = new UdpClient(controlPort);
            _dataServer = new UdpClient(controlPort + 1);

            _isRunning = true;

            Task.Run(() => ControlLoop(_cts.Token));
            Task.Run(() => DataLoop(_cts.Token));

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

    /// <summary>
    /// 停止 RTP-MIDI 服务。
    /// </summary>
    public void Stop()
    {
        if (!_isRunning) return;

        _isRunning = false;
        _cts?.Cancel();

        _controlServer?.Close();
        _dataServer?.Close();
        _controlServer?.Dispose();
        _dataServer?.Dispose();

        _devices.Clear();
        _deviceEndpoints.Clear();

        OnStatusChanged("RTP-MIDI 服务已停止");
    }

    /// <summary>
    /// 发送 MIDI 消息。
    /// </summary>
    public void SendMessage(MidiDevice device, byte[] data)
    {
        if (!_deviceEndpoints.TryGetValue(device.Id, out var endpoint)) return;
        if (_dataServer == null) return;

        try
        {
            var packet = CreateMidiPacket(data);
            _dataServer.Send(packet, packet.Length, endpoint);
            
            device.SentMessages++;
            device.LastActivity = DateTime.Now;
            device.PulseTransmit();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[RtpMidi] 发送消息失败");
        }
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
            catch (SocketException) { break; }
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
            catch (SocketException) { break; }
            catch (Exception ex)
            {
                if (!ct.IsCancellationRequested)
                    Log.Error(ex, "[RtpMidi] 数据端口循环错误");
                break;
            }
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
                SendSyncResponse(data, remoteEP, isDataPort: false);
                break;
        }
    }

    private void ProcessDataPacket(byte[] data, IPEndPoint remoteEP)
    {
        if (data.Length < 4) return;

        string command = Encoding.ASCII.GetString(data, 2, 2);

        if (command == "CK")
        {
            SendSyncResponse(data, remoteEP, isDataPort: true);
        }
        else if (data.Length >= 13)
        {
            ProcessMidiData(data, remoteEP);
        }
    }

    private void HandleInvitation(byte[] data, IPEndPoint remoteEP)
    {
        if (data.Length < 16) return;

        int nameLength = (data[12] << 8) | data[13];
        string name = nameLength > 0 && data.Length >= 16 + nameLength
            ? Encoding.ASCII.GetString(data, 16, nameLength)
            : "RTP-MIDI Device";

        string host = remoteEP.Address.ToString();
        string deviceId = $"rtp-{name}@{host}";

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

        DeviceAdded?.Invoke(this, device);
        SendInvitationAccept(remoteEP);

        Log.Information("[RtpMidi] 设备连接: {Name} ({Host})", name, host);
    }

    private void HandleInvitationAccept(byte[] data, IPEndPoint remoteEP)
    {
        string host = remoteEP.Address.ToString();
        var device = _devices.Values.FirstOrDefault(d => d.Host == host);
        
        if (device != null)
        {
            device.Status = MidiDeviceStatus.Connected;
            DeviceUpdated?.Invoke(this, device);
        }
    }

    private void HandleBye(IPEndPoint remoteEP)
    {
        string host = remoteEP.Address.ToString();
        var device = _devices.Values.FirstOrDefault(d => d.Host == host);
        
        if (device != null)
        {
            _devices.TryRemove(device.Id, out _);
            _deviceEndpoints.TryRemove(device.Id, out _);
            DeviceRemoved?.Invoke(this, device);
        }
    }

    private void ProcessMidiData(byte[] data, IPEndPoint remoteEP)
    {
        string host = remoteEP.Address.ToString();
        var device = _devices.Values.FirstOrDefault(d => d.Host == host);
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
        }
    }

    private void SendInvitationAccept(IPEndPoint remoteEP)
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

            _controlServer?.Send(packet, packet.Length, remoteEP);
        }
        catch { }
    }

    private void SendSyncResponse(byte[] data, IPEndPoint remoteEP, bool isDataPort = false)
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
                _dataServer?.Send(packet, packet.Length, remoteEP);
            }
            else
            {
                _controlServer?.Send(packet, packet.Length, remoteEP);
            }
        }
        catch { }
    }

    private byte[] CreateMidiPacket(byte[] midiData)
    {
        var packet = new byte[13 + midiData.Length];
        
        packet[0] = 0xFF;
        packet[1] = 0xFF;
        WriteBigEndianUInt32(packet, 2, (uint)Random.Shared.Next());
        WriteBigEndianUInt32(packet, 6, (uint)Random.Shared.Next());
        
        packet[10] = 0;
        packet[11] = 0;
        packet[12] = (byte)(0xB0 | (midiData.Length & 0x0F));
        
        Buffer.BlockCopy(midiData, 0, packet, 13, midiData.Length);
        
        return packet;
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
        Stop();
        _cts?.Dispose();
    }
}