using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using MidiBridge.Models;
using Serilog;

namespace MidiBridge.Services.NetworkMidi2;

public class NetworkMidi2Service : IDisposable
{
    private UdpClient? _udpServer;
    private CancellationTokenSource? _cts;
    private bool _isRunning;
    private readonly ConcurrentDictionary<string, NetworkMidi2Protocol.SessionInfo> _sessions = new();
    private readonly ConcurrentDictionary<string, NetworkMidi2Protocol.DiscoveredDevice> _discoveredDevices = new();
    private byte _localSSRC;
    private int _port;
    private string _serviceName;
    private string _productInstanceId;

    private readonly ConcurrentQueue<byte[]> _fecBuffer = new();
    private const int FEC_BUFFER_SIZE = 2;

    public event EventHandler<MidiDevice>? DeviceAdded;
    public event EventHandler<MidiDevice>? DeviceRemoved;
    public event EventHandler<MidiDevice>? DeviceUpdated;
    public event EventHandler<(MidiDevice Device, byte[] Data)>? MidiDataReceived;
    public event EventHandler<string>? StatusChanged;
    public event EventHandler<NetworkMidi2Protocol.DiscoveredDevice>? DeviceDiscovered;

    public ObservableCollection<MidiDevice> InputDevices { get; } = new();
    public ObservableCollection<MidiDevice> OutputDevices { get; } = new();
    public bool IsRunning => _isRunning;
    public IReadOnlyDictionary<string, NetworkMidi2Protocol.SessionInfo> Sessions => _sessions;
    public IReadOnlyDictionary<string, NetworkMidi2Protocol.DiscoveredDevice> DiscoveredDevices => _discoveredDevices;

    public NetworkMidi2Service()
    {
        _localSSRC = (byte)Random.Shared.Next(1, 255);
        _serviceName = NetworkMidi2Protocol.DEFAULT_SERVICE_NAME;
        _productInstanceId = Guid.NewGuid().ToString("N").Substring(0, 16);
        _port = NetworkMidi2Protocol.DEFAULT_PORT;
    }

    public void SetServiceInfo(string name, string productInstanceId = "")
    {
        _serviceName = name;
        _productInstanceId = string.IsNullOrEmpty(productInstanceId) 
            ? Guid.NewGuid().ToString("N").Substring(0, 16) 
            : productInstanceId;
    }

    public bool Start(int port = NetworkMidi2Protocol.DEFAULT_PORT)
    {
        if (_isRunning) Stop();

        try
        {
            _port = port;
            _cts = new CancellationTokenSource();
            _udpServer = new UdpClient(port);
            _isRunning = true;

            Task.Run(() => ReceiveLoop(_cts.Token));
            Task.Run(() => PingLoop(_cts.Token));

            OnStatusChanged($"Network MIDI 2.0 服务已启动: 端口 {port}");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[NM2] 启动失败");
            OnStatusChanged($"启动失败: {ex.Message}");
            return false;
        }
    }

    public void Stop()
    {
        if (!_isRunning) return;
        
        _isRunning = false;
        
        try { _cts?.Cancel(); } catch (ObjectDisposedException) { }

        foreach (var session in _sessions.Values.ToList())
        {
            SendEndSession(session);
        }

        _udpServer?.Close();
        _udpServer?.Dispose();

        _sessions.Clear();

        OnStatusChanged("Network MIDI 2.0 服务已停止");
    }

