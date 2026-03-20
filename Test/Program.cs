using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using MidiBridge.Services.NetworkMidi2;
using NAudio.Midi;
using Test;

Console.WriteLine("=== MIDI 测试工具 ===\n");

[DllImport("user32.dll")]
static extern short GetAsyncKeyState(int vKey);

const int VK_SPACE = 0x20;

static bool IsKeyButtonDown(int vKey) => (GetAsyncKeyState(vKey) & 0x8000) != 0;

while (true)
{
    Console.WriteLine("\n选择测试:");
    Console.WriteLine("1. 列出本地 MIDI 设备");
    Console.WriteLine("2. 测试 MIDI 输入监听");
    Console.WriteLine("3. 测试 MIDI 输出");
    Console.WriteLine("4. RTP-MIDI 服务端测试");
    Console.WriteLine("5. RTP-MIDI 钢琴键盘");
    Console.WriteLine("6. Network MIDI 2.0 测试 (含钢琴)");
    Console.WriteLine("7. NM2 多设备发现测试");
    Console.WriteLine("8. 端口占用测试");
    Console.WriteLine("0. 退出");
    Console.Write("\n请选择: ");

    switch (Console.ReadLine())
    {
        case "1": ListMidiDevices(); break;
        case "2": await TestMidiInput(); break;
        case "3": TestMidiOutput(); break;
        case "4": await RtpMidiServerTest(); break;
        case "5": await RtpMidiPiano(); break;
        case "6": await NM2Test(); break;
        case "7": await StartNM2DiscoverableServer(); break;
        case "8": await PortOccupancyTest(); break;
        case "0": return;
        default: Console.WriteLine("无效选择"); break;
    }
}

static void ListMidiDevices()
{
    Console.WriteLine("\n--- MIDI 输入设备 ---");
    for (int i = 0; i < MidiIn.NumberOfDevices; i++)
        Console.WriteLine($"[{i}] {MidiIn.DeviceInfo(i).ProductName}");
    if (MidiIn.NumberOfDevices == 0) Console.WriteLine("(无)");

    Console.WriteLine("\n--- MIDI 输出设备 ---");
    for (int i = 0; i < MidiOut.NumberOfDevices; i++)
        Console.WriteLine($"[{i}] {MidiOut.DeviceInfo(i).ProductName}");
    if (MidiOut.NumberOfDevices == 0) Console.WriteLine("(无)");
}

static async Task TestMidiInput()
{
    Console.WriteLine("\n--- MIDI 输入监听测试 ---");
    ListMidiDevices();

    if (MidiIn.NumberOfDevices == 0) { Console.WriteLine("没有可用的 MIDI 输入设备"); return; }

    Console.Write("\n选择输入设备编号: ");
    if (!int.TryParse(Console.ReadLine(), out int deviceId) || deviceId < 0 || deviceId >= MidiIn.NumberOfDevices)
    { Console.WriteLine("无效的设备编号"); return; }

    try
    {
        using var midiIn = new MidiIn(deviceId);
        midiIn.MessageReceived += (s, e) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {e.MidiEvent}");
        midiIn.Start();
        Console.WriteLine($"正在监听 {MidiIn.DeviceInfo(deviceId).ProductName}...\n按任意键停止");
        await Task.Run(() => Console.ReadKey(true));
        midiIn.Stop();
    }
    catch (Exception ex) { Console.WriteLine($"错误: {ex.Message}"); }
}

