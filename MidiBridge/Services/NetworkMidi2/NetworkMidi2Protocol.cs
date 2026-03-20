using System.Text;

namespace MidiBridge.Services.NetworkMidi2;

public static class NetworkMidi2Protocol
{
    public const uint SIGNATURE = 0x4D494449;
    public const string MDNS_SERVICE_TYPE = "_midi2._udp.local";
    public const string DEFAULT_SERVICE_NAME = "MidiBridge";
    public const int DEFAULT_PORT = 5506;
    public const int MAX_UDP_PACKET_SIZE = 1400;
    public const int MAX_PAYLOAD_WORDS = 64;
    public const int SESSION_TIMEOUT_MS = 30000;
    public const int INVITATION_RETRY_COUNT = 3;
    public const int INVITATION_RETRY_INTERVAL_MS = 500;
    public const int PING_INTERVAL_MS = 10000;
    public const int PING_TIMEOUT_COUNT = 5;

    public const int FEC_REDUNDANCY = 2;
    public const int IDLE_FIRST_INTERVAL_MS = 300;
    public const int IDLE_MAX_INTERVAL_MS = 5000;
    public const int RETRANSMIT_DELAY_MS = 10;
    public const int RETRANSMIT_MAX_RETRY = 5;
    public const int RETRANSMIT_BUFFER_SIZE = 64;

    public const int COMMAND_RETRY_INTERVAL_MS = 100;
    public const int COMMAND_MAX_RETRY = 5;
    public const int MAX_SESSIONS = 16;
    public const int PENDING_INVITATION_TIMEOUT_MS = 30000;

    public enum CommandCode : byte
    {
        UMPData = 0xFF,
        Invitation = 0x01,
        InvitationWithAuth = 0x02,
        InvitationWithUserAuth = 0x03,
        InvitationReplyAccepted = 0x10,
        InvitationReplyPending = 0x11,
        InvitationReplyAuthRequired = 0x12,
        InvitationReplyUserAuthRequired = 0x13,
        Ping = 0x20,
        PingReply = 0x21,
        RetransmitRequest = 0x80,
        RetransmitError = 0x81,
        SessionReset = 0x82,
        SessionResetReply = 0x83,
        NAK = 0x8F,
        Bye = 0xF0,
        ByeReply = 0xF1,
    }

    public enum ByeReason : byte
    {
        Unknown = 0x00,
        UserTerminated = 0x01,
        PowerDown = 0x02,
        TooManyMissingPackets = 0x03,
        Timeout = 0x04,
        SessionNotEstablished = 0x05,
        NoPendingSession = 0x06,
        ProtocolError = 0x07,
        InvitationFailedTooManySessions = 0x40,
        InvitationAuthRejected = 0x41,
        InvitationRejectedByUser = 0x42,
        AuthenticationFailed = 0x43,
        UsernameNotFound = 0x44,
        NoMatchingAuthMethod = 0x45,
        InvitationCanceled = 0x80,
    }

    public enum NAKReason : byte
    {
        Other = 0x00,
        CommandNotSupported = 0x01,
        CommandNotExpected = 0x02,
        CommandMalformed = 0x03,
        BadPingReply = 0x20,
    }

    public enum RetransmitErrorReason : byte
    {
        Unknown = 0x00,
        BufferDoesNotContainSequence = 0x01,
    }

    public enum AuthenticationState : byte
    {
        FirstAuthRequest = 0x00,
        AuthDigestIncorrect = 0x01,
        UsernameNotFound = 0x02,
    }

    [Flags]
    public enum InvitationCapabilities : byte
    {
        None = 0x00,
        SupportsAuth = 0x01,
        SupportsUserAuth = 0x02,
        All = SupportsAuth | SupportsUserAuth,
    }

    public enum SessionState
    {
        Idle,
        PendingInvitation,
        AuthenticationRequired,
        Established,
        PendingSessionReset,
        PendingBye,
    }

