using System.Net;
using System.Net.Sockets;
using System.Text;
using MidiBridge.Services.NetworkMidi2;

namespace Test.Tests.NetworkMidi2;

public class NetworkMidi2Server : IDisposable
{
    private UdpClient? _dataServer;
    private UdpClient? _mdnsClient;
    private CancellationTokenSource? _cts;
    private uint _localSSRC;
    private uint _remoteSSRC;
    private IPEndPoint? _remoteEP;
    private string _serviceName;
    private int _port;
    private string _productId;
    private ushort _sendSequence;
    
    public event Action<string>? OnLog;
    public event Action<byte[]>? OnMidiReceived;
    
    public bool IsConnected => _remoteEP != null && _remoteSSRC != 0;
    public string ServiceName => _serviceName;
    public int Port => _port;
    
    public NetworkMidi2Server(string serviceName = "TestNM2Device", int port = 5507, string productId = "TestProduct")
    {
        _serviceName = serviceName;
        _port = port;
        _productId = productId;
        _localSSRC = (uint)Random.Shared.Next(1, int.MaxValue);
    }
    
    public bool Start()
    {
        try
        {
            _dataServer = new UdpClient(_port);
            _dataServer.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            
            _mdnsClient = new UdpClient();
            _mdnsClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            
            _cts = new CancellationTokenSource();
            
            Task.Run(() => ReceiveLoop(_cts.Token));
            Task.Run(() => MDNSAnnounceLoop(_cts.Token));
            
            Log($"[NM2 Server] Started on port {_port}");
            Log($"[NM2 Server] Service name: {_serviceName}");
            Log($"[NM2 Server] Local SSRC: 0x{_localSSRC:X8}");
            return true;
        }
        catch (Exception ex)
        {
            Log($"[NM2 Server] Start failed: {ex.Message}");
            return false;
        }
    }
    
    private async void MDNSAnnounceLoop(CancellationToken ct)
    {
        var multicastEP = new IPEndPoint(IPAddress.Parse("224.0.0.251"), 5353);
        
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var announcement = CreateMDNSAnnouncement();
                _mdnsClient?.Send(announcement, announcement.Length, multicastEP);
                Log("[mDNS] Sent announcement");
            }
            catch (Exception ex)
            {
                Log($"[mDNS] Announcement failed: {ex.Message}");
            }
            
