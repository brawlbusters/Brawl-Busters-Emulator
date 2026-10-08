using BrawlBusters.Core.Data;
using BrawlBusters.Core.Network;

namespace BrawlBusters.Core.Protocol.Packets;

public static class InventoryPacket
{
    public static PacketWriter List(IReadOnlyList<InventoryItem> items)
    {
        ushort nextSlot = (ushort)(items.Count == 0 ? 1 : items.Max(item => item.Slot) + 1);
        var writer = new PacketWriter(MsgCategory.sInventory, 0).WriteUInt16(nextSlot).WriteZeros(20);
        foreach (InventoryItem item in items.OrderBy(item => item.Slot))
        {
            writer.WriteUInt16(item.Slot);
            WriteItem(writer, item);
        }
        return writer;
    }

    public static PacketWriter Added(IReadOnlyList<InventoryItem> items)
    {
        var writer = new PacketWriter(MsgCategory.sInventory, 1).WriteByte((byte)items.Count);
        foreach (InventoryItem item in items)
        {
            writer.WriteByte(1).WriteUInt16(item.Slot).WriteUInt16(item.Slot);
            WriteItem(writer, item);
        }
        return writer;
    }

    public static PacketWriter Sold(bool ok, ushort slot)
    {
        var writer = new PacketWriter(MsgCategory.sInventory, 4).WriteBool(ok);
        return ok ? writer.WriteUInt16(slot).WriteBool(true) : writer;
    }

    public static PacketWriter Used(NetError result) => new PacketWriter(MsgCategory.sInventory, 5).WriteByte((byte)result);

    public static PacketWriter Renamed(NetError result, string nickname)
        => new PacketWriter(MsgCategory.sInventory, 7).WriteByte((byte)result).WriteWideString(nickname);

    public static PacketWriter Reinforced(bool ok, byte outcome, ushort itemSlot, ushort stoneSlot, ushort level)
    {
        var writer = new PacketWriter(MsgCategory.sInventory, 8).WriteBool(ok);
        return ok ? writer.WriteByte(outcome).WriteUInt16(itemSlot).WriteUInt16(stoneSlot).WriteUInt16(level) : writer;
    }

    public static PacketWriter Converted(bool ok, ushort itemSlot, ushort converterSlot, ushort option)
    {
        var writer = new PacketWriter(MsgCategory.sInventory, 9).WriteBool(ok);
        return ok ? writer.WriteUInt16(itemSlot).WriteUInt16(converterSlot).WriteUInt16(option).WriteUInt16(0) : writer;
    }

    public static PacketWriter Extended(bool ok, ushort slot, byte option, uint expiry)
    {
        var writer = new PacketWriter(MsgCategory.sInventory, 0x0A).WriteBool(ok);
        return ok ? writer.WriteUInt16(slot).WriteByte(option).WriteUInt32(expiry) : writer;
    }

    public static PacketWriter NicknameChecked(NetError result) => new PacketWriter(MsgCategory.sInventory, 0x0C).WriteByte((byte)result);

    public static PacketWriter PackageOpened(bool success)
        => new PacketWriter(MsgCategory.sInventory, 3).WriteBool(success);

    private static void WriteItem(PacketWriter writer, InventoryItem item)
    {
        writer.WriteByte(item.Type)
            .WriteUInt32(item.ItemId)
            .WriteUInt16(item.Option(2))
            .WriteUInt16(item.Option(3))
            .WriteUInt16(item.Option(4))
            .WriteUInt16(item.Quantity)
            .WriteByte(item.State)
            .WriteUInt32(item.Expiry);
    }
}

public enum InventoryRequest : byte
{
    Equip = 0x0F,

    Unequip = 0x10,

    OpenPackage = 0x17,

    OpenWithKey = 0x18,

    Sell = 0x11,

    ReinforceInsured = 0x12,

    Reinforce = 0x13,

    Convert = 0x14,

    Extend = 0x15,

    CheckNickname = 0x19,

    Rename = 0x1A,

    Use = 0x1B,
}

public static class CapsulePacket
{
    public static PacketWriter Won(ushort slot) => new PacketWriter(MsgCategory.sCapsuleMachine, 0).WriteByte(1).WriteUInt16(slot);

    public static PacketWriter Refused() => new PacketWriter(MsgCategory.sCapsuleMachine, 0).WriteByte(0).WriteUInt16(0);
}

public enum StoreRequest : byte
{
    Buy = 0x1E,
}

public static class StorePacket
{
    public const byte Success = 0;

    public const byte Failed = 1;

    public static PacketWriter BuyResult(byte result) => new PacketWriter(MsgCategory.sStore, 0x1C).WriteByte(result);
}

public static class RecordsPacket
{
    public static PacketWriter Empty() => new PacketWriter(MsgCategory.sUserRecords, 9).WriteZeros(670);

    public static PacketWriter End() => Result(NetError.Success);

    public static PacketWriter Result(NetError result) => new PacketWriter(MsgCategory.sUserRecords, 0x0C).WriteByte((byte)result);

    public static PacketWriter OfPlayer(string nickname)
        => new PacketWriter(MsgCategory.sUserRecords, 0x0B).WriteWideString(nickname).WriteZeros(670);
}
