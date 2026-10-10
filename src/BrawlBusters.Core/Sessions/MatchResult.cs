using System.Buffers.Binary;
using BrawlBusters.Core.Data;
using BrawlBusters.Core.Network;

namespace BrawlBusters.Core.Sessions;

public sealed class PlayerStatistics
{
    public const int Classes = 5;
    public const int Causes = 7;

    private const int ClassSlots = 6;
    private const int ClassBlock = 16;
    private const int SelectAt = 0;
    private const int SecondsAt = 1;
    private const int KillsAsAt = 3;
    private const int AssistsAsAt = 4;
    private const int MobKillsAsAt = 5;
    private const int RevivesAsAt = 7;
    private const int KillsOfAt = 8;
    private const int DeathsByAt = 9;
    private const int TriesAt = 10;
    private const int HitsAt = 12;

    private const int TailAt = ClassSlots * ClassBlock;
    private const int DeathsByTrapAt = TailAt;
    private const int DeathsByMobAt = TailAt + 1;
    private const int CauseUserAt = TailAt + 2;
    private const int CauseMobAt = CauseUserAt + Causes;
    private const int JessiumWinAt = CauseMobAt + Causes * 2 + 2;

    public const int Length = JessiumWinAt + 2;

    private readonly byte[] _data;

    public PlayerStatistics(uint userId, byte[] data)
    {
        UserId = userId;
        _data = data;
    }

    public uint UserId { get; }

    private int Word(int at) => BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(at));

    private static int Slot(int classIndex) => (classIndex + 1) * ClassBlock;

    public int Selected(int classIndex) => _data[Slot(classIndex) + SelectAt];

    public int SecondsAs(int classIndex) => Word(Slot(classIndex) + SecondsAt);

    public int KillsAs(int classIndex) => _data[Slot(classIndex) + KillsAsAt];

    public int AssistsAs(int classIndex) => _data[Slot(classIndex) + AssistsAsAt];

    public int MobKillsAs(int classIndex) => Word(Slot(classIndex) + MobKillsAsAt);

    public int RevivesAs(int classIndex) => _data[Slot(classIndex) + RevivesAsAt];

    public int KillsOf(int classIndex) => _data[Slot(classIndex) + KillsOfAt];

    public int DeathsBy(int classIndex) => _data[Slot(classIndex) + DeathsByAt];

    public int TriesAs(int classIndex) => Word(Slot(classIndex) + TriesAt);

    public int HitsAs(int classIndex) => Word(Slot(classIndex) + HitsAt);

    public int KillsByCause(int cause) => _data[CauseUserAt + cause];

    public int MobKillsByCause(int cause) => Word(CauseMobAt + cause * 2);

    public bool JessiumWon => _data[JessiumWinAt] != 0;

    public bool JessiumLost => _data[JessiumWinAt + 1] != 0;

    private static IEnumerable<int> EverySlot => Enumerable.Range(-1, ClassSlots);

    public int Kills => EverySlot.Sum(KillsAs);

    public int Assists => EverySlot.Sum(AssistsAs);

    public int MobKills => EverySlot.Sum(MobKillsAs);

    public int Revives => EverySlot.Sum(RevivesAs);

    public int Deaths => EverySlot.Sum(DeathsBy) + _data[DeathsByMobAt] + _data[DeathsByTrapAt];
}

public static class HostStatistics
{
    private const int IdLength = 4;
    private const int PlayerBlock = IdLength + PlayerStatistics.Length;

    public static List<PlayerStatistics>? Parse(byte[] report)
    {
        if (report.Length < 1) return null;

        int count = report[0];
        if (count == 0 || report.Length < 1 + count * PlayerBlock) return null;

        var players = new List<PlayerStatistics>();
        for (int i = 0; i < count; i++)
        {
            int at = 1 + i * PlayerBlock;
            uint userId = BinaryPrimitives.ReadUInt32LittleEndian(report.AsSpan(at));
            byte[] counters = report.AsSpan(at + IdLength, PlayerStatistics.Length).ToArray();
            players.Add(new PlayerStatistics(userId, counters));
        }
        return players;
    }
}

