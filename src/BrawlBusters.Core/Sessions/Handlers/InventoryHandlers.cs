using BrawlBusters.Core.Data;
using BrawlBusters.Core.Logging;
using BrawlBusters.Core.Network;
using BrawlBusters.Core.Protocol;
using BrawlBusters.Core.Protocol.Packets;

namespace BrawlBusters.Core.Sessions.Handlers;

public sealed class InventoryHandler : IMessageHandler
{
    public MsgCategory Category => MsgCategory.cInventory;

    public Task HandleAsync(ClientSession session, PacketReader reader, CancellationToken cancellationToken)
    {
        var request = (InventoryRequest)reader.ReadByte();
        if (request is not (InventoryRequest.Equip or InventoryRequest.Unequip or InventoryRequest.CheckNickname) && session.IsItemRequestTooFast())
        {
            Log.Info(session.Tag, $"cInventory 0x{(byte)request:X2} refused: sent too soon after the last request");
            reader.ReadToEnd();
            return session.SendAsync(InventoryPacket.Failed(NetError.Inventory_FastRequest), cancellationToken);
        }

        switch (request)
        {
            case InventoryRequest.Equip:
                return EquipAsync(session, reader.ReadUInt16(), wear: true, cancellationToken);
            case InventoryRequest.Unequip:
                return EquipAsync(session, reader.ReadUInt16(), wear: false, cancellationToken);
            case InventoryRequest.OpenPackage:
                return OpenAsync(session, reader.ReadUInt16(), keySlot: 0, cancellationToken);
            case InventoryRequest.OpenWithKey:
                return OpenAsync(session, reader.ReadUInt16(), reader.ReadUInt16(), cancellationToken);
            case InventoryRequest.Sell:
                return InventoryActions.SellAsync(session, reader.ReadUInt16(), cancellationToken);
            case InventoryRequest.Reinforce:
                return InventoryActions.ReinforceAsync(session, reader.ReadUInt16(), reader.ReadUInt16(), cancellationToken);
            case InventoryRequest.ReinforceInsured:
            {
                // cInventory 12 (client 0x5AA060): u16 item, u16 stone, u8, u8 - the two "prevent" buttons of the reinforce box.
                ushort item = reader.ReadUInt16();
                ushort stone = reader.ReadUInt16();
                bool keepLevel = !reader.EndOfData && reader.ReadByte() != 0;
                bool keepItem = !reader.EndOfData && reader.ReadByte() != 0;
                return InventoryActions.ReinforceAsync(session, item, stone, cancellationToken, keepLevel, keepItem);
            }
            case InventoryRequest.Convert:
                return InventoryActions.ConvertAsync(session, reader.ReadUInt16(), reader.ReadUInt16(), cancellationToken);
            case InventoryRequest.Extend:
            {
                ushort slot = reader.ReadUInt16();
                byte option = reader.ReadByte();
                byte payment = reader.ReadByte();
                return InventoryActions.ExtendAsync(session, slot, option, payment, cancellationToken);
            }
            case InventoryRequest.CheckNickname:
                return InventoryActions.CheckNicknameAsync(session, reader.ReadWideString(), cancellationToken);
            case InventoryRequest.Rename:
            {
                ushort slot = reader.ReadUInt16();
                return InventoryActions.RenameAsync(session, slot, reader.ReadWideString(), cancellationToken);
            }
            case InventoryRequest.Use:
                return InventoryActions.UseAsync(session, reader.ReadUInt16(), cancellationToken);
            default:
                Log.Warn(session.Tag, $"cInventory 0x{(byte)request:X2} (not implemented): {Log.Hex(reader.ReadToEnd())}");
                return Task.CompletedTask;
        }
    }

