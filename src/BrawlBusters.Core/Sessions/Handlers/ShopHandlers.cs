using BrawlBusters.Core.Data;
using BrawlBusters.Core.Logging;
using BrawlBusters.Core.Network;
using BrawlBusters.Core.Protocol;
using BrawlBusters.Core.Protocol.Packets;

namespace BrawlBusters.Core.Sessions.Handlers;

public sealed class StoreHandler : IMessageHandler
{
    private const byte PayWithGold = 1;

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

        if (!data.Catalog.TryGetValue(catalogId, out CatalogEntry? entry)
            || option < 0 || option >= entry.Gold.Count
            || payment != PayWithGold)
        {
            Log.Warn(session.Tag, $"Purchase refused: catalog {catalogId}, option {option + 1}, payment {payment}");
            await session.SendAsync(StorePacket.BuyResult(StorePacket.Failed), cancellationToken);
            return;
        }

        int price = entry.Gold[option];
        ushort quantity = (ushort)(option < entry.Count.Count ? Math.Max(1, entry.Count[option]) : 1);
        byte type = data.TypeOf(catalogId);

        int lifetime = option < entry.Expire.Count ? entry.Expire[option] : -1;
        uint expiry = lifetime > 0 ? (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds() + (uint)lifetime : uint.MaxValue;

        ushort reinforcement = entry.Options.Count >= 3 && entry.Options[2].Count > 0 ? (ushort)entry.Options[2][0] : (ushort)0;

        InventoryItem? bought = null;
        session.Accounts.Update(session.Account.Id, account =>
        {
            if (account.Gold < price) return;

            account.Gold -= price;
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
            Log.Info(session.Tag, $"Purchase refused: catalog {catalogId} costs {price}, player has {session.Account.Gold}");
            await session.SendAsync(StorePacket.BuyResult(StorePacket.Failed), cancellationToken);
            return;
        }

        Log.Info(session.Tag, $"Bought catalog {catalogId} x{quantity} for {price} gold ({session.Account.Gold} left)");

        await session.SendAsync(StorePacket.BuyResult(StorePacket.Success), cancellationToken);
        await session.SendAsync(UserInfoPacket.Gold((uint)session.Account.Gold), cancellationToken);
        await session.SendAsync(InventoryPacket.Added([bought]), cancellationToken);
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
                await session.SendAsync(RecordsPacket.Empty(), cancellationToken);
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
                await session.SendAsync(RecordsPacket.OfPlayer(player.Nickname), cancellationToken);
                await session.SendAsync(RecordsPacket.End(), cancellationToken);
                return;
            }

            default:
                Log.Warn(session.Tag, $"cRecord 0x{sub:X2} (not implemented): {Log.Hex(reader.ReadToEnd())}");
                return;
        }
    }
}