[Flags]
public enum ResultFlag : uint
{
    None = 0,
    FirstKill = 1u << 0,
    LastKill = 1u << 1,
    Revenge = 1u << 2,
    LongLife = 1u << 3,
    Immortal = 1u << 4,
    Crown1 = 1u << 5,
    Crown2 = 1u << 6,
    Crown3 = 1u << 7,
    ItemMania = 1u << 8,
    ChargerMania = 1u << 9,
    InfiniteCombo = 1u << 10,
    LevelUp = 1u << 11,
    PerfectWin = 1u << 12,
    CafeBonus = 1u << 13,
    ItemBonus = 1u << 14,
    EventBonus = 1u << 15,
    KillCrown = 1u << 16,
    AssistCrown = 1u << 17,
    AttackCrown = 1u << 18,
    SlayCrown = 1u << 19,
    ReviveCrown = 1u << 20,
    JessiumCrown = 1u << 21,
    LadderUp = 1u << 24,
    LadderDown = 1u << 25,
}

public sealed class ResultRow
{
    public uint UserId { get; init; }

    public string Nickname { get; init; } = "";

    public byte Level { get; set; }

    public byte LadderLevel { get; set; }

    public byte Team { get; init; }

    public ResultFlag Flags { get; set; }

    public int Kills { get; init; }

    public int Assists { get; init; }

    public int Deaths { get; init; }

    public int Revenges { get; init; }

    public int Attack { get; init; }

    public int MaxCombo { get; init; }

    public int Items { get; init; }

    public int Chargers { get; init; }

    public int Slays { get; init; }

    public int Revives { get; init; }

    public int Jessium { get; init; }

    public int JessiumExtra { get; init; }

    public int Points { get; init; }

    public int Survival { get; init; }

    public TimeSpan LongestLife { get; init; }

    public int Rank { get; set; }

    public int Outcome { get; set; }

    public bool Has(ResultFlag flag) => (Flags & flag) != 0;
}

public sealed class MatchSummary
{
    public MatchMode Mode { get; init; }

    public bool Ladder { get; init; }

    public bool Formal { get; init; }

    public List<ResultRow> Rows { get; init; } = [];

    public int Red { get; init; }

    public int Blue { get; init; }

    public bool Perfect { get; init; }

    public bool Success { get; init; }

    public int Seconds { get; init; }

    public int LastWave { get; init; }

    public int MaxWave { get; init; }

    public int Stars { get; init; }

    public int Grade { get; init; }

    public string BossName { get; init; } = "";

    public ResultRow? Of(uint userId) => Rows.FirstOrDefault(row => row.UserId == userId);
}

public static class ResultMessage
{
    public const int BossFailed = 1;

    private static byte Byte(int value) => (byte)Math.Clamp(value, 0, sbyte.MaxValue);

    private static ushort Word(int value) => (ushort)Math.Clamp(value, 0, short.MaxValue);

    public static bool HasExtraPrivateByte(MatchMode mode) => mode is not (MatchMode.Survival or MatchMode.Bsr);

