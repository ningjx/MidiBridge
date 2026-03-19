namespace MidiBridge.Services.NetworkMidi2;

public static class NetworkMidi2Protocol
{
    public const string MDNS_SERVICE_TYPE = "_midi2._udp";
    public const string DEFAULT_SERVICE_NAME = "MidiBridge";
    public const int DEFAULT_PORT = 5506;
    public const int MAX_PACKET_SIZE = 1500;
    public const int SESSION_TIMEOUT_MS = 5000;
    public const int INVITATION_RETRY_COUNT = 3;
    public const int INVITATION_RETRY_INTERVAL_MS = 100;
    public const int PING_INTERVAL_MS = 10000;
    public const int SESSION_CHECK_INTERVAL_MS = 1000;
    public const int FEC_REDUNDANCY = 2;

    public enum SessionCommand : byte
    {
        Invitation = 0x01,
        EndSession = 0x02,
        Ping = 0x03,
        UMPData = 0x10,
    }

    public enum CommandStatus : byte
    {
        Command = 0x00,
        Reply = 0x01,
        Error = 0x02,
    }

    public enum InvitationError : byte
    {
        None = 0x00,
        HostBusy = 0x01,
        HostRejected = 0x02,
        AuthenticationRequired = 0x03,
        AuthenticationFailed = 0x04,
        HostNotFound = 0x05,
        HostNotReady = 0x06,
        ProtocolError = 0x7F,
    }

    public enum SessionState
    {
        Disconnected,
        Pending,
        Connected,
    }

    public struct SessionInfo
    {
        public string Id;
        public string RemoteName;
        public string RemoteHost;
        public int RemotePort;
        public byte SenderSSRC;
        public byte ReceiverSSRC;
        public SessionState State;
        public DateTime LastActivity;
        public ushort SendSequence;
        public ushort ReceiveSequence;
        public ushort PreviousReceiveSequence;
        public List<byte[]> RetransmitBuffer;
        public bool SupportsFEC;
        public bool SupportsRetransmit;
        public int PacketsSent;
        public int PacketsReceived;
        public int PacketsLost;
        public int PacketsOutOfOrder;
        public int PacketsDuplicate;
    }

    public class DiscoveredDevice
    {
        public string Name { get; set; } = "";
        public string Host { get; set; } = "";
        public int Port { get; set; }
        public string ProductInstanceId { get; set; } = "";
        public DateTime DiscoveredTime { get; set; }
    }

    public struct UMPDataCommand
    {
        public ushort SequenceNumber;
        public byte[] UMPData;
        public bool IsFEC;
    }

    public static byte[] CreateSessionCommandPacket(SessionCommand command, CommandStatus status, byte ssrc, byte remoteSSRC = 0)
    {
        var packet = new byte[4];
        packet[0] = (byte)((byte)command & 0x0F);
        packet[1] = (byte)status;
        packet[2] = ssrc;
        packet[3] = remoteSSRC;
        return packet;
    }

    public static byte[] CreateInvitationPacket(string name, byte ssrc, string productInstanceId = "")
    {
        var nameBytes = System.Text.Encoding.UTF8.GetBytes(name);
        var productBytes = System.Text.Encoding.UTF8.GetBytes(productInstanceId);
        var packet = new byte[4 + 2 + nameBytes.Length + 2 + productBytes.Length];

        int offset = 0;
        packet[offset++] = (byte)SessionCommand.Invitation;
        packet[offset++] = (byte)CommandStatus.Command;
        packet[offset++] = ssrc;
        packet[offset++] = 0;

        packet[offset++] = (byte)((nameBytes.Length >> 8) & 0xFF);
        packet[offset++] = (byte)(nameBytes.Length & 0xFF);
        System.Buffer.BlockCopy(nameBytes, 0, packet, offset, nameBytes.Length);
        offset += nameBytes.Length;

        packet[offset++] = (byte)((productBytes.Length >> 8) & 0xFF);
        packet[offset++] = (byte)(productBytes.Length & 0xFF);
        if (productBytes.Length > 0)
        {
            System.Buffer.BlockCopy(productBytes, 0, packet, offset, productBytes.Length);
        }

        return packet;
    }