    public struct SessionInfo
    {
        public string Id;
        public string RemoteName;
        public string RemoteHost;
        public int RemotePort;
        public SessionState State;
        public DateTime LastActivity;
        public DateTime LastPingSent;
        public DateTime LastPingReceived;
        public int PendingPingCount;
        public ushort SendSequence;
        public ushort ReceiveSequence;
        public List<byte[]> RetransmitBuffer;
        public InvitationCapabilities RemoteCapabilities;
        public int PacketsSent;
        public int PacketsReceived;
        public int PacketsLost;
        public int PacketsDuplicate;
        public int PacketsRecovered;

        public DateTime LastDataSent;
        public bool IsIdle;
        public int IdleIntervalMs;
        public List<ushort> MissingSequences;
        public DateTime LastRetransmitRequest;
        public int RetransmitRetryCount;

        public int PendingRetryCount;
        public DateTime PendingCommandSent;
        public bool NeedsAllNotesOff;
        public string CryptoNonce;
        public bool SupportsRetransmit;

        public int AuthFailCount;
        public DateTime LastAuthFail;
        public int AuthDelayMs;
        public string PendingUsername;
    }

    public class AuthCredentials
    {
        public string SharedSecret { get; set; } = "";
        public Dictionary<string, string> Users { get; set; } = new();
    }

    public class DiscoveredDevice
    {
        public string ServiceInstanceName { get; set; } = "";
        public string UMPEndpointName { get; set; } = "";
        public string Name { get => UMPEndpointName; set => UMPEndpointName = value; }
        public string Host { get; set; } = "";
        public int Port { get; set; }
        public string ProductInstanceId { get; set; } = "";
        public DateTime DiscoveredTime { get; set; }
    }

    public static byte[] CreateUDPPacket(params byte[][] commandPackets)
    {
        int totalLength = 4;
        foreach (var cmd in commandPackets)
            totalLength += cmd.Length;

        var packet = new byte[totalLength];
        int offset = 0;

        packet[offset++] = (byte)((SIGNATURE >> 24) & 0xFF);
        packet[offset++] = (byte)((SIGNATURE >> 16) & 0xFF);
        packet[offset++] = (byte)((SIGNATURE >> 8) & 0xFF);
        packet[offset++] = (byte)(SIGNATURE & 0xFF);

        foreach (var cmd in commandPackets)
        {
            Buffer.BlockCopy(cmd, 0, packet, offset, cmd.Length);
            offset += cmd.Length;
        }

        return packet;
    }

    public static bool ParseUDPPacket(byte[] data, out List<byte[]> commandPackets)
    {
        commandPackets = new List<byte[]>();

        if (data == null || data.Length < 8) return false;

        uint signature = (uint)((data[0] << 24) | (data[1] << 16) | (data[2] << 8) | data[3]);
        if (signature != SIGNATURE) return false;

        int offset = 4;
        while (offset < data.Length)
        {
            if (offset + 4 > data.Length) break;

            byte cmdCode = data[offset];
            byte payloadLen = data[offset + 1];
            int totalCmdLen = 4 + payloadLen * 4;

            if (offset + totalCmdLen > data.Length) break;

            var cmdPacket = new byte[totalCmdLen];
            Buffer.BlockCopy(data, offset, cmdPacket, 0, totalCmdLen);
            commandPackets.Add(cmdPacket);

            offset += totalCmdLen;
        }

        return commandPackets.Count > 0;
    }

