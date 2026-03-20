using System.Net;
using System.Net.Sockets;
using Test.Tests.LocalMidi;
using Test.Tests.NetworkMidi2;
using Test.Tests.RtpMidi;

Console.WriteLine("=== MIDI 测试工具 ===\n");

while (true)
{
    Console.WriteLine("\n选择测试类别:");
    Console.WriteLine("1. 本地 MIDI 设备");
    Console.WriteLine("2. RTP-MIDI 协议");
    Console.WriteLine("3. Network MIDI 2.0 协议");
    Console.WriteLine("4. 工具");
    Console.WriteLine("0. 退出");
    Console.Write("\n请选择: ");

    switch (Console.ReadLine())
    {
        case "1": await LocalMidiMenu(); break;
        case "2": await RtpMidiMenu(); break;
        case "3": await NetworkMidi2Menu(); break;
        case "4": await ToolsMenu(); break;
        case "0": return;
        default: Console.WriteLine("无效选择"); break;
    }
}

static async Task LocalMidiMenu()
{
    while (true)
    {
        Console.WriteLine("\n--- 本地 MIDI 设备 ---");
        Console.WriteLine("1. 列出设备");
        Console.WriteLine("2. 测试输入监听");
        Console.WriteLine("3. 测试输出");
        Console.WriteLine("0. 返回");
        Console.Write("\n请选择: ");

        switch (Console.ReadLine())
        {
            case "1": LocalMidiTests.ListDevices(); break;
            case "2": await LocalMidiTests.TestInput(); break;
            case "3": LocalMidiTests.TestOutput(); break;
            case "0": return;
            default: Console.WriteLine("无效选择"); break;
        }
    }
}

static async Task RtpMidiMenu()
{
    while (true)
    {
        Console.WriteLine("\n--- RTP-MIDI 协议 ---");
        Console.WriteLine("1. 服务端测试");
        Console.WriteLine("2. 钢琴键盘客户端");
        Console.WriteLine("0. 返回");
        Console.Write("\n请选择: ");

        switch (Console.ReadLine())
        {
            case "1": await RtpMidiTests.ServerTest(); break;
            case "2": await RtpMidiTests.PianoTest(); break;
            case "0": return;
            default: Console.WriteLine("无效选择"); break;
        }
    }
}

static async Task NetworkMidi2Menu()
{
    while (true)
    {
        Console.WriteLine("\n--- Network MIDI 2.0 协议 ---");
        Console.WriteLine("1. 客户端测试 (含钢琴)");
        Console.WriteLine("2. 多设备发现测试");
        Console.WriteLine("0. 返回");
        Console.Write("\n请选择: ");

        switch (Console.ReadLine())
        {
            case "1": await NetworkMidi2Tests.ClientTest(); break;
            case "2": await NetworkMidi2Tests.MultiDeviceTest(); break;
            case "0": return;
            default: Console.WriteLine("无效选择"); break;
        }
    }
}

static Task ToolsMenu()
{
    while (true)
    {
        Console.WriteLine("\n--- 工具 ---");
        Console.WriteLine("1. 端口占用测试");
        Console.WriteLine("0. 返回");
        Console.Write("\n请选择: ");

        switch (Console.ReadLine())
        {
            case "1": PortOccupancyTest(); break;
            case "0": return Task.CompletedTask;
            default: Console.WriteLine("无效选择"); break;
        }
    }
}

static void PortOccupancyTest()
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
}