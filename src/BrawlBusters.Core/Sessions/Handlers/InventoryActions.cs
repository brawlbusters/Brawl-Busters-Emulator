using BrawlBusters.Core.Data;
using BrawlBusters.Core.Logging;
using BrawlBusters.Core.Protocol;
using BrawlBusters.Core.Protocol.Packets;

namespace BrawlBusters.Core.Sessions.Handlers;

public static class InventoryActions
{
    private const byte OutcomeSuccess = 0;
    private const byte OutcomeMaintain = 1;
    private const byte OutcomeDecrease = 2;
    private const byte OutcomeDestroy = 3;

    private const byte PayWithGold = 1;

    public static async Task SellAsync(ClientSession session, ushort slot, CancellationToken cancellationToken)
    {
        GameData data = GameData.Instance;
        int paid = -1;
        session.Accounts.Update(session.Account.Id, account =>
        {
            InventoryItem? item = Find(account, slot);
            if (item is null || !data.Upgrades.Resale.TryGetValue(item.ItemId, out int price)) return;

            paid = price * Math.Max(1, (int)item.Quantity);
            account.Gold += paid;
            Remove(account, item);
        });
        session.RefreshAccount();

        if (paid < 0)
        {
            Log.Warn(session.Tag, $"Sell slot {slot} refused: no such item or it has no resale price");
            await session.SendAsync(InventoryPacket.Sold(false, slot), cancellationToken);
            return;
        }

        Log.Info(session.Tag, $"Sold the item in slot {slot} for {paid} BP");
        await session.SendAsync(InventoryPacket.Sold(true, slot), cancellationToken);
        await SendBalanceAndItemsAsync(session, cancellationToken);
    }

    public static async Task ReinforceAsync(ClientSession session, ushort itemSlot, ushort stoneSlot, CancellationToken cancellationToken)
    {
        GameData data = GameData.Instance;
        byte outcome = OutcomeMaintain;
        ushort level = 0;
        string? refusal = null;

        session.Accounts.Update(session.Account.Id, account =>
        {
            InventoryItem? item = Find(account, itemSlot);
            InventoryItem? stone = Find(account, stoneSlot);
            UpgradeTable? table = TableFor(data, item, stone, ItemType.WeaponReinforce, ItemType.CostumeReinforce);
            if (item is null || stone is null || table is null) { refusal = "that stone does not work on that item"; return; }

            int index = table.Addon.IndexOf(item.Option(3));
            if (index < 0) index = 0;
            if (index >= table.Addon.Count - 1 || index >= table.Success.Count) { refusal = "already at the highest level"; return; }

            int roll = Dice.Weighted([At(table.Success, index), At(table.Maintain, index), At(table.Decrease, index), At(table.Destroy, index)]);
            Consume(account, stone);
            switch (roll)
            {
                case 0:
                    outcome = OutcomeSuccess;
                    item.Options[2] = (ushort)table.Addon[index + 1];
                    break;
                case 2 when index > 0:
                    outcome = OutcomeDecrease;
                    item.Options[2] = (ushort)table.Addon[index - 1];
                    break;
                case 3:
                    outcome = OutcomeDestroy;
                    Remove(account, item);
                    break;
                default:
                    outcome = OutcomeMaintain;
                    item.Options[2] = (ushort)table.Addon[index];
                    break;
            }
            level = outcome == OutcomeDestroy ? (ushort)0 : item.Option(3);
        });
        session.RefreshAccount();

        if (refusal is not null)
        {
            Log.Warn(session.Tag, $"Reinforce slot {itemSlot} with {stoneSlot} refused: {refusal}");
            await session.SendAsync(InventoryPacket.Reinforced(false, 0, itemSlot, stoneSlot, 0), cancellationToken);
            return;
        }

        string[] names = ["went up", "stayed", "went down", "broke"];
        Log.Info(session.Tag, $"Reinforced slot {itemSlot}: {names[outcome]} (level value {level})");
        await session.SendAsync(InventoryPacket.Reinforced(true, outcome, itemSlot, stoneSlot, level), cancellationToken);
        await SendBalanceAndItemsAsync(session, cancellationToken);
    }

    public static async Task ConvertAsync(ClientSession session, ushort itemSlot, ushort converterSlot, CancellationToken cancellationToken)
    {
        GameData data = GameData.Instance;
        ushort result = 0;
        bool done = false;

        session.Accounts.Update(session.Account.Id, account =>
        {
            InventoryItem? item = Find(account, itemSlot);
            InventoryItem? converter = Find(account, converterSlot);
            UpgradeTable? table = TableFor(data, item, converter, ItemType.Converter, ItemType.WeaponPerk);
            if (item is null || converter is null || table is null || table.Addon.Count == 0) return;

            result = (ushort)Dice.Pick(table.Addon, table.Success, table.Addon[0]);
            item.Options[3] = result;
            Consume(account, converter);
            done = true;
        });
        session.RefreshAccount();

        Log.Info(session.Tag, done ? $"Converted slot {itemSlot}: option 4 = {result}" : $"Convert slot {itemSlot} with {converterSlot} refused");
        await session.SendAsync(InventoryPacket.Converted(done, itemSlot, converterSlot, result), cancellationToken);
        if (done) await SendBalanceAndItemsAsync(session, cancellationToken);
    }

