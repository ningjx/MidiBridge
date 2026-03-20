using System.Net;
using System.Net.Sockets;
using System.Text;
using MidiBridge.Services.NetworkMidi2;

namespace Test.Tests.NetworkMidi2;

public class NetworkMidi2Client : IDisposable
{
    private UdpClient? _udpClient;
    private CancellationTokenSource? _cts;
    private uint _localSSRC;
    private uint _remoteSSRC;
    private ushort _sendSequence;
    private ushort _receiveSequence;
    private IPEndPoint? _remoteEP;
    private string _remoteName = "";
    private string _cryptoNonce = "";
    private uint _lastPingId;
    private DateTime _lastPingTime;
    private int _pendingPingCount;
    
    public event Action<string>? OnLog;
    public event Action<byte[]>? OnMidiReceived;
    
    public bool IsConnected => _remoteEP != null;
    public string RemoteName => _remoteName;
    public uint LocalSSRC => _localSSRC;
    public uint RemoteSSRC => _remoteSSRC;
    
    public NetworkMidi2Client()
    {
        _localSSRC = (uint)Random.Shared.Next(1, int.MaxValue);
    }
    
    public void StartDiscovery(int listenPort = 5353)
    {
        try
        {
            _udpClient = new UdpClient(listenPort);
            _udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            
            var multicastAddr = IPAddress.Parse("224.0.0.251");
            _udpClient.JoinMulticastGroup(multicastAddr);
            
            _cts = new CancellationTokenSource();
            
            Task.Run(() => DiscoveryLoop(_cts.Token));
            
            Log($"[Discovery] Started listening on port {listenPort}");
        }
        catch (Exception ex)
        {
            Log($"[Discovery] Start failed: {ex.Message}");
        }
    }
    
    public void SendDiscoveryQuery()
    {
        if (_udpClient == null) return;
        
        try
        {
            var query = CreateMDNSQuery();
            var multicastEP = new IPEndPoint(IPAddress.Parse("224.0.0.251"), 5353);
            _udpClient.Send(query, query.Length, multicastEP);
            Log("[Discovery] Sent mDNS query for _midi2._udp");
        }
        catch (Exception ex)
        {
            Log($"[Discovery] Query failed: {ex.Message}");
        }
    }
    