    private async void ReceiveLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _udpServer != null)
        {
            try
            {
                var result = await _udpServer.ReceiveAsync();
                ProcessPacket(result.Buffer, result.RemoteEndPoint);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { break; }
            catch (Exception ex)
            {
                if (!ct.IsCancellationRequested)
                {
                    Log.Debug("[NM2] 接收错误: {Message}", ex.Message);
                }
                break;
            }
        }
    }

    private void ProcessPacket(byte[] data, IPEndPoint remoteEP)
    {
        if (data.Length < 4) return;

        if (!NetworkMidi2Protocol.ParsePacket(data, out var command, out var status, out var ssrc, out var remoteSSRC))
            return;

        Log.Debug("[NM2] 收到命令 cmd={Command}, status={Status}, ssrc={SSRC:X2}, 来源={RemoteEP}", command, status, ssrc, remoteEP);

        switch (command)
        {
            case NetworkMidi2Protocol.SessionCommand.Invitation:
                HandleInvitation(data, status, remoteEP, ssrc, remoteSSRC);
                break;
            case NetworkMidi2Protocol.SessionCommand.EndSession:
                HandleEndSession(remoteEP, ssrc);
                break;
            case NetworkMidi2Protocol.SessionCommand.Ping:
                HandlePing(status, remoteEP, ssrc, remoteSSRC);
                break;
            case NetworkMidi2Protocol.SessionCommand.UMPData:
                HandleUMPData(data, remoteEP);
                break;
        }
    }

    private void HandleInvitation(byte[] data, NetworkMidi2Protocol.CommandStatus status, IPEndPoint remoteEP, byte ssrc, byte remoteSSRC)
    {
        string sessionId = GetSessionId(remoteEP);

        if (status == NetworkMidi2Protocol.CommandStatus.Command)
        {
            if (!NetworkMidi2Protocol.ParseInvitation(data, out var name, out _))
            {
                name = $"Device ({remoteEP.Address})";
            }

            var session = new NetworkMidi2Protocol.SessionInfo
            {
                Id = sessionId,
                RemoteName = name,
                RemoteHost = remoteEP.Address.ToString(),
                RemotePort = remoteEP.Port,
                SenderSSRC = ssrc,
                ReceiverSSRC = _localSSRC,
                State = NetworkMidi2Protocol.SessionState.Connected,
                LastActivity = DateTime.Now,
                SendSequence = 0,
                ReceiveSequence = 0,
                RetransmitBuffer = new List<byte[]>(),
                SupportsFEC = true,
                SupportsRetransmit = true,
            };

            _sessions[sessionId] = session;

            var replyPacket = NetworkMidi2Protocol.CreateInvitationReplyPacket(_localSSRC, ssrc);
            SendPacket(replyPacket, remoteEP);

            AddDevice(session);

            Log.Information("[NM2] 会话已接受: {Name} 来自 {RemoteEP}", name, remoteEP);
        }
        else if (status == NetworkMidi2Protocol.CommandStatus.Reply)
        {
            if (_sessions.TryGetValue(sessionId, out var session) && session.State == NetworkMidi2Protocol.SessionState.Pending)
            {
                session.State = NetworkMidi2Protocol.SessionState.Connected;
                session.ReceiverSSRC = ssrc;
                session.LastActivity = DateTime.Now;
                _sessions[sessionId] = session;

                AddDevice(session);

                Log.Information("[NM2] 会话已建立: {RemoteName}", session.RemoteName);
            }
        }
        else if (status == NetworkMidi2Protocol.CommandStatus.Error)
        {
            if (_sessions.TryRemove(sessionId, out _))
            {
                OnStatusChanged($"Session rejected by {remoteEP}");
            }
        }
    }

    private void HandleEndSession(IPEndPoint remoteEP, byte ssrc)
    {
        string sessionId = GetSessionId(remoteEP);

        if (_sessions.TryRemove(sessionId, out var session))
        {
            string stableId = GetStableDeviceId(session.RemoteName, session.RemoteHost);
            RemoveDevice(stableId);
            OnStatusChanged($"Session ended: {session.RemoteName}");
        }
    }

    private void HandlePing(NetworkMidi2Protocol.CommandStatus status, IPEndPoint remoteEP, byte ssrc, byte remoteSSRC)
    {
        string sessionId = GetSessionId(remoteEP);

        if (status == NetworkMidi2Protocol.CommandStatus.Command)
        {
            var pongPacket = NetworkMidi2Protocol.CreatePongPacket(_localSSRC, ssrc);
            SendPacket(pongPacket, remoteEP);
        }
        else if (status == NetworkMidi2Protocol.CommandStatus.Reply)
        {
            if (_sessions.TryGetValue(sessionId, out var session))
            {
                session.LastActivity = DateTime.Now;
                _sessions[sessionId] = session;
            }
        }
    }

    private void HandleUMPData(byte[] data, IPEndPoint remoteEP)
    {
        if (!NetworkMidi2Protocol.ParseUMPData(data, out var sequenceNumber, out var umpData))
            return;

        string sessionId = GetSessionId(remoteEP);

        if (!_sessions.TryGetValue(sessionId, out var session))
        {
            session = new NetworkMidi2Protocol.SessionInfo
            {
                Id = sessionId,
                RemoteName = $"UMP Device ({remoteEP.Address})",
                RemoteHost = remoteEP.Address.ToString(),
                RemotePort = remoteEP.Port,
                State = NetworkMidi2Protocol.SessionState.Connected,
                LastActivity = DateTime.Now,
                ReceiveSequence = sequenceNumber,
                RetransmitBuffer = new List<byte[]>(),
            };
            _sessions[sessionId] = session;
            AddDevice(session);
        }

        ushort expectedSeq = (ushort)(session.ReceiveSequence + 1);
        if (sequenceNumber != expectedSeq && session.ReceiveSequence != 0)
        {
            ushort diff = (ushort)(expectedSeq - sequenceNumber);
            if (diff > 0 && diff < 100)
            {
                Log.Warning("[NM2] 数据包乱序或丢失: 期望 {ExpectedSeq}, 实际 {Seq}", expectedSeq, sequenceNumber);
            }
        }

        session.ReceiveSequence = sequenceNumber;
        session.LastActivity = DateTime.Now;
        _sessions[sessionId] = session;

        ProcessUMPData(umpData, session);

        string stableId = GetStableDeviceId(session.RemoteName, session.RemoteHost);
        if (InputDevices.FirstOrDefault(d => d.Id == stableId) is { } device)
        {
            device.PulseTransmit();
            DeviceUpdated?.Invoke(this, device);
        }
    }

    private void ProcessUMPData(byte[] umpData, NetworkMidi2Protocol.SessionInfo session)
    {
        if (umpData.Length < 4) return;

        string stableId = GetStableDeviceId(session.RemoteName, session.RemoteHost);
        Log.Debug("[NM2] 处理UMP数据: {Length} 字节 来自 {DeviceId}", umpData.Length, stableId);

        int offset = 0;
        while (offset + 4 <= umpData.Length)
        {
            int messageType = NetworkMidi2Protocol.GetUMPMessageType(umpData, offset);
            int packetSize = NetworkMidi2Protocol.GetUMPPacketSize(messageType);

            if (offset + packetSize > umpData.Length) break;

            byte[] singleUMP = new byte[packetSize];
            Buffer.BlockCopy(umpData, offset, singleUMP, 0, packetSize);

            byte[] midiData = ConvertUMPToMidi1(singleUMP);

            if (midiData.Length > 0 && InputDevices.FirstOrDefault(d => d.Id == stableId) is { } device)
            {
                Log.Debug("[NM2] -> MIDI: {MidiData}", BitConverter.ToString(midiData));
                MidiDataReceived?.Invoke(this, (device, midiData));
            }

            offset += packetSize;
        }
    }

    private byte[] ConvertUMPToMidi1(byte[] ump)
    {
        if (ump.Length < 4) return Array.Empty<byte>();

        int messageType = NetworkMidi2Protocol.GetUMPMessageType(ump, 0);

        if (messageType == 0x2)
        {
            byte status = ump[1];
            byte data1 = ump[2];
            byte data2 = ump[3];
            return new byte[] { status, data1, data2 };
        }
        else if (messageType == 0x4 && ump.Length >= 8)
        {
            byte status = ump[1];
            byte note = ump[2];
            ushort velocity = (ushort)((ump[5] << 8) | ump[6]);
            return new byte[] { status, note, (byte)Math.Min(127, velocity >> 9) };
        }

        return Array.Empty<byte>();
    }

    public async Task<bool> InviteDevice(string host, int port, string? name = null)
    {
        try
        {
            var ep = new IPEndPoint(IPAddress.Parse(host), port);
            string sessionId = GetSessionId(ep);

            if (_sessions.ContainsKey(sessionId))
            {
                OnStatusChanged($"Already connected to {host}:{port}");
                return false;
            }

            var session = new NetworkMidi2Protocol.SessionInfo
            {
                Id = sessionId,
                RemoteName = name ?? $"Device ({host})",
                RemoteHost = host,
                RemotePort = port,
                SenderSSRC = _localSSRC,
                State = NetworkMidi2Protocol.SessionState.Pending,
                LastActivity = DateTime.Now,
                SendSequence = 0,
                ReceiveSequence = 0,
                RetransmitBuffer = new List<byte[]>(),
            };

            _sessions[sessionId] = session;

            var invitePacket = NetworkMidi2Protocol.CreateInvitationPacket(_serviceName, _localSSRC, _productInstanceId);

            for (int i = 0; i < NetworkMidi2Protocol.INVITATION_RETRY_COUNT; i++)
            {
                SendPacket(invitePacket, ep);

                await Task.Delay(NetworkMidi2Protocol.INVITATION_RETRY_INTERVAL_MS);

                if (_sessions.TryGetValue(sessionId, out var s) && s.State == NetworkMidi2Protocol.SessionState.Connected)
                {
                    return true;
                }
            }

            _sessions.TryRemove(sessionId, out _);
            OnStatusChanged($"Connection timeout: {host}:{port}");
            return false;
        }
        catch (Exception ex)
        {
            OnStatusChanged($"Connection failed: {ex.Message}");
            return false;
        }
    }

    public void EndSession(string sessionId)
    {
        if (_sessions.TryRemove(sessionId, out var session))
        {
            SendEndSession(session);
            string stableId = GetStableDeviceId(session.RemoteName, session.RemoteHost);
            RemoveDevice(stableId);
        }
    }

    public void SendUMP(string sessionId, byte[] umpData)
    {
        if (!_sessions.TryGetValue(sessionId, out var session)) return;
        if (session.State != NetworkMidi2Protocol.SessionState.Connected) return;

        var ep = new IPEndPoint(IPAddress.Parse(session.RemoteHost), session.RemotePort);

        session.SendSequence++;
        _sessions[sessionId] = session;

        var packet = NetworkMidi2Protocol.CreateUMPDataPacket(session.SendSequence, umpData);

        if (_fecBuffer.Count >= FEC_BUFFER_SIZE)
        {
            _fecBuffer.TryDequeue(out _);
        }
        _fecBuffer.Enqueue(packet);

        SendPacket(packet, ep);

        if (OutputDevices.FirstOrDefault(d => d.Id == sessionId) is { } device)
        {
            device.PulseTransmit();
            DeviceUpdated?.Invoke(this, device);
        }
    }

    public void SendMidiData(string sessionId, byte[] midiData)
    {
        var ump = ConvertMidi1ToUMP(midiData);
        if (ump.Length > 0)
        {
            SendUMP(sessionId, ump);
        }
    }

    private byte[] ConvertMidi1ToUMP(byte[] midiData)
    {
        if (midiData.Length < 1) return Array.Empty<byte>();

        byte status = midiData[0];
        int messageType;

        if (status >= 0xF0)
        {
            return Array.Empty<byte>();
        }

        messageType = 0x2;

        var ump = new byte[4];
        ump[0] = (byte)((messageType << 4) | 0);
        ump[1] = status;

        if (midiData.Length >= 2)
            ump[2] = midiData[1];
        if (midiData.Length >= 3)
            ump[3] = midiData[2];

        return ump;
    }

    private void SendEndSession(NetworkMidi2Protocol.SessionInfo session)
    {
        try
        {
            var ep = new IPEndPoint(IPAddress.Parse(session.RemoteHost), session.RemotePort);
            var packet = NetworkMidi2Protocol.CreateEndSessionPacket(_localSSRC, session.SenderSSRC);
            SendPacket(packet, ep);
        }
        catch { }
    }

    private void SendPacket(byte[] data, IPEndPoint endpoint)
    {
        try
        {
            _udpServer?.Send(data, data.Length, endpoint);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[NM2] 发送错误");
        }
    }

    private async void PingLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(NetworkMidi2Protocol.PING_INTERVAL_MS, ct);

                foreach (var session in _sessions.Values.ToList())
                {
                    if (session.State == NetworkMidi2Protocol.SessionState.Connected)
                    {
                        try
                        {
                            var ep = new IPEndPoint(IPAddress.Parse(session.RemoteHost), session.RemotePort);
                            var pingPacket = NetworkMidi2Protocol.CreatePingPacket(_localSSRC, session.SenderSSRC);
                            SendPacket(pingPacket, ep);
                        }
                        catch { }
                    }
                }
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch { break; }
        }
    }

    private static string GetSessionId(IPEndPoint ep)
    {
        return $"nm2-{ep.Address}-{ep.Port}";
    }

    private static string GetStableDeviceId(string deviceName, string host)
    {
        string safeName = string.Join("_", deviceName.Split(Path.GetInvalidFileNameChars()));
        return $"nm2-{safeName}@{host}";
    }

    private void AddDevice(NetworkMidi2Protocol.SessionInfo session)
    {
        try
        {
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                string stableId = GetStableDeviceId(session.RemoteName, session.RemoteHost);
                
                var existingDevice = InputDevices.FirstOrDefault(d => d.Id == stableId);
                if (existingDevice != null)
                {
                    session.Id = stableId;
                    return;
                }

                var device = new MidiDevice
                {
                    Id = stableId,
                    Name = session.RemoteName,
                    Type = MidiDeviceType.NetworkMidi2,
                    Protocol = "Network MIDI 2.0",
                    Host = session.RemoteHost,
                    Port = session.RemotePort,
                    Status = MidiDeviceStatus.Connected,
                    ConnectedTime = DateTime.Now,
                };

                session.Id = stableId;
                InputDevices.Add(device);
                DeviceAdded?.Invoke(this, device);
            });
        }
        catch { }
    }

    private void RemoveDevice(string sessionId)
    {
        try
        {
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                var device = InputDevices.FirstOrDefault(d => d.Id == sessionId);
                if (device != null)
                {
                    InputDevices.Remove(device);
                    DeviceRemoved?.Invoke(this, device);
                }
            });
        }
        catch { }
    }

    private void OnStatusChanged(string message)
    {
        StatusChanged?.Invoke(this, message);
    }

    public void Dispose()
    {
        Stop();
        _cts?.Dispose();
    }
}