    public static byte[] CreateInvitationCommand(string umpEndpointName, string productInstanceId, InvitationCapabilities capabilities)
    {
        var nameBytes = Encoding.UTF8.GetBytes(umpEndpointName);
        var productBytes = Encoding.ASCII.GetBytes(productInstanceId);

        int nameWords = (nameBytes.Length + 3) / 4;
        int productWords = (productBytes.Length + 3) / 4;
        int payloadWords = nameWords + productWords;

        var packet = new byte[4 + payloadWords * 4];
        int offset = 0;

        packet[offset++] = (byte)CommandCode.Invitation;
        packet[offset++] = (byte)payloadWords;
        packet[offset++] = (byte)nameWords;
        packet[offset++] = (byte)capabilities;

        Buffer.BlockCopy(nameBytes, 0, packet, offset, nameBytes.Length);
        offset += nameWords * 4;

        if (productBytes.Length > 0)
        {
            Buffer.BlockCopy(productBytes, 0, packet, offset, productBytes.Length);
        }

        return packet;
    }

    public static byte[] CreateInvitationReplyAccepted(string umpEndpointName, string productInstanceId)
    {
        var nameBytes = Encoding.UTF8.GetBytes(umpEndpointName);
        var productBytes = Encoding.ASCII.GetBytes(productInstanceId);

        int nameWords = (nameBytes.Length + 3) / 4;
        int productWords = (productBytes.Length + 3) / 4;
        int payloadWords = nameWords + productWords;

        var packet = new byte[4 + payloadWords * 4];
        int offset = 0;

        packet[offset++] = (byte)CommandCode.InvitationReplyAccepted;
        packet[offset++] = (byte)payloadWords;
        packet[offset++] = (byte)nameWords;
        packet[offset++] = 0;

        Buffer.BlockCopy(nameBytes, 0, packet, offset, nameBytes.Length);
        offset += nameWords * 4;

        if (productBytes.Length > 0)
        {
            Buffer.BlockCopy(productBytes, 0, packet, offset, productBytes.Length);
        }

        return packet;
    }

    public static byte[] CreateInvitationReplyPending(string umpEndpointName, string productInstanceId)
    {
        var nameBytes = Encoding.UTF8.GetBytes(umpEndpointName);
        var productBytes = Encoding.ASCII.GetBytes(productInstanceId);

        int nameWords = (nameBytes.Length + 3) / 4;
        int productWords = (productBytes.Length + 3) / 4;
        int payloadWords = nameWords + productWords;

        var packet = new byte[4 + payloadWords * 4];
        int offset = 0;

        packet[offset++] = (byte)CommandCode.InvitationReplyPending;
        packet[offset++] = (byte)payloadWords;
        packet[offset++] = (byte)nameWords;
        packet[offset++] = 0;

        Buffer.BlockCopy(nameBytes, 0, packet, offset, nameBytes.Length);
        offset += nameWords * 4;

        if (productBytes.Length > 0)
        {
            Buffer.BlockCopy(productBytes, 0, packet, offset, productBytes.Length);
        }

        return packet;
    }

    public static byte[] CreateInvitationReplyAuthRequired(string cryptoNonce, string umpEndpointName, string productInstanceId, AuthenticationState authState = AuthenticationState.FirstAuthRequest)
    {
        var nameBytes = Encoding.UTF8.GetBytes(umpEndpointName);
        var productBytes = Encoding.ASCII.GetBytes(productInstanceId);
        var nonceBytes = Encoding.ASCII.GetBytes(cryptoNonce.PadRight(16).Substring(0, 16));

        int nameWords = (nameBytes.Length + 3) / 4;
        int productWords = (productBytes.Length + 3) / 4;
        int payloadWords = 4 + nameWords + productWords;

        var packet = new byte[4 + payloadWords * 4];
        int offset = 0;

        packet[offset++] = (byte)CommandCode.InvitationReplyAuthRequired;
        packet[offset++] = (byte)payloadWords;
        packet[offset++] = (byte)nameWords;
        packet[offset++] = (byte)authState;

        Buffer.BlockCopy(nonceBytes, 0, packet, offset, 16);
        offset += 16;

        Buffer.BlockCopy(nameBytes, 0, packet, offset, nameBytes.Length);
        offset += nameWords * 4;

        if (productBytes.Length > 0)
        {
            Buffer.BlockCopy(productBytes, 0, packet, offset, productBytes.Length);
        }

        return packet;
    }

