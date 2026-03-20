using System.IO;

namespace MidiBridge.Services.RtpMidi;

public class RecoveryJournal
{
    public const int MAX_CHANNELS = 16;
    public const int MAX_NOTES = 128;
    public const int MAX_CONTROLLERS = 128;

    public class ChannelState
    {
        public ChapterPState ProgramChange { get; } = new();
        public ChapterCState ControlChange { get; } = new();
        public ChapterWState PitchWheel { get; } = new();
        public ChapterNState Notes { get; } = new();

        public uint LastUpdateSeqNum;
    }

    public class ChapterPState
    {
        public byte Program;
        public byte BankMsb;
        public byte BankLsb;
        public bool ProgramValid;
        public bool BankMsbValid;
        public bool BankLsbValid;
        public uint SeqNum;
    }

    public class ChapterCState
    {
        public byte[] Values { get; } = new byte[MAX_CONTROLLERS];
        public byte[] Toggle { get; } = new byte[MAX_CONTROLLERS];
        public byte[] Count { get; } = new byte[MAX_CONTROLLERS];
        public byte[] ToolType { get; } = new byte[MAX_CONTROLLERS];
        public uint[] SeqNums { get; } = new uint[MAX_CONTROLLERS];
        public uint ChapterSeqNum;

        public const byte TOOL_VALUE = 0;
        public const byte TOOL_TOGGLE = 1;
        public const byte TOOL_COUNT = 2;

        public ChapterCState()
        {
            Array.Fill(Values, (byte)0);
            Array.Fill(Toggle, (byte)0);
            Array.Fill(Count, (byte)0);
            Array.Fill(ToolType, (byte)0);
            Array.Fill(SeqNums, (uint)0);
        }
    }

    public class ChapterWState
    {
        public ushort Value = 0x2000;
        public uint SeqNum;
    }

    public class ChapterNState
    {
        public byte[] Velocities { get; } = new byte[MAX_NOTES];
        public uint[] NoteOnSeqNums { get; } = new uint[MAX_NOTES];
        public uint[] NoteOnTimestamps { get; } = new uint[MAX_NOTES];
        public byte[] NoteOffBits { get; } = new byte[16];
        public uint ChapterSeqNum;

        public ChapterNState()
        {
            Array.Fill(Velocities, (byte)0);
            Array.Fill(NoteOnSeqNums, (uint)0);
            Array.Fill(NoteOnTimestamps, (uint)0);
            Array.Fill(NoteOffBits, (byte)0);
        }

        public void SetNoteOffBit(int note)
        {
            if (note >= 0 && note < MAX_NOTES)
            {
                NoteOffBits[note / 8] |= (byte)(1 << (note % 8));
            }
        }

        public void ClearNoteOffBit(int note)
        {
            if (note >= 0 && note < MAX_NOTES)
            {
                NoteOffBits[note / 8] &= (byte)~(1 << (note % 8));
            }
        }

        public bool GetNoteOffBit(int note)
        {
            if (note >= 0 && note < MAX_NOTES)
            {
                return (NoteOffBits[note / 8] & (1 << (note % 8))) != 0;
            }
            return false;
        }
    }

    private readonly ChannelState[] _channels = new ChannelState[MAX_CHANNELS];
    private uint _journalSeqNum;
    private uint _currentTimestamp;

    public RecoveryJournal()
    {
        for (int i = 0; i < MAX_CHANNELS; i++)
        {
            _channels[i] = new ChannelState();
        }
    }

    public void UpdateFromMidiCommand(byte[] midiData, uint seqNum, uint timestamp)
    {
        if (midiData == null || midiData.Length < 2) return;

        _currentTimestamp = timestamp;

        byte status = midiData[0];
        int channel = status & 0x0F;
        int command = status & 0xF0;

        var ch = _channels[channel];
        ch.LastUpdateSeqNum = seqNum;

        switch (command)
        {
            case 0x80:
                UpdateNoteOff(channel, midiData[1], seqNum);
                break;
            case 0x90:
                if (midiData.Length >= 3)
                {
                    if (midiData[2] == 0)
                        UpdateNoteOff(channel, midiData[1], seqNum);
                    else
                        UpdateNoteOn(channel, midiData[1], midiData[2], seqNum, timestamp);
                }
                break;
            case 0xB0:
                if (midiData.Length >= 3)
                    UpdateControlChange(channel, midiData[1], midiData[2], seqNum);
                break;
            case 0xC0:
                if (midiData.Length >= 2)
                    UpdateProgramChange(channel, midiData[1], seqNum);
                break;
            case 0xE0:
                if (midiData.Length >= 3)
                    UpdatePitchWheel(channel, midiData[1], midiData[2], seqNum);
                break;
        }

        _journalSeqNum = seqNum;
    }