    private static async Task EquipAsync(ClientSession session, ushort slot, bool wear, CancellationToken cancellationToken)
    {
        int classIndex = Loadout.ClassIndex(session.Account.Character?.Class ?? 1);
        NetError? reason = null;
        string? refusal = null;
        bool activated = false;

        session.Accounts.Update(session.Account.Id, account =>
        {
            ushort[] equipped = account.EquippedOf(classIndex);
            InventoryItem? item = account.Items.FirstOrDefault(candidate => candidate.Slot == slot);
            if (item is null) { refusal = "no item in that slot"; return; }

            if (!wear)
            {
                for (int type = 0; type < equipped.Length; type++)
                    if (equipped[type] == slot) equipped[type] = 0;
                return;
            }

            if (!Loadout.IsWearable(item.Type)) { refusal = $"item type {item.Type} cannot be worn"; return; }
            if (!GameData.Instance.LevelAllows(item.ItemId, account.DisplayLevel))
            {
                refusal = "the item needs a higher level";
                reason = NetError.Inventory_EquipInactiveItem;
                return;
            }
            if (item.HasExpired) { refusal = "the item has expired"; return; }
            if (GameData.Instance.Items.TryGetValue(item.ItemId, out ItemInfo info) && info.Class != 0 && info.Class != classIndex + 1)
            {
                refusal = $"item is for class {info.Class}";
                return;
            }

            if (item.Type is ItemType.Upper or ItemType.UpperAlt)
                equipped[ItemType.Upper] = equipped[ItemType.UpperAlt] = 0;
            equipped[item.Type] = slot;
            if (item.State == 0)
            {
                item.State = 1;
                activated = true;
            }
        });
        session.RefreshAccount();
        if (activated)
        {
            Log.Info(session.Tag, $"Item in slot {slot} activated");
            await session.SendAsync(NoticePacket.ItemActivated(slot), cancellationToken);
        }

        if (refusal is not null)
        {
            Log.Warn(session.Tag, $"{(wear ? "Equip" : "Unequip")} slot {slot} refused: {refusal}");
            await session.SendAsync(ErrorPacket.Show(reason ?? (wear ? NetError.Item_Equip : NetError.Item_UnEquip)), cancellationToken);
        }
        else
            Log.Info(session.Tag, $"{(wear ? "Equipped" : "Took off")} the item in slot {slot} (class {classIndex + 1})");

        Account current = session.Account;
        await session.SendAsync(
            UserInfoPacket.Equipment(classIndex, Loadout.ClassSlot(current, classIndex), GameFlow.AllEquippedTables(current)),
            cancellationToken);
    }

    private static async Task OpenAsync(ClientSession session, ushort slot, ushort keySlot, CancellationToken cancellationToken)
    {
        GameData data = GameData.Instance;
        var changed = new List<InventoryItem>();
        string? refusal = null;
        bool jackpot = false;

        session.Accounts.Update(session.Account.Id, account =>
        {
            InventoryItem? box = account.Items.FirstOrDefault(candidate => candidate.Slot == slot);
            if (box is null || !data.Packages.Packages.TryGetValue(box.ItemId, out PackageInfo? package))
            {
                refusal = "not a package";
                return;
            }

            InventoryItem? key = null;
            if (package.Key != 0)
            {
                key = account.Items.FirstOrDefault(candidate => candidate.ItemId == package.Key && (keySlot == 0 || candidate.Slot == keySlot));
                if (key is null) { refusal = $"key {package.Key} missing"; return; }
            }

            List<FixedItem> contents = Contents(data, package, out jackpot);
            if (contents.Count == 0) { refusal = "package has no contents"; return; }

            bool boxUsedUp = Consume(account, box);
            if (key is not null && !Consume(account, key)) changed.Add(key);
            if (!boxUsedUp) changed.Add(box);

            uint now = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            for (int i = 0; i < contents.Count; i++)
            {
                FixedItem content = contents[i];
                var item = new InventoryItem
                {
                    Slot = i == 0 && boxUsedUp ? slot : account.FreeSlot(),
                    ItemId = content.ItemId,
                    Type = content.Type,
                    Options = (ushort[])content.Options.Clone(),
                    Quantity = content.Count,
                    State = content.State,
                    Expiry = content.Expire > 0 ? now + (uint)content.Expire : uint.MaxValue,
                };
                account.Items.Add(item);
                changed.Add(item);
            }
        });
        session.RefreshAccount();

        if (refusal is not null)
        {
            Log.Warn(session.Tag, $"Opening slot {slot} refused: {refusal}");
            await session.SendAsync(InventoryPacket.PackageOpened(false), cancellationToken);
            return;
        }

        Log.Info(session.Tag, $"Opened the package in slot {slot}: {string.Join(", ", changed.Select(item => item.ItemId))}");

        await session.SendAsync(InventoryPacket.Added(changed.OrderBy(item => item.Slot).ToList()), cancellationToken);
        await session.SendAsync(InventoryPacket.PackageOpened(true), cancellationToken);
        await session.SendAsync(InventoryPacket.List(session.Account.Items), cancellationToken);

        if (jackpot)
        {
            Log.Info(session.Tag, "Lucky box jackpot: a Jackpot ticket was added");
            await SessionRegistry.BroadcastAsync(() => UserMsgPacket.SystemMessage($"JACKPOT! {session.Account.Nickname} hit the jackpot in a lucky box!"));
        }
    }

