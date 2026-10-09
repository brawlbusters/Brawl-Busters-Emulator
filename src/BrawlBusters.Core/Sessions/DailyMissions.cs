using System.Buffers.Binary;
using BrawlBusters.Core.Data;
using BrawlBusters.Core.Protocol.Packets;

namespace BrawlBusters.Core.Sessions;

/// <summary>
/// What one match gives the daily missions. <see cref="Statistics"/> is the host's per-player report (cHost 0D),
/// which arrives on its own: missions about a victim class or a way to kill are counted from it, all others from
/// the result rows.
/// </summary>
public sealed record MissionFacts(MatchMode Mode)
{
    public int Difficulty { get; init; }
    public bool Played { get; init; }
    public bool Won { get; init; }
    public int Kills { get; init; }
    public int Assists { get; init; }
    public int Slays { get; init; }
    public int Stars { get; init; }
    public ResultFlag Medals { get; init; }
    public PlayerStatistics? Statistics { get; init; }
}

/// <summary>
/// Daily missions (missiondb DAILY). TYPE_MISSION, named after the client's own mission texts:
/// 0 play matches, 1 win matches, 2 kill busters (TARGET class, METHOD), 3 assists, 4 slay zombies, 6 stars in
/// zombie survival, 7 / 8 / 9 single / double / triple crown medal, 10 immortal, 11 longevity, 12 terminator,
/// 13 first kill, 14 continuous combo, 15 item mania, 16 king of the kiosk, 17 avenger, 18 perfect victory.
/// Type 5 is not used by the table.
/// </summary>
public static class DailyMissions
{
    private const int Slots = 5;
    private const int PlayMatches = 0;
    private const int WinMatches = 1;
    private const int Kills = 2;
    private const int Assists = 3;
    private const int Slays = 4;
    private const int Stars = 6;
    private const int FirstCause = 2;

    private static readonly Dictionary<int, ResultFlag> Medals = new()
    {
        [7] = ResultFlag.Crown1, [8] = ResultFlag.Crown2, [9] = ResultFlag.Crown3, [10] = ResultFlag.Immortal,
        [11] = ResultFlag.LongLife, [12] = ResultFlag.LastKill, [13] = ResultFlag.FirstKill,
        [14] = ResultFlag.InfiniteCombo, [15] = ResultFlag.ItemMania, [16] = ResultFlag.ChargerMania,
        [17] = ResultFlag.Revenge, [18] = ResultFlag.PerfectWin,
    };
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

    private static int Gain(MissionInfo mission, MissionFacts facts)
    {
        bool detailed = mission.Type == Kills && (mission.Target != 0 || mission.Method != 0);
        if (facts.Statistics is { } statistics)
        {
            if (!detailed) return 0;
            if (mission.Target != 0) return mission.Target <= PlayerStatistics.Classes ? statistics.KillsOf(mission.Target - 1) : 0;
            int cause = mission.Method - FirstCause;
            return cause >= 0 && cause < PlayerStatistics.Causes ? statistics.KillsByCause(cause) : 0;
        }

        if (detailed) return 0;
        return mission.Type switch
        {
            PlayMatches => facts.Played ? 1 : 0,
            WinMatches => facts.Won ? 1 : 0,
            Kills => facts.Kills,
            Assists => facts.Assists,
            Slays => facts.Slays,
            Stars => facts.Stars,
            _ => Medals.TryGetValue(mission.Type, out ResultFlag medal) && (facts.Medals & medal) != 0 ? 1 : 0,
        };
    }

    public static (bool Changed, List<uint> RewardIds, List<InventoryItem> Items) Progress(Account account, MissionFacts facts)
    {
        MatchMode mode = facts.Mode;
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
            if (mission.Difficulty != 0 && mission.Difficulty != facts.Difficulty) continue;

            int gain = Gain(mission, facts);
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