    private void UpdateNoteOn(int channel, byte note, byte velocity, uint seqNum, uint timestamp)
    {
        var notes = _channels[channel].Notes;
        notes.Velocities[note] = velocity;
        notes.NoteOnSeqNums[note] = seqNum;
        notes.NoteOnTimestamps[note] = timestamp;
        notes.ClearNoteOffBit(note);
        notes.ChapterSeqNum = seqNum;
    }

    private void UpdateNoteOff(int channel, byte note, uint seqNum)
    {
        var notes = _channels[channel].Notes;
        if (notes.Velocities[note] > 0)
        {
            notes.SetNoteOffBit(note);
        }
        notes.Velocities[note] = 0;
        notes.ChapterSeqNum = seqNum;
    }

    private void UpdateControlChange(int channel, byte controller, byte value, uint seqNum)
    {
        var cc = _channels[channel].ControlChange;
        cc.Values[controller] = value;
        cc.SeqNums[controller] = seqNum;
        cc.ChapterSeqNum = seqNum;

        switch (controller)
        {
            case 0:
                _channels[channel].ProgramChange.BankMsb = value;
                _channels[channel].ProgramChange.BankMsbValid = true;
                break;
            case 32:
                _channels[channel].ProgramChange.BankLsb = value;
                _channels[channel].ProgramChange.BankLsbValid = true;
                break;
            case 64:
            case 66:
            case 67:
            case 69:
                cc.ToolType[controller] = ChapterCState.TOOL_TOGGLE;
                cc.Toggle[controller] = (byte)(value >= 64 ? 1 : 0);
                break;
            case 65:
            case 68:
                cc.ToolType[controller] = ChapterCState.TOOL_COUNT;
                cc.Count[controller] = (byte)(value >= 64 ? 1 : 0);
                break;
            default:
                cc.ToolType[controller] = ChapterCState.TOOL_VALUE;
                break;
        }
    }

    private void UpdateProgramChange(int channel, byte program, uint seqNum)
    {
        var pc = _channels[channel].ProgramChange;
        pc.Program = program;
        pc.ProgramValid = true;
        pc.SeqNum = seqNum;
    }

    private void UpdatePitchWheel(int channel, byte lsb, byte msb, uint seqNum)
    {
        var pw = _channels[channel].PitchWheel;
        pw.Value = (ushort)((msb << 7) | lsb);
        pw.SeqNum = seqNum;
    }

    public byte[] Encode(uint checkpointSeqNum, uint currentRtpTimestamp, bool singlePacketLoss = false)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        int activeChannels = 0;
        foreach (var ch in _channels)
        {
            if (ch.LastUpdateSeqNum > checkpointSeqNum) activeChannels++;
        }

        if (activeChannels == 0)
        {
            byte emptyHeader = 0x40;
            emptyHeader |= 0x80;
            writer.Write(emptyHeader);
            writer.Write((byte)0);
            writer.Write((byte)0);
            return ms.ToArray();
        }

        byte header = 0x40;
        int totchan = Math.Min(activeChannels, 15);
        header |= (byte)(totchan << 4);

        writer.Write(header);
        writer.Write((byte)((checkpointSeqNum >> 16) & 0xFF));
        writer.Write((byte)((checkpointSeqNum >> 8) & 0xFF));
        writer.Write((byte)(checkpointSeqNum & 0xFF));

        for (int ch = 0; ch < MAX_CHANNELS; ch++)
        {
            var channel = _channels[ch];
            if (channel.LastUpdateSeqNum <= checkpointSeqNum) continue;

            byte cheader = 0;
            int chapters = 0;

            if (channel.ProgramChange.SeqNum > checkpointSeqNum) chapters |= 0x01;
            if (channel.ControlChange.ChapterSeqNum > checkpointSeqNum) chapters |= 0x02;
            if (channel.PitchWheel.SeqNum > checkpointSeqNum) chapters |= 0x04;
            if (channel.Notes.ChapterSeqNum > checkpointSeqNum) chapters |= 0x08;

            if (chapters == 0) continue;

            cheader |= (byte)(ch << 4);
            cheader |= (byte)(chapters & 0x0F);

            bool sbit = singlePacketLoss && channel.LastUpdateSeqNum > checkpointSeqNum;
            if (sbit) cheader |= 0x80;

            writer.Write(cheader);
            writer.Write((byte)0);
            writer.Write((byte)0);

            if ((chapters & 0x01) != 0)
                EncodeChapterP(writer, channel.ProgramChange, singlePacketLoss);
            if ((chapters & 0x02) != 0)
                EncodeChapterC(writer, channel.ControlChange, singlePacketLoss, checkpointSeqNum);
            if ((chapters & 0x04) != 0)
                EncodeChapterW(writer, channel.PitchWheel, singlePacketLoss);
            if ((chapters & 0x08) != 0)
                EncodeChapterN(writer, channel.Notes, singlePacketLoss, checkpointSeqNum, currentRtpTimestamp);
        }

