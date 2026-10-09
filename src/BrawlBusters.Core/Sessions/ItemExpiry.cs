using BrawlBusters.Core.Data;
using BrawlBusters.Core.Logging;
using BrawlBusters.Core.Protocol.Packets;

namespace BrawlBusters.Core.Sessions;

public static class ItemExpiry
{
    public static async Task<bool> SweepAsync(ClientSession session, bool detail, CancellationToken cancellationToken)
    {
        var expired = new List<ushort>();
        var deleted = new List<ushort>();
        long grace = Math.Max(0, session.Settings.ExpiredItemGraceDays) * 86400L;

        session.Accounts.Update(session.Account.Id, account =>
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            foreach (InventoryItem item in account.Items.ToList())
            {
                if (!item.HasExpired) continue;

                if (now - item.Expiry >= grace)
                {
                    TakeOff(account, item.Slot);
                    account.Items.Remove(item);
                    deleted.Add(item.Slot);
                }
                else if (!item.ExpiryNotified)
                {
                    item.ExpiryNotified = true;
                    TakeOff(account, item.Slot);
                    expired.Add(item.Slot);
                }
            }
        });
        if (expired.Count == 0 && deleted.Count == 0) return false;

        Log.Info(session.Tag, $"Items expired: slots [{string.Join(", ", expired)}]; removed after the grace period: [{string.Join(", ", deleted)}]");
        foreach (ushort slot in expired) await session.SendAsync(NoticePacket.ItemExpired(slot, detail), cancellationToken);
        foreach (ushort slot in deleted) await session.SendAsync(NoticePacket.ItemDeleted(slot, detail), cancellationToken);
        session.RefreshAccount();
        if (deleted.Count > 0) await session.SendAsync(InventoryPacket.List(session.Account.Items), cancellationToken);
        return true;
    }

    private static void TakeOff(Account account, ushort slot)
    {
        foreach (ushort[] equipped in account.Equipped)
            for (int type = 0; type < equipped.Length; type++)
                if (equipped[type] == slot) equipped[type] = 0;
    }
}