    public static byte[] CreateInvitationReplyUserAuthRequired(string cryptoNonce, string umpEndpointName, string productInstanceId, AuthenticationState authState = AuthenticationState.FirstAuthRequest)
    {
        var nameBytes = Encoding.UTF8.GetBytes(umpEndpointName);
        var productBytes = Encoding.ASCII.GetBytes(productInstanceId);
        var nonceBytes = Encoding.ASCII.GetBytes(cryptoNonce.PadRight(16).Substring(0, 16));

        int nameWords = (nameBytes.Length + 3) / 4;
        int productWords = (productBytes.Length + 3) / 4;
        int payloadWords = 4 + nameWords + productWords;

        var packet = new byte[4 + payloadWords * 4];
        int offset = 0;

        packet[offset++] = (byte)CommandCode.InvitationReplyUserAuthRequired;
        packet[offset++] = (byte)payloadWords;
        packet[offset++] = (byte)nameWords;
        packet[offset++] = (byte)authState;

        Buffer.BlockCopy(nonceBytes, 0, packet, offset, 16);
        offset += 16;

        Buffer.BlockCopy(nameBytes, 0, packet, offset, nameBytes.Length);
        offset += nameWords * 4;

        if (productBytes.Length > 0)
        {
            Buffer.BlockCopy(productBytes, 0, packet, offset, productBytes.Length);
        }

        return packet;
    }

    public static byte[] CreateInvitationWithAuth(byte[] authDigest)
    {
        var packet = new byte[36];
        packet[0] = (byte)CommandCode.InvitationWithAuth;
        packet[1] = 8;
        packet[2] = 0;
        packet[3] = 0;

        Buffer.BlockCopy(authDigest, 0, packet, 4, 32);

        return packet;
    }

    public static byte[] CreateInvitationWithUserAuth(byte[] authDigest, string username)
    {
        var usernameBytes = Encoding.UTF8.GetBytes(username);
        int usernameWords = (usernameBytes.Length + 3) / 4;
        int payloadWords = 8 + usernameWords;

        var packet = new byte[4 + payloadWords * 4];
        int offset = 0;

        packet[offset++] = (byte)CommandCode.InvitationWithUserAuth;
        packet[offset++] = (byte)payloadWords;
        packet[offset++] = 0;
        packet[offset++] = 0;

        Buffer.BlockCopy(authDigest, 0, packet, offset, 32);
        offset += 32;

        if (usernameBytes.Length > 0)
        {
            Buffer.BlockCopy(usernameBytes, 0, packet, offset, usernameBytes.Length);
        }

        return packet;
    }

    public static byte[] ComputeAuthDigest(string cryptoNonce, string sharedSecret)
    {
        var data = cryptoNonce + sharedSecret;
        using var sha256 = System.Security.Cryptography.SHA256.Create();
        return sha256.ComputeHash(Encoding.UTF8.GetBytes(data));
    }

    public static byte[] ComputeUserAuthDigest(string cryptoNonce, string username, string password)
    {
        var data = cryptoNonce + username + password;
        using var sha256 = System.Security.Cryptography.SHA256.Create();
        return sha256.ComputeHash(Encoding.UTF8.GetBytes(data));
    }

    public static string GenerateCryptoNonce()
    {
        const string chars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789!@#$%^&*()_+-=[]{}|;:,.<>?";
        var random = Random.Shared;
        var nonce = new char[16];
        for (int i = 0; i < 16; i++)
        {
            nonce[i] = chars[random.Next(chars.Length)];
        }
        return new string(nonce);
    }

    public static bool ParseInvitationWithAuth(byte[] payload, out byte[] authDigest)
    {
        authDigest = Array.Empty<byte>();

        if (payload == null || payload.Length < 36) return false;

        authDigest = new byte[32];
        Buffer.BlockCopy(payload, 4, authDigest, 0, 32);
        return true;
    }