    public static async Task ExtendAsync(ClientSession session, ushort slot, byte option, byte payment, CancellationToken cancellationToken)
    {
        GameData data = GameData.Instance;
        uint expiry = 0;
        string? refusal = null;

        session.Accounts.Update(session.Account.Id, account =>
        {
            InventoryItem? item = Find(account, slot);
            int index = option - 1;
            if (item is null || !data.Catalog.TryGetValue(item.ItemId, out CatalogEntry? entry)
                || index < 0 || index >= entry.Extend.Count || index >= entry.ExtendGold.Count)
            {
                refusal = "this item cannot be extended that way";
                return;
            }
            if (payment != PayWithGold) { refusal = "only BP is accepted"; return; }

            int price = entry.ExtendGold[index];
            int seconds = entry.Extend[index];
            if (price <= 0 && seconds >= 0) { refusal = "this option is not sold for BP"; return; }
            if (account.Gold < price) { refusal = $"costs {price} BP"; return; }

            account.Gold -= price;
            uint now = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            item.Expiry = seconds < 0 || item.Expiry == uint.MaxValue
                ? uint.MaxValue
                : Math.Max(item.Expiry, now) + (uint)seconds;
            expiry = item.Expiry;
        });
        session.RefreshAccount();

        if (refusal is not null)
        {
            Log.Warn(session.Tag, $"Extend slot {slot} refused: {refusal}");
            await session.SendAsync(InventoryPacket.Extended(false, slot, option, 0), cancellationToken);
            return;
        }

        Log.Info(session.Tag, $"Extended the item in slot {slot} (option {option})");
        await session.SendAsync(InventoryPacket.Extended(true, slot, option, expiry), cancellationToken);
        await SendBalanceAndItemsAsync(session, cancellationToken);
    }

    public static Task CheckNicknameAsync(ClientSession session, string nickname, CancellationToken cancellationToken)
    {
        NetError result = AccountRules.ValidateNickname(nickname)
            ?? (session.Accounts.NicknameExists(nickname) ? NetError.Nick_AlreadyExist : NetError.Success);
        Log.Info(session.Tag, $"Nickname check '{nickname}': {result}");
        return session.SendAsync(InventoryPacket.NicknameChecked(result), cancellationToken);
    }

    public static async Task RenameAsync(ClientSession session, ushort slot, string nickname, CancellationToken cancellationToken)
    {
        InventoryItem? changer = Find(session.Account, slot);
        NetError result = changer is null || changer.Type != ItemType.NicknameChanger
            ? NetError.Unknown
            : AccountRules.ValidateNickname(nickname) ?? NetError.Success;

        if (result == NetError.Success && !session.Accounts.TrySetNickname(session.Account.Id, nickname))
            result = NetError.Nick_AlreadyExist;

        if (result == NetError.Success)
        {
            session.Accounts.Update(session.Account.Id, account =>
            {
                if (Find(account, slot) is { } used) Consume(account, used);
            });
        }
        session.RefreshAccount();

        Log.Info(session.Tag, $"Nickname change to '{nickname}': {result}");
        await session.SendAsync(InventoryPacket.Renamed(result, session.Account.Nickname), cancellationToken);
        if (result != NetError.Success) return;

        await session.SendAsync(UserInfoPacket.Nickname(session.Account.Nickname), cancellationToken);
        await session.SendAsync(InventoryPacket.List(session.Account.Items), cancellationToken);
    }

    public static async Task UseAsync(ClientSession session, ushort slot, CancellationToken cancellationToken)
    {
        GameData data = GameData.Instance;
        int gold = 0;
        bool used = false;

        session.Accounts.Update(session.Account.Id, account =>
        {
            InventoryItem? item = Find(account, slot);
            if (item is null || !data.Upgrades.Misc.TryGetValue(item.ItemId, out MiscItem? misc)) return;
            if (misc.Type != ItemType.GoldPack || misc.Gold <= 0) return;

            gold = misc.Gold;
            account.Gold += gold;
            Consume(account, item);
            used = true;
        });
        session.RefreshAccount();

        if (!used)
        {
            Log.Warn(session.Tag, $"Use slot {slot} refused: not an item the server knows how to use");
            await session.SendAsync(InventoryPacket.Used(NetError.Unknown), cancellationToken);
            return;
        }

        Log.Info(session.Tag, $"Used the gold pack in slot {slot}: +{gold} BP");
        await session.SendAsync(InventoryPacket.Used(NetError.Success), cancellationToken);
        await SendBalanceAndItemsAsync(session, cancellationToken);
    }

    private static async Task SendBalanceAndItemsAsync(ClientSession session, CancellationToken cancellationToken)
    {
        await session.SendAsync(UserInfoPacket.Gold((uint)session.Account.Gold), cancellationToken);
        await session.SendAsync(InventoryPacket.List(session.Account.Items), cancellationToken);
    }

    private static InventoryItem? Find(Account account, ushort slot)
        => account.Items.FirstOrDefault(item => item.Slot == slot);

    private static UpgradeTable? TableFor(GameData data, InventoryItem? item, InventoryItem? tool, params byte[] toolTypes)
    {
        if (item is null || tool is null || ReferenceEquals(item, tool)) return null;
        if (!data.Upgrades.Misc.TryGetValue(tool.ItemId, out MiscItem? misc) || !toolTypes.Contains(misc.Type)) return null;
        string? part = GameData.UpgradePart(item.Type);
        return part is not null && misc.Parts.TryGetValue(part, out UpgradeTable? table) ? table : null;
    }

    private static int At(List<int> values, int index) => index < values.Count ? values[index] : 0;

    private static void Consume(Account account, InventoryItem item)
    {
        if (item.Quantity > 1) item.Quantity--;
        else Remove(account, item);
    }

    private static void Remove(Account account, InventoryItem item)
    {
        account.Items.Remove(item);
        foreach (ushort[] equipped in account.Equipped)
            for (int type = 0; type < equipped.Length; type++)
                if (equipped[type] == item.Slot) equipped[type] = 0;
    }
}
