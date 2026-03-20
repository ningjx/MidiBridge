using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Test.Tests.RtpMidi;

public static class RtpMidiTests
{
    public static async Task ServerTest()
    {
        Console.WriteLine("\n--- RTP-MIDI 服务端测试 ---");
        Console.Write("控制端口 (默认 5004): ");
        int controlPort = int.TryParse(Console.ReadLine(), out var cp) ? cp : 5004;
        int dataPort = controlPort + 1;

        using var controlServer = new UdpClient(controlPort);
        using var dataServer = new UdpClient(dataPort);
        var cts = new CancellationTokenSource();

        Console.WriteLine($"RTP-MIDI 服务已启动: 控制端口 {controlPort}, 数据端口 {dataPort}");

        var controlTask = Task.Run(async () =>
        {
            while (!cts.Token.IsCancellationRequested)
            {
                try
                {
                    var result = await controlServer.ReceiveAsync();
                    var cmd = Encoding.ASCII.GetString(result.Buffer, 2, 2);
                    Console.WriteLine($"[控制] 收到 {cmd} from {result.RemoteEndPoint}");

                    if (cmd == "IN") controlServer.Send(CreateOkResponse("TestServer"), 26, result.RemoteEndPoint);
                    else if (cmd == "CK") controlServer.Send(CreateCkResponse(), 12, result.RemoteEndPoint);
                }
                catch { break; }
            }
        }, cts.Token);

        var dataTask = Task.Run(async () =>
        {
            while (!cts.Token.IsCancellationRequested)
            {
                try
                {
                    var result = await dataServer.ReceiveAsync();
                    if (result.Buffer.Length >= 13) ParseMidiData(result.Buffer);
                }
                catch { break; }
            }
        }, cts.Token);

        Console.WriteLine("按任意键停止服务");
        Console.ReadKey(true);
        cts.Cancel();
        await Task.WhenAll(controlTask, dataTask);
    }

    public static async Task PianoTest()
    {
        Console.WriteLine("\n--- RTP-MIDI 钢琴键盘 ---");
        Console.Write("目标 IP (默认 127.0.0.1): ");
        var ipStr = Console.ReadLine();
        if (string.IsNullOrEmpty(ipStr)) ipStr = "127.0.0.1";

        Console.Write("控制端口 (默认 5004): ");
        int controlPort = int.TryParse(Console.ReadLine(), out var cp) ? cp : 5004;

        try
        {
            var ip = IPAddress.Parse(ipStr);
            var controlEp = new IPEndPoint(ip, controlPort);
            var dataEp = new IPEndPoint(ip, controlPort + 1);

            using var client = new UdpClient();
            client.Send(CreateInvitation("PianoClient"), 26, controlEp);
            Console.WriteLine($"连接到 {ipStr}:{controlPort}...");

            client.Client.ReceiveTimeout = 3000;
            try
            {
                var response = await client.ReceiveAsync();
                if (Encoding.ASCII.GetString(response.Buffer, 2, 2) == "OK")
                    Console.WriteLine("连接成功!\n");
            }
            catch { Console.WriteLine("未收到响应，继续发送数据...\n"); }

            Common.PianoHelper.PrintInstructions();
            Common.PianoHelper.RunLoop(
                (note, vel) => client.Send(CreateMidiPacket((byte)(vel > 0 ? 0x90 : 0x80), (byte)note, (byte)(vel > 0 ? vel : 0)), 16, dataEp),
                on => client.Send(CreateCcPacket(64, (byte)(on ? 127 : 0)), 16, dataEp)
            );

            client.Send(CreateByePacket(), 16, controlEp);
            Console.WriteLine("已断开连接");
        }
        catch (Exception ex) { Console.WriteLine($"错误: {ex.Message}"); }
    }

    private static byte[] CreateOkResponse(string name)
    {
        var nameBytes = Encoding.ASCII.GetBytes(name);
        var packet = new byte[16 + nameBytes.Length];
        packet[1] = 0xFF; packet[2] = (byte)'O'; packet[3] = (byte)'K';
        packet[7] = 2;
        var ssrc = Random.Shared.Next();
        packet[8] = (byte)(ssrc >> 24); packet[9] = (byte)(ssrc >> 16);
        packet[10] = (byte)(ssrc >> 8); packet[11] = (byte)ssrc;
        packet[12] = (byte)(nameBytes.Length >> 8); packet[13] = (byte)nameBytes.Length;
        Buffer.BlockCopy(nameBytes, 0, packet, 16, nameBytes.Length);
        return packet;
    }

    private static byte[] CreateCkResponse()
    {
        var packet = new byte[12];
        packet[1] = 0xFF; packet[2] = (byte)'C'; packet[3] = (byte)'K';
        var ssrc = Random.Shared.Next();
        packet[4] = (byte)(ssrc >> 24); packet[5] = (byte)(ssrc >> 16);
        packet[6] = (byte)(ssrc >> 8); packet[7] = (byte)ssrc;
        return packet;
    }

    private static void ParseMidiData(byte[] data)
    {
        if (data.Length < 13) return;
        int offset = 12;
        int length = data[offset++] & 0x0F;
        if (offset + length > data.Length) return;

        Console.Write("[MIDI] ");
        for (int i = 0; i < length; i++) Console.Write($"{data[offset + i]:X2} ");
        Console.WriteLine();
    }

    private static byte[] CreateInvitation(string name)
    {
        var nameBytes = Encoding.ASCII.GetBytes(name);
        var packet = new byte[16 + nameBytes.Length];
        packet[1] = 0xFF; packet[2] = (byte)'I'; packet[3] = (byte)'N';
        packet[7] = 2;
        var ssrc = Random.Shared.Next();
        packet[8] = (byte)(ssrc >> 24); packet[9] = (byte)(ssrc >> 16);
        packet[10] = (byte)(ssrc >> 8); packet[11] = (byte)ssrc;
        packet[12] = (byte)(nameBytes.Length >> 8); packet[13] = (byte)nameBytes.Length;
        Buffer.BlockCopy(nameBytes, 0, packet, 16, nameBytes.Length);
        return packet;
    }

    private static byte[] CreateByePacket()
    {
        var packet = new byte[16];
        packet[1] = 0xFF; packet[2] = (byte)'B'; packet[3] = (byte)'Y';
        packet[7] = 2;
        return packet;
    }

    private static byte[] CreateMidiPacket(params byte[] midiData)
    {
        var packet = new byte[12 + 1 + midiData.Length];
        packet[0] = 0x80; packet[1] = 0x61;
        var ssrc = Random.Shared.Next();
        packet[4] = (byte)(ssrc >> 24); packet[5] = (byte)(ssrc >> 16);
        packet[6] = (byte)(ssrc >> 8); packet[7] = (byte)ssrc;
        packet[12] = (byte)(midiData.Length & 0x0F);
        Buffer.BlockCopy(midiData, 0, packet, 13, midiData.Length);
        return packet;
    }

    private static byte[] CreateCcPacket(byte controller, byte value)
    {
        return CreateMidiPacket(0xB0, controller, value);
    }
}