    private async void DiscoveryLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _udpClient != null)
        {
            try
            {
                var result = await _udpClient.ReceiveAsync();
                ProcessMDNSPacket(result.Buffer, result.RemoteEndPoint);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset) { }
            catch (Exception ex)
            {
                if (!ct.IsCancellationRequested)
                    Log($"[Discovery] Error: {ex.Message}");
            }
        }
    }
    
    private void ProcessMDNSPacket(byte[] data, IPEndPoint remoteEP)
    {
        if (data.Length < 12) return;
        
        try
        {
            ushort flags = (ushort)((data[2] << 8) | data[3]);
            bool isResponse = (flags & 0x8000) != 0;
            
            if (!isResponse) return;
            
            ushort answerCount = (ushort)((data[6] << 8) | data[7]);
            
            int offset = 12;
            ushort questionCount = (ushort)((data[4] << 8) | data[5]);
            
            for (int i = 0; i < questionCount && offset < data.Length; i++)
            {
                offset = SkipName(data, offset);
                if (offset + 4 > data.Length) return;
                offset += 4;
            }
            
            for (int i = 0; i < answerCount && offset < data.Length; i++)
            {
                string name = ReadName(data, ref offset);
                if (string.IsNullOrEmpty(name) || offset + 10 > data.Length) return;
                
                ushort recordType = (ushort)((data[offset] << 8) | data[offset + 1]);
                ushort recordLength = (ushort)((data[offset + 8] << 8) | data[offset + 9]);
                offset += 10;
                
                if (recordType == 33 && name.Contains("_midi2._udp") && recordLength >= 7)
                {
                    ushort port = (ushort)((data[offset + 4] << 8) | data[offset + 5]);
                    
                    if (port != 0)
                    {
                        Log($"[Discovery] Found: {name} at {remoteEP.Address}:{port}");
                    }
                }
                
                offset += recordLength;
            }
        }
        catch { }
    }
    
    public async Task<bool> ConnectAsync(string host, int port, string name = "TestClient", string productId = "TestProduct")
    {
        if (_udpClient == null)
        {
            _udpClient = new UdpClient();
            _cts = new CancellationTokenSource();
            Task.Run(() => ReceiveLoop(_cts.Token));
        }
        
        try
        {
            var ip = IPAddress.Parse(host);
            _remoteEP = new IPEndPoint(ip, port);
            
            for (int retry = 0; retry < 3; retry++)
            {
                var invitation = NetworkMidi2Protocol.CreateInvitationCommand(name, productId, NetworkMidi2Protocol.InvitationCapabilities.All);
                var packet = NetworkMidi2Protocol.CreateUDPPacket(invitation);
                _udpClient.Send(packet, packet.Length, _remoteEP);
                Log($"[NM2] Sent INV to {host}:{port} (attempt {retry + 1})");
                
                _remoteSSRC = 0;
                _cryptoNonce = "";
                
                var startTime = DateTime.Now;
                while ((DateTime.Now - startTime).TotalSeconds < 2)
                {
                    await Task.Delay(100);
                    if (_remoteSSRC != 0)
                    {
                        _remoteName = name;
                        Log($"[NM2] Connected! SSRC: 0x{_localSSRC:X8}");
                        return true;
                    }
                    if (!string.IsNullOrEmpty(_cryptoNonce))
                    {
                        Log($"[NM2] Auth required, nonce: {_cryptoNonce}");
                        return false;
                    }
                }
            }
            
            Log("[NM2] Connection timeout");
            return false;
        }
        catch (Exception ex)
        {
            Log($"[NM2] Connect failed: {ex.Message}");
            return false;
        }
    }
    
    public async Task<bool> ConnectWithAuthAsync(string host, int port, string name, string productId, string sharedSecret)
    {
        if (_udpClient == null)
        {
            _udpClient = new UdpClient();
            _cts = new CancellationTokenSource();
            Task.Run(() => ReceiveLoop(_cts.Token));
        }
        
        try
        {
            var ip = IPAddress.Parse(host);
            _remoteEP = new IPEndPoint(ip, port);
            
            var invitation = NetworkMidi2Protocol.CreateInvitationCommand(name, productId, NetworkMidi2Protocol.InvitationCapabilities.SupportsAuth);
            var packet = NetworkMidi2Protocol.CreateUDPPacket(invitation);
            _udpClient.Send(packet, packet.Length, _remoteEP);
            Log($"[NM2] Sent INV with auth capability");
            
            _cryptoNonce = "";
            
            var startTime = DateTime.Now;
            while ((DateTime.Now - startTime).TotalSeconds < 5)
            {
                await Task.Delay(100);
                
                if (_remoteSSRC != 0)
                {
                    Log($"[NM2] Connected without auth!");
                    return true;
                }
                
                if (!string.IsNullOrEmpty(_cryptoNonce))
                {
                    Log($"[NM2] Received auth challenge, sending response...");
                    
                    var digest = NetworkMidi2Protocol.ComputeAuthDigest(_cryptoNonce, sharedSecret);
                    var authCmd = NetworkMidi2Protocol.CreateInvitationWithAuth(digest);
                    packet = NetworkMidi2Protocol.CreateUDPPacket(authCmd);
                    _udpClient.Send(packet, packet.Length, _remoteEP);
                    Log($"[NM2] Sent INV_WITH_AUTH");
                    
                    _remoteSSRC = 0;
                    _cryptoNonce = "";
                    
                    startTime = DateTime.Now;
                    while ((DateTime.Now - startTime).TotalSeconds < 3)
                    {
                        await Task.Delay(100);
                        if (_remoteSSRC != 0)
                        {
                            Log($"[NM2] Auth successful! Connected!");
                            return true;
                        }
                    }
                    
                    Log("[NM2] Auth failed or timeout");
                    return false;
                }
            }
            
            Log("[NM2] Connection timeout");
            return false;
        }
        catch (Exception ex)
        {
            Log($"[NM2] Connect failed: {ex.Message}");
            return false;
        }
    }
    
    public async Task<bool> ConnectWithUserAuthAsync(string host, int port, string name, string productId, string username, string password)
    {
        if (_udpClient == null)
        {
            _udpClient = new UdpClient();
            _cts = new CancellationTokenSource();
            Task.Run(() => ReceiveLoop(_cts.Token));
        }
        
        try
        {
            var ip = IPAddress.Parse(host);
            _remoteEP = new IPEndPoint(ip, port);
            
            var invitation = NetworkMidi2Protocol.CreateInvitationCommand(name, productId, NetworkMidi2Protocol.InvitationCapabilities.SupportsUserAuth);
            var packet = NetworkMidi2Protocol.CreateUDPPacket(invitation);
            _udpClient.Send(packet, packet.Length, _remoteEP);
            Log($"[NM2] Sent INV with user auth capability");
            
            _cryptoNonce = "";
            
            var startTime = DateTime.Now;
            while ((DateTime.Now - startTime).TotalSeconds < 5)
            {
                await Task.Delay(100);
                
                if (_remoteSSRC != 0)
                {
                    Log($"[NM2] Connected without auth!");
                    return true;
                }
                
                if (!string.IsNullOrEmpty(_cryptoNonce))
                {
                    Log($"[NM2] Received user auth challenge for user: {username}");
                    
                    var digest = NetworkMidi2Protocol.ComputeUserAuthDigest(_cryptoNonce, username, password);
                    var authCmd = NetworkMidi2Protocol.CreateInvitationWithUserAuth(digest, username);
                    packet = NetworkMidi2Protocol.CreateUDPPacket(authCmd);
                    _udpClient.Send(packet, packet.Length, _remoteEP);
                    Log($"[NM2] Sent INV_WITH_USER_AUTH for user: {username}");
                    
                    _remoteSSRC = 0;
                    _cryptoNonce = "";
                    
                    startTime = DateTime.Now;
                    while ((DateTime.Now - startTime).TotalSeconds < 3)
                    {
                        await Task.Delay(100);
                        if (_remoteSSRC != 0)
                        {
                            Log($"[NM2] User auth successful! Connected!");
                            return true;
                        }
                    }
                    
                    Log("[NM2] User auth failed or timeout");
                    return false;
                }
            }
            
            Log("[NM2] Connection timeout");
            return false;
        }
        catch (Exception ex)
        {
            Log($"[NM2] Connect failed: {ex.Message}");
            return false;
        }
    }
    
    private async void ReceiveLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _udpClient != null)
        {
            try
            {
                var result = await _udpClient.ReceiveAsync();
                ProcessPacket(result.Buffer, result.RemoteEndPoint);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset) { }
            catch (Exception ex)
            {
                if (!ct.IsCancellationRequested)
                    Log($"[NM2] Receive error: {ex.Message}");
            }
        }
    }
    
    private void ProcessPacket(byte[] data, IPEndPoint remoteEP)
    {
        if (!NetworkMidi2Protocol.ParseUDPPacket(data, out var commandPackets))
        {
            return;
        }
        
        foreach (var cmdPacket in commandPackets)
        {
            ProcessCommandPacket(cmdPacket, remoteEP);
        }
    }
    
    private void ProcessCommandPacket(byte[] cmdPacket, IPEndPoint remoteEP)
    {
        if (!NetworkMidi2Protocol.ParseCommandPacket(cmdPacket, out var cmdCode, out var payloadLen, out var cmdSpecific1, out var cmdSpecific2, out var payload))
        {
            return;
        }
        
        switch (cmdCode)
        {
            case NetworkMidi2Protocol.CommandCode.InvitationReplyAccepted:
                ProcessInvitationReplyAccepted(payload, cmdSpecific1);
                break;
                
            case NetworkMidi2Protocol.CommandCode.InvitationReplyPending:
                ProcessInvitationReplyPending(payload, cmdSpecific1);
                break;
                
            case NetworkMidi2Protocol.CommandCode.InvitationReplyAuthRequired:
                ProcessInvitationReplyAuthRequired(payload, cmdSpecific1);
                break;
                
            case NetworkMidi2Protocol.CommandCode.InvitationReplyUserAuthRequired:
                ProcessInvitationReplyUserAuthRequired(payload, cmdSpecific1);
                break;
                
            case NetworkMidi2Protocol.CommandCode.Ping:
                ProcessPing(payload, remoteEP);
                break;
                
            case NetworkMidi2Protocol.CommandCode.PingReply:
                ProcessPingReply(payload);
                break;
                
            case NetworkMidi2Protocol.CommandCode.Bye:
                ProcessBye(payload);
                break;
                
            case NetworkMidi2Protocol.CommandCode.ByeReply:
                Log("[NM2] Received BYE_REPLY");
                break;
                
            case NetworkMidi2Protocol.CommandCode.SessionReset:
                ProcessSessionReset(remoteEP);
                break;
                
            case NetworkMidi2Protocol.CommandCode.SessionResetReply:
                Log("[NM2] Received SESSION_RESET_REPLY");
                break;
                
            case NetworkMidi2Protocol.CommandCode.NAK:
                ProcessNAK(payload);
                break;
                
            case NetworkMidi2Protocol.CommandCode.RetransmitRequest:
                ProcessRetransmitRequest(payload);
                break;
                
            case NetworkMidi2Protocol.CommandCode.RetransmitError:
                ProcessRetransmitError(payload);
                break;
                
            case NetworkMidi2Protocol.CommandCode.UMPData:
                NetworkMidi2Protocol.ParseUMPDataCommand(cmdSpecific1, cmdSpecific2, payload, out var seq, out var umpData);
                ProcessUMPData(seq, umpData);
                break;
        }
    }
    
    private void ProcessInvitationReplyAccepted(byte[] payload, byte nameWords)
    {
        NetworkMidi2Protocol.ParseInvitationReply(payload, nameWords, out var umpEndpointName, out var productInstanceId);
        
        _remoteSSRC = (uint)Random.Shared.Next(1, int.MaxValue);
        _remoteName = umpEndpointName;
        
        Log($"[NM2] INV_ACCEPTED from remote: {umpEndpointName}, product: {productInstanceId}");
        Log($"[NM2] Session established!");
    }
    
    private void ProcessInvitationReplyPending(byte[] payload, byte nameWords)
    {
        NetworkMidi2Protocol.ParseInvitationReply(payload, nameWords, out var umpEndpointName, out var productInstanceId);
        Log($"[NM2] INV_PENDING from remote: {umpEndpointName}, waiting for user approval...");
    }
    
    private void ProcessInvitationReplyAuthRequired(byte[] payload, byte nameWords)
    {
        NetworkMidi2Protocol.ParseInvitationReplyAuthRequired(payload, nameWords, out var cryptoNonce, out var umpEndpointName, out var productInstanceId, out var authState);
        
        _cryptoNonce = cryptoNonce;
        Log($"[NM2] INV_AUTH_REQUIRED from remote: {umpEndpointName}");
        Log($"[NM2] Crypto nonce: {cryptoNonce}, auth state: {authState}");
    }
    
    private void ProcessInvitationReplyUserAuthRequired(byte[] payload, byte nameWords)
    {
        NetworkMidi2Protocol.ParseInvitationReplyAuthRequired(payload, nameWords, out var cryptoNonce, out var umpEndpointName, out var productInstanceId, out var authState);
        
        _cryptoNonce = cryptoNonce;
        Log($"[NM2] INV_USER_AUTH_REQUIRED from remote: {umpEndpointName}");
        Log($"[NM2] Crypto nonce: {cryptoNonce}, auth state: {authState}");
    }
    
    private void ProcessPing(byte[] payload, IPEndPoint remoteEP)
    {
        if (!NetworkMidi2Protocol.ParsePingCommand(payload, out var pingId)) return;
        
        var replyCmd = NetworkMidi2Protocol.CreatePingReplyCommand(pingId);
        var packet = NetworkMidi2Protocol.CreateUDPPacket(replyCmd);
        _udpClient?.Send(packet, packet.Length, remoteEP);
        Log($"[NM2] PING id={pingId}, sent PONG");
    }
    
    private void ProcessPingReply(byte[] payload)
    {
        if (!NetworkMidi2Protocol.ParsePingCommand(payload, out var pingId)) return;
        
        if (pingId == _lastPingId)
        {
            var latency = (DateTime.Now - _lastPingTime).TotalMilliseconds;
            _pendingPingCount = 0;
            Log($"[NM2] PONG received, latency: {latency:F1}ms");
        }
        else
        {
            Log($"[NM2] PONG received with unexpected id: {pingId}");
        }
    }
    
    private void ProcessBye(byte[] payload)
    {
        NetworkMidi2Protocol.ParseByeCommand(payload, out var reason, out var textMessage);
        
        var replyCmd = NetworkMidi2Protocol.CreateByeReplyCommand();
        var packet = NetworkMidi2Protocol.CreateUDPPacket(replyCmd);
        _udpClient?.Send(packet, packet.Length, _remoteEP);
        
        Log($"[NM2] BYE received, reason: {reason}, message: {textMessage}");
        
        _remoteEP = null;
        _remoteSSRC = 0;
    }
    
    private void ProcessSessionReset(IPEndPoint remoteEP)
    {
        var replyCmd = NetworkMidi2Protocol.CreateSessionResetReplyCommand();
        var packet = NetworkMidi2Protocol.CreateUDPPacket(replyCmd);
        _udpClient?.Send(packet, packet.Length, remoteEP);
        
        _sendSequence = 0;
        _receiveSequence = 0;
        Log("[NM2] SESSION_RESET received, sent reply, sequence reset");
    }
    
    private void ProcessNAK(byte[] payload)
    {
        NetworkMidi2Protocol.ParseNAKCommand(payload, out var reason, out var originalHeader, out var textMessage);
        Log($"[NM2] NAK received, reason: {reason}, original cmd: 0x{originalHeader[0]:X2}, message: {textMessage}");
    }
    
    private void ProcessRetransmitRequest(byte[] payload)
    {
        NetworkMidi2Protocol.ParseRetransmitRequest(payload, out var seqNum, out var numCommands);
        Log($"[NM2] RETRANSMIT_REQUEST seq={seqNum}, count={numCommands}");
        
        var errorCmd = NetworkMidi2Protocol.CreateRetransmitErrorCommand(NetworkMidi2Protocol.RetransmitErrorReason.BufferDoesNotContainSequence, seqNum);
        var packet = NetworkMidi2Protocol.CreateUDPPacket(errorCmd);
        _udpClient?.Send(packet, packet.Length, _remoteEP);
    }
    
    private void ProcessRetransmitError(byte[] payload)
    {
        var reason = (NetworkMidi2Protocol.RetransmitErrorReason)payload[2];
        var seqNum = (ushort)((payload[4] << 8) | payload[5]);
        Log($"[NM2] RETRANSMIT_ERROR reason: {reason}, seq: {seqNum}");
    }
    
    private void ProcessUMPData(ushort sequenceNumber, byte[] umpData)
    {
        if (sequenceNumber != _receiveSequence)
        {
            if (sequenceNumber > _receiveSequence)
            {
                Log($"[NM2] Packet loss detected: expected {_receiveSequence}, got {sequenceNumber}");
            }
        }
        _receiveSequence = (ushort)(sequenceNumber + 1);
        
        Log($"[NM2] UMP Data seq={sequenceNumber}, {umpData.Length} bytes");
        
        int offset = 0;
        while (offset + 4 <= umpData.Length)
        {
            int mt = (umpData[offset] >> 4) & 0x0F;
            int packetSize = NetworkMidi2Protocol.GetUMPPacketSize(mt);
            
            if (offset + packetSize > umpData.Length) break;
            
            if (mt == 0x2 && packetSize >= 4)
            {
                byte[] midiData = new byte[] { umpData[offset + 1], umpData[offset + 2], umpData[offset + 3] };
                OnMidiReceived?.Invoke(midiData);
                Log($"[NM2] -> MIDI1: {BitConverter.ToString(midiData)}");
            }
            else if (mt == 0x4 && packetSize >= 8)
            {
                byte status = umpData[offset + 1];
                byte note = umpData[offset + 2];
                ushort velocity = (ushort)((umpData[offset + 5] << 8) | umpData[offset + 6]);
                byte vel7 = (byte)(velocity >> 9);
                byte[] midiData = new byte[] { status, note, vel7 };
                OnMidiReceived?.Invoke(midiData);
                Log($"[NM2] -> MIDI2: {BitConverter.ToString(midiData)} (vel16={velocity})");
            }
            
            offset += packetSize;
        }
    }
    
    public void SendMidi(byte[] midiData)
    {
        if (!IsConnected || _remoteEP == null || _udpClient == null) return;
        
        byte[] umpData = new byte[8];
        umpData[0] = 0x40;
        umpData[1] = midiData[0];
        umpData[2] = midiData[1];
        umpData[3] = 0;
        umpData[4] = 0;
        umpData[5] = (byte)(midiData[2] << 1);
        umpData[6] = (byte)((midiData[2] << 9) & 0xFF);
        umpData[7] = 0;
        
        SendUMPData(umpData);
    }
    
    public void SendNoteOn(byte note, byte velocity, byte channel = 0)
    {
        byte status = (byte)(0x90 | channel);
        ushort vel16 = (ushort)(velocity << 9);
        
        byte[] umpData = new byte[8];
        umpData[0] = 0x40;
        umpData[1] = status;
        umpData[2] = note;
        umpData[3] = 0;
        umpData[4] = 0;
        umpData[5] = (byte)((vel16 >> 8) & 0xFF);
        umpData[6] = (byte)(vel16 & 0xFF);
        umpData[7] = 0;
        
        SendUMPData(umpData);
        Log($"[NM2] Sent NoteOn: note={note}, vel={velocity}");
    }
    
    public void SendNoteOff(byte note, byte channel = 0)
    {
        byte status = (byte)(0x80 | channel);
        
        byte[] umpData = new byte[8];
        umpData[0] = 0x40;
        umpData[1] = status;
        umpData[2] = note;
        umpData[3] = 0;
        umpData[4] = 0;
        umpData[5] = 0;
        umpData[6] = 0;
        umpData[7] = 0;
        
        SendUMPData(umpData);
        Log($"[NM2] Sent NoteOff: note={note}");
    }
    
    private void SendUMPData(byte[] umpData)
    {
        if (_remoteEP == null || _udpClient == null) return;
        
        var umpCmd = NetworkMidi2Protocol.CreateUMPDataCommand(_sendSequence++, umpData);
        var packet = NetworkMidi2Protocol.CreateUDPPacket(umpCmd);
        _udpClient.Send(packet, packet.Length, _remoteEP);
    }
    
    public void SendPing()
    {
        if (_remoteEP == null || _udpClient == null) return;
        
        _lastPingId = (uint)Random.Shared.Next();
        _lastPingTime = DateTime.Now;
        _pendingPingCount++;
        
        var pingCmd = NetworkMidi2Protocol.CreatePingCommand(_lastPingId);
        var packet = NetworkMidi2Protocol.CreateUDPPacket(pingCmd);
        _udpClient.Send(packet, packet.Length, _remoteEP);
        Log($"[NM2] Sent PING id={_lastPingId}");
    }
    
    public void SendSessionReset()
    {
        if (_remoteEP == null || _udpClient == null) return;
        
        var resetCmd = NetworkMidi2Protocol.CreateSessionResetCommand();
        var packet = NetworkMidi2Protocol.CreateUDPPacket(resetCmd);
        _udpClient.Send(packet, packet.Length, _remoteEP);
        Log("[NM2] Sent SESSION_RESET");
    }
    
    public void Disconnect()
    {
        if (_remoteEP != null && _udpClient != null)
        {
            var byeCmd = NetworkMidi2Protocol.CreateByeCommand(NetworkMidi2Protocol.ByeReason.UserTerminated);
            var packet = NetworkMidi2Protocol.CreateUDPPacket(byeCmd);
            _udpClient.Send(packet, packet.Length, _remoteEP);
            Log("[NM2] Sent BYE");
        }
        
        _remoteEP = null;
        _remoteSSRC = 0;
        _remoteName = "";
    }
    
    private byte[] CreateMDNSQuery()
    {
        var serviceType = "_midi2._udp.local";
        
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);
        
        ushort transactionId = (ushort)Random.Shared.Next();
        writer.Write((byte)((transactionId >> 8) & 0xFF));
        writer.Write((byte)(transactionId & 0xFF));
        writer.Write((byte)0x00);
        writer.Write((byte)0x00);
        writer.Write((byte)0x00);
        writer.Write((byte)0x01);
        writer.Write((byte)0x00);
        writer.Write((byte)0x00);
        writer.Write((byte)0x00);
        writer.Write((byte)0x00);
        writer.Write((byte)0x00);
        writer.Write((byte)0x00);
        
        WriteName(writer, serviceType);
        writer.Write((byte)0x00);
        writer.Write((byte)0x21);
        writer.Write((byte)0x00);
        writer.Write((byte)0x01);
        
        return ms.ToArray();
    }
    
    private void WriteName(BinaryWriter writer, string name)
    {
        var parts = name.Split('.');
        foreach (var part in parts)
        {
            var partBytes = Encoding.UTF8.GetBytes(part);
            writer.Write((byte)partBytes.Length);
            writer.Write(partBytes);
        }
        writer.Write((byte)0);
    }
    
    private string ReadName(byte[] data, ref int offset)
    {
        var sb = new StringBuilder();
        
        while (offset < data.Length)
        {
            byte len = data[offset++];
            
            if (len == 0) break;
            
            if ((len & 0xC0) == 0xC0)
            {
                if (offset >= data.Length) break;
                offset++;
                break;
            }
            
            if (offset + len > data.Length) break;
            
            if (sb.Length > 0) sb.Append('.');
            sb.Append(Encoding.UTF8.GetString(data, offset, len));
            offset += len;
        }
        
        return sb.ToString();
    }
    
    private int SkipName(byte[] data, int offset)
    {
        while (offset < data.Length)
        {
            byte len = data[offset++];
            if (len == 0) break;
            if ((len & 0xC0) == 0xC0) { offset++; break; }
            offset += len;
        }
        return offset;
    }
    
    private void Log(string message)
    {
        Console.WriteLine(message);
        OnLog?.Invoke(message);
    }
    
    public void Dispose()
    {
        Disconnect();
        _cts?.Cancel();
        _udpClient?.Close();
        _udpClient?.Dispose();
        _cts?.Dispose();
    }
}