        return ms.ToArray();
    }

    private void EncodeChapterP(BinaryWriter writer, ChapterPState pc, bool singlePacketLoss)
    {
        byte header = 0;
        if (pc.BankMsbValid || pc.BankLsbValid) header |= 0x80;
        if (singlePacketLoss) header |= 0x40;
        writer.Write(header);
        writer.Write(pc.Program);
        if (pc.BankMsbValid) writer.Write(pc.BankMsb);
        if (pc.BankLsbValid) writer.Write(pc.BankLsb);
    }

    private void EncodeChapterC(BinaryWriter writer, ChapterCState cc, bool singlePacketLoss, uint checkpointSeqNum)
    {
        var logs = new List<(byte controller, byte toolType, byte value)>();
        for (int i = 0; i < MAX_CONTROLLERS; i++)
        {
            if (i == 0 || i == 32) continue;
            if (cc.SeqNums[i] > checkpointSeqNum)
            {
                byte value = cc.ToolType[i] switch
                {
                    ChapterCState.TOOL_TOGGLE => cc.Toggle[i],
                    ChapterCState.TOOL_COUNT => cc.Count[i],
                    _ => cc.Values[i]
                };
                logs.Add(((byte)i, cc.ToolType[i], value));
            }
        }

        byte header = (byte)Math.Min(logs.Count, 127);
        if (singlePacketLoss) header |= 0x80;
        writer.Write(header);

        foreach (var (controller, toolType, value) in logs)
        {
            byte controllerByte = controller;
            controllerByte |= (byte)(toolType << 5);
            writer.Write(controllerByte);
            writer.Write(value);
        }
    }

    private void EncodeChapterW(BinaryWriter writer, ChapterWState pw, bool singlePacketLoss)
    {
        byte header = 0;
        if (singlePacketLoss) header |= 0x80;
        writer.Write(header);
        writer.Write((byte)((pw.Value >> 7) & 0x7F));
        writer.Write((byte)(pw.Value & 0x7F));
    }

    private void EncodeChapterN(BinaryWriter writer, ChapterNState notes, bool singlePacketLoss, uint checkpointSeqNum, uint currentRtpTimestamp)
    {
        var noteLogs = new List<(byte note, byte velocity, bool yBit)>();

        for (int i = 0; i < MAX_NOTES; i++)
        {
            if (notes.Velocities[i] > 0 && notes.NoteOnSeqNums[i] > checkpointSeqNum)
            {
                bool yBit = false;
                if (notes.NoteOnTimestamps[i] > 0 && currentRtpTimestamp > notes.NoteOnTimestamps[i])
                {
                    uint diff = currentRtpTimestamp - notes.NoteOnTimestamps[i];
                    yBit = diff < 4410;
                }
                noteLogs.Add(((byte)i, notes.Velocities[i], yBit));
            }
        }

        int offBytes = 16;
        int headerLen = 2;
        int logsLen = noteLogs.Count * 2;
        int totalLen = headerLen + offBytes + logsLen;

        if (totalLen > 255)
        {
            noteLogs = noteLogs.Take((255 - headerLen - offBytes) / 2).ToList();
            totalLen = headerLen + offBytes + noteLogs.Count * 2;
        }

        byte low = (byte)(totalLen & 0x0F);
        byte high = (byte)((totalLen >> 4) & 0x0F);
        if (singlePacketLoss) high |= 0x08;

        writer.Write(high);
        writer.Write(low);
        writer.Write(notes.NoteOffBits);

        foreach (var (note, velocity, yBit) in noteLogs)
        {
            byte noteByte = note;
            if (yBit) noteByte |= 0x80;
            writer.Write(noteByte);
            writer.Write(velocity);
        }
    }

    public void Trim(uint receivedSeqNum)
    {
        for (int ch = 0; ch < MAX_CHANNELS; ch++)
        {
            var channel = _channels[ch];
            if (channel.LastUpdateSeqNum <= receivedSeqNum)
            {
                channel.LastUpdateSeqNum = 0;
                continue;
            }

            var notes = channel.Notes;
            for (int n = 0; n < MAX_NOTES; n++)
            {
                if (notes.NoteOnSeqNums[n] <= receivedSeqNum && notes.Velocities[n] == 0)
                {
                    notes.NoteOnSeqNums[n] = 0;
                    notes.ClearNoteOffBit(n);
                }
            }

            var cc = channel.ControlChange;
            for (int c = 0; c < MAX_CONTROLLERS; c++)
            {
                if (cc.SeqNums[c] <= receivedSeqNum)
                    cc.SeqNums[c] = 0;
            }
        }
    }
}