    private static List<FixedItem> Contents(GameData data, PackageInfo package, out bool jackpot)
    {
        jackpot = false;
        var all = new List<FixedItem>();
        if (package.IsLuckyBox)
        {
            int index = Dice.Weighted(package.Prob);
            if (index < package.Fixed.Count && data.Packages.Fixed.TryGetValue(package.Fixed[index], out FixedItem? won))
                all.Add(won);
            if (all.Count > 0 && index < package.Jackpot.Count && package.Jackpot[index] != 0
                && data.Packages.Fixed.TryGetValue((uint)package.Jackpot[index], out FixedItem? ticket))
            {
                all.Add(ticket);
                jackpot = true;
            }
            return all;
        }

        foreach (uint id in package.Fixed)
            if (data.Packages.Fixed.TryGetValue(id, out FixedItem? content)) all.Add(content);
        return all;
    }

    private static bool Consume(Account account, InventoryItem item)
    {
        if (item.Quantity > 1)
        {
            item.Quantity--;
            return false;
        }

        account.Items.Remove(item);
        foreach (ushort[] equipped in account.Equipped)
            for (int type = 0; type < equipped.Length; type++)
                if (equipped[type] == item.Slot) equipped[type] = 0;
        return true;
    }
}

public sealed class CapsuleMachineHandler : IMessageHandler
{
    private const byte Pull = 0;

    private const byte PayWithGold = 1;

    public MsgCategory Category => MsgCategory.cCapsuleMachine;

