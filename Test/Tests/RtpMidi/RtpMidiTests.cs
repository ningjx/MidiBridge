using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Test.Tests.RtpMidi;

public static class RtpMidiTests
{
    public static Task ServerTest()
    {
        Console.WriteLine("\n--- RTP-MIDI 服务端测试 ---");
        Console.Write("控制端口 (默认 5004): ");
        int controlPort = int.TryParse(Console.ReadLine(), out var cp) ? cp : 5004;
        int dataPort = controlPort + 1;

        using var server = new RtpMidiTestServer();
        server.OnLog += msg => Console.WriteLine(msg);
        server.OnMidiReceived += midi => Console.WriteLine($"[MIDI] {BitConverter.ToString(midi)}");

        if (!server.Start(controlPort))
        {
            Console.WriteLine("启动失败");
            return Task.CompletedTask;
        }

        Console.WriteLine("按任意键停止服务");
        Console.ReadKey(true);
        server.Stop();
        return Task.CompletedTask;
    }

    public static async Task ClientTest()
    {
        Console.WriteLine("\n--- RTP-MIDI 客户端测试 ---");
        Console.Write("目标 IP (默认 127.0.0.1): ");
        var ipStr = Console.ReadLine();
        if (string.IsNullOrEmpty(ipStr)) ipStr = "127.0.0.1";

        Console.Write("控制端口 (默认 5004): ");
        int controlPort = int.TryParse(Console.ReadLine(), out var cp) ? cp : 5004;

        using var client = new RtpMidiTestClient();
        client.OnLog += msg => Console.WriteLine(msg);
        client.OnMidiReceived += midi => Console.WriteLine($"[MIDI] {BitConverter.ToString(midi)}");

        if (!await client.ConnectAsync(ipStr, controlPort, "TestClient"))
        {
            Console.WriteLine("连接失败");
            return;
        }

        Console.WriteLine("\n连接成功! 按任意键进入钢琴模式，ESC 退出");
        Console.ReadKey(true);

        Common.PianoHelper.PrintInstructions();
        Common.PianoHelper.RunLoop(
            (note, vel) => client.SendMidi(vel > 0 ? (byte)0x90 : (byte)0x80, (byte)note, (byte)(vel > 0 ? vel : 0)),
            on => client.SendMidi(0xB0, 64, (byte)(on ? 127 : 0))
        );

        client.Disconnect();
        Console.WriteLine("已断开连接");
    }
}

public class RtpMidiTestServer : IDisposable
{
    private UdpClient? _controlServer;
    private UdpClient? _dataServer;
    private CancellationTokenSource? _cts;
    private uint _localSSRC;
    private uint _remoteSSRC;
    private IPEndPoint? _remoteControlEP;
    private IPEndPoint? _remoteDataEP;
    private ushort _sendSeq;
    private ushort _expectedSeq;
    private long _startTimeTicks;
    private const int SAMPLE_RATE = 44100;
    private const int HEARTBEAT_INTERVAL_MS = 5000;
    private DateTime _lastReceiveTime;
    private DateTime _lastCkSent = DateTime.MinValue;

    public event Action<string>? OnLog;
    public event Action<byte[]>? OnMidiReceived;
    public bool IsConnected => _remoteControlEP != null;

    public RtpMidiTestServer()
    {
        _localSSRC = (uint)Random.Shared.Next(1, int.MaxValue);
        _startTimeTicks = DateTimeOffset.UtcNow.Ticks;
    }

    public bool Start(int controlPort)
    {
        try
        {
            _controlServer = new UdpClient(controlPort);
            _dataServer = new UdpClient(controlPort + 1);
            _cts = new CancellationTokenSource();

            Task.Run(() => ControlLoop(_cts.Token));
            Task.Run(() => DataLoop(_cts.Token));
            Task.Run(() => HeartbeatLoop(_cts.Token));
            Task.Run(() => TimeoutCheckLoop(_cts.Token));

            OnLog?.Invoke($"RTP-MIDI 服务已启动: 端口 {controlPort}, SSRC: 0x{_localSSRC:X8}");
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
        if (_remoteControlEP != null && _controlServer != null)
        {
            SendBye(_remoteControlEP);
        }
        _cts?.Cancel();
        _controlServer?.Close();
        _dataServer?.Close();
        _remoteControlEP = null;
        _remoteDataEP = null;
        OnLog?.Invoke("服务已停止");
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
            catch { break; }
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
            catch { break; }
        }
    }