    public static bool ParseInvitationWithUserAuth(byte[] payload, out byte[] authDigest, out string username)
    {
        authDigest = Array.Empty<byte>();
        username = "";

        if (payload == null || payload.Length < 36) return false;

        authDigest = new byte[32];
        Buffer.BlockCopy(payload, 4, authDigest, 0, 32);

        if (payload.Length > 36)
        {
            int usernameLen = payload.Length - 36;
            username = Encoding.UTF8.GetString(payload, 36, usernameLen).TrimEnd('\0');
        }

        return true;
    }

    public static bool ParseInvitationReplyAuthRequired(byte[] payload, byte nameWords, out string cryptoNonce, out string umpEndpointName, out string productInstanceId, out AuthenticationState authState)
    {
        cryptoNonce = "";
        umpEndpointName = "";
        productInstanceId = "";
        authState = AuthenticationState.FirstAuthRequest;

        if (payload == null || payload.Length < 20) return false;

        authState = (AuthenticationState)payload[3];
        cryptoNonce = Encoding.ASCII.GetString(payload, 4, 16).TrimEnd('\0');

        int nameLen = nameWords * 4;
        if (payload.Length < 20 + nameLen) return false;

        umpEndpointName = TrimString(Encoding.UTF8.GetString(payload, 20, nameLen));

        int productOffset = 20 + nameLen;
        int productLen = payload.Length - productOffset;
        if (productLen > 0)
        {
            productInstanceId = TrimString(Encoding.ASCII.GetString(payload, productOffset, productLen));
        }

        return true;
    }

    public static byte[] CreatePingCommand(uint pingId)
    {
        var packet = new byte[8];
        packet[0] = (byte)CommandCode.Ping;
        packet[1] = 1;
        packet[2] = 0;
        packet[3] = 0;
        packet[4] = (byte)((pingId >> 24) & 0xFF);
        packet[5] = (byte)((pingId >> 16) & 0xFF);
        packet[6] = (byte)((pingId >> 8) & 0xFF);
        packet[7] = (byte)(pingId & 0xFF);
        return packet;
    }

    public static byte[] CreatePingReplyCommand(uint pingId)
    {
        var packet = new byte[8];
        packet[0] = (byte)CommandCode.PingReply;
        packet[1] = 1;
        packet[2] = 0;
        packet[3] = 0;
        packet[4] = (byte)((pingId >> 24) & 0xFF);
        packet[5] = (byte)((pingId >> 16) & 0xFF);
        packet[6] = (byte)((pingId >> 8) & 0xFF);
        packet[7] = (byte)(pingId & 0xFF);
        return packet;
    }

    public static byte[] CreateByeCommand(ByeReason reason, string? textMessage = null)
    {
        var textBytes = string.IsNullOrEmpty(textMessage) ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(textMessage);
        int textWords = (textBytes.Length + 3) / 4;

        var packet = new byte[4 + textWords * 4];
        packet[0] = (byte)CommandCode.Bye;
        packet[1] = (byte)textWords;
        packet[2] = (byte)reason;
        packet[3] = 0;

        if (textBytes.Length > 0)
        {
            Buffer.BlockCopy(textBytes, 0, packet, 4, textBytes.Length);
        }

        return packet;
    }

    public static byte[] CreateByeReplyCommand()
    {
        var packet = new byte[4];
        packet[0] = (byte)CommandCode.ByeReply;
        packet[1] = 0;
        packet[2] = 0;
        packet[3] = 0;
        return packet;
    }