static void TestMidiOutput()
{
    Console.WriteLine("\n--- MIDI 输出测试 ---");
    ListMidiDevices();

    if (MidiOut.NumberOfDevices == 0) { Console.WriteLine("没有可用的 MIDI 输出设备"); return; }

    Console.Write("\n选择输出设备编号: ");
    if (!int.TryParse(Console.ReadLine(), out int deviceId) || deviceId < 0 || deviceId >= MidiOut.NumberOfDevices)
    { Console.WriteLine("无效的设备编号"); return; }

    try
    {
        using var midiOut = new MidiOut(deviceId);
        Console.WriteLine($"\n已连接到 {MidiOut.DeviceInfo(deviceId).ProductName}");
        Console.WriteLine("1. 播放 C4 音符");
        Console.WriteLine("2. 播放 C 大调音阶");
        Console.Write("选择: ");

        switch (Console.ReadLine())
        {
            case "1":
                midiOut.Send(new NoteOnEvent(0, 1, 60, 100, 500).GetAsShortMessage());
                Thread.Sleep(500);
                midiOut.Send(new NoteEvent(0, 1, MidiCommandCode.NoteOff, 60, 0).GetAsShortMessage());
                break;
            case "2":
                for (int note = 60; note <= 72; note++)
                {
                    midiOut.Send(new NoteOnEvent(0, 1, note, 100, 200).GetAsShortMessage());
                    Thread.Sleep(200);
                    midiOut.Send(new NoteEvent(0, 1, MidiCommandCode.NoteOff, note, 0).GetAsShortMessage());
                }
                break;
        }
    }
    catch (Exception ex) { Console.WriteLine($"错误: {ex.Message}"); }
}

static async Task RtpMidiServerTest()
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

static byte[] CreateOkResponse(string name)
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

static byte[] CreateCkResponse()
{
    var packet = new byte[12];
    packet[1] = 0xFF; packet[2] = (byte)'C'; packet[3] = (byte)'K';
    var ssrc = Random.Shared.Next();
    packet[4] = (byte)(ssrc >> 24); packet[5] = (byte)(ssrc >> 16);
    packet[6] = (byte)(ssrc >> 8); packet[7] = (byte)ssrc;
    return packet;
}

static void ParseMidiData(byte[] data)
{
    if (data.Length < 13) return;
    int offset = 12;
    int length = data[offset++] & 0x0F;
    if (offset + length > data.Length) return;

    Console.Write("[MIDI] ");
    for (int i = 0; i < length; i++) Console.Write($"{data[offset + i]:X2} ");
    Console.WriteLine();
}

static byte[] CreateInvitation(string name)
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

static byte[] CreateByePacket()
{
    var packet = new byte[16];
    packet[1] = 0xFF; packet[2] = (byte)'B'; packet[3] = (byte)'Y';
    packet[7] = 2;
    return packet;
}

static byte[] CreateMidiPacket(params byte[] midiData)
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

static byte[] CreateCcPacket(byte controller, byte value)
{
    return CreateMidiPacket(0xB0, controller, value);
}

static async Task RtpMidiPiano()
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

        Console.WriteLine("键盘映射:");
        Console.WriteLine("  白键: A S D F G H J K L (C4-C5)");
        Console.WriteLine("  黑键: W E   T Y U      (C#4-F#4)");
        Console.WriteLine("  空格: 延音踏板 (Sustain)");
        Console.WriteLine("  +/- : 升/降八度");
        Console.WriteLine("  ESC : 退出\n");

        RunPianoLoop(
            (note, vel) => client.Send(CreateMidiPacket((byte)(vel > 0 ? 0x90 : 0x80), (byte)note, (byte)(vel > 0 ? vel : 0)), 16, dataEp),
            on => client.Send(CreateCcPacket(64, (byte)(on ? 127 : 0)), 16, dataEp)
        );

        client.Send(CreateByePacket(), 16, controlEp);
        Console.WriteLine("已断开连接");
    }
    catch (Exception ex) { Console.WriteLine($"错误: {ex.Message}"); }
}