    public static byte[] CreateInvitationReplyPacket(byte ssrc, byte remoteSSRC)
    {
        return CreateSessionCommandPacket(SessionCommand.Invitation, CommandStatus.Reply, ssrc, remoteSSRC);
    }

    public static byte[] CreateInvitationErrorPacket(byte ssrc, InvitationError error)
    {
        var packet = CreateSessionCommandPacket(SessionCommand.Invitation, CommandStatus.Error, ssrc);
        return packet;
    }

    public static byte[] CreateEndSessionPacket(byte ssrc, byte remoteSSRC)
    {
        return CreateSessionCommandPacket(SessionCommand.EndSession, CommandStatus.Command, ssrc, remoteSSRC);
    }

    public static byte[] CreatePingPacket(byte ssrc, byte remoteSSRC)
    {
        return CreateSessionCommandPacket(SessionCommand.Ping, CommandStatus.Command, ssrc, remoteSSRC);
    }

    public static byte[] CreatePongPacket(byte ssrc, byte remoteSSRC)
    {
        return CreateSessionCommandPacket(SessionCommand.Ping, CommandStatus.Reply, ssrc, remoteSSRC);
    }

    public static byte[] CreateUMPDataPacket(ushort sequenceNumber, byte[] umpData)
    {
        var packet = new byte[4 + umpData.Length];
        packet[0] = (byte)SessionCommand.UMPData;
        packet[1] = (byte)((sequenceNumber >> 8) & 0xFF);
        packet[2] = (byte)(sequenceNumber & 0xFF);
        packet[3] = 0;
        System.Buffer.BlockCopy(umpData, 0, packet, 4, umpData.Length);
        return packet;
    }

    public static bool ParsePacket(byte[] data, out SessionCommand command, out CommandStatus status, out byte ssrc, out byte remoteSSRC)
    {
        command = default;
        status = default;
        ssrc = 0;
        remoteSSRC = 0;

        if (data == null || data.Length < 4) return false;

        byte cmdByte = data[0];
        
        if (cmdByte == 0x10)
        {
            command = SessionCommand.UMPData;
        }
        else
        {
            command = (SessionCommand)(cmdByte & 0x0F);
        }
        
        status = (CommandStatus)data[1];
        ssrc = data[2];
        remoteSSRC = data[3];

        return true;
    }

    public static bool ParseInvitation(byte[] data, out string name, out string productInstanceId)
    {
        name = "";
        productInstanceId = "";

        if (data == null || data.Length < 6) return false;

        int offset = 4;
        int nameLength = (data[offset] << 8) | data[offset + 1];
        offset += 2;

        if (offset + nameLength > data.Length) return false;

        name = System.Text.Encoding.UTF8.GetString(data, offset, nameLength);
        offset += nameLength;

        if (offset + 2 > data.Length) return true;

        int productLength = (data[offset] << 8) | data[offset + 1];
        offset += 2;

        if (offset + productLength <= data.Length && productLength > 0)
        {
            productInstanceId = System.Text.Encoding.UTF8.GetString(data, offset, productLength);
        }

        return true;
    }

    public static bool ParseUMPData(byte[] data, out ushort sequenceNumber, out byte[] umpData)
    {
        sequenceNumber = 0;
        umpData = Array.Empty<byte>();

        if (data == null || data.Length < 5) return false;
        if (data[0] != 0x10) return false;

        sequenceNumber = (ushort)((data[1] << 8) | data[2]);
        umpData = new byte[data.Length - 4];
        System.Buffer.BlockCopy(data, 4, umpData, 0, umpData.Length);

        return true;
    }

    public static int GetUMPPacketSize(int messageType)
    {
        return messageType switch
        {
            0x0 or 0x1 or 0x3 or 0x7 or 0x8 or 0x9 or 0xA or 0xB or 0xC or 0xD or 0xE or 0xF => 4,
            0x2 or 0x4 or 0x6 => 8,
            0x5 => 16,
            _ => 4
        };
    }

    public static int GetUMPMessageType(byte[] data, int offset)
    {
        if (data == null || offset >= data.Length) return 0;
        return (data[offset] >> 4) & 0x0F;
    }
}