    public static byte[] CreateNAKCommand(NAKReason reason, byte[] originalCommandHeader, string? textMessage = null)
    {
        var textBytes = string.IsNullOrEmpty(textMessage) ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(textMessage);
        int textWords = (textBytes.Length + 3) / 4;
        int payloadWords = 1 + textWords;

        var packet = new byte[4 + payloadWords * 4];
        int offset = 0;

        packet[offset++] = (byte)CommandCode.NAK;
        packet[offset++] = (byte)payloadWords;
        packet[offset++] = (byte)reason;
        packet[offset++] = 0;

        if (originalCommandHeader != null && originalCommandHeader.Length >= 4)
        {
            Buffer.BlockCopy(originalCommandHeader, 0, packet, offset, 4);
        }
        offset += 4;

        if (textBytes.Length > 0)
        {
            Buffer.BlockCopy(textBytes, 0, packet, offset, textBytes.Length);
        }

        return packet;
    }

    public static byte[] CreateSessionResetCommand()
    {
        var packet = new byte[4];
        packet[0] = (byte)CommandCode.SessionReset;
        packet[1] = 0;
        packet[2] = 0;
        packet[3] = 0;
        return packet;
    }

    public static byte[] CreateSessionResetReplyCommand()
    {
        var packet = new byte[4];
        packet[0] = (byte)CommandCode.SessionResetReply;
        packet[1] = 0;
        packet[2] = 0;
        packet[3] = 0;
        return packet;
    }

    public static byte[] CreateUMPDataCommand(ushort sequenceNumber, byte[] umpData)
    {
        int payloadWords = (umpData.Length + 3) / 4;
        if (payloadWords > MAX_PAYLOAD_WORDS)
            payloadWords = MAX_PAYLOAD_WORDS;

        var packet = new byte[4 + payloadWords * 4];
        int offset = 0;

        packet[offset++] = (byte)CommandCode.UMPData;
        packet[offset++] = (byte)payloadWords;
        packet[offset++] = (byte)((sequenceNumber >> 8) & 0xFF);
        packet[offset++] = (byte)(sequenceNumber & 0xFF);

        if (umpData.Length > 0)
        {
            Buffer.BlockCopy(umpData, 0, packet, offset, Math.Min(umpData.Length, payloadWords * 4));
        }

        return packet;
    }

    public static byte[] CreateRetransmitRequestCommand(ushort sequenceNumber, ushort numberOfCommands = 0)
    {
        var packet = new byte[8];
        packet[0] = (byte)CommandCode.RetransmitRequest;
        packet[1] = 1;
        packet[2] = (byte)((sequenceNumber >> 8) & 0xFF);
        packet[3] = (byte)(sequenceNumber & 0xFF);
        packet[4] = (byte)((numberOfCommands >> 8) & 0xFF);
        packet[5] = (byte)(numberOfCommands & 0xFF);
        packet[6] = 0;
        packet[7] = 0;
        return packet;
    }

    public static byte[] CreateRetransmitErrorCommand(RetransmitErrorReason reason, ushort sequenceNumber)
    {
        var packet = new byte[8];
        packet[0] = (byte)CommandCode.RetransmitError;
        packet[1] = 1;
        packet[2] = (byte)reason;
        packet[3] = 0;
        packet[4] = (byte)((sequenceNumber >> 8) & 0xFF);
        packet[5] = (byte)(sequenceNumber & 0xFF);
        packet[6] = 0;
        packet[7] = 0;
        return packet;
    }

    public static bool ParseCommandPacket(byte[] data, out CommandCode commandCode, out byte payloadLength, out byte cmdSpecific1, out byte cmdSpecific2, out byte[] payload)
    {
        commandCode = default;
        payloadLength = 0;
        cmdSpecific1 = 0;
        cmdSpecific2 = 0;
        payload = Array.Empty<byte>();

        if (data == null || data.Length < 4) return false;

        commandCode = (CommandCode)data[0];
        payloadLength = data[1];
        cmdSpecific1 = data[2];
        cmdSpecific2 = data[3];

        int payloadBytes = payloadLength * 4;
        if (data.Length >= 4 + payloadBytes)
        {
            payload = new byte[payloadBytes];
            Buffer.BlockCopy(data, 4, payload, 0, payloadBytes);
        }

        return true;
    }

