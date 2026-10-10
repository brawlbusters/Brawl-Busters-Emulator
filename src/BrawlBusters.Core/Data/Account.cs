using System.Text.Json.Serialization;

namespace BrawlBusters.Core.Data;

public sealed class Account
{
    public uint Id { get; set; }
    public string LoginId { get; set; } = "";
    public string PasswordHash { get; set; } = "";

    public string Nickname { get; set; } = "";

    public CharacterShape? Character { get; set; }

    public int Gold { get; set; } = 60000;

    public int Cash { get; set; }

    [JsonPropertyName("grade")]
    public AccountGrade Grade { get; set; } = AccountGrade.Player;

    [JsonPropertyName("level")]
    public byte Level { get; set; }

    [JsonPropertyName("experience")]
    public int Experience { get; set; }

    [JsonPropertyName("gem_rank")]
    public byte GemRank { get; set; }

    [JsonPropertyName("gem")]
    public int Gem { get; set; }

    public MatchStats Stats { get; set; } = new();

    public MatchStats Ladder { get; set; } = new();

    public Dictionary<byte, MatchStats> ModeStats { get; set; } = [];

    public RecordBook Records { get; set; } = new();

    public string MissionDay { get; set; } = "";

    public List<MissionSlot> Missions { get; set; } = [];

    public byte UnlockedClasses { get; set; }

    public byte LevelRewardsUpTo { get; set; }

    public byte OwnedClassMask()
        => (byte)(UnlockedClasses | (Character is null ? 0 : 1 << (Character.Class & 7)));

    public bool OwnsClass(byte characterClass) => (OwnedClassMask() & (1 << (characterClass & 7))) != 0;

    public string DailyDay { get; set; } = "";

    public Dictionary<byte, MatchStats> DailyStats { get; set; } = [];

    public static string DayOf(DateTime utc) => utc.ToString("yyyy-MM-dd");

    public MatchStats TodayOf(MatchMode mode)
    {
        string today = DayOf(DateTime.UtcNow);
        if (DailyDay != today)
        {
            DailyDay = today;
            DailyStats = [];
        }

        if (!DailyStats.TryGetValue((byte)mode, out MatchStats? stats)) DailyStats[(byte)mode] = stats = new MatchStats();
        return stats;
    }

    public MatchStats StatsOf(MatchMode mode)
    {
        if (!ModeStats.TryGetValue((byte)mode, out MatchStats? stats)) ModeStats[(byte)mode] = stats = new MatchStats();
        return stats;
    }

    public int LadderPoints { get; set; }

    [JsonIgnore]
    public byte DisplayLevel => Math.Max((byte)1, Level);

    public List<InventoryItem> Items { get; set; } = [];

    [JsonPropertyName("equipped")]
    public ushort[][] Equipped { get; set; } = [];

    public ushort[] EquippedOf(int classIndex)
    {
        if (Equipped.Length != Loadout.ClassCount || Equipped.Any(row => row is null || row.Length != ItemType.EquipTableSize))
        {
            var table = new ushort[Loadout.ClassCount][];
            for (int i = 0; i < table.Length; i++)
            {
                table[i] = new ushort[ItemType.EquipTableSize];
                if (i < Equipped.Length && Equipped[i] is { } old)
                    Array.Copy(old, table[i], Math.Min(old.Length, table[i].Length));
            }
            Equipped = table;
        }
        return Equipped[classIndex];
    }

    public ushort FreeSlot()
    {
        ushort slot = 1;
        while (Items.Any(item => item.Slot == slot)) slot++;
        return slot;
    }

    [JsonPropertyName("buddies")]
    public List<BuddyEntry> Buddies { get; set; } = [];

    [JsonPropertyName("buddy_requests")]
    public List<uint> BuddyRequests { get; set; } = [];

    [JsonPropertyName("single_cleared")]
    public List<ushort> SingleCleared { get; set; } = [];

    public byte SingleProgressA { get; set; } = 1;
    public byte SingleProgressB { get; set; }

    public List<ushort> ClearedStages()
    {
        if (SingleCleared.Count == 0 && SingleProgressB != 0)
        {
            for (ushort stage = 0; stage < 8; stage++)
                if ((SingleProgressB & (1 << stage)) != 0) SingleCleared.Add(stage);
        }
        SingleProgressA = 1;
        SingleProgressB = 0;
        return SingleCleared;
    }

    public bool TutorialDone { get; set; }

