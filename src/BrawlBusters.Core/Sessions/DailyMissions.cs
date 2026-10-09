using System.Buffers.Binary;
using BrawlBusters.Core.Data;
using BrawlBusters.Core.Protocol.Packets;

namespace BrawlBusters.Core.Sessions;

public static class DailyMissions
{
    private const int Slots = 5;
    private const int PlayMatches = 0;
    private const int WinMatches = 1;
    private const int Kills = 2;
    private static readonly int[] GradePlan = [0, 0, 1, 1, 2];

    public static bool Ensure(Account account)
    {
        string today = Account.DayOf(DateTime.UtcNow);
        if (account.MissionDay == today && account.Missions.Count == Slots) return false;

        List<MissionInfo> all = GameData.Instance.Results.Missions;
        var picked = new List<MissionInfo>();
        foreach (int grade in GradePlan)
        {
            List<MissionInfo> pool = all.Where(mission => mission.Grade == grade && !picked.Contains(mission)).ToList();
            if (pool.Count == 0) pool = all.Where(mission => !picked.Contains(mission)).ToList();
            if (pool.Count == 0) break;
            picked.Add(pool[Random.Shared.Next(pool.Count)]);
        }

        account.MissionDay = today;
        account.Missions = picked.Select(mission => new MissionSlot { Id = mission.Id }).ToList();
        return true;
    }

    public static byte[] Block(Account account)
    {
        byte[] block = new byte[UserInfoPacket.MissionsSize];
        for (int i = 0; i < Slots && i < account.Missions.Count; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(i * 4), account.Missions[i].Id);
            BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(i * 4 + 2), account.Missions[i].Count);
        }

        if (DateTime.TryParse(account.MissionDay, out DateTime day))
            BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(Slots * 4), (uint)new DateTimeOffset(day, TimeSpan.Zero).ToUnixTimeSeconds());
        return block;
    }

    public static (bool Changed, List<uint> RewardIds, List<InventoryItem> Items) Progress(
        Account account, MatchMode mode, bool played, bool won, int kills)
    {
        Ensure(account);
        bool changed = false;
        var rewardIds = new List<uint>();
        var items = new List<InventoryItem>();
        byte characterClass = account.Character?.Class ?? 0;

        foreach (MissionSlot slot in account.Missions)
        {
            MissionInfo? mission = GameData.Instance.Results.Missions.FirstOrDefault(candidate => candidate.Id == slot.Id);
            if (mission is null || slot.Count >= mission.Count) continue;
            if (mission.Mode != 0 && mission.Mode != (int)mode) continue;
            if (mission.Class != 0 && mission.Class != characterClass) continue;

            int gain = mission.Type switch
            {
                PlayMatches => played ? 1 : 0,
                WinMatches => won ? 1 : 0,
                Kills => kills,
                _ => 0,
            };
            if (gain <= 0) continue;

            slot.Count = (ushort)Math.Min(mission.Count, slot.Count + gain);
            changed = true;
            if (slot.Count < mission.Count || slot.Rewarded || mission.Rewards.Count == 0) continue;

            slot.Rewarded = true;
            uint rewardId = mission.Rewards[Math.Min(Handlers.Dice.Weighted(mission.Prob), mission.Rewards.Count - 1)];
            rewardIds.Add(rewardId);
            if (Rewards.Grant(account, rewardId) is { } item) items.Add(item);
        }

        return (changed, rewardIds, items);
    }
}
