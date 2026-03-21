using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using MidiBridge.Models;
using MidiBridge.Services.Interfaces;
using Serilog;

namespace MidiBridge.Services.NetworkMidi2;

public class NetworkMidi2Service : INetworkMidi2Service
{
    private UdpClient? _udpServer;
    private CancellationTokenSource? _cts;
    private bool _isRunning;
    private readonly ConcurrentDictionary<string, NetworkMidi2Protocol.SessionInfo> _sessions = new();
    private readonly ConcurrentDictionary<string, NetworkMidi2Protocol.DiscoveredDevice> _discoveredDevices = new();

    private int _port;
    private string _serviceName;
    private string _productInstanceId;

    private readonly ConcurrentDictionary<uint, DateTime> _pendingPings = new();

    public event EventHandler<MidiDevice>? DeviceAdded;
    public event EventHandler<MidiDevice>? DeviceRemoved;
    public event EventHandler<MidiDevice>? DeviceUpdated;
    public event EventHandler<(MidiDevice Device, byte[] Data)>? MidiDataReceived;
    public event EventHandler<string>? StatusChanged;
    public event EventHandler<NetworkMidi2Protocol.DiscoveredDevice>? DeviceDiscovered;

    public event EventHandler<(string SessionId, string DeviceName, string Host, int Port)>? InvitationReceived;
    public event EventHandler<(string SessionId, int LostPackets)>? RetransmitErrorReceived;

    public event EventHandler<(string SessionId, string CryptoNonce, bool RequireUserAuth)>? AuthenticationRequired;
    public event EventHandler<(string SessionId, string DeviceName, bool Success)>? AuthenticationResult;

    public int MaxSessions { get; set; } = NetworkMidi2Protocol.MAX_SESSIONS;
    public bool RequireAuthentication { get; set; } = false;
    public bool RequireUserAuthentication { get; set; } = false;
    public string SharedSecret { get; set; } = "";
    public Dictionary<string, string> AuthorizedUsers { get; } = new();

    private readonly ConcurrentDictionary<string, string> _pendingCryptoNonces = new();
    private readonly ConcurrentDictionary<string, int> _authFailCounts = new();

    public ObservableCollection<MidiDevice> InputDevices { get; } = new();
    public ObservableCollection<MidiDevice> OutputDevices { get; } = new();
    public bool IsRunning => _isRunning;
    public IReadOnlyDictionary<string, NetworkMidi2Protocol.SessionInfo> Sessions => _sessions;
    public IReadOnlyDictionary<string, NetworkMidi2Protocol.DiscoveredDevice> DiscoveredDevices => _discoveredDevices;

    public NetworkMidi2Service()
    {
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
            Task.Run(() => SessionTimeoutCheckLoop(_cts.Token));
            Task.Run(() => IdlePeriodLoop(_cts.Token));
            Task.Run(() => PendingInvitationTimeoutLoop(_cts.Token));

            OnStatusChanged($"Network MIDI 2.0 服务已启动: 端口 {port}");
            Log.Information("[NM2] 服务启动成功: 端口 {Port}", port);
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
            if (session.State == NetworkMidi2Protocol.SessionState.Established)
            {
                SendBye(session, NetworkMidi2Protocol.ByeReason.PowerDown);
            }
        }

        _udpServer?.Close();
        _udpServer?.Dispose();

        _sessions.Clear();
        _pendingPings.Clear();

