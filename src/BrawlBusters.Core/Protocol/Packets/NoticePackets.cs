using BrawlBusters.Core.Network;

namespace BrawlBusters.Core.Protocol.Packets;

public static class NoticePacket
{
    private const byte ItemActivate = 0;
    private const byte ItemExpire = 1;
    private const byte ItemDelete = 2;
    private const byte ItemExpireDetail = 3;
    private const byte ItemDeleteDetail = 4;
    private const byte ItemStack = 5;
    private const byte RoomDisappear = 6;

    public static PacketWriter ItemActivated(ushort slot) => Build(ItemActivate, slot);

    public static PacketWriter ItemExpired(ushort slot, bool detail) => Build(detail ? ItemExpireDetail : ItemExpire, slot);

    public static PacketWriter ItemDeleted(ushort slot, bool detail) => Build(detail ? ItemDeleteDetail : ItemDelete, slot);

    public static PacketWriter ItemStacked(ushort slot) => Build(ItemStack, slot);

    public static PacketWriter RoomDisappeared(ushort roomId) => Build(RoomDisappear, roomId);

    private static PacketWriter Build(byte sub, ushort value) => new PacketWriter(MsgCategory.sNotice, sub).WriteUInt16(value);
}

public static class ErrorPacket
{
    public static PacketWriter Show(NetError error) => new PacketWriter(MsgCategory.sError, 0).WriteByte((byte)error);
}