            await Task.Delay(5000, ct);
        }
    }
    
    private byte[] CreateMDNSAnnouncement()
    {
        var serviceName = _serviceName + "._midi2._udp.local";
        var txtEntry = $"productInstanceId={_productId}";
        var txtBytes = Encoding.UTF8.GetBytes(txtEntry);
        var localIP = GetLocalIPAddress();
        var ipBytes = localIP.GetAddressBytes();
        uint ttl = 4500;
        
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);
        
        writer.Write((byte)0x00);
        writer.Write((byte)0x00);
        writer.Write((byte)0x84);
        writer.Write((byte)0x00);
        writer.Write((byte)0x00);
        writer.Write((byte)0x00);
        writer.Write((byte)0x00);
        writer.Write((byte)0x03);
        writer.Write((byte)0x00);
        writer.Write((byte)0x00);
        writer.Write((byte)0x00);
        writer.Write((byte)0x00);
        
        WriteName(writer, serviceName);
        writer.Write((byte)0x00);
        writer.Write((byte)0x21);
        writer.Write((byte)0x00);
        writer.Write((byte)0x01);
        writer.Write((byte)((ttl >> 24) & 0xFF));
        writer.Write((byte)((ttl >> 16) & 0xFF));
        writer.Write((byte)((ttl >> 8) & 0xFF));
        writer.Write((byte)(ttl & 0xFF));
        ushort srvLen = (ushort)(6 + 1);
        writer.Write((byte)((srvLen >> 8) & 0xFF));
        writer.Write((byte)(srvLen & 0xFF));
        writer.Write((byte)0x00);
        writer.Write((byte)0x00);
        writer.Write((byte)0x00);
        writer.Write((byte)0x00);
        writer.Write((byte)((_port >> 8) & 0xFF));
        writer.Write((byte)(_port & 0xFF));
        writer.Write((byte)0x00);
        
        WriteName(writer, serviceName);
        writer.Write((byte)0x00);
        writer.Write((byte)0x01);
        writer.Write((byte)0x00);
        writer.Write((byte)0x01);
        writer.Write((byte)((ttl >> 24) & 0xFF));
        writer.Write((byte)((ttl >> 16) & 0xFF));
        writer.Write((byte)((ttl >> 8) & 0xFF));
        writer.Write((byte)(ttl & 0xFF));
        writer.Write((byte)0x00);
        writer.Write((byte)0x04);
        writer.Write(ipBytes);
        
        WriteName(writer, serviceName);
        writer.Write((byte)0x00);
        writer.Write((byte)0x10);
        writer.Write((byte)0x00);
        writer.Write((byte)0x01);
        writer.Write((byte)((ttl >> 24) & 0xFF));
        writer.Write((byte)((ttl >> 16) & 0xFF));
        writer.Write((byte)((ttl >> 8) & 0xFF));
        writer.Write((byte)(ttl & 0xFF));
        ushort txtLen = (ushort)(1 + txtBytes.Length);
        writer.Write((byte)((txtLen >> 8) & 0xFF));
        writer.Write((byte)(txtLen & 0xFF));
        writer.Write((byte)txtEntry.Length);
        writer.Write(txtBytes);
        
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
    
    private async void ReceiveLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _dataServer != null)
        {
            try
            {
                var result = await _dataServer.ReceiveAsync();
                ProcessPacket(result.Buffer, result.RemoteEndPoint);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset) { }
            catch (Exception ex)
            {
                if (!ct.IsCancellationRequested)
                    Log($"[NM2 Server] Receive error: {ex.Message}");
            }
        }
    }
    
    private void ProcessPacket(byte[] data, IPEndPoint remoteEP)
    {
        if (!NetworkMidi2Protocol.ParseUDPPacket(data, out var commandPackets))
        {
            Log($"[NM2 Server] Invalid packet signature");
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
            case NetworkMidi2Protocol.CommandCode.Invitation:
                ProcessInvitation(payload, cmdSpecific1, remoteEP);
                break;
                
            case NetworkMidi2Protocol.CommandCode.InvitationWithAuth:
                ProcessInvitationWithAuth(payload, remoteEP);
                break;
                
            case NetworkMidi2Protocol.CommandCode.InvitationWithUserAuth:
                ProcessInvitationWithUserAuth(payload, remoteEP);
                break;
                
            case NetworkMidi2Protocol.CommandCode.Ping:
                ProcessPing(payload, remoteEP);
                break;
                
            case NetworkMidi2Protocol.CommandCode.Bye:
                ProcessBye(payload, remoteEP);
                break;
                
            case NetworkMidi2Protocol.CommandCode.SessionReset:
                ProcessSessionReset(remoteEP);
                break;
                
            case NetworkMidi2Protocol.CommandCode.RetransmitRequest:
                ProcessRetransmitRequest(payload);
                break;
                
            case NetworkMidi2Protocol.CommandCode.UMPData:
                NetworkMidi2Protocol.ParseUMPDataCommand(cmdSpecific1, cmdSpecific2, payload, out var seq, out var umpData);
                ProcessUMPData(seq, umpData);
                break;
                
            default:
                Log($"[NM2 Server] Unknown command: 0x{((byte)cmdCode):X2}");
                break;
        }
    }
    
    private void ProcessInvitation(byte[] payload, byte nameWords, IPEndPoint remoteEP)
    {
        if (!NetworkMidi2Protocol.ParseInvitationCommand(payload, nameWords, out var umpEndpointName, out var productInstanceId, out var capabilities))
        {
            Log("[NM2 Server] Failed to parse invitation");
            SendNAK(NetworkMidi2Protocol.NAKReason.CommandMalformed, new byte[4], remoteEP);
            return;
        }
        
        _remoteEP = remoteEP;
        
        var replyCmd = NetworkMidi2Protocol.CreateInvitationReplyAccepted(_serviceName, _productId);
        var packet = NetworkMidi2Protocol.CreateUDPPacket(replyCmd);
        _dataServer?.Send(packet, packet.Length, remoteEP);
        
        Log($"[NM2 Server] INV from {remoteEP} (name: {umpEndpointName}, product: {productInstanceId})");
        Log($"[NM2 Server] Sent INV_ACCEPTED");
    }
    
    private void ProcessInvitationWithAuth(byte[] payload, IPEndPoint remoteEP)
    {
        if (!NetworkMidi2Protocol.ParseInvitationWithAuth(payload, out var authDigest))
        {
            SendNAK(NetworkMidi2Protocol.NAKReason.CommandMalformed, new byte[4], remoteEP);
            return;
        }
        
        Log($"[NM2 Server] INV_WITH_AUTH from {remoteEP}, digest: {BitConverter.ToString(authDigest).Replace("-", "")[..16]}...");
        
        _remoteEP = remoteEP;
        
        var replyCmd = NetworkMidi2Protocol.CreateInvitationReplyAccepted(_serviceName, _productId);
        var packet = NetworkMidi2Protocol.CreateUDPPacket(replyCmd);
        _dataServer?.Send(packet, packet.Length, remoteEP);
        
        Log($"[NM2 Server] Sent INV_ACCEPTED (auth accepted)");
    }
    
    private void ProcessInvitationWithUserAuth(byte[] payload, IPEndPoint remoteEP)
    {
        if (!NetworkMidi2Protocol.ParseInvitationWithUserAuth(payload, out var authDigest, out var username))
        {
            SendNAK(NetworkMidi2Protocol.NAKReason.CommandMalformed, new byte[4], remoteEP);
            return;
        }
        
        Log($"[NM2 Server] INV_WITH_USER_AUTH from {remoteEP}, user: {username}");
        
        _remoteEP = remoteEP;
        
        var replyCmd = NetworkMidi2Protocol.CreateInvitationReplyAccepted(_serviceName, _productId);
        var packet = NetworkMidi2Protocol.CreateUDPPacket(replyCmd);
        _dataServer?.Send(packet, packet.Length, remoteEP);
        
        Log($"[NM2 Server] Sent INV_ACCEPTED (user auth accepted)");
    }
    
    private void ProcessPing(byte[] payload, IPEndPoint remoteEP)
    {
        if (!NetworkMidi2Protocol.ParsePingCommand(payload, out var pingId))
        {
            return;
        }
        
        var replyCmd = NetworkMidi2Protocol.CreatePingReplyCommand(pingId);
        var packet = NetworkMidi2Protocol.CreateUDPPacket(replyCmd);
        _dataServer?.Send(packet, packet.Length, remoteEP);
        
        Log($"[NM2 Server] PING id={pingId}, sent PONG");
    }
    
    private void ProcessBye(byte[] payload, IPEndPoint remoteEP)
    {
        NetworkMidi2Protocol.ParseByeCommand(payload, out var reason, out var textMessage);
        
        var replyCmd = NetworkMidi2Protocol.CreateByeReplyCommand();
        var packet = NetworkMidi2Protocol.CreateUDPPacket(replyCmd);
        _dataServer?.Send(packet, packet.Length, remoteEP);
        
        Log($"[NM2 Server] BYE received, reason: {reason}, message: {textMessage}");
        
        _remoteEP = null;
        _remoteSSRC = 0;
    }
    
    private void ProcessSessionReset(IPEndPoint remoteEP)
    {
        var replyCmd = NetworkMidi2Protocol.CreateSessionResetReplyCommand();
        var packet = NetworkMidi2Protocol.CreateUDPPacket(replyCmd);
        _dataServer?.Send(packet, packet.Length, remoteEP);
        
        _sendSequence = 0;
        Log("[NM2 Server] SESSION_RESET received, sent reply, sequence reset");
    }
    
    private void ProcessRetransmitRequest(byte[] payload)
    {
        NetworkMidi2Protocol.ParseRetransmitRequest(payload, out var seqNum, out var numCommands);
        Log($"[NM2 Server] RETRANSMIT_REQUEST seq={seqNum}, count={numCommands}");
        
        var errorCmd = NetworkMidi2Protocol.CreateRetransmitErrorCommand(NetworkMidi2Protocol.RetransmitErrorReason.BufferDoesNotContainSequence, seqNum);
        var packet = NetworkMidi2Protocol.CreateUDPPacket(errorCmd);
        _dataServer?.Send(packet, packet.Length, _remoteEP);
    }
    
    private void ProcessUMPData(ushort sequenceNumber, byte[] umpData)
    {
        Log($"[NM2 Server] UMP Data seq={sequenceNumber}, {umpData.Length} bytes");
        
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
                Log($"[NM2 Server] -> MIDI1: {BitConverter.ToString(midiData)}");
            }
            else if (mt == 0x4 && packetSize >= 8)
            {
                byte status = umpData[offset + 1];
                byte note = umpData[offset + 2];
                ushort velocity = (ushort)((umpData[offset + 5] << 8) | umpData[offset + 6]);
                byte vel7 = (byte)(velocity >> 9);
                byte[] midiData = new byte[] { status, note, vel7 };
                OnMidiReceived?.Invoke(midiData);
                Log($"[NM2 Server] -> MIDI2: {BitConverter.ToString(midiData)} (vel16={velocity})");
            }
            
            offset += packetSize;
        }
    }
    
    private void SendNAK(NetworkMidi2Protocol.NAKReason reason, byte[] originalHeader, IPEndPoint remoteEP)
    {
        var nakCmd = NetworkMidi2Protocol.CreateNAKCommand(reason, originalHeader);
        var packet = NetworkMidi2Protocol.CreateUDPPacket(nakCmd);
        _dataServer?.Send(packet, packet.Length, remoteEP);
        Log($"[NM2 Server] Sent NAK, reason: {reason}");
    }
    
    public void SendMidi(byte[] midiData)
    {
        if (_remoteEP == null || _dataServer == null) return;
        
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
        Log($"[NM2 Server] Sent MIDI: {BitConverter.ToString(midiData)}");
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
        Log($"[NM2 Server] Sent NoteOn: note={note}, vel={velocity}");
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
        Log($"[NM2 Server] Sent NoteOff: note={note}");
    }
    
    public void SendPing()
    {
        if (_remoteEP == null || _dataServer == null)
        {
            Log("[NM2 Server] Not connected, cannot send PING");
            return;
        }
        
        var pingId = (uint)Random.Shared.Next();
        var pingCmd = NetworkMidi2Protocol.CreatePingCommand(pingId);
        var packet = NetworkMidi2Protocol.CreateUDPPacket(pingCmd);
        _dataServer.Send(packet, packet.Length, _remoteEP);
        Log($"[NM2 Server] Sent PING id={pingId}");
    }
    
    public void SendBye(NetworkMidi2Protocol.ByeReason reason = NetworkMidi2Protocol.ByeReason.UserTerminated)
    {
        if (_remoteEP == null || _dataServer == null) return;
        
        var byeCmd = NetworkMidi2Protocol.CreateByeCommand(reason);
        var packet = NetworkMidi2Protocol.CreateUDPPacket(byeCmd);
        _dataServer.Send(packet, packet.Length, _remoteEP);
        Log($"[NM2 Server] Sent BYE, reason: {reason}");
    }
    
    private void SendUMPData(byte[] umpData)
    {
        if (_remoteEP == null || _dataServer == null) return;
        
        var umpCmd = NetworkMidi2Protocol.CreateUMPDataCommand(_sendSequence++, umpData);
        var packet = NetworkMidi2Protocol.CreateUDPPacket(umpCmd);
        _dataServer.Send(packet, packet.Length, _remoteEP);
    }
    
    public void Stop()
    {
        if (_remoteEP != null && _dataServer != null)
        {
            SendBye();
        }
        
        _remoteEP = null;
        _remoteSSRC = 0;
    }
    
    private IPAddress GetLocalIPAddress()
    {
        var host = Dns.GetHostEntry(Dns.GetHostName());
        foreach (var ip in host.AddressList)
        {
            if (ip.AddressFamily == AddressFamily.InterNetwork)
            {
                return ip;
            }
        }
        return IPAddress.Loopback;
    }
    
    private void Log(string message)
    {
        Console.WriteLine(message);
        OnLog?.Invoke(message);
    }
    
    public void Dispose()
    {
        Stop();
        _cts?.Cancel();
        _dataServer?.Close();
        _dataServer?.Dispose();
        _mdnsClient?.Close();
        _mdnsClient?.Dispose();
        _cts?.Dispose();
    }
}