    public async Task HandleAsync(ClientSession session, PacketReader reader, CancellationToken cancellationToken)
    {
        byte sub = reader.ReadByte();
        if (sub != Pull)
        {
            Log.Warn(session.Tag, $"cCapsuleMachine 0x{sub:X2} (not implemented): {Log.Hex(reader.ReadToEnd())}");
            return;
        }

        ushort machineId = reader.ReadUInt16();
        int part = reader.ReadByte() - 1;
        bool withGold = reader.ReadByte() == PayWithGold;

        GameData data = GameData.Instance;
        int classIndex = Loadout.ClassIndex(session.Account.Character?.Class ?? 1);

        if (session.IsItemRequestTooFast())
        {
            Log.Info(session.Tag, $"Capsule pull on machine {machineId} refused: sent too soon after the last request");
            await session.SendAsync(CapsulePacket.Error(NetError.Inventory_FastRequest), cancellationToken);
            return;
        }

        if (!data.Capsules.Machines.TryGetValue(machineId, out List<CapsulePart>? parts)
            || part < 0 || part >= parts.Count
            || classIndex >= parts[part].Index.Count
            || !data.Capsules.Items.TryGetValue(parts[part].Index[classIndex], out CapsuleItems? pool)
            || pool.Items.Count == 0)
        {
            Log.Warn(session.Tag, $"Capsule pull refused: machine {machineId}, part {part + 1}, class {classIndex + 1}");
            await session.SendAsync(CapsulePacket.Error(NetError.Inventory_UseGashaponItem), cancellationToken);
            return;
        }

        int price = withGold ? parts[part].Gold : parts[part].Cash;
        int pick = Dice.Weighted(pool.Prob);
        uint itemId = pool.Items[Math.Min(pick, pool.Items.Count - 1)];
        List<uint> optionIds = withGold ? pool.OptGold : pool.OptCash;
        data.Capsules.Options.TryGetValue(pick < optionIds.Count ? optionIds[pick] : 0, out CapsuleOptions? rolls);

        InventoryItem? won = null;
        session.Accounts.Update(session.Account.Id, account =>
        {
            if (price <= 0 || (withGold ? account.Gold : account.Cash) < price) return;
            if (withGold) account.Gold -= price; else account.Cash -= price;

            int lifetime = rolls is null ? -1 : Dice.Pick(rolls.Expire, rolls.ExpireProb, -1);
            won = new InventoryItem
            {
                Slot = account.FreeSlot(),
                ItemId = itemId,
                Type = data.TypeOf(itemId),
                Options = [RollOption(rolls, 0), RollOption(rolls, 1), RollOption(rolls, 2), RollOption(rolls, 3)],
                Quantity = (ushort)Math.Max(1, rolls is null ? 1 : Dice.Pick(rolls.Count, rolls.CountProb, 1)),
                State = rolls?.State ?? 1,
                Expiry = lifetime > 0 ? (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds() + (uint)lifetime : uint.MaxValue,
            };
            account.Items.Add(won);
        });
        session.RefreshAccount();

        if (won is null)
        {
            Log.Info(session.Tag, $"Capsule pull refused: costs {price} {(withGold ? "BP" : "RT")}");
            await session.SendAsync(CapsulePacket.Error(price <= 0 ? NetError.Inventory_UseGashaponItem
                : withGold ? NetError.Store_NoGold : NetError.Store_NoCash), cancellationToken);
            return;
        }

        Log.Info(session.Tag, $"Capsule machine {machineId} part {part + 1}: item {itemId} +opt {won.Option(3)} for {price} {(withGold ? "BP" : "RT")}");

        await session.SendAsync(
            withGold ? UserInfoPacket.Gold((uint)session.Account.Gold) : UserInfoPacket.Cash((uint)session.Account.Cash),
            cancellationToken);
        await session.SendAsync(InventoryPacket.Added([won]), cancellationToken);
        await session.SendAsync(CapsulePacket.Won(won.Slot), cancellationToken);
    }

    private static ushort RollOption(CapsuleOptions? rolls, int index)
    {
        if (rolls is null || index >= rolls.Values.Count) return 0;
        List<int> weights = index < rolls.Prob.Count ? rolls.Prob[index] : [];
        return (ushort)Dice.Pick(rolls.Values[index], weights, 0);
    }
}

internal static class Dice
{
    public static int Weighted(IReadOnlyList<int> weights)
    {
        int total = weights.Where(weight => weight > 0).Sum();
        if (total <= 0) return 0;

        int roll = Random.Shared.Next(total);
        for (int i = 0; i < weights.Count; i++)
        {
            if (weights[i] <= 0) continue;
            if (roll < weights[i]) return i;
            roll -= weights[i];
        }
        return 0;
    }

    public static int Pick(IReadOnlyList<int> values, IReadOnlyList<int> weights, int fallback)
    {
        if (values.Count == 0) return fallback;
        int index = weights.Count == values.Count ? Weighted(weights) : 0;
        return values[index];
    }
}
