using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Test;

public class NetworkMidi2Server : IDisposable
{
    private UdpClient? _dataServer;
    private UdpClient? _mdnsClient;
    private CancellationTokenSource? _cts;
    private bool _isRunning;
    private byte _localSSRC;
    private byte _remoteSSRC;
    private IPEndPoint? _remoteEP;
    private string _serviceName;
    private int _port;
    
    public event Action<string>? OnLog;
    public event Action<byte[]>? OnMidiReceived;
    
    public bool IsConnected => _remoteEP != null && _remoteSSRC != 0;
    public string ServiceName => _serviceName;
    public int Port => _port;
    
    public NetworkMidi2Server(string serviceName = "TestNM2Device", int port = 5507)
    {
        _serviceName = serviceName;
        _port = port;
        _localSSRC = (byte)Random.Shared.Next(1, 255);
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
            _isRunning = true;
            
            Task.Run(() => ReceiveLoop(_cts.Token));
            Task.Run(() => MDNSAnnounceLoop(_cts.Token));
            
            Log($"[NM2 Server] Started on port {_port}");
            Log($"[NM2 Server] Service name: {_serviceName}");
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
        var serviceType = "_midi2._udp.local";
        var localDomain = "local";
        
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);
        
        // Header
        writer.Write((byte)0x00);
        writer.Write((byte)0x00);
        writer.Write((byte)0x84); // Flags: response, authoritative
        writer.Write((byte)0x00);
        writer.Write((byte)0x00);
        writer.Write((byte)0x00); // Questions: 0
        writer.Write((byte)0x00);
        writer.Write((byte)0x01); // Answers: 1
        writer.Write((byte)0x00);
        writer.Write((byte)0x00); // Authority: 0
        writer.Write((byte)0x00);
        writer.Write((byte)0x00); // Additional: 0
        
        // SRV record
        WriteName(writer, serviceName);
        writer.Write((byte)0x00);
        writer.Write((byte)0x21); // Type: SRV (33)
        writer.Write((byte)0x00);
        writer.Write((byte)0x01); // Class: IN
        writer.Write((byte)0x00);
        writer.Write((byte)0x00);
        writer.Write((byte)0x11);
        writer.Write((byte)0x94); // TTL: 4500
        writer.Write((byte)0x00);
        writer.Write((byte)0x08); // Data length: 8
        writer.Write((byte)0x00);
        writer.Write((byte)0x00); // Priority
        writer.Write((byte)0x00);
        writer.Write((byte)0x00); // Weight
        writer.Write((byte)((_port >> 8) & 0xFF));
        writer.Write((byte)(_port & 0xFF)); // Port
        WriteName(writer, localDomain); // Target
        
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
        if (data.Length < 4) return;
        
        byte cmdByte = data[0];
        byte cmd = (byte)(cmdByte & 0x0F);
        byte status = data[1];
        byte ssrc = data[2];
        byte remoteSSRC = data[3];
        
        if (cmdByte == 0x10)
        {
            ProcessUMPData(data, remoteEP);
            return;
        }
        
        switch (cmd)
        {
            case 0x01:
                if (status == 0x00)
                {
                    ProcessInvitation(data, remoteEP, ssrc);
                }
                break;
                
            case 0x02:
                Log($"[NM2 Server] END SESSION from {remoteEP}");
                _remoteEP = null;
                _remoteSSRC = 0;
                break;
                
            case 0x03:
                if (status == 0x00)
                {
                    var pong = new byte[] { 0x03, 0x01, _localSSRC, ssrc };
                    _dataServer?.Send(pong, pong.Length, remoteEP);
                    Log($"[NM2 Server] PING from {remoteEP}, sent PONG");
                }
                break;
        }
    }
    
    private void ProcessInvitation(byte[] data, IPEndPoint remoteEP, byte ssrc)
    {
        _remoteSSRC = ssrc;
        _remoteEP = remoteEP;
        
        string name = "";
        if (data.Length > 6)
        {
            int offset = 4;
            int nameLength = (data[offset] << 8) | data[offset + 1];
            offset += 2;
            if (offset + nameLength <= data.Length)
            {
                name = Encoding.UTF8.GetString(data, offset, nameLength);
            }
        }
        
        var reply = new byte[] { 0x01, 0x01, _localSSRC, ssrc };
        _dataServer?.Send(reply, reply.Length, remoteEP);
        
        Log($"[NM2 Server] INV from {remoteEP} (name: {name}), sent ACCEPT");
        Log($"[NM2 Server] Connected! SSRC: {_localSSRC:X2} <-> {ssrc:X2}");
    }
    
    private void ProcessUMPData(byte[] data, IPEndPoint remoteEP)
    {
        if (data.Length < 5) return;
        
        ushort seq = (ushort)((data[1] << 8) | data[2]);
        
        byte[] umpData = new byte[data.Length - 4];
        Buffer.BlockCopy(data, 4, umpData, 0, umpData.Length);
        
        Log($"[NM2 Server] UMP Data seq={seq}, {umpData.Length} bytes");
        
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
                Log($"[NM2 Server] -> MIDI: {BitConverter.ToString(midiData)}");
            }
            else if (mt == 0x4 && packetSize >= 8)
            {
                byte status = umpData[offset + 1];
                byte note = umpData[offset + 2];
                ushort velocity = (ushort)((umpData[offset + 5] << 8) | umpData[offset + 6]);
                byte vel7 = (byte)(velocity >> 9);
                byte[] midiData = new byte[] { status, note, vel7 };
                OnMidiReceived?.Invoke(midiData);
                Log($"[NM2 Server] -> MIDI: {BitConverter.ToString(midiData)} (vel16={velocity})");
            }
            
            offset += packetSize;
        }
    }
    
    private ushort _sendSequence;
    
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
        
        var packet = new byte[4 + umpData.Length];
        packet[0] = 0x10;
        packet[1] = (byte)((_sendSequence >> 8) & 0xFF);
        packet[2] = (byte)(_sendSequence & 0xFF);
        packet[3] = 0;
        Buffer.BlockCopy(umpData, 0, packet, 4, umpData.Length);
        
        _sendSequence++;
        _dataServer.Send(packet, packet.Length, _remoteEP);
        Log($"[NM2 Server] Sent MIDI: {BitConverter.ToString(midiData)}");
    }
    
    public void SendNoteOn(byte note, byte velocity, byte channel = 0)
    {
        byte status = (byte)(0x90 | channel);
        SendMidi(new byte[] { status, note, velocity });
    }
    
    public void SendNoteOff(byte note, byte channel = 0)
    {
        byte status = (byte)(0x80 | channel);
        SendMidi(new byte[] { status, note, 0 });
    }
    
    public void Stop()
    {
        if (_remoteEP != null && _dataServer != null && _remoteSSRC != 0)
        {
            var packet = new byte[] { 0x02, 0x00, _localSSRC, _remoteSSRC };
            _dataServer.Send(packet, packet.Length, _remoteEP);
            Log("[NM2 Server] Sent END");
        }
        
        _isRunning = false;
        _remoteEP = null;
        _remoteSSRC = 0;
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