static async Task NM2Test()
{
    Console.WriteLine("\n=== Network MIDI 2.0 测试 ===");
    Console.WriteLine("请确保 MidiBridge 已启动网络服务 (端口 5506)");
    Console.WriteLine("按任意键开始...");
    Console.ReadKey();

    using var client = new NetworkMidi2Client();
    client.OnMidiReceived += midi => Console.WriteLine($"  [MIDI] {BitConverter.ToString(midi)}");

    Console.WriteLine("\n[Step 1] mDNS 发现...");
    client.StartDiscovery();
    await Task.Delay(500);
    client.SendDiscoveryQuery();
    await Task.Delay(2000);

    Console.WriteLine("\n[Step 2] 连接...");
    Console.Write("目标 IP (默认 127.0.0.1): ");
    var ipStr = Console.ReadLine();
    if (string.IsNullOrEmpty(ipStr)) ipStr = "127.0.0.1";

    Console.Write("端口 (默认 5506): ");
    int port = int.TryParse(Console.ReadLine(), out var p) ? p : 5506;

    if (!await client.ConnectAsync(ipStr, port, "TestClient"))
    { Console.WriteLine("连接失败"); return; }

    Console.WriteLine("\n[Step 3] 发送测试音符...");
    client.SendNoteOn(60, 100);
    await Task.Delay(500);
    client.SendNoteOff(60);

    Console.WriteLine("\n[Step 4] 钢琴模式 (ESC 退出)");
    Console.WriteLine("  白键: A S D F G H J K L (C4-C5)");
    Console.WriteLine("  黑键: W E   T Y U      (C#4-F#4)");
    Console.WriteLine("  空格: 延音踏板");
    Console.WriteLine("  +/- : 升/降八度\n");

    RunPianoLoop(
        (note, vel) => { if (vel > 0) client.SendNoteOn((byte)note, (byte)vel); else client.SendNoteOff((byte)note); },
        on => client.SendMidi(new byte[] { 0xB0, 64, (byte)(on ? 127 : 0) })
    );

    client.Disconnect();
    Console.WriteLine("\n测试完成");
}

static void RunPianoLoop(Action<int, int> sendNote, Action<bool> sendSustain)
{
    var keyNoteMap = new Dictionary<ConsoleKey, int>
    {
        { ConsoleKey.A, 0 }, { ConsoleKey.W, 1 }, { ConsoleKey.S, 2 }, { ConsoleKey.E, 3 },
        { ConsoleKey.D, 4 }, { ConsoleKey.F, 5 }, { ConsoleKey.T, 6 }, { ConsoleKey.G, 7 },
        { ConsoleKey.Y, 8 }, { ConsoleKey.H, 9 }, { ConsoleKey.U, 10 }, { ConsoleKey.J, 11 },
        { ConsoleKey.K, 12 }, { ConsoleKey.O, 13 }, { ConsoleKey.L, 14 }, { ConsoleKey.P, 15 },
    };
    var noteNames = new[] { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };
    var pressedKeys = new HashSet<ConsoleKey>();
    bool sustainPressed = false;
    int octave = 4;

    while (true)
    {
        if (IsKeyButtonDown((int)ConsoleKey.Escape))
        {
            foreach (var key in pressedKeys)
                if (keyNoteMap.TryGetValue(key, out int offset))
                    sendNote((octave * 12) + offset + 12, 0);
            if (sustainPressed) sendSustain(false);
            break;
        }

        if (Console.KeyAvailable)
        {
            var keyInfo = Console.ReadKey(true);
            if (keyInfo.Key == ConsoleKey.OemPlus || keyInfo.Key == ConsoleKey.Add)
                Console.WriteLine($"八度: {++octave}");
            else if (keyInfo.Key == ConsoleKey.OemMinus || keyInfo.Key == ConsoleKey.Subtract)
                Console.WriteLine($"八度: {--octave}");
        }

        bool sustainDown = IsKeyButtonDown(VK_SPACE);
        if (sustainDown != sustainPressed)
        {
            sustainPressed = sustainDown;
            sendSustain(sustainPressed);
            Console.WriteLine(sustainPressed ? "[SUSTAIN ON]" : "[SUSTAIN OFF]");
        }

        foreach (var kvp in keyNoteMap)
        {
            bool isDown = IsKeyButtonDown((int)kvp.Key);
            bool wasPressed = pressedKeys.Contains(kvp.Key);

            if (isDown && !wasPressed)
            {
                pressedKeys.Add(kvp.Key);
                int note = (octave * 12) + kvp.Value + 12;
                sendNote(note, 100);
                Console.WriteLine($"[ON]  {noteNames[note % 12]}{(note / 12) - 1}");
            }
            else if (!isDown && wasPressed)
            {
                pressedKeys.Remove(kvp.Key);
                int note = (octave * 12) + kvp.Value + 12;
                sendNote(note, 0);
                Console.WriteLine($"[OFF] {noteNames[note % 12]}{(note / 12) - 1}");
            }
        }

        Thread.Sleep(10);
    }
}