    private async void HeartbeatLoop(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(HEARTBEAT_INTERVAL_MS, ct);
                if (_remoteControlEP != null)
                {
                    SendCk(_remoteControlEP);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch { }
    }

    private async void TimeoutCheckLoop(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(5000, ct);
                if (_remoteControlEP != null && (DateTime.Now - _lastReceiveTime).TotalSeconds > 60)
                {
                    OnLog?.Invoke("对端超时，断开连接");
                    _remoteControlEP = null;
                    _remoteDataEP = null;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch { }
    }

    private void ProcessControlPacket(byte[] data, IPEndPoint remoteEP)
    {
        if (data.Length < 12) return;

        string cmd = Encoding.ASCII.GetString(data, 2, 2);
        _lastReceiveTime = DateTime.Now;

        switch (cmd)
        {
            case "IN":
                HandleInvitation(data, remoteEP);
                break;
            case "OK":
                HandleInvitationAccept(data, remoteEP);
                break;
            case "CK":
                HandleCk(data, remoteEP);
                break;
            case "BY":
                HandleBye(data, remoteEP);
                break;
        }
    }

    private void HandleInvitation(byte[] data, IPEndPoint remoteEP)
    {
        if (data.Length < 16) return;
        _remoteSSRC = ReadBigEndianUInt32(data, 8);
        int nameLen = (data[12] << 8) | data[13];
        string name = nameLen > 0 && data.Length >= 16 + nameLen 
            ? Encoding.ASCII.GetString(data, 16, nameLen) 
            : "Unknown";

        _remoteControlEP = remoteEP;
        _remoteDataEP = new IPEndPoint(remoteEP.Address, remoteEP.Port + 1);
        _expectedSeq = 0;
        _sendSeq = 0;

        SendOk(remoteEP, name);
        OnLog?.Invoke($"INV from {remoteEP} (name: {name}, ssrc: 0x{_remoteSSRC:X8})");
    }

    private void HandleInvitationAccept(byte[] data, IPEndPoint remoteEP)
    {
        if (data.Length < 16) return;
        _remoteSSRC = ReadBigEndianUInt32(data, 8);
        int nameLen = (data[12] << 8) | data[13];
        string name = nameLen > 0 && data.Length >= 16 + nameLen
            ? Encoding.ASCII.GetString(data, 16, nameLen)
            : "Unknown";

        _remoteControlEP = remoteEP;
        _remoteDataEP = new IPEndPoint(remoteEP.Address, remoteEP.Port + 1);

        OnLog?.Invoke($"OK from {remoteEP} (name: {name})");
    }

    private void HandleCk(byte[] data, IPEndPoint remoteEP)
    {
        if (data.Length >= 8)
        {
            uint receivedSSRC = ReadBigEndianUInt32(data, 4);
            if (receivedSSRC == _localSSRC)
            {
                return;
            }
        }
        SendCkReply(data, remoteEP);
        OnLog?.Invoke($"CK from {remoteEP}");
    }

    private void HandleBye(byte[] data, IPEndPoint remoteEP)
    {
        SendByeReply(remoteEP);
        _remoteControlEP = null;
        _remoteDataEP = null;
        OnLog?.Invoke("BYE received, connection closed");
    }

    private void ProcessDataPacket(byte[] data, IPEndPoint remoteEP)
    {
        _lastReceiveTime = DateTime.Now;
        if (data.Length < 13) return;

        ushort seq = (ushort)((data[2] << 8) | data[3]);

        int offset = 12;
        byte header = data[offset++];
        int midiLen = header & 0x0F;

        if (midiLen == 15 && offset < data.Length)
        {
            int lenByte = data[offset++];
            if (lenByte < 255) midiLen = lenByte;
            else if (offset + 2 <= data.Length)
            {
                midiLen = (data[offset] << 8) | data[offset + 1];
                offset += 2;
            }
        }

        bool hasJournal = (header & 0x40) != 0;
        if (hasJournal && offset < data.Length)
        {
            offset += 3;
        }

        if (offset + midiLen <= data.Length && midiLen > 0)
        {
            byte[] midiData = new byte[midiLen];
            Buffer.BlockCopy(data, offset, midiData, 0, midiLen);
            OnMidiReceived?.Invoke(midiData);
        }

        _expectedSeq = (ushort)(seq + 1);
    }

    private void SendOk(IPEndPoint remoteEP, string name)
    {
        var nameBytes = Encoding.ASCII.GetBytes("TestServer");
        var packet = new byte[16 + nameBytes.Length];

        packet[0] = 0xFF; packet[1] = 0xFF;
        packet[2] = (byte)'O'; packet[3] = (byte)'K';

        WriteBigEndianUInt32(packet, 4, 2);
        WriteBigEndianUInt32(packet, 8, _remoteSSRC);
        packet[12] = (byte)((nameBytes.Length >> 8) & 0xFF);
        packet[13] = (byte)(nameBytes.Length & 0xFF);
        Buffer.BlockCopy(nameBytes, 0, packet, 16, nameBytes.Length);

        _controlServer?.Send(packet, packet.Length, remoteEP);
    }

    private void SendCk(IPEndPoint remoteEP)
    {
        _lastCkSent = DateTime.Now;
        var packet = new byte[12];
        packet[0] = 0xFF; packet[1] = 0xFF;
        packet[2] = (byte)'C'; packet[3] = (byte)'K';

        WriteBigEndianUInt32(packet, 4, _localSSRC);

        long ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        packet[8] = (byte)(ms >> 24);
        packet[9] = (byte)(ms >> 16);
        packet[10] = (byte)(ms >> 8);
        packet[11] = (byte)ms;

        _controlServer?.Send(packet, packet.Length, remoteEP);
    }

    private void SendCkReply(byte[] data, IPEndPoint remoteEP)
    {
        var packet = new byte[12];
        packet[0] = 0xFF; packet[1] = 0xFF;
        packet[2] = (byte)'C'; packet[3] = (byte)'K';

        WriteBigEndianUInt32(packet, 4, _localSSRC);

        long ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        packet[8] = (byte)(ms >> 24);
        packet[9] = (byte)(ms >> 16);
        packet[10] = (byte)(ms >> 8);
        packet[11] = (byte)ms;

        _controlServer?.Send(packet, packet.Length, remoteEP);
    }

    private void SendBye(IPEndPoint remoteEP)
    {
        var packet = new byte[4];
        packet[0] = 0xFF; packet[1] = 0xFF;
        packet[2] = (byte)'B'; packet[3] = (byte)'Y';
        _controlServer?.Send(packet, packet.Length, remoteEP);
    }

    private void SendByeReply(IPEndPoint remoteEP)
    {
        var packet = new byte[4];
        packet[0] = 0xFF; packet[1] = 0xFF;
        packet[2] = (byte)'B'; packet[3] = (byte)'Y';
        _controlServer?.Send(packet, packet.Length, remoteEP);
    }

    public void SendMidi(byte status, byte data1, byte data2)
    {
        if (_remoteDataEP == null || _dataServer == null) return;

        var midiData = new byte[] { status, data1, data2 };
        ushort seq = _sendSeq++;

        uint timestamp = GetRtpTimestamp();

        var packet = new byte[16 + midiData.Length];
        packet[0] = 0x80; packet[1] = 0x61;
        WriteBigEndianUInt32(packet, 4, _localSSRC);

        packet[2] = (byte)(seq >> 8);
        packet[3] = (byte)seq;

        packet[8] = (byte)(timestamp >> 24);
        packet[9] = (byte)(timestamp >> 16);
        packet[10] = (byte)(timestamp >> 8);
        packet[11] = (byte)timestamp;

        packet[12] = (byte)(midiData.Length & 0x0F);
        Buffer.BlockCopy(midiData, 0, packet, 13, midiData.Length);

        _dataServer.Send(packet, packet.Length, _remoteDataEP);
    }

    private uint GetRtpTimestamp()
    {
        long elapsedTicks = DateTimeOffset.UtcNow.Ticks - _startTimeTicks;
        double elapsedSeconds = elapsedTicks / (double)TimeSpan.TicksPerSecond;
        return (uint)(elapsedSeconds * SAMPLE_RATE);
    }

    private static uint ReadBigEndianUInt32(byte[] data, int offset)
    {
        return (uint)((data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3]);
    }

    private static void WriteBigEndianUInt32(byte[] data, int offset, uint value)
    {
        data[offset] = (byte)(value >> 24);
        data[offset + 1] = (byte)(value >> 16);
        data[offset + 2] = (byte)(value >> 8);
        data[offset + 3] = (byte)value;
    }

    public void Dispose()
    {
        Stop();
        _controlServer?.Dispose();
        _dataServer?.Dispose();
    }
}

public class RtpMidiTestClient : IDisposable
{
    private UdpClient? _controlClient;
    private UdpClient? _dataClient;
    private CancellationTokenSource? _cts;
    private uint _localSSRC;
    private uint _remoteSSRC;
    private IPEndPoint? _remoteControlEP;
    private IPEndPoint? _remoteDataEP;
    private ushort _sendSeq;
    private ushort _expectedSeq;
    private long _startTimeTicks;
    private const int SAMPLE_RATE = 44100;
    private const int HEARTBEAT_INTERVAL_MS = 5000;
    private DateTime _lastReceiveTime;
    private DateTime _lastCkSent = DateTime.MinValue;

    public event Action<string>? OnLog;
    public event Action<byte[]>? OnMidiReceived;
    public bool IsConnected => _remoteControlEP != null;

    public RtpMidiTestClient()
    {
        _localSSRC = (uint)Random.Shared.Next(1, int.MaxValue);
        _startTimeTicks = DateTimeOffset.UtcNow.Ticks;
    }

    public async Task<bool> ConnectAsync(string host, int controlPort, string name)
    {
        try
        {
            var ip = IPAddress.Parse(host);
            _remoteControlEP = new IPEndPoint(ip, controlPort);
            _remoteDataEP = new IPEndPoint(ip, controlPort + 1);

            _controlClient = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
            _dataClient = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
            _cts = new CancellationTokenSource();

            OnLog?.Invoke($"本地控制端口: {((IPEndPoint)_controlClient.Client.LocalEndPoint).Port}");
            OnLog?.Invoke($"本地数据端口: {((IPEndPoint)_dataClient.Client.LocalEndPoint).Port}");

            Task.Run(() => ControlLoop(_cts.Token));
            Task.Run(() => DataLoop(_cts.Token));
            Task.Run(() => HeartbeatLoop(_cts.Token));
            Task.Run(() => TimeoutCheckLoop(_cts.Token));

            SendInvitation(name);
            OnLog?.Invoke($"发送 INV 到 {host}:{controlPort}，等待响应...");

            for (int i = 0; i < 10; i++)
            {
                await Task.Delay(200);
                if (_remoteSSRC != 0) break;
            }

            if (_remoteSSRC == 0)
            {
                OnLog?.Invoke("未收到 OK 响应（等待 2 秒）");
                return false;
            }

            _lastReceiveTime = DateTime.Now;
            return true;
        }
        catch (Exception ex)
        {
            OnLog?.Invoke($"连接失败: {ex.Message}");
            return false;
        }
    }

    public void Disconnect()
    {
        if (_remoteControlEP != null && _controlClient != null)
        {
            SendBye(_remoteControlEP);
        }
        _cts?.Cancel();
        _controlClient?.Close();
        _dataClient?.Close();
        _controlClient?.Dispose();
        _dataClient?.Dispose();
        _controlClient = null;
        _dataClient = null;
        _cts?.Dispose();
        _cts = null;
        _remoteControlEP = null;
        _remoteDataEP = null;
        _remoteSSRC = 0;
        OnLog?.Invoke("已断开连接");
    }

    private async void ControlLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _controlClient != null)
        {
            try
            {
                var result = await _controlClient.ReceiveAsync();
                OnLog?.Invoke($"[控制] 收到 {result.Buffer.Length} 字节 from {result.RemoteEndPoint}");
                ProcessControlPacket(result.Buffer, result.RemoteEndPoint);
            }
            catch (Exception ex) 
            {
                OnLog?.Invoke($"ControlLoop 异常: {ex.Message}");
                break;
            }
        }
    }

