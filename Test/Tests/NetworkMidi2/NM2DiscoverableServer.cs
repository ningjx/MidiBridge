using System.Net;
using System.Net.Sockets;
using System.Text;
using MidiBridge.Services.NetworkMidi2;

namespace Test.Tests.NetworkMidi2;

public class NM2DiscoverableServer : IDisposable
{
    private const string MDNS_MULTICAST_ADDRESS = "224.0.0.251";
    private const int MDNS_PORT = 5353;

    private UdpClient? _dataServer;
    private UdpClient? _mdnsClient;
    private CancellationTokenSource? _cts;

    private readonly string _serviceName;
    private readonly int _port;
    private readonly string _productId;
    private readonly uint _localSSRC;

    private uint _remoteSSRC;
    private IPEndPoint? _remoteEP;
    private ushort _sendSequence;

    public event Action<string>? OnLog;
    public event Action<byte[]>? OnMidiReceived;

    public bool IsConnected => _remoteEP != null && _remoteSSRC != 0;
    public int ReceivedMidiCount { get; private set; }

    public NM2DiscoverableServer(string serviceName, int port, string productId)
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
            _mdnsClient.Client.Bind(new IPEndPoint(IPAddress.Any, MDNS_PORT));
            _mdnsClient.JoinMulticastGroup(IPAddress.Parse(MDNS_MULTICAST_ADDRESS));

            _cts = new CancellationTokenSource();

            Task.Run(() => ReceiveDataLoop(_cts.Token));
            Task.Run(() => ReceiveMdnsLoop(_cts.Token));
            Task.Run(() => AnnounceLoop(_cts.Token));