    public static byte[] Public(MatchSummary summary)
    {
        var writer = new PacketWriter().WriteBool(summary.Formal).WriteByte((byte)summary.Rows.Count);
        foreach (ResultRow row in summary.Rows)
        {
            writer.WriteWideString(row.Nickname)
                .WriteByte(row.Level)
                .WriteByte(row.LadderLevel)
                .WriteByte(row.Team)
                .WriteUInt32((uint)row.Flags);
            switch (summary.Mode)
            {
                case MatchMode.Survival:
                    writer.WriteUInt16(Word(row.Slays)).WriteByte(Byte(row.Attack)).WriteByte(Byte(row.Revives));
                    break;
                case MatchMode.Bsr:
                    writer.WriteByte(Byte(row.Attack)).WriteByte(Byte(row.Revives)).WriteUInt16(Word(row.Slays));
                    break;
                case MatchMode.Jessium:
                    writer.WriteByte(Byte(row.Kills)).WriteByte(Byte(row.Assists)).WriteByte(Byte(row.Jessium))
                        .WriteByte(Byte(row.JessiumExtra)).WriteByte(Byte(row.Attack));
                    break;
                case MatchMode.FreeForAll:
                    writer.WriteByte(Byte(row.Kills)).WriteByte(Byte(row.Assists)).WriteByte(Byte(row.Attack)).WriteByte(Byte(row.Rank));
                    break;
                case MatchMode.Zim:
                    writer.WriteByte(Byte(row.Kills)).WriteByte(Byte(row.Assists)).WriteUInt16(Word(row.Survival));
                    break;
                case MatchMode.Szm:
                    writer.WriteByte(Byte(row.Kills)).WriteByte(Byte(row.Assists)).WriteUInt16(Word(row.Points));
                    break;
                default:
                    writer.WriteByte(Byte(row.Kills)).WriteByte(Byte(row.Assists)).WriteByte(Byte(row.Attack));
                    if (summary.Ladder) writer.WriteByte(Byte(row.Deaths));
                    break;
            }
        }

        switch (summary.Mode)
        {
            case MatchMode.Survival:
                writer.WriteBool(summary.Success)
                    .WriteUInt32(BitConverter.SingleToUInt32Bits(summary.Seconds))
                    .WriteByte(Byte(summary.LastWave))
                    .WriteByte(Byte(summary.MaxWave))
                    .WriteByte(Byte(summary.Stars));
                break;
            case MatchMode.Bsr:
                writer.WriteByte(Byte(summary.Grade))
                    .WriteUInt32(BitConverter.SingleToUInt32Bits(summary.Seconds))
                    .WriteString(summary.BossName);
                break;
            case MatchMode.Szm:
                writer.WriteUInt16(Word(summary.Red)).WriteUInt16(Word(summary.Blue));
                break;
            case MatchMode.FreeForAll or MatchMode.Zim:
                break;
            default:
                writer.WriteByte(Byte(summary.Red)).WriteByte(Byte(summary.Blue));
                break;
        }

        return writer.ToArray();
    }
}

public sealed record MatchPayout(int ExpBefore, int ExpGain, int ExpBonus, int GoldBefore, int GoldGain, int GoldBonus, bool LevelUp, int ExpBoost, int GoldBoost)
{
    public int LadderPoints { get; init; }

    public int WinCount { get; init; }

    public int LoseCount { get; init; }

    public byte[] ToPrivateRecord(bool extraByte)
    {
        static ushort Short(int value) => (ushort)Math.Clamp(value, 0, ushort.MaxValue);

        var writer = new PacketWriter()
            .WriteUInt32((uint)Math.Max(0, ExpBefore))
            .WriteUInt16(Short(ExpGain))
            .WriteUInt16(Short(ExpBonus))
            .WriteUInt32((uint)Math.Max(0, GoldBefore))
            .WriteUInt16(Short(GoldGain))
            .WriteUInt16(Short(GoldBonus))
            .WriteUInt16((ushort)(short)Math.Clamp(LadderPoints, short.MinValue, short.MaxValue))
            .WriteBool(LevelUp)
            .WriteByte((byte)Math.Clamp(WinCount, 0, 127))
            .WriteByte((byte)Math.Clamp(LoseCount, 0, 127))
            .WriteZeros(8)
            .WriteUInt16(Short(GoldBoost))
            .WriteUInt16(Short(ExpBoost));
        if (extraByte) writer.WriteByte(0);
        return writer.ToArray();
    }
}
