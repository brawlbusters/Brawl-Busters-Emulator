using BrawlBusters.Core.Network;

namespace BrawlBusters.Core.Protocol.Packets;

public enum UserMsgRequest : byte
{
    RoomChat = 5,
}

public static class UserMsgPacket
{
    public static PacketWriter RoomChat(uint senderId, string text)
        => new PacketWriter(MsgCategory.sUserMsg, 0).WriteUInt32(senderId).WriteWideString(text).WriteUInt16(0);

    public static PacketWriter SystemMessage(string text)
        => new PacketWriter(MsgCategory.sUserMsg, 4).WriteWideString(text);
}