        OnStatusChanged("Network MIDI 2.0 服务已停止");
        Log.Information("[NM2] 服务已停止");
    }

    private async void ReceiveLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _udpServer != null)
        {
            try
            {
                var result = await _udpServer.ReceiveAsync();
                if (result.Buffer.Length > 0)
                {
                    ProcessPacket(result.Buffer, result.RemoteEndPoint);
                }
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
                Log.Debug("[NM2] Socket 错误: {Error}", ex.SocketErrorCode);
                break;
            }
            catch (Exception ex)
            {
                if (!ct.IsCancellationRequested)
                {
                    Log.Debug(ex, "[NM2] 接收错误");
                }
                break;
            }
        }
    }

    private void ProcessPacket(byte[] data, IPEndPoint remoteEP)
    {
        if (!NetworkMidi2Protocol.ParseUDPPacket(data, out var commandPackets))
        {
            Log.Debug("[NM2] 无效数据包: 签名不匹配");
            return;
        }

        string sessionId = GetSessionId(remoteEP);

        foreach (var cmdPacket in commandPackets)
        {
            ProcessCommandPacket(cmdPacket, remoteEP, sessionId);
        }
    }

    private void ProcessCommandPacket(byte[] cmdPacket, IPEndPoint remoteEP, string sessionId)
    {
        if (!NetworkMidi2Protocol.ParseCommandPacket(cmdPacket, out var cmdCode, out var payloadLen, out var cmdSpecific1, out var cmdSpecific2, out var payload))
        {
            return;
        }

        Log.Debug("[NM2] 收到命令: {CmdCode:X2}, 来源: {RemoteEP}", (byte)cmdCode, remoteEP);

        switch (cmdCode)
        {
            case NetworkMidi2Protocol.CommandCode.Invitation:
                HandleInvitation(cmdPacket, payload, cmdSpecific1, remoteEP, sessionId);
                break;

            case NetworkMidi2Protocol.CommandCode.InvitationWithAuth:
                HandleInvitationWithAuth(payload, remoteEP, sessionId);
                break;

            case NetworkMidi2Protocol.CommandCode.InvitationWithUserAuth:
                HandleInvitationWithUserAuth(payload, remoteEP, sessionId);
                break;

            case NetworkMidi2Protocol.CommandCode.InvitationReplyAccepted:
                HandleInvitationReplyAccepted(payload, cmdSpecific1, remoteEP, sessionId);
                break;

            case NetworkMidi2Protocol.CommandCode.InvitationReplyPending:
                HandleInvitationReplyPending(payload, cmdSpecific1, remoteEP, sessionId);
                break;

            case NetworkMidi2Protocol.CommandCode.InvitationReplyAuthRequired:
                HandleInvitationReplyAuthRequired(payload, cmdSpecific1, remoteEP, sessionId);
                break;

            case NetworkMidi2Protocol.CommandCode.InvitationReplyUserAuthRequired:
                HandleInvitationReplyUserAuthRequired(payload, cmdSpecific1, remoteEP, sessionId);
                break;

            case NetworkMidi2Protocol.CommandCode.Ping:
                HandlePing(payload, remoteEP);
                break;

            case NetworkMidi2Protocol.CommandCode.PingReply:
                HandlePingReply(payload, sessionId);
                break;

            case NetworkMidi2Protocol.CommandCode.UMPData:
                HandleUMPData(cmdSpecific1, cmdSpecific2, payload, remoteEP, sessionId);
                break;

            case NetworkMidi2Protocol.CommandCode.RetransmitRequest:
                HandleRetransmitRequest(payload, remoteEP, sessionId);
                break;

            case NetworkMidi2Protocol.CommandCode.RetransmitError:
                HandleRetransmitError(payload, sessionId);
                break;

            case NetworkMidi2Protocol.CommandCode.SessionReset:
                HandleSessionReset(remoteEP, sessionId);
                break;

            case NetworkMidi2Protocol.CommandCode.SessionResetReply:
                HandleSessionResetReply(sessionId);
                break;

            case NetworkMidi2Protocol.CommandCode.Bye:
                HandleBye(payload, remoteEP, sessionId);
                break;

            case NetworkMidi2Protocol.CommandCode.ByeReply:
                HandleByeReply(sessionId);
                break;

            case NetworkMidi2Protocol.CommandCode.NAK:
                HandleNAK(payload, sessionId);
                break;

            default:
                SendNAK(remoteEP, NetworkMidi2Protocol.NAKReason.CommandNotSupported, cmdPacket);
                break;
        }
    }

    private void HandleInvitation(byte[] cmdPacket, byte[] payload, byte nameWords, IPEndPoint remoteEP, string sessionId)
    {
        if (!NetworkMidi2Protocol.ParseInvitationCommand(payload, nameWords, out var name, out var productInstanceId, out var capabilities))
        {
            SendNAK(remoteEP, NetworkMidi2Protocol.NAKReason.CommandMalformed, cmdPacket);
            return;
        }

        if (_sessions.TryGetValue(sessionId, out var existingSession) && existingSession.State == NetworkMidi2Protocol.SessionState.Established)
        {
            var reply = NetworkMidi2Protocol.CreateInvitationReplyAccepted(_serviceName, _productInstanceId);
            SendPacket(NetworkMidi2Protocol.CreateUDPPacket(reply), remoteEP);
            return;
        }

        int establishedCount = _sessions.Values.Count(s => s.State == NetworkMidi2Protocol.SessionState.Established);
        if (establishedCount >= MaxSessions)
        {
            var bye = NetworkMidi2Protocol.CreateByeCommand(NetworkMidi2Protocol.ByeReason.InvitationFailedTooManySessions);
            SendPacket(NetworkMidi2Protocol.CreateUDPPacket(bye), remoteEP);
            Log.Warning("[NM2] 拒绝连接: 已达最大会话数 {MaxSessions}", MaxSessions);
            return;
        }

        bool clientSupportsAuth = capabilities.HasFlag(NetworkMidi2Protocol.InvitationCapabilities.SupportsAuth);
        bool clientSupportsUserAuth = capabilities.HasFlag(NetworkMidi2Protocol.InvitationCapabilities.SupportsUserAuth);

        if (RequireAuthentication && clientSupportsAuth && !RequireUserAuthentication)
        {
            SendAuthRequired(sessionId, name, remoteEP, capabilities, false);
            return;
        }

        if (RequireUserAuthentication && clientSupportsUserAuth)
        {
            SendAuthRequired(sessionId, name, remoteEP, capabilities, true);
            return;
        }

        if ((RequireAuthentication || RequireUserAuthentication) && !clientSupportsAuth && !clientSupportsUserAuth)
        {
            var bye = NetworkMidi2Protocol.CreateByeCommand(NetworkMidi2Protocol.ByeReason.NoMatchingAuthMethod);
            SendPacket(NetworkMidi2Protocol.CreateUDPPacket(bye), remoteEP);
            Log.Warning("[NM2] 拒绝连接: 客户端不支持认证");
            return;
        }

        bool userAccepted = OnInvitationReceived(sessionId, name, remoteEP.Address.ToString(), remoteEP.Port);

        if (!userAccepted)
        {
            var pending = NetworkMidi2Protocol.CreateInvitationReplyPending(_serviceName, _productInstanceId);
            SendPacket(NetworkMidi2Protocol.CreateUDPPacket(pending), remoteEP);
            Log.Information("[NM2] 邀请等待用户确认: {Name} 来自 {RemoteEP}", name, remoteEP);

            var pendingSession = new NetworkMidi2Protocol.SessionInfo
            {
                Id = sessionId,
                RemoteName = name,
                RemoteHost = remoteEP.Address.ToString(),
                RemotePort = remoteEP.Port,
                State = NetworkMidi2Protocol.SessionState.PendingInvitation,
                LastActivity = DateTime.Now,
                RemoteCapabilities = capabilities,
            };
            _sessions[sessionId] = pendingSession;
            return;
        }

        AcceptSession(sessionId, name, remoteEP, capabilities);
    }

    private void SendAuthRequired(string sessionId, string name, IPEndPoint remoteEP, NetworkMidi2Protocol.InvitationCapabilities capabilities, bool requireUserAuth)
    {
        string cryptoNonce = NetworkMidi2Protocol.GenerateCryptoNonce();
        _pendingCryptoNonces[sessionId] = cryptoNonce;

        var session = new NetworkMidi2Protocol.SessionInfo
        {
            Id = sessionId,
            RemoteName = name,
            RemoteHost = remoteEP.Address.ToString(),
            RemotePort = remoteEP.Port,
            State = NetworkMidi2Protocol.SessionState.AuthenticationRequired,
            LastActivity = DateTime.Now,
            RemoteCapabilities = capabilities,
            CryptoNonce = cryptoNonce,
            AuthFailCount = 0,
            AuthDelayMs = 100,
        };
        _sessions[sessionId] = session;

        byte[] reply;
        if (requireUserAuth)
        {
            reply = NetworkMidi2Protocol.CreateInvitationReplyUserAuthRequired(cryptoNonce, _serviceName, _productInstanceId);
        }
        else
        {
            reply = NetworkMidi2Protocol.CreateInvitationReplyAuthRequired(cryptoNonce, _serviceName, _productInstanceId);
        }
        SendPacket(NetworkMidi2Protocol.CreateUDPPacket(reply), remoteEP);

        Log.Information("[NM2] 已发送认证要求: {Name}, UserAuth={UserAuth}", name, requireUserAuth);
    }

    private void HandleInvitationWithAuth(byte[] payload, IPEndPoint remoteEP, string sessionId)
    {
        if (!NetworkMidi2Protocol.ParseInvitationWithAuth(payload, out var clientDigest))
        {
            SendNAK(remoteEP, NetworkMidi2Protocol.NAKReason.CommandMalformed);
            return;
        }

        if (!_sessions.TryGetValue(sessionId, out var session))
        {
            var bye = NetworkMidi2Protocol.CreateByeCommand(NetworkMidi2Protocol.ByeReason.InvitationAuthRejected);
            SendPacket(NetworkMidi2Protocol.CreateUDPPacket(bye), remoteEP);
            return;
        }

        if (session.State != NetworkMidi2Protocol.SessionState.AuthenticationRequired)
        {
            SendNAK(remoteEP, NetworkMidi2Protocol.NAKReason.CommandNotExpected);
            return;
        }

        if (!_pendingCryptoNonces.TryGetValue(sessionId, out var cryptoNonce))
        {
            var bye = NetworkMidi2Protocol.CreateByeCommand(NetworkMidi2Protocol.ByeReason.InvitationAuthRejected);
            SendPacket(NetworkMidi2Protocol.CreateUDPPacket(bye), remoteEP);
            return;
        }

        var expectedDigest = NetworkMidi2Protocol.ComputeAuthDigest(cryptoNonce, SharedSecret);

        if (CompareDigests(clientDigest, expectedDigest))
        {
            _pendingCryptoNonces.TryRemove(sessionId, out _);
            _authFailCounts.TryRemove(sessionId, out _);

            AuthenticationResult?.Invoke(this, (sessionId, session.RemoteName, true));

            AcceptSession(sessionId, session.RemoteName, remoteEP, session.RemoteCapabilities);
            Log.Information("[NM2] 认证成功: {Name}", session.RemoteName);
        }
        else
        {
            HandleAuthFailure(sessionId, session, remoteEP, false);
        }
    }

    private void HandleInvitationWithUserAuth(byte[] payload, IPEndPoint remoteEP, string sessionId)
    {
        if (!NetworkMidi2Protocol.ParseInvitationWithUserAuth(payload, out var clientDigest, out var username))
        {
            SendNAK(remoteEP, NetworkMidi2Protocol.NAKReason.CommandMalformed);
            return;
        }

        if (!_sessions.TryGetValue(sessionId, out var session))
        {
            var bye = NetworkMidi2Protocol.CreateByeCommand(NetworkMidi2Protocol.ByeReason.InvitationAuthRejected);
            SendPacket(NetworkMidi2Protocol.CreateUDPPacket(bye), remoteEP);
            return;
        }

        if (session.State != NetworkMidi2Protocol.SessionState.AuthenticationRequired)
        {
            SendNAK(remoteEP, NetworkMidi2Protocol.NAKReason.CommandNotExpected);
            return;
        }

        if (!_pendingCryptoNonces.TryGetValue(sessionId, out var cryptoNonce))
        {
            var bye = NetworkMidi2Protocol.CreateByeCommand(NetworkMidi2Protocol.ByeReason.InvitationAuthRejected);
            SendPacket(NetworkMidi2Protocol.CreateUDPPacket(bye), remoteEP);
            return;
        }

        if (!AuthorizedUsers.TryGetValue(username, out var password))
        {
            var reply = NetworkMidi2Protocol.CreateInvitationReplyUserAuthRequired(
                cryptoNonce, _serviceName, _productInstanceId,
                NetworkMidi2Protocol.AuthenticationState.UsernameNotFound);
            SendPacket(NetworkMidi2Protocol.CreateUDPPacket(reply), remoteEP);

            Log.Warning("[NM2] 用户不存在: {Username}", username);
            AuthenticationResult?.Invoke(this, (sessionId, session.RemoteName, false));
            return;
        }

        var expectedDigest = NetworkMidi2Protocol.ComputeUserAuthDigest(cryptoNonce, username, password);

        if (CompareDigests(clientDigest, expectedDigest))
        {
            _pendingCryptoNonces.TryRemove(sessionId, out _);
            _authFailCounts.TryRemove(sessionId, out _);

            AuthenticationResult?.Invoke(this, (sessionId, session.RemoteName, true));

            AcceptSession(sessionId, session.RemoteName, remoteEP, session.RemoteCapabilities);
            Log.Information("[NM2] 用户认证成功: {Name}, User={User}", session.RemoteName, username);
        }
        else
        {
            HandleAuthFailure(sessionId, session, remoteEP, true);
        }
    }

    private async void HandleAuthFailure(string sessionId, NetworkMidi2Protocol.SessionInfo session, IPEndPoint remoteEP, bool isUserAuth)
    {
        session.AuthFailCount++;
        session.LastAuthFail = DateTime.Now;

        int failCount = _authFailCounts.AddOrUpdate(sessionId, 1, (_, c) => c + 1);
        int delayMs = Math.Min(100 * (int)Math.Pow(2, failCount - 1), 30000);

        session.AuthDelayMs = delayMs;
        _sessions[sessionId] = session;

        await Task.Delay(delayMs);

        if (!_pendingCryptoNonces.TryGetValue(sessionId, out var cryptoNonce))
        {
            var bye = NetworkMidi2Protocol.CreateByeCommand(NetworkMidi2Protocol.ByeReason.AuthenticationFailed);
            SendPacket(NetworkMidi2Protocol.CreateUDPPacket(bye), remoteEP);
            _sessions.TryRemove(sessionId, out _);
            return;
        }

        if (failCount >= 5)
        {
            var bye = NetworkMidi2Protocol.CreateByeCommand(NetworkMidi2Protocol.ByeReason.AuthenticationFailed);
            SendPacket(NetworkMidi2Protocol.CreateUDPPacket(bye), remoteEP);
            _sessions.TryRemove(sessionId, out _);
            _pendingCryptoNonces.TryRemove(sessionId, out _);
            _authFailCounts.TryRemove(sessionId, out _);
            Log.Warning("[NM2] 认证失败次数过多: {Name}", session.RemoteName);
            AuthenticationResult?.Invoke(this, (sessionId, session.RemoteName, false));
            return;
        }

        byte[] reply;
        if (isUserAuth)
        {
            reply = NetworkMidi2Protocol.CreateInvitationReplyUserAuthRequired(
                cryptoNonce, _serviceName, _productInstanceId,
                NetworkMidi2Protocol.AuthenticationState.AuthDigestIncorrect);
        }
        else
        {
            reply = NetworkMidi2Protocol.CreateInvitationReplyAuthRequired(
                cryptoNonce, _serviceName, _productInstanceId,
                NetworkMidi2Protocol.AuthenticationState.AuthDigestIncorrect);
        }
        SendPacket(NetworkMidi2Protocol.CreateUDPPacket(reply), remoteEP);

        Log.Warning("[NM2] 认证失败: {Name}, 失败次数={Count}, 延迟={Delay}ms", session.RemoteName, failCount, delayMs);
        AuthenticationResult?.Invoke(this, (sessionId, session.RemoteName, false));
    }

    private static bool CompareDigests(byte[] a, byte[] b)
    {
        if (a == null || b == null || a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
        {
            if (a[i] != b[i]) return false;
        }
        return true;
    }

    private void AcceptSession(string sessionId, string name, IPEndPoint remoteEP, NetworkMidi2Protocol.InvitationCapabilities capabilities)
    {
        var now = DateTime.Now;
        var session = new NetworkMidi2Protocol.SessionInfo
        {
            Id = sessionId,
            RemoteName = name,
            RemoteHost = remoteEP.Address.ToString(),
            RemotePort = remoteEP.Port,
            State = NetworkMidi2Protocol.SessionState.Established,
            LastActivity = now,
            LastPingSent = DateTime.MinValue,
            LastPingReceived = now,
            LastDataSent = now,
            IdleIntervalMs = NetworkMidi2Protocol.IDLE_FIRST_INTERVAL_MS,
            PendingPingCount = 0,
            SendSequence = 0,
            ReceiveSequence = 0,
            RetransmitBuffer = new List<byte[]>(),
            RemoteCapabilities = capabilities,
            SupportsRetransmit = true,
        };

        _sessions[sessionId] = session;

        var replyPacket = NetworkMidi2Protocol.CreateInvitationReplyAccepted(_serviceName, _productInstanceId);
        SendPacket(NetworkMidi2Protocol.CreateUDPPacket(replyPacket), remoteEP);

        AddDevice(session);

        Log.Information("[NM2] 会话已接受: {Name} 来自 {RemoteEP}", name, remoteEP);
    }

    private bool OnInvitationReceived(string sessionId, string deviceName, string host, int port)
    {
        var handler = InvitationReceived;
        if (handler != null)
        {
            var args = (sessionId, deviceName, host, port);
            handler.Invoke(this, args);
            return false;
        }
        return true;
    }

    public void AcceptPendingInvitation(string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var session)) return;
        if (session.State != NetworkMidi2Protocol.SessionState.PendingInvitation) return;

        try
        {
            var ep = new IPEndPoint(IPAddress.Parse(session.RemoteHost), session.RemotePort);

            session.State = NetworkMidi2Protocol.SessionState.Established;
            session.LastActivity = DateTime.Now;
            session.LastPingReceived = DateTime.Now;
            session.RetransmitBuffer = new List<byte[]>();
            session.SupportsRetransmit = true;
            _sessions[sessionId] = session;

            var reply = NetworkMidi2Protocol.CreateInvitationReplyAccepted(_serviceName, _productInstanceId);
            SendPacket(NetworkMidi2Protocol.CreateUDPPacket(reply), ep);

            AddDevice(session);
            Log.Information("[NM2] 用户确认接受邀请: {Name}", session.RemoteName);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[NM2] 接受邀请失败");
        }
    }

    public void RejectPendingInvitation(string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var session)) return;
        if (session.State != NetworkMidi2Protocol.SessionState.PendingInvitation) return;

        try
        {
            var ep = new IPEndPoint(IPAddress.Parse(session.RemoteHost), session.RemotePort);

            var bye = NetworkMidi2Protocol.CreateByeCommand(NetworkMidi2Protocol.ByeReason.InvitationRejectedByUser);
            SendPacket(NetworkMidi2Protocol.CreateUDPPacket(bye), ep);

            _sessions.TryRemove(sessionId, out _);
            Log.Information("[NM2] 用户拒绝邀请: {Name}", session.RemoteName);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[NM2] 拒绝邀请失败");
        }
    }

    private void HandleInvitationReplyAccepted(byte[] payload, byte nameWords, IPEndPoint remoteEP, string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var session)) return;
        if (session.State != NetworkMidi2Protocol.SessionState.PendingInvitation) return;

        if (!NetworkMidi2Protocol.ParseInvitationReply(payload, nameWords, out var name, out var productInstanceId))
        {
            return;
        }

        session.State = NetworkMidi2Protocol.SessionState.Established;
        session.RemoteName = name;
        session.LastActivity = DateTime.Now;
        session.LastPingReceived = DateTime.Now;
        _sessions[sessionId] = session;

        AddDevice(session);

        Log.Information("[NM2] 会话已建立: {Name}", name);
    }

    private void HandleInvitationReplyPending(byte[] payload, byte nameWords, IPEndPoint remoteEP, string sessionId)
    {
        Log.Debug("[NM2] 收到 Invitation Pending: {SessionId}", sessionId);
    }

    private void HandleInvitationReplyAuthRequired(byte[] payload, byte nameWords, IPEndPoint remoteEP, string sessionId)
    {
        if (!NetworkMidi2Protocol.ParseInvitationReplyAuthRequired(payload, nameWords, out var cryptoNonce, out var umpEndpointName, out var productInstanceId, out var authState))
        {
            SendNAK(remoteEP, NetworkMidi2Protocol.NAKReason.CommandMalformed);
            return;
        }

        if (!_sessions.TryGetValue(sessionId, out var session))
        {
            SendNAK(remoteEP, NetworkMidi2Protocol.NAKReason.CommandNotExpected);
            return;
        }

        if (session.State != NetworkMidi2Protocol.SessionState.PendingInvitation)
        {
            SendNAK(remoteEP, NetworkMidi2Protocol.NAKReason.CommandNotExpected);
            return;
        }

        session.State = NetworkMidi2Protocol.SessionState.AuthenticationRequired;
        session.CryptoNonce = cryptoNonce;
        session.LastActivity = DateTime.Now;
        _sessions[sessionId] = session;

        AuthenticationRequired?.Invoke(this, (sessionId, cryptoNonce, false));

        Log.Information("[NM2] 收到认证要求: {Name}, CryptoNonce={Nonce}, State={State}", session.RemoteName, cryptoNonce, authState);
    }

    private void HandleInvitationReplyUserAuthRequired(byte[] payload, byte nameWords, IPEndPoint remoteEP, string sessionId)
    {
        if (!NetworkMidi2Protocol.ParseInvitationReplyAuthRequired(payload, nameWords, out var cryptoNonce, out var umpEndpointName, out var productInstanceId, out var authState))
        {
            SendNAK(remoteEP, NetworkMidi2Protocol.NAKReason.CommandMalformed);
            return;
        }

        if (!_sessions.TryGetValue(sessionId, out var session))
        {
            SendNAK(remoteEP, NetworkMidi2Protocol.NAKReason.CommandNotExpected);
            return;
        }

        if (session.State != NetworkMidi2Protocol.SessionState.PendingInvitation &&
            session.State != NetworkMidi2Protocol.SessionState.AuthenticationRequired)
        {
            SendNAK(remoteEP, NetworkMidi2Protocol.NAKReason.CommandNotExpected);
            return;
        }

        session.State = NetworkMidi2Protocol.SessionState.AuthenticationRequired;
        session.CryptoNonce = cryptoNonce;
        session.LastActivity = DateTime.Now;
        _sessions[sessionId] = session;

        AuthenticationRequired?.Invoke(this, (sessionId, cryptoNonce, true));

        Log.Information("[NM2] 收到用户认证要求: {Name}, CryptoNonce={Nonce}, State={State}", session.RemoteName, cryptoNonce, authState);
    }

    public void SendAuthentication(string sessionId, string sharedSecret)
    {
        if (!_sessions.TryGetValue(sessionId, out var session)) return;
        if (session.State != NetworkMidi2Protocol.SessionState.AuthenticationRequired) return;
        if (string.IsNullOrEmpty(session.CryptoNonce)) return;

        try
        {
            var ep = new IPEndPoint(IPAddress.Parse(session.RemoteHost), session.RemotePort);
            var digest = NetworkMidi2Protocol.ComputeAuthDigest(session.CryptoNonce, sharedSecret);
            var cmd = NetworkMidi2Protocol.CreateInvitationWithAuth(digest);
            SendPacket(NetworkMidi2Protocol.CreateUDPPacket(cmd), ep);

            Log.Information("[NM2] 已发送认证: {SessionId}", sessionId);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[NM2] 发送认证失败");
        }
    }

    public void SendUserAuthentication(string sessionId, string username, string password)
    {
        if (!_sessions.TryGetValue(sessionId, out var session)) return;
        if (session.State != NetworkMidi2Protocol.SessionState.AuthenticationRequired) return;
        if (string.IsNullOrEmpty(session.CryptoNonce)) return;

        try
        {
            var ep = new IPEndPoint(IPAddress.Parse(session.RemoteHost), session.RemotePort);
            var digest = NetworkMidi2Protocol.ComputeUserAuthDigest(session.CryptoNonce, username, password);
            var cmd = NetworkMidi2Protocol.CreateInvitationWithUserAuth(digest, username);
            SendPacket(NetworkMidi2Protocol.CreateUDPPacket(cmd), ep);

            session.PendingUsername = username;
            _sessions[sessionId] = session;

            Log.Information("[NM2] 已发送用户认证: {SessionId}, User={User}", sessionId, username);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[NM2] 发送用户认证失败");
        }
    }

    private void HandlePing(byte[] payload, IPEndPoint remoteEP)
    {
        if (NetworkMidi2Protocol.ParsePingCommand(payload, out var pingId))
        {
            var reply = NetworkMidi2Protocol.CreatePingReplyCommand(pingId);
            SendPacket(NetworkMidi2Protocol.CreateUDPPacket(reply), remoteEP);
            Log.Information("[NM2] 收到 Ping -> 发送 Pong: {PingId} -> {RemoteEP}", pingId, remoteEP);
        }
    }

    private void HandlePingReply(byte[] payload, string sessionId)
    {
        if (!NetworkMidi2Protocol.ParsePingCommand(payload, out var pingId)) return;

        if (_pendingPings.TryRemove(pingId, out _))
        {
            if (_sessions.TryGetValue(sessionId, out var session))
            {
                session.LastPingReceived = DateTime.Now;
                session.LastActivity = DateTime.Now;
                session.PendingPingCount = 0;
                _sessions[sessionId] = session;
                Log.Debug("[NM2] 收到 Ping Reply: {PingId}", pingId);
            }
        }
    }

    private void HandleUMPData(byte cmdSpecific1, byte cmdSpecific2, byte[] payload, IPEndPoint remoteEP, string sessionId)
    {
        if (!NetworkMidi2Protocol.ParseUMPDataCommand(cmdSpecific1, cmdSpecific2, payload, out var sequenceNumber, out var umpData))
            return;

        if (!_sessions.TryGetValue(sessionId, out var session))
        {
            var now = DateTime.Now;
            session = new NetworkMidi2Protocol.SessionInfo
            {
                Id = sessionId,
                RemoteName = $"UMP Device ({remoteEP.Address})",
                RemoteHost = remoteEP.Address.ToString(),
                RemotePort = remoteEP.Port,
                State = NetworkMidi2Protocol.SessionState.Established,
                LastActivity = now,
                LastPingReceived = now,
                LastDataSent = now,
                IdleIntervalMs = NetworkMidi2Protocol.IDLE_FIRST_INTERVAL_MS,
                PendingPingCount = 0,
                SendSequence = 0,
                ReceiveSequence = sequenceNumber,
                RetransmitBuffer = new List<byte[]>(),
            };
            _sessions[sessionId] = session;
            AddDevice(session);
        }
        else
        {
            CheckSequenceNumber(ref session, sequenceNumber);
            session.LastActivity = DateTime.Now;
            session.PacketsReceived++;
            _sessions[sessionId] = session;
        }

        ProcessUMPData(umpData, session);

        UpdateDeviceTransmit(session);
    }

    private void CheckSequenceNumber(ref NetworkMidi2Protocol.SessionInfo session, ushort sequenceNumber)
    {
        if (session.ReceiveSequence == 0)
        {
            session.ReceiveSequence = sequenceNumber;
            return;
        }

        ushort expectedSeq = (ushort)(session.ReceiveSequence + 1);

        if (sequenceNumber == expectedSeq)
        {
            session.ReceiveSequence = sequenceNumber;
            session.MissingSequences?.Remove(sequenceNumber);
            session.RetransmitRetryCount = 0;
        }
        else if (sequenceNumber == session.ReceiveSequence)
        {
            session.PacketsDuplicate++;
            Log.Debug("[NM2] 重复包 (FEC): Seq={Seq}", sequenceNumber);
        }
        else if (IsSequenceNewer(sequenceNumber, session.ReceiveSequence))
        {
            int lost = CountPacketsLost(session.ReceiveSequence, sequenceNumber);

            string sId = session.Id;
            for (int i = 1; i <= lost; i++)
            {
                ushort missingSeq = (ushort)(session.ReceiveSequence + i);
                session.MissingSequences ??= new List<ushort>();
                if (!session.MissingSequences.Contains(missingSeq))
                {
                    session.MissingSequences.Add(missingSeq);
                    Log.Debug("[NM2] 检测到丢包: Seq={Seq}", missingSeq);

                    ushort capturedSeq = missingSeq;
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(NetworkMidi2Protocol.RETRANSMIT_DELAY_MS);
                        if (_sessions.TryGetValue(sId, out var s) && s.MissingSequences?.Contains(capturedSeq) == true)
                        {
                            RequestRetransmitInternal(sId, capturedSeq);
                        }
                    });
                }
            }

            session.PacketsLost += lost;
            session.ReceiveSequence = sequenceNumber;
            Log.Warning("[NM2] 丢包: 期望 {Expected}, 收到 {Actual}, 丢失 {Lost}", expectedSeq, sequenceNumber, lost);
        }
        else if (IsSequenceNewer(sequenceNumber, (ushort)(session.ReceiveSequence - NetworkMidi2Protocol.FEC_REDUNDANCY - 1)))
        {
            session.PacketsRecovered++;
            Log.Debug("[NM2] FEC 恢复: Seq={Seq}", sequenceNumber);
        }
        else
        {
            session.PacketsDuplicate++;
        }
    }

    private void RequestRetransmitInternal(string sessionId, ushort missingSeq)
    {
        if (!_sessions.TryGetValue(sessionId, out var session)) return;
        if (session.State != NetworkMidi2Protocol.SessionState.Established) return;
        if (session.RetransmitRetryCount >= NetworkMidi2Protocol.RETRANSMIT_MAX_RETRY)
        {
            Log.Warning("[NM2] 重传请求达到最大重试次数: Seq={Seq}", missingSeq);
            return;
        }

        try
        {
            var ep = new IPEndPoint(IPAddress.Parse(session.RemoteHost), session.RemotePort);
            var request = NetworkMidi2Protocol.CreateRetransmitRequestCommand(missingSeq, 1);
            SendPacket(NetworkMidi2Protocol.CreateUDPPacket(request), ep);

            session.LastRetransmitRequest = DateTime.Now;
            session.RetransmitRetryCount++;
            _sessions[sessionId] = session;

            Log.Debug("[NM2] 自动发送重传请求: Seq={Seq}", missingSeq);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[NM2] 重传请求发送失败");
        }
    }

    private static bool IsSequenceNewer(ushort received, ushort current)
    {
        int diff = received - current;
        return diff > 0 && diff < 32768;
    }

    private static int CountPacketsLost(ushort lastSeq, ushort currentSeq)
    {
        int diff = currentSeq - lastSeq;
        if (diff < 0) diff += 65536;
        return Math.Max(0, diff - 1);
    }

    private void ProcessUMPData(byte[] umpData, NetworkMidi2Protocol.SessionInfo session)
    {
        if (umpData == null || umpData.Length == 0)
        {
            Log.Debug("[NM2] 收到 Zero Length UMP Data");
            return;
        }

        if (umpData.Length < 4) return;

        string stableId = GetStableDeviceId(session.RemoteName, session.RemoteHost);
        Log.Debug("[NM2] 处理UMP数据: {Length} 字节", umpData.Length);

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

    private void HandleRetransmitRequest(byte[] payload, IPEndPoint remoteEP, string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var session)) return;

        if (!NetworkMidi2Protocol.ParseRetransmitRequest(payload, out var seqNum, out var count))
            return;

        Log.Debug("[NM2] 重传请求: Seq={Seq}, Count={Count}", seqNum, count);

        if (session.RetransmitBuffer.Count == 0)
        {
            var error = NetworkMidi2Protocol.CreateRetransmitErrorCommand(
                NetworkMidi2Protocol.RetransmitErrorReason.BufferDoesNotContainSequence, seqNum);
            SendPacket(NetworkMidi2Protocol.CreateUDPPacket(error), remoteEP);
            return;
        }

        var packetsToSend = new List<byte[]>();
        int bufferStartSeq = (session.SendSequence - (ushort)session.RetransmitBuffer.Count + 1) & 0xFFFF;

        int startIndex = (seqNum - bufferStartSeq) & 0xFFFF;
        if (startIndex < 0 || startIndex >= session.RetransmitBuffer.Count)
        {
            var error = NetworkMidi2Protocol.CreateRetransmitErrorCommand(
                NetworkMidi2Protocol.RetransmitErrorReason.BufferDoesNotContainSequence, seqNum);
            SendPacket(NetworkMidi2Protocol.CreateUDPPacket(error), remoteEP);
            return;
        }

        int sendCount = count == 0 ? session.RetransmitBuffer.Count - startIndex : Math.Min(count, session.RetransmitBuffer.Count - startIndex);

        for (int i = 0; i < sendCount; i++)
        {
            packetsToSend.Add(session.RetransmitBuffer[startIndex + i]);
        }

        if (packetsToSend.Count > 0)
        {
            SendPacket(NetworkMidi2Protocol.CreateUDPPacket(packetsToSend.ToArray()), remoteEP);
        }
    }

    private void HandleRetransmitError(byte[] payload, string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var session)) return;

        Log.Warning("[NM2] 收到 Retransmit Error: {SessionId}", sessionId);

        SendAllNotesOff(sessionId);

        int lostPackets = session.MissingSequences?.Count ?? 0;
        RetransmitErrorReceived?.Invoke(this, (sessionId, lostPackets));

        OnStatusChanged($"重传错误: {session.RemoteName}, 丢失 {lostPackets} 个包");
    }

    private void HandleSessionReset(IPEndPoint remoteEP, string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var session)) return;

        if (session.State != NetworkMidi2Protocol.SessionState.Established)
        {
            var bye = NetworkMidi2Protocol.CreateByeCommand(NetworkMidi2Protocol.ByeReason.SessionNotEstablished);
            SendPacket(NetworkMidi2Protocol.CreateUDPPacket(bye), remoteEP);
            return;
        }

        session.SendSequence = 0;
        session.ReceiveSequence = 0;
        session.RetransmitBuffer?.Clear();
        session.MissingSequences?.Clear();
        session.RetransmitRetryCount = 0;
        session.LastActivity = DateTime.Now;
        _sessions[sessionId] = session;

        var reply = NetworkMidi2Protocol.CreateSessionResetReplyCommand();
        SendPacket(NetworkMidi2Protocol.CreateUDPPacket(reply), remoteEP);

        SendAllNotesOff(sessionId);

        Log.Information("[NM2] 会话重置: {SessionId}", sessionId);
    }

    private void HandleSessionResetReply(string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var session)) return;

        if (session.State == NetworkMidi2Protocol.SessionState.PendingSessionReset)
        {
            session.State = NetworkMidi2Protocol.SessionState.Established;
            session.PendingRetryCount = 0;
        }

        session.SendSequence = 0;
        session.ReceiveSequence = 0;
        session.RetransmitBuffer?.Clear();
        session.MissingSequences?.Clear();
        _sessions[sessionId] = session;

        Log.Information("[NM2] 会话重置确认: {SessionId}", sessionId);
    }

    private void HandleBye(byte[] payload, IPEndPoint remoteEP, string sessionId)
    {
        NetworkMidi2Protocol.ParseByeCommand(payload, out var reason, out var message);

        if (_sessions.TryRemove(sessionId, out var session))
        {
            var reply = NetworkMidi2Protocol.CreateByeReplyCommand();
            SendPacket(NetworkMidi2Protocol.CreateUDPPacket(reply), remoteEP);

            SendAllNotesOff(sessionId);

            string stableId = GetStableDeviceId(session.RemoteName, session.RemoteHost);
            RemoveDevice(stableId);

            Log.Information("[NM2] 会话结束: {Name}, 原因: {Reason}", session.RemoteName, reason);
            OnStatusChanged($"会话结束: {session.RemoteName} (原因: {reason})");
        }
    }

    private void HandleByeReply(string sessionId)
    {
        if (_sessions.TryRemove(sessionId, out var session))
        {
            Log.Debug("[NM2] 收到 Bye Reply: {SessionId}", sessionId);
        }
    }

    private void HandleNAK(byte[] payload, string sessionId)
    {
        NetworkMidi2Protocol.ParseNAKCommand(payload, out var reason, out var originalHeader, out var message);
        Log.Warning("[NM2] 收到 NAK: 原因={Reason}, 消息={Message}", reason, message);
    }

    private void SendNAK(IPEndPoint remoteEP, NetworkMidi2Protocol.NAKReason reason, byte[]? originalCommand = null)
    {
        var nak = NetworkMidi2Protocol.CreateNAKCommand(reason, originalCommand ?? Array.Empty<byte>());
        SendPacket(NetworkMidi2Protocol.CreateUDPPacket(nak), remoteEP);
    }

    private void SendBye(NetworkMidi2Protocol.SessionInfo session, NetworkMidi2Protocol.ByeReason reason)
    {
        try
        {
            var ep = new IPEndPoint(IPAddress.Parse(session.RemoteHost), session.RemotePort);
            var bye = NetworkMidi2Protocol.CreateByeCommand(reason);
            SendPacket(NetworkMidi2Protocol.CreateUDPPacket(bye), ep);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[NM2] 发送 Bye 失败");
        }
    }

    public async Task<bool> InviteDevice(string host, int port, string? name = null)
    {
        try
        {
            var ep = new IPEndPoint(IPAddress.Parse(host), port);
            string sessionId = GetSessionId(ep);

            if (_sessions.ContainsKey(sessionId))
            {
                OnStatusChanged($"已连接到 {host}:{port}");
                return false;
            }

            var session = new NetworkMidi2Protocol.SessionInfo
            {
                Id = sessionId,
                RemoteName = name ?? $"Device ({host})",
                RemoteHost = host,
                RemotePort = port,
                State = NetworkMidi2Protocol.SessionState.PendingInvitation,
                LastActivity = DateTime.Now,
                SendSequence = 0,
                ReceiveSequence = 0,
                RetransmitBuffer = new List<byte[]>(),
            };

            _sessions[sessionId] = session;

            var capabilities = NetworkMidi2Protocol.InvitationCapabilities.All;
            var invite = NetworkMidi2Protocol.CreateInvitationCommand(_serviceName, _productInstanceId, capabilities);

            for (int i = 0; i < NetworkMidi2Protocol.INVITATION_RETRY_COUNT; i++)
            {
                SendPacket(NetworkMidi2Protocol.CreateUDPPacket(invite), ep);

                await Task.Delay(NetworkMidi2Protocol.INVITATION_RETRY_INTERVAL_MS);

                if (_sessions.TryGetValue(sessionId, out var s))
                {
                    if (s.State == NetworkMidi2Protocol.SessionState.Established)
                    {
                        return true;
                    }
                    if (s.State == NetworkMidi2Protocol.SessionState.AuthenticationRequired)
                    {
                        OnStatusChanged($"需要认证: {host}:{port}");
                        return false;
                    }
                }
            }

            _sessions.TryRemove(sessionId, out _);
            OnStatusChanged($"连接超时: {host}:{port}");
            return false;
        }
        catch (Exception ex)
        {
            OnStatusChanged($"连接失败: {ex.Message}");
            return false;
        }
    }

    public void EndSession(string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var session)) return;

        _ = Task.Run(async () =>
        {
            session.State = NetworkMidi2Protocol.SessionState.PendingBye;
            session.PendingRetryCount = 0;
            _sessions[sessionId] = session;

            for (int i = 0; i < NetworkMidi2Protocol.COMMAND_MAX_RETRY; i++)
            {
                if (!_sessions.ContainsKey(sessionId)) break;

                SendBye(session, NetworkMidi2Protocol.ByeReason.UserTerminated);

                await Task.Delay(NetworkMidi2Protocol.COMMAND_RETRY_INTERVAL_MS);

                if (!_sessions.TryGetValue(sessionId, out var s) || s.State != NetworkMidi2Protocol.SessionState.PendingBye)
                    break;

                session.PendingRetryCount++;
                _sessions[sessionId] = session;
            }

            if (_sessions.TryRemove(sessionId, out var removed))
            {
                string stableId = GetStableDeviceId(removed.RemoteName, removed.RemoteHost);
                RemoveDevice(stableId);
                Log.Information("[NM2] 会话已结束: {Name}", removed.RemoteName);
            }
        });
    }

    public void RequestSessionReset(string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var session)) return;
        if (session.State != NetworkMidi2Protocol.SessionState.Established) return;

        _ = Task.Run(async () =>
        {
            session.State = NetworkMidi2Protocol.SessionState.PendingSessionReset;
            session.PendingRetryCount = 0;
            session.PendingCommandSent = DateTime.Now;
            _sessions[sessionId] = session;

            var ep = new IPEndPoint(IPAddress.Parse(session.RemoteHost), session.RemotePort);

            for (int i = 0; i < NetworkMidi2Protocol.COMMAND_MAX_RETRY; i++)
            {
                if (!_sessions.TryGetValue(sessionId, out var s)) break;
                if (s.State != NetworkMidi2Protocol.SessionState.PendingSessionReset) break;

                var reset = NetworkMidi2Protocol.CreateSessionResetCommand();
                SendPacket(NetworkMidi2Protocol.CreateUDPPacket(reset), ep);

                await Task.Delay(NetworkMidi2Protocol.COMMAND_RETRY_INTERVAL_MS);

                if (_sessions.TryGetValue(sessionId, out var current) && current.State == NetworkMidi2Protocol.SessionState.Established)
                    break;

                session.PendingRetryCount++;
                _sessions[sessionId] = session;
            }

            if (_sessions.TryGetValue(sessionId, out var final) && final.State == NetworkMidi2Protocol.SessionState.PendingSessionReset)
            {
                Log.Warning("[NM2] Session Reset 超时: {SessionId}", sessionId);
                EndSession(sessionId);
            }
        });
    }

    private void SendAllNotesOff(string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var session)) return;

        var ep = new IPEndPoint(IPAddress.Parse(session.RemoteHost), session.RemotePort);

        for (byte channel = 0; channel < 16; channel++)
        {
            byte[] allNotesOff = new byte[] { (byte)(0xB0 | channel), 0x7B, 0x00 };
            var ump = ConvertMidi1ToUMP(allNotesOff);
            if (ump.Length > 0)
            {
                session.SendSequence++;
                var cmd = NetworkMidi2Protocol.CreateUMPDataCommand(session.SendSequence, ump);
                SendPacket(NetworkMidi2Protocol.CreateUDPPacket(cmd), ep);
            }
        }

        _sessions[sessionId] = session;
        Log.Debug("[NM2] 已发送 All Notes Off: {SessionId}", sessionId);
    }

    public void SendUMP(string sessionId, byte[] umpData)
    {
        if (!_sessions.TryGetValue(sessionId, out var session)) return;
        if (session.State != NetworkMidi2Protocol.SessionState.Established) return;

        var ep = new IPEndPoint(IPAddress.Parse(session.RemoteHost), session.RemotePort);

        session.SendSequence++;
        session.PacketsSent++;
        session.LastDataSent = DateTime.Now;
        session.IsIdle = false;
        session.IdleIntervalMs = NetworkMidi2Protocol.IDLE_FIRST_INTERVAL_MS;

        var cmd = NetworkMidi2Protocol.CreateUMPDataCommand(session.SendSequence, umpData);

        UpdateRetransmitBuffer(session, cmd);

        var fecPackets = BuildFEPackets(session, cmd);
        SendPacket(NetworkMidi2Protocol.CreateUDPPacket(fecPackets), ep);

        _sessions[sessionId] = session;
        UpdateDeviceTransmit(session);
    }

    private byte[][] BuildFEPackets(NetworkMidi2Protocol.SessionInfo session, byte[] currentCmd)
    {
        var packets = new List<byte[]>();

        int fecCount = Math.Min(NetworkMidi2Protocol.FEC_REDUNDANCY, session.RetransmitBuffer.Count - 1);
        for (int i = fecCount; i >= 1; i--)
        {
            int index = session.RetransmitBuffer.Count - 1 - i;
            if (index >= 0 && index < session.RetransmitBuffer.Count)
            {
                packets.Add(session.RetransmitBuffer[index]);
            }
        }

        packets.Add(currentCmd);

        return packets.ToArray();
    }

    private NetworkMidi2Protocol.SessionInfo SendZeroLengthData(NetworkMidi2Protocol.SessionInfo session)
    {
        if (session.State != NetworkMidi2Protocol.SessionState.Established) return session;

        var ep = new IPEndPoint(IPAddress.Parse(session.RemoteHost), session.RemotePort);

        session.SendSequence++;
        session.LastDataSent = DateTime.Now;

        var cmd = NetworkMidi2Protocol.CreateUMPDataCommand(session.SendSequence, Array.Empty<byte>());

        UpdateRetransmitBuffer(session, cmd);

        var fecPackets = BuildFEPackets(session, cmd);
        SendPacket(NetworkMidi2Protocol.CreateUDPPacket(fecPackets), ep);

        if (session.IdleIntervalMs < NetworkMidi2Protocol.IDLE_MAX_INTERVAL_MS)
        {
            session.IdleIntervalMs = Math.Min(
                session.IdleIntervalMs * 2,
                NetworkMidi2Protocol.IDLE_MAX_INTERVAL_MS);
        }

        return session;
    }

    private void UpdateRetransmitBuffer(NetworkMidi2Protocol.SessionInfo session, byte[] cmd)
    {
        session.RetransmitBuffer ??= new List<byte[]>();
        session.RetransmitBuffer.Add(cmd);

        while (session.RetransmitBuffer.Count > NetworkMidi2Protocol.RETRANSMIT_BUFFER_SIZE)
        {
            session.RetransmitBuffer.RemoveAt(0);
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

        if (status >= 0xF0)
        {
            return Array.Empty<byte>();
        }

        int messageType = 0x2;

        var ump = new byte[4];
        ump[0] = (byte)((messageType << 4) | 0);
        ump[1] = status;

        if (midiData.Length >= 2)
            ump[2] = midiData[1];
        if (midiData.Length >= 3)
            ump[3] = midiData[2];

        return ump;
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
                    if (session.State == NetworkMidi2Protocol.SessionState.Established)
                    {
                        try
                        {
                            var ep = new IPEndPoint(IPAddress.Parse(session.RemoteHost), session.RemotePort);
                            uint pingId = (uint)Random.Shared.Next();

                            _pendingPings[pingId] = DateTime.Now;

                            var ping = NetworkMidi2Protocol.CreatePingCommand(pingId);
                            SendPacket(NetworkMidi2Protocol.CreateUDPPacket(ping), ep);

                            if (_sessions.TryGetValue(session.Id, out var s))
                            {
                                s.LastPingSent = DateTime.Now;
                                s.PendingPingCount++;
                                _sessions[session.Id] = s;
                            }

                            Log.Debug("[NM2] 发送 Ping: {PingId} -> {Host}:{Port}", pingId, session.RemoteHost, session.RemotePort);
                        }
                        catch (Exception ex)
                        {
                            Log.Debug(ex, "[NM2] Ping 发送失败");
                        }
                    }
                }
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch { break; }
        }
    }

    private async void SessionTimeoutCheckLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(1000, ct);

                var now = DateTime.Now;
                var timeoutSessions = new List<string>();

                foreach (var kvp in _sessions)
                {
                    var session = kvp.Value;

                    if (session.PendingPingCount > NetworkMidi2Protocol.PING_TIMEOUT_COUNT)
                    {
                        timeoutSessions.Add(kvp.Key);
                        continue;
                    }

                    var inactiveMs = (now - session.LastActivity).TotalMilliseconds;
                    if (inactiveMs > NetworkMidi2Protocol.SESSION_TIMEOUT_MS)
                    {
                        timeoutSessions.Add(kvp.Key);
                    }
                }

                foreach (var sessionId in timeoutSessions)
                {
                    if (_sessions.TryRemove(sessionId, out var session))
                    {
                        string stableId = GetStableDeviceId(session.RemoteName, session.RemoteHost);
                        RemoveDevice(stableId);

                        var stats = $"Sent:{session.PacketsSent} Recv:{session.PacketsReceived} Lost:{session.PacketsLost} Dup:{session.PacketsDuplicate}";
                        Log.Warning("[NM2] 会话超时断开: {Name} [{Stats}]", session.RemoteName, stats);
                        OnStatusChanged($"会话超时: {session.RemoteName}");
                    }
                }
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch { break; }
            }
    }

    private async void IdlePeriodLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(100, ct);

                var now = DateTime.Now;

                foreach (var kvp in _sessions.ToList())
                {
                    var session = kvp.Value;
                    if (session.State != NetworkMidi2Protocol.SessionState.Established) continue;

                    var idleMs = (now - session.LastDataSent).TotalMilliseconds;

                    if (idleMs >= session.IdleIntervalMs)
                    {
                        session = SendZeroLengthData(session);
                        _sessions[kvp.Key] = session;
                    }
                }
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch { break; }
        }
    }

    private async void PendingInvitationTimeoutLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(1000, ct);

                var now = DateTime.Now;
                var expiredInvitations = new List<string>();

                foreach (var kvp in _sessions)
                {
                    var session = kvp.Value;
                    if (session.State != NetworkMidi2Protocol.SessionState.PendingInvitation) continue;

                    var pendingMs = (now - session.LastActivity).TotalMilliseconds;
                    if (pendingMs > NetworkMidi2Protocol.PENDING_INVITATION_TIMEOUT_MS)
                    {
                        expiredInvitations.Add(kvp.Key);
                    }
                }

                foreach (var sessionId in expiredInvitations)
                {
                    if (_sessions.TryRemove(sessionId, out var session))
                    {
                        try
                        {
                            var ep = new IPEndPoint(IPAddress.Parse(session.RemoteHost), session.RemotePort);
                            var bye = NetworkMidi2Protocol.CreateByeCommand(NetworkMidi2Protocol.ByeReason.Timeout);
                            SendPacket(NetworkMidi2Protocol.CreateUDPPacket(bye), ep);
                        }
                        catch { }

                        Log.Information("[NM2] 待确认邀请超时: {Name}", session.RemoteName);
                        OnStatusChanged($"邀请超时: {session.RemoteName}");
                    }
                }
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch { break; }
        }
    }

    private async void RequestRetransmit(string sessionId, ushort missingSeq)
    {
        if (!_sessions.TryGetValue(sessionId, out var session)) return;
        if (session.State != NetworkMidi2Protocol.SessionState.Established) return;

        var ep = new IPEndPoint(IPAddress.Parse(session.RemoteHost), session.RemotePort);

        session.MissingSequences ??= new List<ushort>();
        if (session.MissingSequences.Contains(missingSeq)) return;

        session.MissingSequences.Add(missingSeq);
        _sessions[sessionId] = session;

        await Task.Delay(NetworkMidi2Protocol.RETRANSMIT_DELAY_MS);

        if (!_sessions.TryGetValue(sessionId, out var currentSession)) return;

        if (currentSession.ReceiveSequence >= missingSeq)
        {
            currentSession.MissingSequences?.Remove(missingSeq);
            _sessions[sessionId] = currentSession;
            return;
        }

        var request = NetworkMidi2Protocol.CreateRetransmitRequestCommand(missingSeq, 1);
        SendPacket(NetworkMidi2Protocol.CreateUDPPacket(request), ep);

        currentSession.LastRetransmitRequest = DateTime.Now;
        currentSession.RetransmitRetryCount++;
        _sessions[sessionId] = currentSession;

        Log.Debug("[NM2] 发送重传请求: Seq={Seq}, Retry={Retry}", missingSeq, currentSession.RetransmitRetryCount);
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
            DispatcherService.RunOnUIThread(() =>
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
        catch (Exception ex)
        {
            Log.Debug(ex, "[NM2] 添加设备失败");
        }
    }

    private void RemoveDevice(string sessionId)
    {
        try
        {
            DispatcherService.RunOnUIThread(() =>
            {
                var device = InputDevices.FirstOrDefault(d => d.Id == sessionId);
                if (device != null)
                {
                    InputDevices.Remove(device);
                    DeviceRemoved?.Invoke(this, device);
                }
            });
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[NM2] 移除设备失败: {SessionId}", sessionId);
        }
    }

    private void UpdateDeviceTransmit(NetworkMidi2Protocol.SessionInfo session)
    {
        string stableId = GetStableDeviceId(session.RemoteName, session.RemoteHost);
        if (InputDevices.FirstOrDefault(d => d.Id == stableId) is { } device)
        {
            device.PulseTransmit();
            DeviceUpdated?.Invoke(this, device);
        }
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