static Task PortOccupancyTest()
{
    Console.WriteLine("\n--- 端口占用测试 ---");
    Console.Write("RTP 端口 (默认 5004，0 跳过): ");
    int rtpPort = int.TryParse(Console.ReadLine(), out var rp) ? rp : 5004;
    Console.Write("NM2 端口 (默认 5506，0 跳过): ");
    int nm2Port = int.TryParse(Console.ReadLine(), out var np) ? np : 5506;

    var listeners = new List<TcpListener>();
    try
    {
        if (rtpPort > 0) { var l = new TcpListener(IPAddress.Any, rtpPort); l.Start(); listeners.Add(l); Console.WriteLine($"✓ RTP 端口 {rtpPort} 已占用"); }
        if (nm2Port > 0) { var l = new TcpListener(IPAddress.Any, nm2Port); l.Start(); listeners.Add(l); Console.WriteLine($"✓ NM2 端口 {nm2Port} 已占用"); }
        Console.WriteLine("\n按任意键释放端口...");
        Console.ReadKey(true);
    }
    catch (Exception ex) { Console.WriteLine($"错误: {ex.Message}"); }
    finally { foreach (var l in listeners) try { l.Stop(); } catch { } Console.WriteLine("端口已释放"); }
    return Task.CompletedTask;
}

static Task StartNM2DiscoverableServer()
{
    Console.WriteLine("\n=== NM2 多设备发现测试 ===");
    var servers = new List<(string Name, int Port, NM2DiscoverableServer Server)>();
    int defaultPort = 5507, deviceNum = 1;

    Console.WriteLine("命令: a [name] - 添加设备 | l - 列出 | r <num> - 移除 | q - 退出\n");

    while (true)
    {
        Console.Write("> ");
        var line = Console.ReadLine();
        if (string.IsNullOrEmpty(line)) continue;

        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        switch (parts[0].ToLower())
        {
            case "a":
                string name = parts.Length > 1 ? parts[1] : $"NM2Device{deviceNum}";
                int port = defaultPort;
                while (servers.Any(s => s.Port == port)) port++;
                var server = new NM2DiscoverableServer(name, port, $"test-{deviceNum}");
                server.OnLog += msg => Console.WriteLine($"  [{name}] {msg}");
                server.OnMidiReceived += midi => Console.WriteLine($"  [{name}] MIDI: {BitConverter.ToString(midi)}");
                if (server.Start()) { servers.Add((name, port, server)); Console.WriteLine($"✓ 已添加: {name} (端口 {port})"); deviceNum++; defaultPort = port + 1; }
                else { server.Dispose(); Console.WriteLine($"✗ 添加失败"); }
                break;
            case "l":
                if (servers.Count == 0) Console.WriteLine("(无设备)");
                else for (int i = 0; i < servers.Count; i++) Console.WriteLine($"  [{i + 1}] {servers[i].Name} :{servers[i].Port} ({(servers[i].Server.IsConnected ? "已连接" : "等待")})");
                break;
            case "r":
                if (parts.Length < 2 || !int.TryParse(parts[1], out int num) || num < 1 || num > servers.Count) Console.WriteLine("用法: r <编号>");
                else { servers[num - 1].Server.Stop(); servers[num - 1].Server.Dispose(); Console.WriteLine($"✓ 已移除: {servers[num - 1].Name}"); servers.RemoveAt(num - 1); }
                break;
            case "q":
                foreach (var s in servers) { s.Server.Stop(); s.Server.Dispose(); }
                return Task.CompletedTask;
        }
    }
}