    public ulong SessionKey { get; set; }

    public DateTime? BannedUntilUtc { get; set; }

    [JsonIgnore]
    public bool IsBanned => BannedUntilUtc is { } until && until > DateTime.UtcNow;

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime LastLoginUtc { get; set; }

    [JsonIgnore]
    public bool HasNickname => Nickname.Length > 0;

    [JsonIgnore]
    public bool HasCharacter => Character is not null;
}

public enum AccountGrade
{
    Player = 0,
    Moderator = 1,
    GameMaster = 2,
    Developer = 3,
}

public sealed class BuddyEntry
{
    [JsonPropertyName("id")]
    public uint Id { get; set; }

    [JsonPropertyName("nickname")]
    public string Nickname { get; set; } = "";

    [JsonPropertyName("added")]
    public uint AddedUnix { get; set; }
}

public sealed class InventoryItem
{
    public ushort Slot { get; set; }
    public uint ItemId { get; set; }

    public byte Type { get; set; }

    [JsonPropertyName("opt")]
    public ushort[] Options { get; set; } = [0, 0, 0, 0];

    public ushort Option(int number) => number >= 1 && number <= Options.Length ? Options[number - 1] : (ushort)0;

    public ushort Quantity { get; set; } = 1;

    public byte State { get; set; } = 1;

    public uint Expiry { get; set; } = uint.MaxValue;

    public bool ExpiryNotified { get; set; }

    [JsonIgnore]
    public bool HasExpired => Expiry != uint.MaxValue && Expiry <= (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
}

public sealed class CharacterShape
{
    public byte Class { get; set; }

    public ushort[] Values { get; set; } = [];
}

public sealed class MatchStats
{
    public int Matches { get; set; }

    public int Wins { get; set; }

    public int Losses { get; set; }

    public int Draws { get; set; }

    public int Score { get; set; }

    public int Assists { get; set; }

    public void Add(int outcome, int score)
    {
        Matches++;
        Score += score;
        if (outcome > 0) Wins++;
        else if (outcome < 0) Losses++;
        else Draws++;
    }
}

public sealed class RecordBook
{
    public const int Places = 8;
    public const int Crowns = 3;
    public const int Classes = 5;
    public const int BossGrades = 4;

    public int[] Placements { get; set; } = new int[Places];

    public Dictionary<byte, int[]> CrownsByMode { get; set; } = [];

    public int[] ClassSeconds { get; set; } = new int[Classes];

    public int[] ClassKills { get; set; } = new int[Classes];

    public int[] ClassAssists { get; set; } = new int[Classes];

    public int[] ClassSlays { get; set; } = new int[Classes];

    public int[] ClassRevives { get; set; } = new int[Classes];

    public int[] ClassTries { get; set; } = new int[Classes];

    public int[] ClassHits { get; set; } = new int[Classes];

    /// <summary>Kills of enemies playing each class, and deaths by them (cHost 0D "killsOf" / "dieBy"): the superiority rates.</summary>
    public int[] ClassKillsOf { get; set; } = new int[Classes];

    public int[] ClassDeathsBy { get; set; } = new int[Classes];

    /// <summary>Single play: per stage id, [0] times cleared, [1] seconds spent on the cleared runs (My Stats, "tr_SPPractice").</summary>
    public Dictionary<ushort, int[]> SingleStages { get; set; } = [];

    public void SingleStageCleared(ushort stage, int seconds)
    {
        if (!SingleStages.TryGetValue(stage, out int[]? record) || record.Length != 2) SingleStages[stage] = record = new int[2];
        record[0]++;
        record[1] += Math.Max(0, seconds);
    }

    public int[] KillsByCause { get; set; } = new int[7];

    public int[] MobKillsByCause { get; set; } = new int[7];

    public int Deaths { get; set; }

    public bool HasHostStatistics { get; set; }

    public int[] BossClears { get; set; } = new int[BossGrades];

    public const int SurvivalLevels = 3;

    /// <summary>Zombie survival per difficulty (rookie, regular, veteran): rounds, clears, stars, most stars in a round, fastest clear in seconds.</summary>
    public int[] SurvivalRounds { get; set; } = new int[SurvivalLevels];

    public int[] SurvivalClears { get; set; } = new int[SurvivalLevels];

    public int[] SurvivalStars { get; set; } = new int[SurvivalLevels];

    public int[] SurvivalStarMax { get; set; } = new int[SurvivalLevels];