            OnLog?.Invoke($"已启动，端口 {_port}，SSRC: 0x{_localSSRC:X8}");
            return true;
        }
        catch (Exception ex)
        {
            OnLog?.Invoke($"启动失败: {ex.Message}");
            return false;
        }
    }

    public void Stop()
    {
        if (_remoteEP != null && _dataServer != null)
        {
            var byeCmd = NetworkMidi2Protocol.CreateByeCommand(NetworkMidi2Protocol.ByeReason.UserTerminated);
            var packet = NetworkMidi2Protocol.CreateUDPPacket(byeCmd);
            _dataServer.Send(packet, packet.Length, _remoteEP);
            OnLog?.Invoke("发送 BYE");
        }

        _cts?.Cancel();
        _remoteEP = null;
        _remoteSSRC = 0;
    }

    private async void ReceiveDataLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _dataServer != null)
        {
            try
            {
                var result = await _dataServer.ReceiveAsync();
                ProcessDataPacket(result.Buffer, result.RemoteEndPoint);
            }
            catch { break; }
        }
    }

    private async void ReceiveMdnsLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _mdnsClient != null)
        {
            try
            {
                var result = await _mdnsClient.ReceiveAsync();
                ProcessMdnsPacket(result.Buffer, result.RemoteEndPoint);
            }
            catch { break; }
        }
    }

    private void ProcessMdnsPacket(byte[] data, IPEndPoint remoteEP)
    {
        if (data.Length < 12) return;

        ushort flags = (ushort)((data[2] << 8) | data[3]);
        if ((flags & 0x8000) != 0) return;

        ushort questionCount = (ushort)((data[4] << 8) | data[5]);
        if (questionCount == 0) return;

        int offset = 12;
        for (int i = 0; i < questionCount && offset < data.Length; i++)
        {
            string name = ReadMdnsName(data, ref offset);
            if (offset + 4 > data.Length) return;

            ushort qtype = (ushort)((data[offset] << 8) | data[offset + 1]);
            offset += 4;

            if (name.Contains("_midi2._udp") && qtype == 33)
                SendMdnsResponse(remoteEP);
        }
    }

    private void SendMdnsResponse(IPEndPoint remoteEP)
    {
        var serviceName = $"{_serviceName}._midi2._udp.local";
        var txtEntry = $"productInstanceId={_productId}";
        var txtBytes = Encoding.UTF8.GetBytes(txtEntry);
        var localIP = GetLocalIPAddress();
        var ipBytes = localIP.GetAddressBytes();
        uint ttl = 4500;

        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        writer.Write((byte)0x00); writer.Write((byte)0x00);
        writer.Write((byte)0x84); writer.Write((byte)0x00);
        writer.Write((byte)0x00); writer.Write((byte)0x00);
        writer.Write((byte)0x00); writer.Write((byte)0x03);
        writer.Write((byte)0x00); writer.Write((byte)0x00);
        writer.Write((byte)0x00); writer.Write((byte)0x00);

        ushort srvLen = 7;
        WriteMdnsName(writer, serviceName);
        writer.Write((byte)0x00); writer.Write((byte)0x21);
        writer.Write((byte)0x00); writer.Write((byte)0x01);
        writer.Write((byte)(ttl >> 24)); writer.Write((byte)(ttl >> 16));
        writer.Write((byte)(ttl >> 8)); writer.Write((byte)ttl);
        writer.Write((byte)(srvLen >> 8)); writer.Write((byte)srvLen);
        writer.Write((byte)0x00); writer.Write((byte)0x00);
        writer.Write((byte)0x00); writer.Write((byte)0x00);
        writer.Write((byte)(_port >> 8)); writer.Write((byte)_port);
        writer.Write((byte)0x00);

        WriteMdnsName(writer, serviceName);
        writer.Write((byte)0x00); writer.Write((byte)0x01);
        writer.Write((byte)0x00); writer.Write((byte)0x01);
        writer.Write((byte)(ttl >> 24)); writer.Write((byte)(ttl >> 16));
        writer.Write((byte)(ttl >> 8)); writer.Write((byte)ttl);
        writer.Write((byte)0x00); writer.Write((byte)0x04);
        writer.Write(ipBytes);

        WriteMdnsName(writer, serviceName);
        writer.Write((byte)0x00); writer.Write((byte)0x10);
        writer.Write((byte)0x00); writer.Write((byte)0x01);
        writer.Write((byte)(ttl >> 24)); writer.Write((byte)(ttl >> 16));
        writer.Write((byte)(ttl >> 8)); writer.Write((byte)ttl);
        ushort txtLen = (ushort)(1 + txtBytes.Length);
        writer.Write((byte)(txtLen >> 8)); writer.Write((byte)txtLen);
        writer.Write((byte)txtEntry.Length);
        writer.Write(txtBytes);

        var response = ms.ToArray();
        _mdnsClient?.Send(response, response.Length, new IPEndPoint(IPAddress.Parse(MDNS_MULTICAST_ADDRESS), MDNS_PORT));
        OnLog?.Invoke("mDNS 响应已发送");
    }

    private async void AnnounceLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var announcement = CreateMdnsAnnouncement();
                _mdnsClient?.Send(announcement, announcement.Length, new IPEndPoint(IPAddress.Parse(MDNS_MULTICAST_ADDRESS), MDNS_PORT));
                OnLog?.Invoke("mDNS 公告已发送");
                await Task.Delay(TimeSpan.FromMinutes(1), ct);
            }
            catch { break; }
        }
    }

    private byte[] CreateMdnsAnnouncement()
    {
        var serviceName = $"{_serviceName}._midi2._udp.local";
        var txtEntry = $"productInstanceId={_productId}";
        var txtBytes = Encoding.UTF8.GetBytes(txtEntry);
        var localIP = GetLocalIPAddress();
        var ipBytes = localIP.GetAddressBytes();
        uint ttl = 4500;

        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        writer.Write((byte)0x00); writer.Write((byte)0x00);
        writer.Write((byte)0x84); writer.Write((byte)0x00);
        writer.Write((byte)0x00); writer.Write((byte)0x00);
        writer.Write((byte)0x00); writer.Write((byte)0x03);
        writer.Write((byte)0x00); writer.Write((byte)0x00);
        writer.Write((byte)0x00); writer.Write((byte)0x00);

        ushort srvLen = 7;
        WriteMdnsName(writer, serviceName);
        writer.Write((byte)0x00); writer.Write((byte)0x21);
        writer.Write((byte)0x00); writer.Write((byte)0x01);
        writer.Write((byte)(ttl >> 24)); writer.Write((byte)(ttl >> 16));
        writer.Write((byte)(ttl >> 8)); writer.Write((byte)ttl);
        writer.Write((byte)(srvLen >> 8)); writer.Write((byte)srvLen);
        writer.Write((byte)0x00); writer.Write((byte)0x00);
        writer.Write((byte)0x00); writer.Write((byte)0x00);
        writer.Write((byte)(_port >> 8)); writer.Write((byte)_port);
        writer.Write((byte)0x00);

        WriteMdnsName(writer, serviceName);
        writer.Write((byte)0x00); writer.Write((byte)0x01);
        writer.Write((byte)0x00); writer.Write((byte)0x01);
        writer.Write((byte)(ttl >> 24)); writer.Write((byte)(ttl >> 16));
        writer.Write((byte)(ttl >> 8)); writer.Write((byte)ttl);
        writer.Write((byte)0x00); writer.Write((byte)0x04);
        writer.Write(ipBytes);

        WriteMdnsName(writer, serviceName);
        writer.Write((byte)0x00); writer.Write((byte)0x10);
        writer.Write((byte)0x00); writer.Write((byte)0x01);
        writer.Write((byte)(ttl >> 24)); writer.Write((byte)(ttl >> 16));
        writer.Write((byte)(ttl >> 8)); writer.Write((byte)ttl);
        ushort txtLen = (ushort)(1 + txtBytes.Length);
        writer.Write((byte)(txtLen >> 8)); writer.Write((byte)txtLen);
        writer.Write((byte)txtEntry.Length);
        writer.Write(txtBytes);

        return ms.ToArray();
    }

    private void WriteMdnsName(BinaryWriter writer, string name)
    {
        foreach (var part in name.Split('.'))
        {
            var partBytes = Encoding.UTF8.GetBytes(part);
            writer.Write((byte)partBytes.Length);
            writer.Write(partBytes);
        }
        writer.Write((byte)0);
    }

    private string ReadMdnsName(byte[] data, ref int offset)
    {
        var sb = new StringBuilder();
        while (offset < data.Length)
        {
            byte len = data[offset++];
            if (len == 0) break;
            if ((len & 0xC0) == 0xC0) { offset++; break; }
            if (offset + len > data.Length) break;
            if (sb.Length > 0) sb.Append('.');
            sb.Append(Encoding.UTF8.GetString(data, offset, len));
            offset += len;
        }
        return sb.ToString();
    }

    private void ProcessDataPacket(byte[] data, IPEndPoint remoteEP)
    {
        if (!NetworkMidi2Protocol.ParseUDPPacket(data, out var commandPackets)) return;
        foreach (var cmdPacket in commandPackets)
            ProcessCommandPacket(cmdPacket, remoteEP);
    }

    private void ProcessCommandPacket(byte[] cmdPacket, IPEndPoint remoteEP)
    {
        if (!NetworkMidi2Protocol.ParseCommandPacket(cmdPacket, out var cmdCode, out _, out var cmdSpecific1, out var cmdSpecific2, out var payload)) return;

        switch (cmdCode)
        {
            case NetworkMidi2Protocol.CommandCode.Invitation:
                ProcessInvitation(payload, cmdSpecific1, remoteEP);
                break;
            case NetworkMidi2Protocol.CommandCode.InvitationWithAuth:
            case NetworkMidi2Protocol.CommandCode.InvitationWithUserAuth:
                ProcessAuthInvitation(cmdCode, payload, remoteEP);
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
            case NetworkMidi2Protocol.CommandCode.UMPData:
                NetworkMidi2Protocol.ParseUMPDataCommand(cmdSpecific1, cmdSpecific2, payload, out var seq, out var umpData);
                ProcessUMPData(seq, umpData);
                break;
        }
    }

    private void ProcessInvitation(byte[] payload, byte nameWords, IPEndPoint remoteEP)
    {
        if (!NetworkMidi2Protocol.ParseInvitationCommand(payload, nameWords, out var umpEndpointName, out var productInstanceId, out _))
        { SendNAK(remoteEP); return; }

        _remoteEP = remoteEP;
        _remoteSSRC = (uint)Random.Shared.Next(1, int.MaxValue);

        var replyCmd = NetworkMidi2Protocol.CreateInvitationReplyAccepted(_serviceName, _productId);
        _dataServer?.Send(NetworkMidi2Protocol.CreateUDPPacket(replyCmd), replyCmd.Length + 4, remoteEP);

        OnLog?.Invoke($"INV from {remoteEP} (name: {umpEndpointName})");
    }

    private void ProcessAuthInvitation(NetworkMidi2Protocol.CommandCode cmdCode, byte[] payload, IPEndPoint remoteEP)
    {
        _remoteEP = remoteEP;
        _remoteSSRC = (uint)Random.Shared.Next(1, int.MaxValue);

        var replyCmd = NetworkMidi2Protocol.CreateInvitationReplyAccepted(_serviceName, _productId);
        _dataServer?.Send(NetworkMidi2Protocol.CreateUDPPacket(replyCmd), replyCmd.Length + 4, remoteEP);

        OnLog?.Invoke($"{cmdCode} from {remoteEP}");
    }

    private void ProcessPing(byte[] payload, IPEndPoint remoteEP)
    {
        if (!NetworkMidi2Protocol.ParsePingCommand(payload, out var pingId)) return;
        var replyCmd = NetworkMidi2Protocol.CreatePingReplyCommand(pingId);
        _dataServer?.Send(NetworkMidi2Protocol.CreateUDPPacket(replyCmd), replyCmd.Length + 4, remoteEP);
        OnLog?.Invoke($"PING id={pingId}");
    }

    private void ProcessBye(byte[] payload, IPEndPoint remoteEP)
    {
        var replyCmd = NetworkMidi2Protocol.CreateByeReplyCommand();
        _dataServer?.Send(NetworkMidi2Protocol.CreateUDPPacket(replyCmd), replyCmd.Length + 4, remoteEP);
        OnLog?.Invoke("BYE received");
        _remoteEP = null;
        _remoteSSRC = 0;
    }

    private void ProcessSessionReset(IPEndPoint remoteEP)
    {
        var replyCmd = NetworkMidi2Protocol.CreateSessionResetReplyCommand();
        _dataServer?.Send(NetworkMidi2Protocol.CreateUDPPacket(replyCmd), replyCmd.Length + 4, remoteEP);
        _sendSequence = 0;
        OnLog?.Invoke("SESSION_RESET received");
    }

    private void ProcessUMPData(ushort sequenceNumber, byte[] umpData)
    {
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
                ReceivedMidiCount++;
            }
            else if (mt == 0x4 && packetSize >= 8)
            {
                byte status = umpData[offset + 1];
                byte note = umpData[offset + 2];
                ushort velocity = (ushort)((umpData[offset + 5] << 8) | umpData[offset + 6]);
                OnMidiReceived?.Invoke(new byte[] { status, note, (byte)(velocity >> 9) });
                ReceivedMidiCount++;
            }
            offset += packetSize;
        }
    }

    private void SendNAK(IPEndPoint remoteEP)
    {
        var nakCmd = NetworkMidi2Protocol.CreateNAKCommand(NetworkMidi2Protocol.NAKReason.CommandMalformed, new byte[4]);
        _dataServer?.Send(NetworkMidi2Protocol.CreateUDPPacket(nakCmd), nakCmd.Length + 4, remoteEP);
    }

    public void SendNoteOn(byte note, byte velocity, byte channel = 0)
    {
        if (_remoteEP == null || _dataServer == null) return;

        byte[] umpData = new byte[8];
        umpData[0] = 0x40;
        umpData[1] = (byte)(0x90 | channel);
        umpData[2] = note;
        umpData[5] = (byte)((velocity << 1) >> 8);
        umpData[6] = (byte)((velocity << 1) & 0xFF);

        var umpCmd = NetworkMidi2Protocol.CreateUMPDataCommand(_sendSequence++, umpData);
        _dataServer.Send(NetworkMidi2Protocol.CreateUDPPacket(umpCmd), umpCmd.Length + 4, _remoteEP);
    }

    public void SendNoteOff(byte note, byte channel = 0)
    {
        if (_remoteEP == null || _dataServer == null) return;

        byte[] umpData = new byte[8];
        umpData[0] = 0x40;
        umpData[1] = (byte)(0x80 | channel);
        umpData[2] = note;

        var umpCmd = NetworkMidi2Protocol.CreateUMPDataCommand(_sendSequence++, umpData);
        _dataServer.Send(NetworkMidi2Protocol.CreateUDPPacket(umpCmd), umpCmd.Length + 4, _remoteEP);
    }

    private IPAddress GetLocalIPAddress()
    {
        foreach (var ip in Dns.GetHostEntry(Dns.GetHostName()).AddressList)
            if (ip.AddressFamily == AddressFamily.InterNetwork) return ip;
        return IPAddress.Loopback;
    }

    public void Dispose()
    {
        Stop();
        try { _mdnsClient?.DropMulticastGroup(IPAddress.Parse(MDNS_MULTICAST_ADDRESS)); } catch { }
        _dataServer?.Close();
        _mdnsClient?.Close();
        _cts?.Dispose();
    }
}