using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Test;

public class NetworkMidi2Client : IDisposable
{
    private UdpClient? _udpClient;
    private CancellationTokenSource? _cts;
    private bool _isRunning;
    private byte _localSSRC;
    private byte _remoteSSRC;
    private ushort _sendSequence;
    private IPEndPoint? _remoteEP;
    private string _remoteName = "";
    
    public event Action<string>? OnLog;
    public event Action<byte[]>? OnMidiReceived;
    
    public bool IsConnected => _isRunning && _remoteEP != null;
    public string RemoteName => _remoteName;
    
    public NetworkMidi2Client()
    {
        _localSSRC = (byte)Random.Shared.Next(1, 255);
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
            _isRunning = true;
            
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
    
    public async Task<bool> ConnectAsync(string host, int port, string name = "TestClient")
    {
        if (_udpClient == null)
        {
            _udpClient = new UdpClient();
            _cts = new CancellationTokenSource();
            _isRunning = true;
            Task.Run(() => ReceiveLoop(_cts.Token));
        }
        
        try
        {
            var ip = IPAddress.Parse(host);
            _remoteEP = new IPEndPoint(ip, port);
            
            var invitation = CreateInvitation(name, "TestProduct");
            _udpClient.Send(invitation, invitation.Length, _remoteEP);
            Log($"[NM2] Sent INV to {host}:{port}");
            
            var startTime = DateTime.Now;
            while ((DateTime.Now - startTime).TotalSeconds < 5)
            {
                await Task.Delay(100);
                if (_remoteSSRC != 0)
                {
                    _remoteName = name;
                    Log($"[NM2] Connected! SSRC: {_localSSRC:X2} <-> {_remoteSSRC:X2}");
                    return true;
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
        if (data.Length < 4) return;
        
        byte cmdByte = data[0];
        byte cmd = (byte)(cmdByte & 0x0F);
        byte status = data[1];
        byte ssrc = data[2];
        byte remoteSSRC = data[3];
        
        if (cmdByte == 0x10)
        {
            ProcessUMPData(data);
            return;
        }
        
        switch (cmd)
        {
            case 0x01:
                if (status == 0x01)
                {
                    _remoteSSRC = ssrc;
                    Log($"[NM2] INV ACCEPTED from {remoteEP}, SSRC: {ssrc:X2}");
                }
                else if (status == 0x02)
                {
                    Log($"[NM2] INV REJECTED from {remoteEP}");
                }
                break;
                
            case 0x02:
                Log($"[NM2] END SESSION from {remoteEP}");
                _isRunning = false;
                break;
                
            case 0x03:
                if (status == 0x00)
                {
                    var pong = CreatePongPacket(ssrc);
                    _udpClient?.Send(pong, pong.Length, remoteEP);
                    Log($"[NM2] PING from {remoteEP}, sent PONG");
                }
                break;
        }
    }
    
    private void ProcessUMPData(byte[] data)
    {
        if (data.Length < 5) return;
        
        ushort seq = (ushort)((data[1] << 8) | data[2]);
        
        byte[] umpData = new byte[data.Length - 4];
        Buffer.BlockCopy(data, 4, umpData, 0, umpData.Length);
        
        Log($"[NM2] UMP Data seq={seq}, {umpData.Length} bytes");
        
        int offset = 0;
        while (offset + 4 <= umpData.Length)
        {
            int mt = (umpData[offset] >> 4) & 0x0F;
            int packetSize = mt switch
            {
                0x2 => 4,
                0x4 => 8,
                _ => 4
            };
            
            if (offset + packetSize > umpData.Length) break;
            
            if (mt == 0x2 && packetSize >= 4)
            {
                byte[] midiData = new byte[] { umpData[offset + 1], umpData[offset + 2], umpData[offset + 3] };
                OnMidiReceived?.Invoke(midiData);
                Log($"[NM2] -> MIDI: {BitConverter.ToString(midiData)}");
            }
            else if (mt == 0x4 && packetSize >= 8)
            {
                byte status = umpData[offset + 1];
                byte note = umpData[offset + 2];
                ushort velocity = (ushort)((umpData[offset + 5] << 8) | umpData[offset + 6]);
                byte vel7 = (byte)(velocity >> 9);
                byte[] midiData = new byte[] { status, note, vel7 };
                OnMidiReceived?.Invoke(midiData);
                Log($"[NM2] -> MIDI: {BitConverter.ToString(midiData)} (vel16={velocity})");
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
        
        var packet = new byte[4 + umpData.Length];
        packet[0] = 0x10;
        packet[1] = (byte)((_sendSequence >> 8) & 0xFF);
        packet[2] = (byte)(_sendSequence & 0xFF);
        packet[3] = 0;
        Buffer.BlockCopy(umpData, 0, packet, 4, umpData.Length);
        
        _sendSequence++;
        _udpClient.Send(packet, packet.Length, _remoteEP);
    }
    
    public void SendPing()
    {
        if (_remoteEP == null || _udpClient == null) return;
        
        var packet = CreatePingPacket(_localSSRC, _remoteSSRC);
        _udpClient.Send(packet, packet.Length, _remoteEP);
        Log("[NM2] Sent PING");
    }
    
    public void Disconnect()
    {
        if (_remoteEP != null && _udpClient != null && _remoteSSRC != 0)
        {
            var packet = CreateEndPacket(_localSSRC, _remoteSSRC);
            _udpClient.Send(packet, packet.Length, _remoteEP);
            Log("[NM2] Sent END");
        }
        
        _isRunning = false;
        _remoteEP = null;
        _remoteSSRC = 0;
        _remoteName = "";
    }
    
    private byte[] CreateMDNSQuery()
    {
        var serviceType = "_midi2._udp.local";
        var nameBytes = Encoding.UTF8.GetBytes(serviceType);
        
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
    
    private byte[] CreateInvitation(string name, string productId)
    {
        var nameBytes = Encoding.UTF8.GetBytes(name);
        var productBytes = Encoding.UTF8.GetBytes(productId);
        var packet = new byte[4 + 2 + nameBytes.Length + 2 + productBytes.Length];
        
        int offset = 0;
        packet[offset++] = 0x01;
        packet[offset++] = 0x00;
        packet[offset++] = _localSSRC;
        packet[offset++] = 0;
        
        packet[offset++] = (byte)((nameBytes.Length >> 8) & 0xFF);
        packet[offset++] = (byte)(nameBytes.Length & 0xFF);
        Buffer.BlockCopy(nameBytes, 0, packet, offset, nameBytes.Length);
        offset += nameBytes.Length;
        
        packet[offset++] = (byte)((productBytes.Length >> 8) & 0xFF);
        packet[offset++] = (byte)(productBytes.Length & 0xFF);
        if (productBytes.Length > 0)
        {
            Buffer.BlockCopy(productBytes, 0, packet, offset, productBytes.Length);
        }
        
        return packet;
    }
    
    private byte[] CreatePingPacket(byte ssrc, byte remoteSSRC)
    {
        return new byte[] { 0x03, 0x00, ssrc, remoteSSRC };
    }
    
    private byte[] CreatePongPacket(byte ssrc)
    {
        return new byte[] { 0x03, 0x01, _localSSRC, ssrc };
    }
    
    private byte[] CreateEndPacket(byte ssrc, byte remoteSSRC)
    {
        return new byte[] { 0x02, 0x00, ssrc, remoteSSRC };
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