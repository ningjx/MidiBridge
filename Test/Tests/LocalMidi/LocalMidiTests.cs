using NAudio.Midi;

namespace Test.Tests.LocalMidi;

public static class LocalMidiTests
{
    public static void ListDevices()
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

    public static async Task TestInput()
    {
        Console.WriteLine("\n--- MIDI 输入监听测试 ---");
        ListDevices();

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

    public static void TestOutput()
    {
        Console.WriteLine("\n--- MIDI 输出测试 ---");
        ListDevices();

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
}