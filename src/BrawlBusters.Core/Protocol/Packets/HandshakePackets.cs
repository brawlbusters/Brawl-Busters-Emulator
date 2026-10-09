using System.Text;
using BrawlBusters.Core.Network;
using BrawlBusters.Core.Security;

namespace BrawlBusters.Core.Protocol.Packets;

public static class ServerGreeting
{
    public static byte[] Build(string greeting, uint seed)
    {
        var packet = new byte[ProtocolConstants.GreetingLength + 4];
        Encoding.ASCII.GetBytes(greeting, packet);
        BitConverter.TryWriteBytes(packet.AsSpan(ProtocolConstants.GreetingLength), seed);
        return packet;
    }
}

public sealed class ClientLoginInfo
{
    public ushort PacketVersion { get; private init; }
    public ushort ClientWord { get; private init; }
    public uint ClientDword { get; private init; }
    public string LoginId { get; private init; } = "";
    public bool TokenMode { get; private init; }
    public ulong Token { get; private init; }
    public string Password { get; private init; } = "";
    public string Locale { get; private init; } = "";

    public static ClientLoginInfo Parse(byte[] packet, uint seed)
    {
        var reader = new PacketReader(packet, offset: 1);
        ushort version = reader.ReadUInt16();
        ushort word = reader.ReadUInt16();
        uint dword = reader.ReadUInt32();
        string loginId = reader.ReadString();
        bool tokenMode = reader.ReadBool();

        ulong token = 0;
        string password = "";
        if (tokenMode)
        {
            token = reader.ReadUInt64();
        }
        else
        {
            bool encrypted = reader.ReadBool();
            byte[] raw = reader.ReadBlob();
            password = Encoding.Latin1.GetString(encrypted ? PasswordCipher.Decrypt(seed, raw) : raw);
        }

        return new ClientLoginInfo
        {
            PacketVersion = version,
            ClientWord = word,
            ClientDword = dword,
            LoginId = loginId,
            TokenMode = tokenMode,
            Token = token,
            Password = password,
            Locale = reader.EndOfData ? "" : reader.ReadString(),
        };
    }
}

public sealed class ClientTransferInfo
{
    public ushort PacketVersion { get; private init; }
    public string LoginId { get; private init; } = "";
    public string Nickname { get; private init; } = "";
    public ulong SessionKey { get; private init; }

    /// <summary>The second u16 and the second u32 of the packet (client builder 0x5BEF10); in a server change they repeat what sTransServer said.</summary>
    public ushort Channel { get; private init; }
    public uint Key { get; private init; }

    public static ClientTransferInfo Parse(byte[] packet)
    {
        var reader = new PacketReader(packet, offset: 1);
        ushort version = reader.ReadUInt16();
        ushort channel = reader.ReadUInt16();
        reader.ReadUInt32();
        uint key = reader.ReadUInt32();
        string loginId = reader.ReadString();
        string nickname = reader.ReadWideString();
        reader.ReadByte();

        return new ClientTransferInfo
        {
            PacketVersion = version,
            Channel = channel,
            Key = key,
            LoginId = loginId,
            Nickname = nickname,
            SessionKey = reader.ReadUInt64(),
        };
    }
}

public static class LoginReply
{
    public static byte[] Failed(NetError error, uint blockedSeconds = 0)
    {
        var writer = new PacketWriter(MsgCategory.sLoginFailed).WriteByte((byte)error);
        if (error == NetError.ID_ConnectionBlocked) writer.WriteUInt32(blockedSeconds);
        return writer.ToArray();
    }

    public static byte[] CreateIdFailed(NetError error)
        => new PacketWriter(MsgCategory.sCreateIDFailed).WriteByte((byte)error).ToArray();

    public static byte[] SessionInfo(ulong sessionKey)
        => new PacketWriter(MsgCategory.sSessionInfo)
            .WriteUInt64(sessionKey)
            .WriteString(ProtocolConstants.ClientWriteStream)
            .WriteString(ProtocolConstants.ClientReadStream)
            .ToArray();

    public static byte[] StartSession() => [(byte)MsgCategory.sStartSession];
}
