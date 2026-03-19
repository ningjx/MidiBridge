using System.Text;

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
        RetransmitRequest = 0x04,
        RetransmitResponse = 0x05,
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

    [Flags]
    public enum PeerCapabilities : ushort
    {
        None = 0x0000,
        Retransmission = 0x0001,
        FEC = 0x0002,
        All = Retransmission | FEC,
    }

    public struct SessionInfo
    {
        public string Id;
        public string RemoteName;
        public string RemoteHost;
        public int RemotePort;
        public uint SenderSSRC;
        public uint ReceiverSSRC;
        public SessionState State;
        public DateTime LastActivity;
        public ushort SendSequence;
        public ushort ReceiveSequence;
        public ushort PreviousReceiveSequence;
        public List<byte[]> RetransmitBuffer;
        public PeerCapabilities LocalCapabilities;
        public PeerCapabilities RemoteCapabilities;
        public int PacketsSent;
        public int PacketsReceived;
        public int PacketsLost;
        public int PacketsOutOfOrder;
        public int PacketsDuplicate;
        public int PacketsRecovered;
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

    public static byte[] CreateSessionCommandPacket(SessionCommand command, CommandStatus status, uint ssrc, uint remoteSSRC = 0)
    {
        var packet = new byte[12];
        packet[0] = (byte)((byte)command & 0x0F);
        packet[1] = (byte)status;
        packet[2] = (byte)((ssrc >> 24) & 0xFF);
        packet[3] = (byte)((ssrc >> 16) & 0xFF);
        packet[4] = (byte)((ssrc >> 8) & 0xFF);
        packet[5] = (byte)(ssrc & 0xFF);
        packet[6] = (byte)((remoteSSRC >> 24) & 0xFF);
        packet[7] = (byte)((remoteSSRC >> 16) & 0xFF);
        packet[8] = (byte)((remoteSSRC >> 8) & 0xFF);
        packet[9] = (byte)(remoteSSRC & 0xFF);
        return packet;
    }

    public static byte[] CreateInvitationPacket(string name, uint ssrc, string productInstanceId = "", PeerCapabilities capabilities = PeerCapabilities.All)
    {
        var nameBytes = Encoding.UTF8.GetBytes(name);
        var productBytes = Encoding.UTF8.GetBytes(productInstanceId);
        var packet = new byte[16 + 2 + nameBytes.Length + 2 + productBytes.Length + 2];

        int offset = 0;
        packet[offset++] = (byte)SessionCommand.Invitation;
        packet[offset++] = (byte)CommandStatus.Command;
        
        packet[offset++] = (byte)((ssrc >> 24) & 0xFF);
        packet[offset++] = (byte)((ssrc >> 16) & 0xFF);
        packet[offset++] = (byte)((ssrc >> 8) & 0xFF);
        packet[offset++] = (byte)(ssrc & 0xFF);

        packet[offset++] = 0;
        packet[offset++] = 0;
        packet[offset++] = 0;
        packet[offset++] = 0;

        packet[offset++] = (byte)((nameBytes.Length >> 8) & 0xFF);
        packet[offset++] = (byte)(nameBytes.Length & 0xFF);
        Buffer.BlockCopy(nameBytes, 0, packet, offset, nameBytes.Length);
        offset += nameBytes.Length;

        packet[offset++] = (byte)((productBytes.Length >> 8) & 0xFF);
        packet[offset++] = (byte)(productBytes.Length & 0xFF);
        if (productBytes.Length > 0)
        {
            Buffer.BlockCopy(productBytes, 0, packet, offset, productBytes.Length);
            offset += productBytes.Length;
        }

        packet[offset++] = (byte)(((ushort)capabilities >> 8) & 0xFF);
        packet[offset++] = (byte)((ushort)capabilities & 0xFF);

        return packet;
    }

    public static byte[] CreateInvitationReplyPacket(uint ssrc, uint remoteSSRC, PeerCapabilities capabilities = PeerCapabilities.All)
    {
        var packet = new byte[16];
        packet[0] = (byte)SessionCommand.Invitation;
        packet[1] = (byte)CommandStatus.Reply;
        packet[2] = (byte)((ssrc >> 24) & 0xFF);
        packet[3] = (byte)((ssrc >> 16) & 0xFF);
        packet[4] = (byte)((ssrc >> 8) & 0xFF);
        packet[5] = (byte)(ssrc & 0xFF);
        packet[6] = (byte)((remoteSSRC >> 24) & 0xFF);
        packet[7] = (byte)((remoteSSRC >> 16) & 0xFF);
        packet[8] = (byte)((remoteSSRC >> 8) & 0xFF);
        packet[9] = (byte)(remoteSSRC & 0xFF);
        packet[14] = (byte)(((ushort)capabilities >> 8) & 0xFF);
        packet[15] = (byte)((ushort)capabilities & 0xFF);
        return packet;
    }

    public static byte[] CreateInvitationErrorPacket(uint ssrc, InvitationError error)
    {
        var packet = new byte[12];
        packet[0] = (byte)SessionCommand.Invitation;
        packet[1] = (byte)CommandStatus.Error;
        packet[2] = (byte)((ssrc >> 24) & 0xFF);
        packet[3] = (byte)((ssrc >> 16) & 0xFF);
        packet[4] = (byte)((ssrc >> 8) & 0xFF);
        packet[5] = (byte)(ssrc & 0xFF);
        packet[11] = (byte)error;
        return packet;
    }

    public static byte[] CreateEndSessionPacket(uint ssrc, uint remoteSSRC)
    {
        return CreateSessionCommandPacket(SessionCommand.EndSession, CommandStatus.Command, ssrc, remoteSSRC);
    }

    public static byte[] CreatePingPacket(uint ssrc, uint remoteSSRC)
    {
        return CreateSessionCommandPacket(SessionCommand.Ping, CommandStatus.Command, ssrc, remoteSSRC);
    }

    public static byte[] CreatePongPacket(uint ssrc, uint remoteSSRC)
    {
        return CreateSessionCommandPacket(SessionCommand.Ping, CommandStatus.Reply, ssrc, remoteSSRC);
    }

    public static byte[] CreateRetransmitRequestPacket(uint ssrc, uint remoteSSRC, ushort firstSequence, ushort count)
    {
        var packet = new byte[16];
        packet[0] = (byte)SessionCommand.RetransmitRequest;
        packet[1] = (byte)CommandStatus.Command;
        packet[2] = (byte)((ssrc >> 24) & 0xFF);
        packet[3] = (byte)((ssrc >> 16) & 0xFF);
        packet[4] = (byte)((ssrc >> 8) & 0xFF);
        packet[5] = (byte)(ssrc & 0xFF);
        packet[6] = (byte)((remoteSSRC >> 24) & 0xFF);
        packet[7] = (byte)((remoteSSRC >> 16) & 0xFF);
        packet[8] = (byte)((remoteSSRC >> 8) & 0xFF);
        packet[9] = (byte)(remoteSSRC & 0xFF);
        packet[10] = (byte)((firstSequence >> 8) & 0xFF);
        packet[11] = (byte)(firstSequence & 0xFF);
        packet[12] = (byte)((count >> 8) & 0xFF);
        packet[13] = (byte)(count & 0xFF);
        return packet;
    }

    public static byte[] CreateUMPDataPacket(ushort sequenceNumber, byte[] umpData)
    {
        var packet = new byte[8 + umpData.Length];
        packet[0] = (byte)SessionCommand.UMPData;
        packet[1] = (byte)((sequenceNumber >> 8) & 0xFF);
        packet[2] = (byte)(sequenceNumber & 0xFF);
        packet[3] = 0;
        Buffer.BlockCopy(umpData, 0, packet, 8, umpData.Length);
        return packet;
    }

    public static bool ParsePacket(byte[] data, out SessionCommand command, out CommandStatus status, out uint ssrc, out uint remoteSSRC)
    {
        command = default;
        status = default;
        ssrc = 0;
        remoteSSRC = 0;

        if (data == null || data.Length < 12) return false;

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
        ssrc = (uint)((data[2] << 24) | (data[3] << 16) | (data[4] << 8) | data[5]);
        
        if (data.Length >= 10)
        {
            remoteSSRC = (uint)((data[6] << 24) | (data[7] << 16) | (data[8] << 8) | data[9]);
        }

        return true;
    }

    public static bool ParseInvitation(byte[] data, out string name, out string productInstanceId, out PeerCapabilities capabilities)
    {
        name = "";
        productInstanceId = "";
        capabilities = PeerCapabilities.None;

        if (data == null || data.Length < 16) return false;

        int offset = 16;

        if (offset + 2 > data.Length) return true;
        int nameLength = (data[offset] << 8) | data[offset + 1];
        offset += 2;

        if (offset + nameLength > data.Length) return false;

        name = Encoding.UTF8.GetString(data, offset, nameLength);
        offset += nameLength;

        if (offset + 2 > data.Length) return true;
        int productLength = (data[offset] << 8) | data[offset + 1];
        offset += 2;

        if (offset + productLength <= data.Length && productLength > 0)
        {
            productInstanceId = Encoding.UTF8.GetString(data, offset, productLength);
            offset += productLength;
        }

        if (offset + 2 <= data.Length)
        {
            capabilities = (PeerCapabilities)((data[offset] << 8) | data[offset + 1]);
        }

        return true;
    }

    public static bool ParseRetransmitRequest(byte[] data, out ushort firstSequence, out ushort count)
    {
        firstSequence = 0;
        count = 0;

        if (data == null || data.Length < 14) return false;
        if ((SessionCommand)data[0] != SessionCommand.RetransmitRequest) return false;

        firstSequence = (ushort)((data[10] << 8) | data[11]);
        count = (ushort)((data[12] << 8) | data[13]);
        return true;
    }

    public static bool ParseUMPData(byte[] data, out ushort sequenceNumber, out byte[] umpData)
    {
        sequenceNumber = 0;
        umpData = Array.Empty<byte>();

        if (data == null || data.Length < 12) return false;
        if (data[0] != 0x10) return false;

        sequenceNumber = (ushort)((data[1] << 8) | data[2]);
        umpData = new byte[data.Length - 8];
        Buffer.BlockCopy(data, 8, umpData, 0, umpData.Length);

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