    private async void DataLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _dataClient != null)
        {
            try
            {
                var result = await _dataClient.ReceiveAsync();
                ProcessDataPacket(result.Buffer, result.RemoteEndPoint);
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"DataLoop 异常: {ex.Message}");
                break;
            }
        }
    }

    private async void HeartbeatLoop(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(HEARTBEAT_INTERVAL_MS, ct);
                if (_remoteControlEP != null)
                {
                    SendCk(_remoteControlEP);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch { }
    }

    private async void TimeoutCheckLoop(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(5000, ct);
                if (_remoteControlEP != null && (DateTime.Now - _lastReceiveTime).TotalSeconds > 60)
                {
                    OnLog?.Invoke("对端超时，断开连接");
                    _remoteControlEP = null;
                    _remoteDataEP = null;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch { }
    }

    private void ProcessControlPacket(byte[] data, IPEndPoint remoteEP)
    {
        if (data.Length < 4) return;
        string cmd = Encoding.ASCII.GetString(data, 2, 2);
        _lastReceiveTime = DateTime.Now;

        switch (cmd)
        {
            case "OK":
                HandleOk(data, remoteEP);
                break;
            case "CK":
                HandleCk(data, remoteEP);
                break;
            case "BY":
                HandleBye(data, remoteEP);
                break;
        }
    }

    private void HandleOk(byte[] data, IPEndPoint remoteEP)
    {
        if (data.Length < 16) return;
        _remoteSSRC = ReadBigEndianUInt32(data, 8);
        int nameLen = (data[12] << 8) | data[13];
        string name = nameLen > 0 && data.Length >= 16 + nameLen
            ? Encoding.ASCII.GetString(data, 16, nameLen)
            : "Unknown";

        _remoteControlEP = remoteEP;
        _remoteDataEP = new IPEndPoint(remoteEP.Address, remoteEP.Port + 1);
        _sendSeq = 0;
        _expectedSeq = 0;

        OnLog?.Invoke($"收到 OK from {remoteEP} (name: {name}, ssrc: 0x{_remoteSSRC:X8})");
    }

    private void HandleCk(byte[] data, IPEndPoint remoteEP)
    {
        if (data.Length >= 8)
        {
            uint receivedSSRC = ReadBigEndianUInt32(data, 4);
            if (receivedSSRC == _localSSRC)
            {
                return;
            }
        }
        SendCkReply(data, remoteEP);
    }

    private void HandleBye(byte[] data, IPEndPoint remoteEP)
    {
        SendByeReply(remoteEP);
        _remoteControlEP = null;
        _remoteDataEP = null;
        OnLog?.Invoke("收到 BYE，连接已断开");
    }

    private void ProcessDataPacket(byte[] data, IPEndPoint remoteEP)
    {
        _lastReceiveTime = DateTime.Now;
        if (data.Length < 13) return;

        ushort seq = (ushort)((data[2] << 8) | data[3]);

        int offset = 12;
        byte header = data[offset++];
        int midiLen = header & 0x0F;

        if (midiLen == 15 && offset < data.Length)
        {
            int lenByte = data[offset++];
            if (lenByte < 255) midiLen = lenByte;
            else if (offset + 2 <= data.Length)
            {
                midiLen = (data[offset] << 8) | data[offset + 1];
                offset += 2;
            }
        }

        bool hasJournal = (header & 0x40) != 0;
        if (hasJournal && offset < data.Length)
        {
            offset += 3;
        }

        if (offset + midiLen <= data.Length && midiLen > 0)
        {
            byte[] midiData = new byte[midiLen];
            Buffer.BlockCopy(data, offset, midiData, 0, midiLen);
            OnMidiReceived?.Invoke(midiData);
        }

        _expectedSeq = (ushort)(seq + 1);
    }

    private void SendInvitation(string name)
    {
        var nameBytes = Encoding.ASCII.GetBytes(name);
        var packet = new byte[16 + nameBytes.Length];

        packet[0] = 0xFF; packet[1] = 0xFF;
        packet[2] = (byte)'I'; packet[3] = (byte)'N';

        WriteBigEndianUInt32(packet, 4, 2);
        WriteBigEndianUInt32(packet, 8, _localSSRC);
        packet[12] = (byte)((nameBytes.Length >> 8) & 0xFF);
        packet[13] = (byte)(nameBytes.Length & 0xFF);
        Buffer.BlockCopy(nameBytes, 0, packet, 16, nameBytes.Length);

        _controlClient?.Send(packet, packet.Length, _remoteControlEP);
    }

    private void SendCk(IPEndPoint remoteEP)
    {
        _lastCkSent = DateTime.Now;
        var packet = new byte[12];
        packet[0] = 0xFF; packet[1] = 0xFF;
        packet[2] = (byte)'C'; packet[3] = (byte)'K';

        WriteBigEndianUInt32(packet, 4, _localSSRC);

        long ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        packet[8] = (byte)(ms >> 24);
        packet[9] = (byte)(ms >> 16);
        packet[10] = (byte)(ms >> 8);
        packet[11] = (byte)ms;

        _controlClient?.Send(packet, packet.Length, remoteEP);
    }

    private void SendCkReply(byte[] data, IPEndPoint remoteEP)
    {
        var packet = new byte[12];
        packet[0] = 0xFF; packet[1] = 0xFF;
        packet[2] = (byte)'C'; packet[3] = (byte)'K';

        WriteBigEndianUInt32(packet, 4, _localSSRC);

        long ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        packet[8] = (byte)(ms >> 24);
        packet[9] = (byte)(ms >> 16);
        packet[10] = (byte)(ms >> 8);
        packet[11] = (byte)ms;

        _controlClient?.Send(packet, packet.Length, remoteEP);
    }

    private void SendBye(IPEndPoint remoteEP)
    {
        var packet = new byte[4];
        packet[0] = 0xFF; packet[1] = 0xFF;
        packet[2] = (byte)'B'; packet[3] = (byte)'Y';
        _controlClient?.Send(packet, packet.Length, remoteEP);
    }

    private void SendByeReply(IPEndPoint remoteEP)
    {
        var packet = new byte[4];
        packet[0] = 0xFF; packet[1] = 0xFF;
        packet[2] = (byte)'B'; packet[3] = (byte)'Y';
        _controlClient?.Send(packet, packet.Length, remoteEP);
    }

    public void SendMidi(byte status, byte data1, byte data2)
    {
        if (_remoteDataEP == null || _dataClient == null) return;

        var midiData = new byte[] { status, data1, data2 };
        ushort seq = _sendSeq++;

        uint timestamp = GetRtpTimestamp();

        var packet = new byte[16 + midiData.Length];
        packet[0] = 0x80; packet[1] = 0x61;
        WriteBigEndianUInt32(packet, 4, _localSSRC);

        packet[2] = (byte)(seq >> 8);
        packet[3] = (byte)seq;

        packet[8] = (byte)(timestamp >> 24);
        packet[9] = (byte)(timestamp >> 16);
        packet[10] = (byte)(timestamp >> 8);
        packet[11] = (byte)timestamp;

        packet[12] = (byte)(midiData.Length & 0x0F);
        Buffer.BlockCopy(midiData, 0, packet, 13, midiData.Length);

        _dataClient.Send(packet, packet.Length, _remoteDataEP);
    }

    private uint GetRtpTimestamp()
    {
        long elapsedTicks = DateTimeOffset.UtcNow.Ticks - _startTimeTicks;
        double elapsedSeconds = elapsedTicks / (double)TimeSpan.TicksPerSecond;
        return (uint)(elapsedSeconds * SAMPLE_RATE);
    }

    private static uint ReadBigEndianUInt32(byte[] data, int offset)
    {
        return (uint)((data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3]);
    }

    private static void WriteBigEndianUInt32(byte[] data, int offset, uint value)
    {
        data[offset] = (byte)(value >> 24);
        data[offset + 1] = (byte)(value >> 16);
        data[offset + 2] = (byte)(value >> 8);
        data[offset + 3] = (byte)value;
    }

    public void Dispose()
    {
        Disconnect();
        _controlClient?.Dispose();
        _dataClient?.Dispose();
    }
}