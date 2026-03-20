using System.Runtime.InteropServices;

namespace Test.Common;

public static class PianoHelper
{
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    private const int VK_SPACE = 0x20;

    private static readonly Dictionary<ConsoleKey, int> KeyNoteMap = new()
    {
        { ConsoleKey.A, 0 }, { ConsoleKey.W, 1 }, { ConsoleKey.S, 2 }, { ConsoleKey.E, 3 },
        { ConsoleKey.D, 4 }, { ConsoleKey.F, 5 }, { ConsoleKey.T, 6 }, { ConsoleKey.G, 7 },
        { ConsoleKey.Y, 8 }, { ConsoleKey.H, 9 }, { ConsoleKey.U, 10 }, { ConsoleKey.J, 11 },
        { ConsoleKey.K, 12 }, { ConsoleKey.O, 13 }, { ConsoleKey.L, 14 }, { ConsoleKey.P, 15 },
    };

    private static readonly string[] NoteNames = { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };

    public static void PrintInstructions()
    {
        Console.WriteLine("键盘映射:");
        Console.WriteLine("  白键: A S D F G H J K L (C4-C5)");
        Console.WriteLine("  黑键: W E   T Y U      (C#4-F#4)");
        Console.WriteLine("  空格: 延音踏板 (Sustain)");
        Console.WriteLine("  +/- : 升/降八度");
        Console.WriteLine("  ESC : 退出\n");
    }

    public static void RunLoop(Action<int, int> sendNote, Action<bool> sendSustain)
    {
        var pressedKeys = new HashSet<ConsoleKey>();
        bool sustainPressed = false;
        int octave = 4;

        while (true)
        {
            if (IsKeyDown((int)ConsoleKey.Escape))
            {
                foreach (var key in pressedKeys)
                    if (KeyNoteMap.TryGetValue(key, out int offset))
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

            bool sustainDown = IsKeyDown(VK_SPACE);
            if (sustainDown != sustainPressed)
            {
                sustainPressed = sustainDown;
                sendSustain(sustainPressed);
                Console.WriteLine(sustainPressed ? "[SUSTAIN ON]" : "[SUSTAIN OFF]");
            }

            foreach (var kvp in KeyNoteMap)
            {
                bool isDown = IsKeyDown((int)kvp.Key);
                bool wasPressed = pressedKeys.Contains(kvp.Key);

                if (isDown && !wasPressed)
                {
                    pressedKeys.Add(kvp.Key);
                    int note = (octave * 12) + kvp.Value + 12;
                    sendNote(note, 100);
                    Console.WriteLine($"[ON]  {NoteNames[note % 12]}{(note / 12) - 1}");
                }
                else if (!isDown && wasPressed)
                {
                    pressedKeys.Remove(kvp.Key);
                    int note = (octave * 12) + kvp.Value + 12;
                    sendNote(note, 0);
                    Console.WriteLine($"[OFF] {NoteNames[note % 12]}{(note / 12) - 1}");
                }
            }

            Thread.Sleep(10);
        }
    }

    private static bool IsKeyDown(int vKey) => (GetAsyncKeyState(vKey) & 0x8000) != 0;
}