    public static bool ParseInvitationCommand(byte[] payload, int nameWords, out string umpEndpointName, out string productInstanceId, out InvitationCapabilities capabilities)
    {
        umpEndpointName = "";
        productInstanceId = "";
        capabilities = (InvitationCapabilities)0;

        if (payload == null || payload.Length < 4) return false;

        capabilities = (InvitationCapabilities)payload[3];

        int nameLen = nameWords * 4;
        if (payload.Length < 4 + nameLen) return false;

        umpEndpointName = TrimString(Encoding.UTF8.GetString(payload, 4, nameLen));

        int productOffset = 4 + nameLen;
        int productLen = payload.Length - productOffset;
        if (productLen > 0)
        {
            productInstanceId = TrimString(Encoding.ASCII.GetString(payload, productOffset, productLen));
        }

        return true;
    }

    public static bool ParseInvitationReply(byte[] payload, int nameWords, out string umpEndpointName, out string productInstanceId)
    {
        umpEndpointName = "";
        productInstanceId = "";

        if (payload == null || payload.Length < 4) return false;

        int nameLen = nameWords * 4;
        if (payload.Length < 4 + nameLen) return false;

        umpEndpointName = TrimString(Encoding.UTF8.GetString(payload, 4, nameLen));

        int productOffset = 4 + nameLen;
        int productLen = payload.Length - productOffset;
        if (productLen > 0)
        {
            productInstanceId = TrimString(Encoding.ASCII.GetString(payload, productOffset, productLen));
        }

        return true;
    }

    public static bool ParsePingCommand(byte[] payload, out uint pingId)
    {
        pingId = 0;

        if (payload == null || payload.Length < 8) return false;

        pingId = (uint)((payload[4] << 24) | (payload[5] << 16) | (payload[6] << 8) | payload[7]);
        return true;
    }

    public static bool ParseUMPDataCommand(byte cmdSpecific1, byte cmdSpecific2, byte[] payload, out ushort sequenceNumber, out byte[] umpData)
    {
        sequenceNumber = (ushort)((cmdSpecific1 << 8) | cmdSpecific2);
        umpData = payload;
        return true;
    }

    public static bool ParseRetransmitRequest(byte[] payload, out ushort sequenceNumber, out ushort numberOfCommands)
    {
        sequenceNumber = 0;
        numberOfCommands = 0;

        if (payload == null || payload.Length < 4) return false;

        sequenceNumber = (ushort)((payload[0] << 8) | payload[1]);
        numberOfCommands = (ushort)((payload[2] << 8) | payload[3]);
        return true;
    }

    public static bool ParseByeCommand(byte[] payload, out ByeReason reason, out string textMessage)
    {
        reason = ByeReason.Unknown;
        textMessage = "";

        if (payload == null || payload.Length < 4) return false;

        reason = (ByeReason)payload[2];

        if (payload.Length > 4)
        {
            textMessage = TrimString(Encoding.UTF8.GetString(payload, 4, payload.Length - 4));
        }

        return true;
    }

    public static bool ParseNAKCommand(byte[] payload, out NAKReason reason, out byte[] originalHeader, out string textMessage)
    {
        reason = NAKReason.Other;
        originalHeader = Array.Empty<byte>();
        textMessage = "";

        if (payload == null || payload.Length < 8) return false;

        reason = (NAKReason)payload[2];
        originalHeader = new byte[4];
        Buffer.BlockCopy(payload, 4, originalHeader, 0, 4);

        if (payload.Length > 8)
        {
            textMessage = TrimString(Encoding.UTF8.GetString(payload, 8, payload.Length - 8));
        }

        return true;
    }

    private static string TrimString(string s)
    {
        int nullIndex = s.IndexOf('\0');
        if (nullIndex >= 0)
            return s.Substring(0, nullIndex).TrimEnd();
        return s.TrimEnd();
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