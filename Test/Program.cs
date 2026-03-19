using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using MidiBridge.Services.NetworkMidi2;
using NAudio.Midi;
using Test;

Console.WriteLine("=== MIDI 测试工具 ===\n");

// Win32 API for key state detection
[DllImport("user32.dll")]
static extern short GetAsyncKeyState(int vKey);

static bool IsKeyDown(ConsoleKey key)
{
    return (GetAsyncKeyState((int)key) & 0x8000) != 0;
}

while (true)
{
    Console.WriteLine("\n选择测试:");
    Console.WriteLine("1. 列出本地 MIDI 设备");
    Console.WriteLine("2. 测试 MIDI 输入监听");
    Console.WriteLine("3. 测试 MIDI 输出");
    Console.WriteLine("4. 启动 RTP-MIDI 服务端");
    Console.WriteLine("5. 发送 RTP-MIDI 测试消息");
    Console.WriteLine("6. RTP-MIDI 钢琴键盘 (向 MidiBridge 发送音符)");
    Console.WriteLine("7. Network MIDI 2.0 完整测试 (发现+会话+数据)");
    Console.WriteLine("8. Network MIDI 2.0 钢琴键盘");
    Console.WriteLine("9. 端口占用测试");
    Console.WriteLine("10. 创建多个 NM2 可发现设备 (测试发现窗口)");
    Console.WriteLine("0. 退出");
    Console.Write("\n请选择: ");

    var choice = Console.ReadLine();

    switch (choice)
    {
        case "1":
            ListMidiDevices();
            break;
        case "2":
            await TestMidiInput();
            break;
        case "3":
            TestMidiOutput();
            break;
        case "4":
            await StartRtpMidiServer();
            break;
        case "5":
            await SendRtpMidiTest();
            break;
        case "6":
            await RtpMidiPiano();
            break;
        case "7":
            await TestNM2FullSession();
            break;
        case "8":
            await NM2PianoStandalone();
            break;
        case "9":
            await PortOccupancyTest();
            break;
        case "10":
            await StartNM2DiscoverableServer();
            break;
        case "0":
            return;
        default:
            Console.WriteLine("无效选择");
            break;
    }
}

static void ListMidiDevices()
{
    Console.WriteLine("\n--- MIDI 输入设备 ---");
    for (int i = 0; i < MidiIn.NumberOfDevices; i++)
    {
        var info = MidiIn.DeviceInfo(i);
        Console.WriteLine($"[{i}] {info.ProductName} (ID: {info.ProductId})");
    }
    if (MidiIn.NumberOfDevices == 0)
        Console.WriteLine("(无)");

    Console.WriteLine("\n--- MIDI 输出设备 ---");
    for (int i = 0; i < MidiOut.NumberOfDevices; i++)
    {
        var info = MidiOut.DeviceInfo(i);
        Console.WriteLine($"[{i}] {info.ProductName} (ID: {info.ProductId})");
    }
    if (MidiOut.NumberOfDevices == 0)
        Console.WriteLine("(无)");
}

static async Task TestMidiInput()
{
    Console.WriteLine("\n--- MIDI 输入监听测试 ---");
    ListMidiDevices();

    if (MidiIn.NumberOfDevices == 0)
    {
        Console.WriteLine("没有可用的 MIDI 输入设备");
        return;
    }

    Console.Write("\n选择输入设备编号: ");
    if (!int.TryParse(Console.ReadLine(), out int deviceId) || deviceId < 0 || deviceId >= MidiIn.NumberOfDevices)
    {
        Console.WriteLine("无效的设备编号");
        return;
    }

    try
    {
        using var midiIn = new MidiIn(deviceId);
        midiIn.MessageReceived += (s, e) =>
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {e.MidiEvent?.ToString() ?? "Unknown"}");
        };

        midiIn.Start();
        Console.WriteLine($"正在监听 {MidiIn.DeviceInfo(deviceId).ProductName}...");
        Console.WriteLine("按任意键停止");

        await Task.Run(() => Console.ReadKey(true));
        midiIn.Stop();
        Console.WriteLine("监听已停止");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"错误: {ex.Message}");
    }
}

static void TestMidiOutput()
{
    Console.WriteLine("\n--- MIDI 输出测试 ---");
    ListMidiDevices();

    if (MidiOut.NumberOfDevices == 0)
    {
        Console.WriteLine("没有可用的 MIDI 输出设备");
        return;
    }

    Console.Write("\n选择输出设备编号: ");
    if (!int.TryParse(Console.ReadLine(), out int deviceId) || deviceId < 0 || deviceId >= MidiOut.NumberOfDevices)
    {
        Console.WriteLine("无效的设备编号");
        return;
    }

    try
    {
        using var midiOut = new MidiOut(deviceId);
        Console.WriteLine($"已连接到 {MidiOut.DeviceInfo(deviceId).ProductName}");

        Console.WriteLine("\n选择测试:");
        Console.WriteLine("1. 播放 C4 音符");
        Console.WriteLine("2. 播放 C 大调音阶");
        Console.WriteLine("3. 发送 Control Change (CC1 Modulation)");
        Console.WriteLine("4. 发送 Pitch Bend");

        Console.Write("选择: ");
        var choice = Console.ReadLine();

        switch (choice)
        {
            case "1":
                PlayNote(midiOut, 60, 100, 500);
                break;
            case "2":
                for (int note = 60; note <= 72; note++)
                {
                    PlayNote(midiOut, note, 100, 200);
                }
                break;
            case "3":
                SendControlChange(midiOut, 1, 127);
                Thread.Sleep(500);
                SendControlChange(midiOut, 1, 0);
                break;
            case "4":
                SendPitchBend(midiOut, 8192 + 2000);
                Thread.Sleep(500);
                SendPitchBend(midiOut, 8192);
                break;
        }

        Console.WriteLine("测试完成");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"错误: {ex.Message}");
    }
}

static void PlayNote(MidiOut midiOut, int note, int velocity, int durationMs)
{
    var noteOn = new NoteOnEvent(0, 1, note, velocity, durationMs);
    midiOut.Send(noteOn.GetAsShortMessage());
    Console.WriteLine($"Note On: {note} (vel: {velocity})");
    Thread.Sleep(durationMs);
    var noteOff = new NoteEvent(0, 1, MidiCommandCode.NoteOff, note, 0);
    midiOut.Send(noteOff.GetAsShortMessage());
    Console.WriteLine($"Note Off: {note}");
}

static void SendControlChange(MidiOut midiOut, int controller, int value)
{
    var cc = new ControlChangeEvent(0, 1, (MidiController)controller, value);
    midiOut.Send(cc.GetAsShortMessage());
    Console.WriteLine($"CC: controller={controller}, value={value}");
}