    public int[] SurvivalBestTime { get; set; } = new int[SurvivalLevels];

    public void SurvivalPlayed(int difficulty, bool cleared, int stars, int seconds)
    {
        int level = Math.Clamp(difficulty - 1, 0, SurvivalLevels - 1);
        SurvivalRounds[level]++;
        SurvivalStars[level] += Math.Max(0, stars);
        SurvivalStarMax[level] = Math.Max(SurvivalStarMax[level], stars);
        if (!cleared) return;
        SurvivalClears[level]++;
        if (seconds > 0 && (SurvivalBestTime[level] == 0 || seconds < SurvivalBestTime[level])) SurvivalBestTime[level] = seconds;
    }

    public Dictionary<string, BossRecord> Bosses { get; set; } = [];

    public Dictionary<string, ModeRecord> Modes { get; set; } = [];

    public ModeRecord ModeOf(string key)
    {
        if (!Modes.TryGetValue(key, out ModeRecord? record)) Modes[key] = record = new ModeRecord();
        return record;
    }

    public static string? KeyOf(MatchMode mode, bool ladder) => mode switch
    {
        _ when ladder => "ladder",
        MatchMode.TeamDeathmatch or MatchMode.Channel5Team => "tdm",
        MatchMode.Jessium => "jes",
        MatchMode.Survival => "suv",
        MatchMode.FreeForAll => "ffa",
        MatchMode.Bsr => "bsr",
        _ => null,
    };

    public int[] CrownsOf(MatchMode mode)
    {
        if (!CrownsByMode.TryGetValue((byte)mode, out int[]? crowns) || crowns.Length != Crowns) CrownsByMode[(byte)mode] = crowns = new int[Crowns];
        return crowns;
    }

    public void Played(MatchMode mode, byte characterClass, int seconds, int score, int rank, bool ranked)
    {
        if (!HasHostStatistics && characterClass is >= 1 and <= Classes)
        {
            ClassSeconds[characterClass - 1] += seconds;
            if (ranked) ClassKills[characterClass - 1] += score;
        }

    }
}

public sealed class ModeRecord
{
    public const int BestCount = 3;

    public int[] Sums { get; set; } = new int[4];

    public Dictionary<int, int> Titles { get; set; } = [];

    public int[] Best { get; set; } = new int[BestCount];

    public int[] Extra { get; set; } = new int[3];

    public int PerfectWins { get; set; }

    public int PerfectLosses { get; set; }

    public int GreatWins { get; set; }

    public int GreatLosses { get; set; }

    public int CloseWins { get; set; }

    /// <summary>Glow rush: matches the host reported as won / lost "by MP" (JES_WIN_MP, JES_LOSE_MP of cHost 0D).</summary>
    public int MpWins { get; set; }

    public int MpLosses { get; set; }

    public int CloseLosses { get; set; }

    public int WinStreak { get; set; }

    public int LoseStreak { get; set; }

    public int BestWinStreak { get; set; }

    public int BestLoseStreak { get; set; }

    public int Title(int bit) => Titles.GetValueOrDefault(bit);

    public void Add(int[] values, uint flags, IReadOnlyList<int> titleBits, int[] best)
    {
        for (int i = 0; i < values.Length && i < Sums.Length; i++) Sums[i] += values[i];
        foreach (int bit in titleBits)
            if ((flags >> bit & 1) != 0) Titles[bit] = Titles.GetValueOrDefault(bit) + 1;
        for (int i = 0; i < best.Length && i < Best.Length; i++) Best[i] = Math.Max(Best[i], best[i]);
    }

    public void Streak(int outcome)
    {
        if (outcome > 0)
        {
            WinStreak++;
            LoseStreak = 0;
        }
        else if (outcome < 0)
        {
            LoseStreak++;
            WinStreak = 0;
        }
        else
        {
            WinStreak = 0;
            LoseStreak = 0;
        }
        BestWinStreak = Math.Max(BestWinStreak, WinStreak);
        BestLoseStreak = Math.Max(BestLoseStreak, LoseStreak);
    }
}

public sealed class MissionSlot
{
    public ushort Id { get; set; }

    public ushort Count { get; set; }

    public bool Rewarded { get; set; }
}

public sealed class BossRecord
{
    public int Rounds { get; set; }

    public int Clears { get; set; }

    public int BestGrade { get; set; }

    public int BestSeconds { get; set; }
}