public class RecoveryJournalReceiver
{
    private const int MAX_CHANNELS = 16;
    private const int MAX_NOTES = 128;

    private readonly RecoveryJournal.ChannelState[] _channels = new RecoveryJournal.ChannelState[MAX_CHANNELS];
    private ushort _lastReceivedSeq;

    public RecoveryJournalReceiver()
    {
        for (int i = 0; i < MAX_CHANNELS; i++)
            _channels[i] = new RecoveryJournal.ChannelState();
    }

    public void UpdateFromReceivedMidi(byte[] midiData, ushort seqNum)
    {
        if (midiData == null || midiData.Length < 2) return;

        _lastReceivedSeq = seqNum;

        byte status = midiData[0];
        int channel = status & 0x0F;
        int command = status & 0xF0;

        switch (command)
        {
            case 0x80:
                _channels[channel].Notes.Velocities[midiData[1]] = 0;
                break;
            case 0x90:
                _channels[channel].Notes.Velocities[midiData[1]] = midiData.Length >= 3 ? midiData[2] : (byte)0;
                break;
            case 0xB0:
                if (midiData.Length >= 3)
                    _channels[channel].ControlChange.Values[midiData[1]] = midiData[2];
                break;
            case 0xC0:
                if (midiData.Length >= 2)
                {
                    _channels[channel].ProgramChange.Program = midiData[1];
                    _channels[channel].ProgramChange.ProgramValid = true;
                }
                break;
            case 0xE0:
                if (midiData.Length >= 3)
                    _channels[channel].PitchWheel.Value = (ushort)((midiData[2] << 7) | midiData[1]);
                break;
        }
    }

    public List<byte[]> ProcessRecoveryJournal(byte[] journalData, ushort receivedSeqNum, ushort expectedSeqNum)
    {
        var recoveryCommands = new List<byte[]>();

        if (journalData == null || journalData.Length < 3) return recoveryCommands;

        int missingPackets = (receivedSeqNum - expectedSeqNum) & 0xFFFF;
        if (missingPackets == 0) return recoveryCommands;

        bool singlePacketLoss = missingPackets == 1;

        int offset = 0;
        byte header = journalData[offset++];

        bool isEmpty = (header & 0x80) != 0;
        if (isEmpty) return recoveryCommands;

        uint checkpointSeqNum = (uint)((journalData[offset++] << 16) | (journalData[offset++] << 8) | journalData[offset++]);

        while (offset < journalData.Length)
        {
            if (offset >= journalData.Length) break;
            byte cheader = journalData[offset++];

            bool sbit = (cheader & 0x80) != 0;
            if (singlePacketLoss && sbit)
            {
                offset += 2;
                continue;
            }

            int channel = (cheader >> 4) & 0x0F;
            int chapters = cheader & 0x0F;

            offset += 2;

            if ((chapters & 0x01) != 0)
                offset = ProcessChapterP(journalData, offset, channel, recoveryCommands);
            if ((chapters & 0x02) != 0)
                offset = ProcessChapterC(journalData, offset, channel, singlePacketLoss, recoveryCommands);
            if ((chapters & 0x04) != 0)
                offset = ProcessChapterW(journalData, offset, channel, recoveryCommands);
            if ((chapters & 0x08) != 0)
                offset = ProcessChapterN(journalData, offset, channel, singlePacketLoss, recoveryCommands);
        }

        return recoveryCommands;
    }

    private int ProcessChapterP(byte[] data, int offset, int channel, List<byte[]> commands)
    {
        if (offset >= data.Length) return offset;

        byte header = data[offset++];
        bool hasBank = (header & 0x80) != 0;
        byte program = data[offset++];

        var pc = _channels[channel].ProgramChange;
        if (pc.Program != program || !pc.ProgramValid)
        {
            commands.Add(new byte[] { (byte)(0xC0 | channel), program });
            pc.Program = program;
            pc.ProgramValid = true;
        }

        if (hasBank && offset + 1 < data.Length)
        {
            byte msb = data[offset++];
            byte lsb = data[offset++];

            if (pc.BankMsb != msb || !pc.BankMsbValid)
            {
                commands.Add(new byte[] { (byte)(0xB0 | channel), 0, msb });
                pc.BankMsb = msb;
                pc.BankMsbValid = true;
            }
            if (pc.BankLsb != lsb || !pc.BankLsbValid)
            {
                commands.Add(new byte[] { (byte)(0xB0 | channel), 32, lsb });
                pc.BankLsb = lsb;
                pc.BankLsbValid = true;
            }
        }

        return offset;
    }

