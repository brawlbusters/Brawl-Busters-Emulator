using BrawlBusters.Core.Data;

namespace BrawlBusters.Core.Sessions;

public static class Rewards
{
    private const int FixedItemReward = 1;
    private const int GoldReward = 2;
    private const int ExpReward = 3;
    private const int UnlockItemReward = 4;
    private const int CashReward = 7;

    public static InventoryItem? Grant(Account account, uint rewardId)
    {
        GameData data = GameData.Instance;
        if (rewardId == 0 || !data.Results.Rewards.TryGetValue(rewardId, out int[]? reward) || reward.Length < 2) return null;

        switch (reward[0])
        {
            case GoldReward:
                account.Gold = (int)Math.Min((long)account.Gold + reward[1], int.MaxValue);
                return null;

            case CashReward:
                account.Cash = (int)Math.Min((long)account.Cash + reward[1], int.MaxValue);
                return null;

            case ExpReward:
                account.Experience += reward[1];
                account.Level = data.LevelForExp(account.Experience, account.Level);
                return null;

            case FixedItemReward or UnlockItemReward when data.Packages.Fixed.TryGetValue((uint)reward[1], out FixedItem? item):
            {
                var granted = new InventoryItem
                {
                    Slot = account.FreeSlot(),
                    ItemId = item.ItemId,
                    Type = item.Type,
                    Options = (ushort[])item.Options.Clone(),
                    Quantity = item.Count,
                    State = item.State,
                    Expiry = item.Expire > 0 ? (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds() + (uint)item.Expire : uint.MaxValue,
                };
                account.Items.Add(granted);
                return granted;
            }

            default:
                return null;
        }
    }

    public static List<InventoryItem> GrantForLevels(Account account)
    {
        var granted = new List<InventoryItem>();
        GameData data = GameData.Instance;
        byte reached = account.DisplayLevel;
        for (int level = account.LevelRewardsUpTo + 1; level <= reached; level++)
        {
            if (!data.Results.LevelRewards.TryGetValue((byte)level, out List<uint>? rewardIds)) continue;
            foreach (uint rewardId in rewardIds)
            {
                if (Grant(account, rewardId) is { } item) granted.Add(item);
            }
        }

        if (reached > account.LevelRewardsUpTo) account.LevelRewardsUpTo = reached;
        return granted;
    }
}