static void SendPitchBend(MidiOut midiOut, int value)
{
    var pb = new PitchWheelChangeEvent(0, 1, value);
    midiOut.Send(pb.GetAsShortMessage());
    Console.WriteLine($"Pitch Bend: {value}");
}

static async Task StartRtpMidiServer()
{
    Console.WriteLine("\n--- RTP-MIDI 服务端 ---");
    Console.Write("控制端口 (默认 5004): ");
    var portStr = Console.ReadLine();
    int controlPort = string.IsNullOrEmpty(portStr) ? 5004 : int.Parse(portStr);
    int dataPort = controlPort + 1;

    using var controlServer = new UdpClient(controlPort);
    using var dataServer = new UdpClient(dataPort);

    var cts = new CancellationTokenSource();

    Console.WriteLine($"RTP-MIDI 服务已启动:");
    Console.WriteLine($"  控制端口: {controlPort}");
    Console.WriteLine($"  数据端口: {dataPort}");
    Console.WriteLine("等待连接...");

    var controlTask = Task.Run(async () =>
    {
        while (!cts.Token.IsCancellationRequested)
        {
            try
            {
                var result = await controlServer.ReceiveAsync();
                var data = result.Buffer;
                var ep = result.RemoteEndPoint;

                if (data.Length >= 4)
                {
                    var cmd = Encoding.ASCII.GetString(data, 2, 2);
                    Console.WriteLine($"[控制端口] 收到 {cmd} 从 {ep.Address}:{ep.Port}");

                    if (cmd == "IN")
                    {
                        var response = CreateOkResponse("TestServer");
                        controlServer.Send(response, response.Length, ep);
                        Console.WriteLine($"[控制端口] 发送 OK 响应");
                    }
                    else if (cmd == "CK")
                    {
                        var response = CreateCkResponse();
                        controlServer.Send(response, response.Length, ep);
                        Console.WriteLine($"[控制端口] 发送 CK 响应");
                    }
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { Console.WriteLine($"控制端口错误: {ex.Message}"); }
        }
    }, cts.Token);

    var dataTask = Task.Run(async () =>
    {
        while (!cts.Token.IsCancellationRequested)
        {
            try
            {
                var result = await dataServer.ReceiveAsync();
                var data = result.Buffer;
                var ep = result.RemoteEndPoint;

                if (data.Length >= 4)
                {
                    var cmd = Encoding.ASCII.GetString(data, 2, 2);
                    Console.WriteLine($"[数据端口] 收到 {data.Length} 字节 从 {ep.Address}:{ep.Port}");

                    if (cmd == "CK")
                    {
                        var response = CreateCkResponse();
                        dataServer.Send(response, response.Length, ep);
                    }
                    else if (data.Length >= 13)
                    {
                        ParseMidiData(data);
                    }
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { Console.WriteLine($"数据端口错误: {ex.Message}"); }
        }
    }, cts.Token);

    Console.WriteLine("按任意键停止服务");
    Console.ReadKey(true);
    cts.Cancel();
    await Task.WhenAll(controlTask, dataTask);
    Console.WriteLine("服务已停止");
}

static byte[] CreateOkResponse(string name)
{
    var nameBytes = Encoding.ASCII.GetBytes(name);
    var packet = new byte[16 + nameBytes.Length];

    packet[0] = 0xFF;
    packet[1] = 0xFF;
    packet[2] = (byte)'O';
    packet[3] = (byte)'K';

    packet[4] = 0;
    packet[5] = 0;
    packet[6] = 0;
    packet[7] = 2;

    var ssrc = Random.Shared.Next();
    packet[8] = (byte)((ssrc >> 24) & 0xFF);
    packet[9] = (byte)((ssrc >> 16) & 0xFF);
    packet[10] = (byte)((ssrc >> 8) & 0xFF);
    packet[11] = (byte)(ssrc & 0xFF);

    packet[12] = (byte)((nameBytes.Length >> 8) & 0xFF);
    packet[13] = (byte)(nameBytes.Length & 0xFF);
    packet[14] = 0;
    packet[15] = 0;

    Buffer.BlockCopy(nameBytes, 0, packet, 16, nameBytes.Length);

    return packet;
}

static byte[] CreateCkResponse()
{
    var packet = new byte[12];
    packet[0] = 0xFF;
    packet[1] = 0xFF;
    packet[2] = (byte)'C';
    packet[3] = (byte)'K';

    var ssrc = Random.Shared.Next();
    packet[4] = (byte)((ssrc >> 24) & 0xFF);
    packet[5] = (byte)((ssrc >> 16) & 0xFF);
    packet[6] = (byte)((ssrc >> 8) & 0xFF);
    packet[7] = (byte)(ssrc & 0xFF);

    var count = BitConverter.GetBytes(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    if (BitConverter.IsLittleEndian) Array.Reverse(count);
    Buffer.BlockCopy(count, 0, packet, 8, 4);

    return packet;
}

static void ParseMidiData(byte[] data)
{
    if (data.Length < 13) return;

    int offset = 12;
    byte header = data[offset];
    int length = header & 0x0F;
    offset++;

    if (offset + length <= data.Length)
    {
        Console.Write($"[MIDI数据] ");
        for (int i = 0; i < length; i++)
        {
            Console.Write($"{data[offset + i]:X2} ");
        }
        Console.WriteLine();

        if (length >= 3)
        {
            byte status = data[offset];
            byte data1 = data[offset + 1];
            byte data2 = data[offset + 2];

            int command = status & 0xF0;
            int channel = status & 0x0F;

            switch (command)
            {
                case 0x90:
                    Console.WriteLine($"  -> Note On: ch{channel + 1}, note={data1}, vel={data2}");
                    break;
                case 0x80:
                    Console.WriteLine($"  -> Note Off: ch{channel + 1}, note={data1}");
                    break;
                case 0xB0:
                    Console.WriteLine($"  -> CC: ch{channel + 1}, controller={data1}, value={data2}");
                    break;
                case 0xE0:
                    int pitchBend = (data2 << 7) | data1;
                    Console.WriteLine($"  -> Pitch Bend: ch{channel + 1}, value={pitchBend}");
                    break;
                default:
                    Console.WriteLine($"  -> Command: {command:X2}, data={data1:X2} {data2:X2}");
                    break;
            }
        }
    }
}

static async Task SendRtpMidiTest()
{
    Console.WriteLine("\n--- RTP-MIDI 客户端测试 ---");
    Console.Write("目标 IP: ");
    var ipStr = Console.ReadLine() ?? "127.0.0.1";
    Console.Write("控制端口 (默认 5004): ");
    var portStr = Console.ReadLine();
    int port = string.IsNullOrEmpty(portStr) ? 5004 : int.Parse(portStr);

    try
    {
        var ip = IPAddress.Parse(ipStr);
        var ep = new IPEndPoint(ip, port);

        using var client = new UdpClient();

        // 发送 IN 邀请
        var invitation = CreateInvitation("TestClient");
        client.Send(invitation, invitation.Length, ep);
        Console.WriteLine($"发送 IN 邀请到 {ip}:{port}");

        // 接收响应
        client.Client.ReceiveTimeout = 5000;
        try
        {
            var response = await client.ReceiveAsync();
            var cmd = Encoding.ASCII.GetString(response.Buffer, 2, 2);
            Console.WriteLine($"收到响应: {cmd}");

            if (cmd == "OK")
            {
                Console.WriteLine("连接成功!");

                // 发送 MIDI 数据
                Console.WriteLine("\n发送 MIDI 测试数据...");

                // 发送 Note On C4
                var noteOnPacket = CreateMidiPacket(0x90, 60, 100);
                client.Send(noteOnPacket, noteOnPacket.Length, new IPEndPoint(ip, port + 1));
                Console.WriteLine("发送 Note On C4");

                await Task.Delay(500);

                // 发送 Note Off C4
                var noteOffPacket = CreateMidiPacket(0x80, 60, 0);
                client.Send(noteOffPacket, noteOffPacket.Length, new IPEndPoint(ip, port + 1));
                Console.WriteLine("发送 Note Off C4");
            }
        }
        catch (SocketException)
        {
            Console.WriteLine("未收到响应 (超时)");
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"错误: {ex.Message}");
    }
}

static byte[] CreateInvitation(string name)
{
    var nameBytes = Encoding.ASCII.GetBytes(name);
    var packet = new byte[16 + nameBytes.Length];

    packet[0] = 0xFF;
    packet[1] = 0xFF;
    packet[2] = (byte)'I';
    packet[3] = (byte)'N';

    packet[4] = 0;
    packet[5] = 0;
    packet[6] = 0;
    packet[7] = 2;

    var ssrc = Random.Shared.Next();
    packet[8] = (byte)((ssrc >> 24) & 0xFF);
    packet[9] = (byte)((ssrc >> 16) & 0xFF);
    packet[10] = (byte)((ssrc >> 8) & 0xFF);
    packet[11] = (byte)(ssrc & 0xFF);

    packet[12] = (byte)((nameBytes.Length >> 8) & 0xFF);
    packet[13] = (byte)(nameBytes.Length & 0xFF);
    packet[14] = 0;
    packet[15] = 0;

    Buffer.BlockCopy(nameBytes, 0, packet, 16, nameBytes.Length);

    return packet;
}

static byte[] CreateByePacket()
{
    var packet = new byte[16];

    packet[0] = 0xFF;
    packet[1] = 0xFF;
    packet[2] = (byte)'B';
    packet[3] = (byte)'Y';

    packet[4] = 0;
    packet[5] = 0;
    packet[6] = 0;
    packet[7] = 2;

    var ssrc = Random.Shared.Next();
    packet[8] = (byte)((ssrc >> 24) & 0xFF);
    packet[9] = (byte)((ssrc >> 16) & 0xFF);
    packet[10] = (byte)((ssrc >> 8) & 0xFF);
    packet[11] = (byte)(ssrc & 0xFF);

    packet[12] = 0;
    packet[13] = 0;
    packet[14] = 0;
    packet[15] = 0;

    return packet;
}

static byte[] CreateMidiPacket(params byte[] midiData)
{
    // RTP-MIDI 数据包格式
    // RTP Header (12 bytes) + MIDI Section
    var packet = new byte[12 + 1 + midiData.Length];

    // RTP Header
    packet[0] = 0x80; // V=2, P=0, X=0, CC=0
    packet[1] = 0x61; // M=1, PT=97 (RTP-MIDI)
    packet[2] = 0;
    packet[3] = 0;

    var ssrc = Random.Shared.Next();
    packet[4] = (byte)((ssrc >> 24) & 0xFF);
    packet[5] = (byte)((ssrc >> 16) & 0xFF);
    packet[6] = (byte)((ssrc >> 8) & 0xFF);
    packet[7] = (byte)(ssrc & 0xFF);

    // Sequence number and timestamp (simplified)
    var seq = (ushort)Random.Shared.Next();
    packet[8] = (byte)((seq >> 8) & 0xFF);
    packet[9] = (byte)(seq & 0xFF);

    var timestamp = (uint)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    packet[10] = (byte)((timestamp >> 16) & 0xFF);
    packet[11] = (byte)((timestamp >> 8) & 0xFF);

    // MIDI Section
    packet[12] = (byte)(midiData.Length & 0x0F); // B-flag=0, length

    Buffer.BlockCopy(midiData, 0, packet, 13, midiData.Length);

    return packet;
}

static byte[] CreateUmpMidi2Note(byte group, byte status, byte note, ushort velocity)
{
    var data = new byte[8];

    int mt = 0x4;
    data[0] = (byte)((mt << 4) | (group & 0x0F));
    data[1] = status;
    data[2] = note;
    data[3] = 0;

    data[4] = 0;
    data[5] = (byte)((velocity >> 8) & 0xFF);
    data[6] = (byte)(velocity & 0xFF);
    data[7] = 0;

    return data;
}

static async Task RtpMidiPiano()
{
    Console.WriteLine("\n--- RTP-MIDI 钢琴键盘 ---");
    Console.WriteLine("使用电脑键盘演奏，发送音符到 MidiBridge");
    Console.Write("目标 IP (默认 127.0.0.1): ");
    var ipStr = Console.ReadLine();
    if (string.IsNullOrEmpty(ipStr)) ipStr = "127.0.0.1";
    
    Console.Write("控制端口 (默认 5004): ");
    var portStr = Console.ReadLine();
    int controlPort = string.IsNullOrEmpty(portStr) ? 5004 : int.Parse(portStr);
    int dataPort = controlPort + 1;

    try
    {
        var ip = IPAddress.Parse(ipStr);
        var controlEp = new IPEndPoint(ip, controlPort);
        var dataEp = new IPEndPoint(ip, dataPort);

        using var client = new UdpClient();
        
        // 发送 IN 邀请
        Console.WriteLine($"\n连接到 {ip}:{controlPort}...");
        var invitation = CreateInvitation("TestPiano");
        client.Send(invitation, invitation.Length, controlEp);

        // 接收响应
        client.Client.ReceiveTimeout = 3000;
        try
        {
            var response = await client.ReceiveAsync();
            var cmd = Encoding.ASCII.GetString(response.Buffer, 2, 2);
            if (cmd == "OK")
            {
                Console.WriteLine("连接成功!\n");
            }
            else
            {
                Console.WriteLine($"收到响应: {cmd}");
            }
        }
        catch (SocketException)
        {
            Console.WriteLine("未收到响应，继续发送数据...\n");
        }

        Console.WriteLine("键盘映射:");
        Console.WriteLine("  白键: A S D F G H J K L (C4-C5)");
        Console.WriteLine("  黑键: W E   T Y U      (C#4-F#4)");
        Console.WriteLine("  +/- : 升/降八度");
        Console.WriteLine("  ESC : 退出");
        Console.WriteLine("  (长按持续发音，抬起停止)");
        Console.WriteLine("\n开始演奏...\n");

        int octave = 4;
        
        var keyNoteMap = new Dictionary<ConsoleKey, int>
        {
            { ConsoleKey.A, 0 },   // C
            { ConsoleKey.W, 1 },   // C#
            { ConsoleKey.S, 2 },   // D
            { ConsoleKey.E, 3 },   // D#
            { ConsoleKey.D, 4 },   // E
            { ConsoleKey.F, 5 },   // F
            { ConsoleKey.T, 6 },   // F#
            { ConsoleKey.G, 7 },   // G
            { ConsoleKey.Y, 8 },   // G#
            { ConsoleKey.H, 9 },   // A
            { ConsoleKey.U, 10 },  // A#
            { ConsoleKey.J, 11 },  // B
            { ConsoleKey.K, 12 },  // C
            { ConsoleKey.O, 13 },  // C#
            { ConsoleKey.L, 14 },  // D
            { ConsoleKey.P, 15 },  // D#
        };

        var noteNames = new[] { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };
        var pressedKeys = new HashSet<ConsoleKey>();

        while (true)
        {
            // 检查 ESC
            if (IsKeyDown(ConsoleKey.Escape))
            {
                // 停止所有音符
                foreach (var key in pressedKeys)
                {
                    if (keyNoteMap.TryGetValue(key, out int offset))
                    {
                        int note = (octave * 12) + offset + 12;
                        var noteOffPacket = CreateMidiPacket(0x80, (byte)note, 0);
                        client.Send(noteOffPacket, noteOffPacket.Length, dataEp);
                    }
                }
                
                // 发送 BY 断开连接命令
                var byePacket = CreateByePacket();
                client.Send(byePacket, byePacket.Length, controlEp);
                Console.WriteLine("\n发送断开连接信号 (BY)");
                
                Console.WriteLine("退出钢琴模式");
                break;
            }

            // 检查八度切换
            if (Console.KeyAvailable)
            {
                var keyInfo = Console.ReadKey(true);
                if (keyInfo.Key == ConsoleKey.OemPlus || keyInfo.Key == ConsoleKey.Add)
                {
                    octave = Math.Min(8, octave + 1);
                    Console.WriteLine($"八度: {octave}");
                }
                else if (keyInfo.Key == ConsoleKey.OemMinus || keyInfo.Key == ConsoleKey.Subtract)
                {
                    octave = Math.Max(1, octave - 1);
                    Console.WriteLine($"八度: {octave}");
                }
            }

            // 检查每个音符键
            foreach (var kvp in keyNoteMap)
            {
                var key = kvp.Key;
                int offset = kvp.Value;
                bool isDown = IsKeyDown(key);
                bool wasPressed = pressedKeys.Contains(key);

                if (isDown && !wasPressed)
                {
                    // 按下新键
                    pressedKeys.Add(key);
                    int note = (octave * 12) + offset + 12;
                    var noteOnPacket = CreateMidiPacket(0x90, (byte)note, 100);
                    client.Send(noteOnPacket, noteOnPacket.Length, dataEp);
                    
                    int displayOctave = (note / 12) - 1;
                    string noteName = noteNames[note % 12];
                    Console.WriteLine($"[ON]  {noteName}{displayOctave}");
                }
                else if (!isDown && wasPressed)
                {
                    // 抬起键
                    pressedKeys.Remove(key);
                    int note = (octave * 12) + offset + 12;
                    var noteOffPacket = CreateMidiPacket(0x80, (byte)note, 0);
                    client.Send(noteOffPacket, noteOffPacket.Length, dataEp);
                    
                    int displayOctave = (note / 12) - 1;
                    string noteName = noteNames[note % 12];
                    Console.WriteLine($"[OFF] {noteName}{displayOctave}");
                }
            }

await Task.Delay(10);
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"错误: {ex.Message}");
    }
}

static async Task TestNM2FullSession()
{
    Console.WriteLine("\n=== Network MIDI 2.0 完整测试 ===");
    Console.WriteLine("这个测试将演示完整的 Network MIDI 2.0 协议流程:");
    Console.WriteLine("  1. mDNS 发现服务");
    Console.WriteLine("  2. INV 会话建立");
    Console.WriteLine("  3. UMP 数据传输");
    Console.WriteLine("  4. PING 心跳");
    Console.WriteLine("  5. END 会话结束\n");

    Console.WriteLine("请确保 MidiBridge 主程序已启动网络服务 (端口 5506)");
    Console.WriteLine("按任意键开始测试...");
    Console.ReadKey();

    using var client = new NetworkMidi2Client();
    
    client.OnMidiReceived += midiData =>
    {
        Console.WriteLine($"  [MIDI收到] {BitConverter.ToString(midiData)}");
    };
    
    // Step 1: Discovery
    Console.WriteLine("\n[Step 1] 开始 mDNS 发现...");
    client.StartDiscovery();
    await Task.Delay(500);
    client.SendDiscoveryQuery();
    
    Console.WriteLine("等待 3 秒监听 mDNS 响应...");
    await Task.Delay(3000);
    
    // Step 2: Connect
    Console.WriteLine("\n[Step 2] 连接到 MidiBridge...");
    Console.Write("目标 IP (默认 127.0.0.1): ");
    var ipStr = Console.ReadLine();
    if (string.IsNullOrEmpty(ipStr)) ipStr = "127.0.0.1";
    
    Console.Write("端口 (默认 5506): ");
    var portStr = Console.ReadLine();
    int port = string.IsNullOrEmpty(portStr) ? 5506 : int.Parse(portStr);
    
    var connected = await client.ConnectAsync(ipStr, port, "TestClient");
    
    if (!connected)
    {
        Console.WriteLine("连接失败，请检查 MidiBridge 是否已启动服务");
        return;
    }
    
    // Step 3: Send MIDI Data
    Console.WriteLine("\n[Step 3] 发送 MIDI 数据...");
    Console.WriteLine("发送 Note On C4 (velocity=100)");
    client.SendNoteOn(60, 100);
    
    await Task.Delay(500);
    
    Console.WriteLine("发送 Note Off C4");
    client.SendNoteOff(60);
    
    await Task.Delay(500);
    
    Console.WriteLine("发送 Note On E4 (velocity=80)");
    client.SendNoteOn(64, 80);
    
    await Task.Delay(300);
    
    Console.WriteLine("发送 Note Off E4");
    client.SendNoteOff(64);
    
    // Step 4: Ping
    Console.WriteLine("\n[Step 4] 发送 PING 心跳...");
    client.SendPing();
    
    await Task.Delay(1000);
    
    // Interactive piano mode
    Console.WriteLine("\n[Step 5] 进入交互钢琴模式 (ESC 退出)...");
    Console.WriteLine("白键: A S D F G H J K L (C4-C5)");
    Console.WriteLine("黑键: W E   T Y U      (C#4-F#4)");
    Console.WriteLine("+/-: 升/降八度");
    Console.WriteLine("P: 发送 PING");
    
    int octave = 4;
    var keyNoteMap = new Dictionary<ConsoleKey, int>
    {
        { ConsoleKey.A, 0 }, { ConsoleKey.W, 1 }, { ConsoleKey.S, 2 }, { ConsoleKey.E, 3 },
        { ConsoleKey.D, 4 }, { ConsoleKey.F, 5 }, { ConsoleKey.T, 6 }, { ConsoleKey.G, 7 },
        { ConsoleKey.Y, 8 }, { ConsoleKey.H, 9 }, { ConsoleKey.U, 10 }, { ConsoleKey.J, 11 },
        { ConsoleKey.K, 12 }, { ConsoleKey.O, 13 }, { ConsoleKey.L, 14 }, { ConsoleKey.P, 15 },
    };
    var noteNames = new[] { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };
    var pressedKeys = new HashSet<ConsoleKey>();
    
    while (true)
    {
        if (IsKeyDown(ConsoleKey.Escape))
        {
            foreach (var key in pressedKeys)
            {
                if (keyNoteMap.TryGetValue(key, out int offset))
                {
                    int note = (octave * 12) + offset + 12;
                    client.SendNoteOff((byte)note);
                }
            }
            Console.WriteLine("\n退出钢琴模式");
            break;
        }
        
        if (Console.KeyAvailable)
        {
            var keyInfo = Console.ReadKey(true);
            if (keyInfo.Key == ConsoleKey.OemPlus || keyInfo.Key == ConsoleKey.Add)
            {
                octave = Math.Min(8, octave + 1);
                Console.WriteLine($"八度: {octave}");
            }
            else if (keyInfo.Key == ConsoleKey.OemMinus || keyInfo.Key == ConsoleKey.Subtract)
            {
                octave = Math.Max(1, octave - 1);
                Console.WriteLine($"八度: {octave}");
            }
        }
        
        foreach (var kvp in keyNoteMap)
        {
            var key = kvp.Key;
            int offset = kvp.Value;
            bool isDown = IsKeyDown(key);
            bool wasPressed = pressedKeys.Contains(key);
            
            if (isDown && !wasPressed)
            {
                pressedKeys.Add(key);
                int note = (octave * 12) + offset + 12;
                client.SendNoteOn((byte)note, 100);
                Console.WriteLine($"[ON]  {noteNames[note % 12]}{(note / 12) - 1}");
            }
            else if (!isDown && wasPressed)
            {
                pressedKeys.Remove(key);
                int note = (octave * 12) + offset + 12;
                client.SendNoteOff((byte)note);
                Console.WriteLine($"[OFF] {noteNames[note % 12]}{(note / 12) - 1}");
            }
        }
        
        await Task.Delay(10);
    }
    
    // Step 5: Disconnect
    Console.WriteLine("\n[Step 6] 断开会话...");
    client.Disconnect();
    
    Console.WriteLine("\n测试完成! 按任意键返回主菜单...");
    Console.ReadKey();
}

static async Task NM2PianoStandalone()
{
    Console.WriteLine("\n--- Network MIDI 2.0 钢琴键盘 ---");
    
    Console.Write("目标 IP (默认 127.0.0.1): ");
    var ipStr = Console.ReadLine();
    if (string.IsNullOrEmpty(ipStr)) ipStr = "127.0.0.1";
    
    Console.Write("端口 (默认 5506): ");
    var portStr = Console.ReadLine();
    int port = string.IsNullOrEmpty(portStr) ? 5506 : int.Parse(portStr);
    
    using var client = new NetworkMidi2Client();
    
    Console.WriteLine($"\n连接到 {ipStr}:{port}...");
    var connected = await client.ConnectAsync(ipStr, port, "PianoClient");
    
    if (!connected)
    {
        Console.WriteLine("连接失败");
        return;
    }
    
    Console.WriteLine("\n键盘映射:");
    Console.WriteLine("  白键: A S D F G H J K L (C4-C5)");
    Console.WriteLine("  黑键: W E   T Y U      (C#4-F#4)");
    Console.WriteLine("  +/- : 升/降八度");
    Console.WriteLine("  ESC : 退出");
    Console.WriteLine("\n开始演奏...\n");
    
    int octave = 4;
    var keyNoteMap = new Dictionary<ConsoleKey, int>
    {
        { ConsoleKey.A, 0 }, { ConsoleKey.W, 1 }, { ConsoleKey.S, 2 }, { ConsoleKey.E, 3 },
        { ConsoleKey.D, 4 }, { ConsoleKey.F, 5 }, { ConsoleKey.T, 6 }, { ConsoleKey.G, 7 },
        { ConsoleKey.Y, 8 }, { ConsoleKey.H, 9 }, { ConsoleKey.U, 10 }, { ConsoleKey.J, 11 },
        { ConsoleKey.K, 12 }, { ConsoleKey.O, 13 }, { ConsoleKey.L, 14 }, { ConsoleKey.P, 15 },
    };
    var noteNames = new[] { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };
    var pressedKeys = new HashSet<ConsoleKey>();
    
    while (true)
    {
        if (IsKeyDown(ConsoleKey.Escape))
        {
            foreach (var key in pressedKeys)
            {
                if (keyNoteMap.TryGetValue(key, out int offset))
                {
                    int note = (octave * 12) + offset + 12;
                    client.SendNoteOff((byte)note);
                }
            }
            break;
        }
        
        if (Console.KeyAvailable)
        {
            var keyInfo = Console.ReadKey(true);
            if (keyInfo.Key == ConsoleKey.OemPlus || keyInfo.Key == ConsoleKey.Add)
            {
                octave = Math.Min(8, octave + 1);
                Console.WriteLine($"八度: {octave}");
            }
            else if (keyInfo.Key == ConsoleKey.OemMinus || keyInfo.Key == ConsoleKey.Subtract)
            {
                octave = Math.Max(1, octave - 1);
                Console.WriteLine($"八度: {octave}");
            }
        }
        
        foreach (var kvp in keyNoteMap)
        {
            var key = kvp.Key;
            int offset = kvp.Value;
            bool isDown = IsKeyDown(key);
            bool wasPressed = pressedKeys.Contains(key);
            
            if (isDown && !wasPressed)
            {
                pressedKeys.Add(key);
                int note = (octave * 12) + offset + 12;
                client.SendNoteOn((byte)note, 100);
                Console.WriteLine($"[ON]  {noteNames[note % 12]}{(note / 12) - 1}");
            }
            else if (!isDown && wasPressed)
            {
                pressedKeys.Remove(key);
                int note = (octave * 12) + offset + 12;
                client.SendNoteOff((byte)note);
                Console.WriteLine($"[OFF] {noteNames[note % 12]}{(note / 12) - 1}");
            }
        }
        
        await Task.Delay(10);
    }
    
client.Disconnect();
    Console.WriteLine("已断开连接");
}

static async Task PortOccupancyTest()
{
    Console.WriteLine("\n--- 端口占用测试 ---");
    Console.WriteLine("此测试将占用指定的端口，用于测试 MidiBridge 的端口检测功能");
    Console.WriteLine("占用后，MidiBridge 对应的端口输入框应该会显示红色闪烁边框");
    Console.WriteLine();
    
    Console.Write("要占用的 RTP 端口 (默认 5004，输入 0 跳过): ");
    var rtpStr = Console.ReadLine();
    int rtpPort = string.IsNullOrEmpty(rtpStr) ? 5004 : int.Parse(rtpStr);
    
    Console.Write("要占用的 NM2 端口 (默认 5506，输入 0 跳过): ");
    var nm2Str = Console.ReadLine();
    int nm2Port = string.IsNullOrEmpty(nm2Str) ? 5506 : int.Parse(nm2Str);
    
    var listeners = new List<TcpListener>();
    
    try
    {
        if (rtpPort > 0)
        {
            var rtpListener = new TcpListener(IPAddress.Any, rtpPort);
            rtpListener.Start();
            listeners.Add(rtpListener);
            Console.WriteLine($"✓ RTP 端口 {rtpPort}-{rtpPort + 1} 已占用");
        }
        
        if (nm2Port > 0)
        {
            var nm2Listener = new TcpListener(IPAddress.Any, nm2Port);
            nm2Listener.Start();
            listeners.Add(nm2Listener);
            Console.WriteLine($"✓ NM2 端口 {nm2Port} 已占用");
        }
        
        Console.WriteLine("\n端口已占用，现在可以在 MidiBridge 中测试：");
        Console.WriteLine("  1. 修改对应端口输入框的值");
        Console.WriteLine("  2. 输入框边框应该会红色闪烁");
        Console.WriteLine("  3. 鼠标悬停应显示错误信息");
        Console.WriteLine("\n按任意键释放端口...");
        Console.ReadKey(true);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"错误: {ex.Message}");
    }
    finally
    {
        foreach (var listener in listeners)
        {
            try
            {
                listener.Stop();
            }
            catch { }
        }
        Console.WriteLine("端口已释放");
    }
}

static async Task StartNM2DiscoverableServer()
{
    Console.WriteLine("\n=== NM2 多设备发现测试 ===");
    Console.WriteLine("创建多个可通过 mDNS 发现的 Network MIDI 2.0 服务端");
    Console.WriteLine("用于测试 MidiBridge 主程序的设备发现窗口\n");
    
    var servers = new List<(string Name, int Port, NM2DiscoverableServer Server)>();
    int defaultPort = 5507;
    int deviceNum = 1;
    
    void PrintHelp()
    {
        Console.WriteLine("\n命令:");
        Console.WriteLine("  a [name]  - 添加新设备 (可选名称，默认 NM2DeviceN)");
        Console.WriteLine("  l         - 列出所有设备");
        Console.WriteLine("  r <num>   - 移除指定编号的设备");
        Console.WriteLine("  q         - 退出并关闭所有设备");
        Console.WriteLine("  h         - 显示帮助");
    }
    
    PrintHelp();
    
    while (true)
    {
        Console.Write("\n> ");
        var line = Console.ReadLine();
        if (string.IsNullOrEmpty(line)) continue;
        
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var cmd = parts[0].ToLower();
        
        switch (cmd)
        {
            case "h":
                PrintHelp();
                break;
                
            case "a":
                string name;
                if (parts.Length > 1)
                {
                    name = parts[1];
                }
                else
                {
                    name = $"NM2Device{deviceNum}";
                }
                
                int port = defaultPort;
                while (servers.Any(s => s.Port == port))
                {
                    port++;
                }
                
                var server = new NM2DiscoverableServer(name, port, $"test-{deviceNum}");
                server.OnLog += msg => Console.WriteLine($"  [{name}] {msg}");
                server.OnMidiReceived += midiData =>
                {
                    Console.WriteLine($"  [{name}] MIDI: {BitConverter.ToString(midiData)}");
                };
                
                if (server.Start())
                {
                    servers.Add((name, port, server));
                    Console.WriteLine($"✓ 已添加: {name} (端口 {port})");
                    deviceNum++;
                    defaultPort = port + 1;
                }
                else
                {
                    Console.WriteLine($"✗ 添加失败: {name}");
                    server.Dispose();
                }
                break;
                
            case "l":
                if (servers.Count == 0)
                {
                    Console.WriteLine("(无设备)");
                }
                else
                {
                    Console.WriteLine($"共 {servers.Count} 个设备:");
                    for (int i = 0; i < servers.Count; i++)
                    {
                        var (n, p, s) = servers[i];
                        string status = s.IsConnected ? "已连接" : "等待连接";
                        Console.WriteLine($"  [{i + 1}] {n} :{p} ({status})");
                    }
                }
                break;
                
            case "r":
                if (parts.Length < 2 || !int.TryParse(parts[1], out int num) || num < 1 || num > servers.Count)
                {
                    Console.WriteLine("用法: r <编号> (用 l 命令查看编号)");
                }
                else
                {
                    var (n, p, s) = servers[num - 1];
                    s.Stop();
                    s.Dispose();
                    servers.RemoveAt(num - 1);
                    Console.WriteLine($"✓ 已移除: {n}");
                }
                break;
                
            case "q":
                foreach (var (n, p, s) in servers)
                {
                    s.Stop();
                    s.Dispose();
                }
                servers.Clear();
                Console.WriteLine("所有设备已关闭");
                return;
        }
    }
}

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
            
            OnLog?.Invoke($"[服务端] 已启动，端口 {_port}");
            OnLog?.Invoke($"[服务端] SSRC: 0x{_localSSRC:X8}");
            return true;
        }
        catch (Exception ex)
        {
            OnLog?.Invoke($"[服务端] 启动失败: {ex.Message}");
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
            OnLog?.Invoke("[服务端] 发送 BYE");
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
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { }
            catch (Exception ex)
            {
                if (!ct.IsCancellationRequested)
                    OnLog?.Invoke($"[服务端] 接收错误: {ex.Message}");
            }
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
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { }
            catch { }
        }
    }
    
    private void ProcessMdnsPacket(byte[] data, IPEndPoint remoteEP)
    {
        if (data.Length < 12) return;
        
        ushort flags = (ushort)((data[2] << 8) | data[3]);
        bool isResponse = (flags & 0x8000) != 0;
        
        if (isResponse) return;
        
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
            {
                SendMdnsResponse(remoteEP);
            }
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
        
        ushort srvLen = 7;
        WriteMdnsName(writer, serviceName);
        writer.Write((byte)0x00);
        writer.Write((byte)0x21);
        writer.Write((byte)0x00);
        writer.Write((byte)0x01);
        writer.Write((byte)((ttl >> 24) & 0xFF));
        writer.Write((byte)((ttl >> 16) & 0xFF));
        writer.Write((byte)((ttl >> 8) & 0xFF));
        writer.Write((byte)(ttl & 0xFF));
        writer.Write((byte)((srvLen >> 8) & 0xFF));
        writer.Write((byte)(srvLen & 0xFF));
        writer.Write((byte)0x00);
        writer.Write((byte)0x00);
        writer.Write((byte)0x00);
        writer.Write((byte)0x00);
        writer.Write((byte)((_port >> 8) & 0xFF));
        writer.Write((byte)(_port & 0xFF));
        writer.Write((byte)0x00);
        
        WriteMdnsName(writer, serviceName);
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
        
        WriteMdnsName(writer, serviceName);
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
        
        var response = ms.ToArray();
        _mdnsClient?.Send(response, response.Length, new IPEndPoint(IPAddress.Parse(MDNS_MULTICAST_ADDRESS), MDNS_PORT));
        OnLog?.Invoke($"[mDNS] 发送响应");
    }
    
    private async void AnnounceLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var announcement = CreateMdnsAnnouncement();
                _mdnsClient?.Send(announcement, announcement.Length, new IPEndPoint(IPAddress.Parse(MDNS_MULTICAST_ADDRESS), MDNS_PORT));
                OnLog?.Invoke("[mDNS] 发送公告");
                await Task.Delay(TimeSpan.FromMinutes(1), ct);
            }
            catch (OperationCanceledException) { break; }
            catch { }
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
        
        ushort srvLen = 7;
        WriteMdnsName(writer, serviceName);
        writer.Write((byte)0x00);
        writer.Write((byte)0x21);
        writer.Write((byte)0x00);
        writer.Write((byte)0x01);
        writer.Write((byte)((ttl >> 24) & 0xFF));
        writer.Write((byte)((ttl >> 16) & 0xFF));
        writer.Write((byte)((ttl >> 8) & 0xFF));
        writer.Write((byte)(ttl & 0xFF));
        writer.Write((byte)((srvLen >> 8) & 0xFF));
        writer.Write((byte)(srvLen & 0xFF));
        writer.Write((byte)0x00);
        writer.Write((byte)0x00);
        writer.Write((byte)0x00);
        writer.Write((byte)0x00);
        writer.Write((byte)((_port >> 8) & 0xFF));
        writer.Write((byte)(_port & 0xFF));
        writer.Write((byte)0x00);
        
        WriteMdnsName(writer, serviceName);
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
        
        WriteMdnsName(writer, serviceName);
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
    
    private void WriteMdnsName(BinaryWriter writer, string name)
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
    
    private string ReadMdnsName(byte[] data, ref int offset)
    {
        var sb = new StringBuilder();
        while (offset < data.Length)
        {
            byte len = data[offset++];
            if (len == 0) break;
            if ((len & 0xC0) == 0xC0)
            {
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
    
    private void ProcessDataPacket(byte[] data, IPEndPoint remoteEP)
    {
        if (!NetworkMidi2Protocol.ParseUDPPacket(data, out var commandPackets))
        {
            OnLog?.Invoke($"[服务端] 无效的数据包签名");
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
                OnLog?.Invoke($"[服务端] 未知命令: 0x{((byte)cmdCode):X2}");
                break;
        }
    }
    
    private void ProcessInvitation(byte[] payload, byte nameWords, IPEndPoint remoteEP)
    {
        if (!NetworkMidi2Protocol.ParseInvitationCommand(payload, nameWords, out var umpEndpointName, out var productInstanceId, out var capabilities))
        {
            SendNAK(NetworkMidi2Protocol.NAKReason.CommandMalformed, new byte[4], remoteEP);
            return;
        }
        
        _remoteEP = remoteEP;
        _remoteSSRC = (uint)Random.Shared.Next(1, int.MaxValue);
        
        var replyCmd = NetworkMidi2Protocol.CreateInvitationReplyAccepted(_serviceName, _productId);
        var packet = NetworkMidi2Protocol.CreateUDPPacket(replyCmd);
        _dataServer?.Send(packet, packet.Length, remoteEP);
        
        OnLog?.Invoke($"[服务端] INV from {remoteEP} (name: {umpEndpointName}, product: {productInstanceId})");
        OnLog?.Invoke($"[服务端] 已接受连接! SSRC: 0x{_localSSRC:X8}");
    }
    
    private void ProcessInvitationWithAuth(byte[] payload, IPEndPoint remoteEP)
    {
        if (!NetworkMidi2Protocol.ParseInvitationWithAuth(payload, out var authDigest))
        {
            SendNAK(NetworkMidi2Protocol.NAKReason.CommandMalformed, new byte[4], remoteEP);
            return;
        }
        
        OnLog?.Invoke($"[服务端] INV_WITH_AUTH from {remoteEP}");
        
        _remoteEP = remoteEP;
        _remoteSSRC = (uint)Random.Shared.Next(1, int.MaxValue);
        
        var replyCmd = NetworkMidi2Protocol.CreateInvitationReplyAccepted(_serviceName, _productId);
        var packet = NetworkMidi2Protocol.CreateUDPPacket(replyCmd);
        _dataServer?.Send(packet, packet.Length, remoteEP);
        
        OnLog?.Invoke($"[服务端] 已接受认证连接!");
    }
    
    private void ProcessInvitationWithUserAuth(byte[] payload, IPEndPoint remoteEP)
    {
        if (!NetworkMidi2Protocol.ParseInvitationWithUserAuth(payload, out var authDigest, out var username))
        {
            SendNAK(NetworkMidi2Protocol.NAKReason.CommandMalformed, new byte[4], remoteEP);
            return;
        }
        
        OnLog?.Invoke($"[服务端] INV_WITH_USER_AUTH from {remoteEP}, user: {username}");
        
        _remoteEP = remoteEP;
        _remoteSSRC = (uint)Random.Shared.Next(1, int.MaxValue);
        
        var replyCmd = NetworkMidi2Protocol.CreateInvitationReplyAccepted(_serviceName, _productId);
        var packet = NetworkMidi2Protocol.CreateUDPPacket(replyCmd);
        _dataServer?.Send(packet, packet.Length, remoteEP);
        
        OnLog?.Invoke($"[服务端] 已接受用户认证连接!");
    }
    
    private void ProcessPing(byte[] payload, IPEndPoint remoteEP)
    {
        if (!NetworkMidi2Protocol.ParsePingCommand(payload, out var pingId)) return;
        
        var replyCmd = NetworkMidi2Protocol.CreatePingReplyCommand(pingId);
        var packet = NetworkMidi2Protocol.CreateUDPPacket(replyCmd);
        _dataServer?.Send(packet, packet.Length, remoteEP);
        
        OnLog?.Invoke($"[服务端] PING id={pingId} -> PONG");
    }
    
    private void ProcessBye(byte[] payload, IPEndPoint remoteEP)
    {
        NetworkMidi2Protocol.ParseByeCommand(payload, out var reason, out var textMessage);
        
        var replyCmd = NetworkMidi2Protocol.CreateByeReplyCommand();
        var packet = NetworkMidi2Protocol.CreateUDPPacket(replyCmd);
        _dataServer?.Send(packet, packet.Length, remoteEP);
        
        OnLog?.Invoke($"[服务端] 收到 BYE，原因: {reason}");
        
        _remoteEP = null;
        _remoteSSRC = 0;
    }
    
    private void ProcessSessionReset(IPEndPoint remoteEP)
    {
        var replyCmd = NetworkMidi2Protocol.CreateSessionResetReplyCommand();
        var packet = NetworkMidi2Protocol.CreateUDPPacket(replyCmd);
        _dataServer?.Send(packet, packet.Length, remoteEP);
        
        _sendSequence = 0;
        OnLog?.Invoke("[服务端] 收到 SESSION_RESET，已重置序列号");
    }
    
    private void ProcessRetransmitRequest(byte[] payload)
    {
        NetworkMidi2Protocol.ParseRetransmitRequest(payload, out var seqNum, out var numCommands);
        OnLog?.Invoke($"[服务端] RETRANSMIT_REQUEST seq={seqNum}, count={numCommands}");
        
        var errorCmd = NetworkMidi2Protocol.CreateRetransmitErrorCommand(NetworkMidi2Protocol.RetransmitErrorReason.BufferDoesNotContainSequence, seqNum);
        var packet = NetworkMidi2Protocol.CreateUDPPacket(errorCmd);
        _dataServer?.Send(packet, packet.Length, _remoteEP);
    }
    
    private void ProcessUMPData(ushort sequenceNumber, byte[] umpData)
    {
        OnLog?.Invoke($"[服务端] UMP seq={sequenceNumber}, {umpData.Length} bytes");
        
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
                byte vel7 = (byte)(velocity >> 9);
                byte[] midiData = new byte[] { status, note, vel7 };
                OnMidiReceived?.Invoke(midiData);
                ReceivedMidiCount++;
                OnLog?.Invoke($"[服务端] MIDI2 -> {BitConverter.ToString(midiData)} (vel16={velocity})");
            }
            
            offset += packetSize;
        }
    }
    
    private void SendNAK(NetworkMidi2Protocol.NAKReason reason, byte[] originalHeader, IPEndPoint remoteEP)
    {
        var nakCmd = NetworkMidi2Protocol.CreateNAKCommand(reason, originalHeader);
        var packet = NetworkMidi2Protocol.CreateUDPPacket(nakCmd);
        _dataServer?.Send(packet, packet.Length, remoteEP);
        OnLog?.Invoke($"[服务端] 发送 NAK, 原因: {reason}");
    }
    
    public void SendNoteOn(byte note, byte velocity, byte channel = 0)
    {
        if (_remoteEP == null || _dataServer == null) return;
        
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
        OnLog?.Invoke($"[服务端] 发送 Note On {note}");
    }
    
    public void SendNoteOff(byte note, byte channel = 0)
    {
        if (_remoteEP == null || _dataServer == null) return;
        
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
        OnLog?.Invoke($"[服务端] 发送 Note Off {note}");
    }
    
    public void SendPing()
    {
        if (_remoteEP == null || _dataServer == null)
        {
            OnLog?.Invoke("[服务端] 未连接，无法发送 PING");
            return;
        }
        
        var pingId = (uint)Random.Shared.Next();
        var pingCmd = NetworkMidi2Protocol.CreatePingCommand(pingId);
        var packet = NetworkMidi2Protocol.CreateUDPPacket(pingCmd);
        _dataServer.Send(packet, packet.Length, _remoteEP);
        OnLog?.Invoke($"[服务端] 发送 PING id={pingId}");
    }
    
    private void SendUMPData(byte[] umpData)
    {
        if (_remoteEP == null || _dataServer == null) return;
        
        var umpCmd = NetworkMidi2Protocol.CreateUMPDataCommand(_sendSequence++, umpData);
        var packet = NetworkMidi2Protocol.CreateUDPPacket(umpCmd);
        _dataServer.Send(packet, packet.Length, _remoteEP);
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
    
public void Dispose()
    {
        Stop();
        try { _mdnsClient?.DropMulticastGroup(IPAddress.Parse(MDNS_MULTICAST_ADDRESS)); } catch { }
        _dataServer?.Close();
        _mdnsClient?.Close();
        _cts?.Dispose();
    }
}