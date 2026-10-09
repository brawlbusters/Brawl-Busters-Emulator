using BrawlBusters.Core.Data;
using BrawlBusters.Core.Logging;
using BrawlBusters.Core.Network;
using BrawlBusters.Core.Protocol;
using BrawlBusters.Core.Protocol.Packets;

namespace BrawlBusters.Core.Sessions.Handlers;

public sealed class StoreHandler : IMessageHandler
{
    private const byte PayWithGold = 1;
    private const byte PayWithCash = 2;

    public MsgCategory Category => MsgCategory.cStore;

    public Task HandleAsync(ClientSession session, PacketReader reader, CancellationToken cancellationToken)
    {
        var request = (StoreRequest)reader.ReadByte();
        if (request != StoreRequest.Buy)
        {
            Log.Warn(session.Tag, $"cStore 0x{(byte)request:X2} (not implemented): {Log.Hex(reader.ReadToEnd())}");
            return Task.CompletedTask;
        }

        uint catalogId = reader.ReadUInt32();
        reader.ReadBytes(7);
        int option = reader.ReadByte() - 1;
        byte payment = reader.ReadByte();
        return BuyAsync(session, catalogId, option, payment, cancellationToken);
    }

    private static async Task BuyAsync(ClientSession session, uint catalogId, int option, byte payment, CancellationToken cancellationToken)
    {
        GameData data = GameData.Instance;

        if (session.IsItemRequestTooFast())
        {
            Log.Info(session.Tag, $"Purchase of catalog {catalogId} refused: sent too soon after the last request");
            await session.SendAsync(StorePacket.Error(NetError.Inventory_FastRequest), cancellationToken);
            return;
        }

        if (!data.Catalog.TryGetValue(catalogId, out CatalogEntry? entry))
        {
            Log.Warn(session.Tag, $"Purchase refused: catalog {catalogId} is not in the store");
            await session.SendAsync(StorePacket.Error(NetError.Store_NotExist), cancellationToken);
            return;
        }

        // The client sends 1 as the last byte even for an item that only has an RT price (seen with catalog 2024):
        // an option without a BP price is paid in RT.
        if (payment == PayWithGold && option >= 0 && option < entry.Gold.Count && entry.Gold[option] <= 0
            && option < entry.Cash.Count && entry.Cash[option] > 0)
            payment = PayWithCash;

        if (option < 0 || option >= entry.Gold.Count
            || payment is not (PayWithGold or PayWithCash)
            || (payment == PayWithCash && option >= entry.Cash.Count)
            || !IsSold(entry, option, payment))
        {
            Log.Warn(session.Tag, $"Purchase refused: catalog {catalogId}, option {option + 1}, payment {payment}");
            await session.SendAsync(StorePacket.Error(NetError.Store_InvalidItemInfo), cancellationToken);
            return;
        }

        bool cash = payment == PayWithCash;
        int price = cash ? entry.Cash[option] : entry.Gold[option];
        string currency = cash ? "RT" : "BP";
        ushort quantity = (ushort)(option < entry.Count.Count ? Math.Max(1, entry.Count[option]) : 1);
        byte type = data.TypeOf(catalogId);

        int lifetime = option < entry.Expire.Count ? entry.Expire[option] : -1;
        uint expiry = lifetime > 0 ? (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds() + (uint)lifetime : uint.MaxValue;

        ushort reinforcement = entry.Options.Count >= 3 && entry.Options[2].Count > 0 ? (ushort)entry.Options[2][0] : (ushort)0;

        InventoryItem? bought = null;
        session.Accounts.Update(session.Account.Id, account =>
        {
            if ((cash ? account.Cash : account.Gold) < price) return;

            if (cash) account.Cash -= price;
            else account.Gold -= price;
            bought = new InventoryItem
            {
                Slot = account.FreeSlot(),
                ItemId = catalogId,
                Type = type,
                Quantity = quantity,
                Options = [0, 0, reinforcement, 0],
                Expiry = expiry,
            };
            account.Items.Add(bought);
        });
        session.RefreshAccount();

        if (bought is null)
        {
            Log.Info(session.Tag, $"Purchase refused: catalog {catalogId} costs {price} {currency}, player has {(cash ? session.Account.Cash : session.Account.Gold)}");
            await session.SendAsync(StorePacket.Error(cash ? NetError.Store_NoCash : NetError.Store_NoGold), cancellationToken);
            return;
        }

        Log.Info(session.Tag, $"Bought catalog {catalogId} x{quantity} for {price} {currency} ({(cash ? session.Account.Cash : session.Account.Gold)} left)");
        session.Accounts.RecordPurchase(session.Account.Id, catalogId, cash ? 0 : price, cash ? price : 0, "buy");

        await session.SendAsync(StorePacket.BuyResult(StorePacket.Success), cancellationToken);
        await session.SendAsync(
            cash ? UserInfoPacket.Cash((uint)session.Account.Cash) : UserInfoPacket.Gold((uint)session.Account.Gold), cancellationToken);
        await session.SendAsync(InventoryPacket.Added([bought]), cancellationToken);
    }

    private static bool IsSold(CatalogEntry entry, int option, byte payment)
    {
        int gold = entry.Gold[option];
        int cash = option < entry.Cash.Count ? entry.Cash[option] : 0;
        if (payment == PayWithCash) return cash > 0;
        return gold > 0 || cash <= 0;
    }
}

public sealed class RecordHandler : IMessageHandler
{
    private const byte PlayerRecords = 0;
    private const byte MyRecords = 1;

    public MsgCategory Category => MsgCategory.cRecord;

    public async Task HandleAsync(ClientSession session, PacketReader reader, CancellationToken cancellationToken)
    {
        byte sub = reader.ReadByte();
        switch (sub)
        {
            case MyRecords:
                await session.SendAsync(RecordsPacket.Mine(session.Account), cancellationToken);
                await session.SendAsync(RecordsPacket.End(), cancellationToken);
                return;

            case PlayerRecords:
            {
                string nickname = reader.ReadWideString();
                Account? player = session.Accounts.FindByNickname(nickname);
                if (player is null)
                {
                    Log.Info(session.Tag, $"Records of '{nickname}': no such player");
                    await session.SendAsync(RecordsPacket.Result(NetError.MyRecord_NotExistNick), cancellationToken);
                    return;
                }

                Log.Info(session.Tag, $"Records of '{player.Nickname}'");
                await session.SendAsync(RecordsPacket.OfPlayer(player), cancellationToken);
                await session.SendAsync(RecordsPacket.End(), cancellationToken);
                return;
            }

            default:
                Log.Warn(session.Tag, $"cRecord 0x{sub:X2} (not implemented): {Log.Hex(reader.ReadToEnd())}");
                return;
        }
    }
}