    private int ProcessChapterC(byte[] data, int offset, int channel, bool singlePacketLoss, List<byte[]> commands)
    {
        if (offset >= data.Length) return offset;

        byte header = data[offset++];
        bool sbit = (header & 0x80) != 0;
        int count = header & 0x7F;

        for (int i = 0; i < count && offset + 1 < data.Length; i++)
        {
            if (singlePacketLoss && sbit) continue;

            byte controllerByte = data[offset++];
            byte value = data[offset++];

            byte controller = (byte)(controllerByte & 0x1F);
            byte toolType = (byte)((controllerByte >> 5) & 0x03);

            var cc = _channels[channel].ControlChange;
            
            byte actualValue = toolType switch
            {
                RecoveryJournal.ChapterCState.TOOL_TOGGLE => (byte)(value > 0 ? 127 : 0),
                RecoveryJournal.ChapterCState.TOOL_COUNT => value,
                _ => value
            };

            if (cc.Values[controller] != actualValue)
            {
                commands.Add(new byte[] { (byte)(0xB0 | channel), controller, actualValue });
                cc.Values[controller] = actualValue;
            }
        }

        return offset;
    }

    private int ProcessChapterW(byte[] data, int offset, int channel, List<byte[]> commands)
    {
        if (offset + 2 >= data.Length) return offset;

        offset++;
        byte msb = data[offset++];
        byte lsb = data[offset++];

        ushort value = (ushort)((msb << 7) | lsb);
        var pw = _channels[channel].PitchWheel;

        if (pw.Value != value)
        {
            commands.Add(new byte[] { (byte)(0xE0 | channel), lsb, msb });
            pw.Value = value;
        }

        return offset;
    }

    private int ProcessChapterN(byte[] data, int offset, int channel, bool singlePacketLoss, List<byte[]> commands)
    {
        if (offset + 1 >= data.Length) return offset;

        byte high = data[offset++];
        byte low = data[offset++];

        bool sbit = (high & 0x08) != 0;
        int length = ((high & 0x07) << 4) | (low & 0x0F);

        int offBytes = 16;
        if (offset + offBytes > data.Length) return data.Length;

        for (int i = 0; i < offBytes; i++)
        {
            byte offByte = data[offset++];
            for (int bit = 0; bit < 8; bit++)
            {
                if ((offByte & (1 << bit)) != 0)
                {
                    int note = i * 8 + bit;
                    var notes = _channels[channel].Notes;
                    if (notes.Velocities[note] != 0)
                    {
                        commands.Add(new byte[] { (byte)(0x80 | channel), (byte)note, 0 });
                        notes.Velocities[note] = 0;
                    }
                }
            }
        }

        while (offset + 1 < data.Length)
        {
            if (singlePacketLoss && sbit) break;

            byte noteByte = data[offset++];
            byte velocity = data[offset++];

            int note = noteByte & 0x7F;
            bool yBit = (noteByte & 0x80) != 0;
            var notes = _channels[channel].Notes;

            if (notes.Velocities[note] == 0)
            {
                if (yBit)
                {
                    commands.Add(new byte[] { (byte)(0x90 | channel), (byte)note, velocity });
                    notes.Velocities[note] = velocity;
                }
            }
            else if (notes.Velocities[note] != velocity)
            {
                commands.Add(new byte[] { (byte)(0x80 | channel), (byte)note, 0 });
                commands.Add(new byte[] { (byte)(0x90 | channel), (byte)note, velocity });
                notes.Velocities[note] = velocity;
            }
        }

        return offset;
    }

    public List<byte[]> GenerateAllNotesOff()
    {
        var commands = new List<byte[]>();

        for (int ch = 0; ch < MAX_CHANNELS; ch++)
        {
            var notes = _channels[ch].Notes;
            for (int n = 0; n < MAX_NOTES; n++)
            {
                if (notes.Velocities[n] != 0)
                {
                    commands.Add(new byte[] { (byte)(0x80 | ch), (byte)n, 0 });
                    notes.Velocities[n] = 0;
                }
            }
        }

        return commands;
    }
}