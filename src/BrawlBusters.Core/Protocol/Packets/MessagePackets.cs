using BrawlBusters.Core.Network;

namespace BrawlBusters.Core.Protocol.Packets;

public enum UserMsgRequest : byte
{
    RoomChat = 5,
}

public static class UserMsgPacket
{
    public const byte ChatToAll = 0;
    public const byte ChatToTeam = 1;

    /// <summary>
    /// sUserMsg 00 (client 0x5CC8BD): `u32 sender, wstr text, u8 kind` - kind 0 is a line for the whole room, 1 a line
    /// for the sender's team (the client shows it as team chat). The recorded packet has one more zero byte after it.
    /// </summary>
    public static PacketWriter RoomChat(uint senderId, string text, byte kind = ChatToAll)
        => new PacketWriter(MsgCategory.sUserMsg, 0).WriteUInt32(senderId).WriteWideString(text).WriteByte(kind).WriteByte(0);

    public static PacketWriter SystemMessage(string text)
        => new PacketWriter(MsgCategory.sUserMsg, 4).WriteWideString(text);
}
