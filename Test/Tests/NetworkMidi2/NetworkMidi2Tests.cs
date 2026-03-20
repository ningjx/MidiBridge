namespace Test.Tests.NetworkMidi2;

public static class NetworkMidi2Tests
{
    public static async Task ClientTest()
    {
        Console.WriteLine("\n=== Network MIDI 2.0 客户端测试 ===");
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
        Common.PianoHelper.PrintInstructions();
        Common.PianoHelper.RunLoop(
            (note, vel) => { if (vel > 0) client.SendNoteOn((byte)note, (byte)vel); else client.SendNoteOff((byte)note); },
            on => client.SendMidi(new byte[] { 0xB0, 64, (byte)(on ? 127 : 0) })
        );

        client.Disconnect();
        Console.WriteLine("\n测试完成");
    }

    public static Task MultiDeviceTest()
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
}