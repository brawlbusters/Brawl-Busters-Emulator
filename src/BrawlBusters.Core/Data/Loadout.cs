using System.Buffers.Binary;
using BrawlBusters.Core.Protocol.Packets;

namespace BrawlBusters.Core.Data;

public static class Loadout
{
    public const int ClassCount = 5;
    public const int ClassSlotSize = 62;
    public const int EquippedTableSize = ItemType.EquipTableSize * 2;

    private const int ShapeSize = 10;

    private static int GearOffset(byte type) => type switch
    {
        ItemType.Weapon => 10,
        ItemType.Helmet => 18,
        ItemType.Upper or ItemType.UpperAlt => 24,
        ItemType.Hand => 32,
        ItemType.Lower => 38,
        ItemType.Foot => 46,
        ItemType.Cloak => 52,
        ItemType.Glasses => 54,
        ItemType.Mask => 56,
        ItemType.Decal => 58,
        _ => 0,
    };

    public static bool IsWearable(byte type) => GearOffset(type) != 0;

    public static int ClassIndex(byte characterClass) => Math.Clamp(characterClass - 1, 0, ClassCount - 1);

    public static byte[] ClassSlot(Account account, int classIndex)
    {
        byte[] slot = new byte[ClassSlotSize];
        ushort[] shape = account.Character?.Values ?? [];
        for (int i = 0; i < ShapeSize / 2; i++)
            BinaryPrimitives.WriteUInt16LittleEndian(slot.AsSpan(i * 2), i < shape.Length ? shape[i] : (ushort)0);
        CapturedBlocks.ClassSlotTails[classIndex].CopyTo(slot, ShapeSize);

        ushort[] equipped = account.EquippedOf(classIndex);
        for (byte type = 1; type < ItemType.EquipTableSize; type++)
        {
            if (equipped[type] == 0) continue;
            InventoryItem? item = account.Items.FirstOrDefault(candidate => candidate.Slot == equipped[type]);
            if (item is not null) WriteGear(slot, item);
        }
        return slot;
    }

    public static byte[] ClassSlots(Account account)
    {
        byte[] slots = new byte[ClassCount * ClassSlotSize];
        for (int i = 0; i < ClassCount; i++)
            ClassSlot(account, i).CopyTo(slots, i * ClassSlotSize);
        return slots;
    }

    public static byte[] EquippedTable(Account account, int classIndex)
    {
        byte[] table = new byte[EquippedTableSize];
        ushort[] equipped = account.EquippedOf(classIndex);
        for (int type = 0; type < ItemType.EquipTableSize; type++)
            BinaryPrimitives.WriteUInt16LittleEndian(table.AsSpan(type * 2), equipped[type]);
        return table;
    }

    private static void WriteGear(byte[] slot, InventoryItem item)
    {
        Span<byte> gear = slot.AsSpan(GearOffset(item.Type));
        BinaryPrimitives.WriteUInt16LittleEndian(gear, (ushort)item.ItemId);

        switch (item.Type)
        {
            case ItemType.Weapon:
                GameData.Instance.Items.TryGetValue(item.ItemId, out ItemInfo info);
                BinaryPrimitives.WriteUInt16LittleEndian(gear[2..], item.Option(3));
                BinaryPrimitives.WriteUInt16LittleEndian(gear[4..], item.Option(2) != 0 ? item.Option(2) : info.ConvertR);
                BinaryPrimitives.WriteUInt16LittleEndian(gear[6..], item.Option(4) != 0 ? item.Option(4) : info.ConvertLR);
                break;
            case ItemType.Helmet or ItemType.Hand or ItemType.Foot:
                BinaryPrimitives.WriteUInt16LittleEndian(gear[2..], item.Option(3));
                BinaryPrimitives.WriteUInt16LittleEndian(gear[4..], item.Option(4));
                break;
            case ItemType.Upper or ItemType.UpperAlt or ItemType.Lower:
                BinaryPrimitives.WriteUInt16LittleEndian(gear[4..], item.Option(3));
                BinaryPrimitives.WriteUInt16LittleEndian(gear[6..], item.Option(4));
                break;
        }
    }
}
