using BrawlBusters.Core.Data;
using BrawlBusters.Core.Logging;
using BrawlBusters.Core.Network;
using BrawlBusters.Core.Protocol;

namespace BrawlBusters.Core.Sessions.Handlers;

public sealed class RankHandler : IMessageHandler
{
    private const byte OwnStanding = 0x0B;
    private const byte Page = 0x0C;
    private const byte OwnStandingReply = 0x07;
    private const byte PageReply = 0x08;
    private const int MaxRows = 50;

    private const byte Daily = 0;
    private const byte HallOfFame = 1;
    private const byte ColumnWins = 1;
    private const byte ColumnKills = 2;
    private const byte ColumnAssists = 3;
    private static readonly byte[] Types = [Daily, HallOfFame];
    private static readonly byte[] Columns = [ColumnWins, ColumnKills, ColumnAssists];

    private static readonly MatchMode[][] RankingModes =
    [
        [MatchMode.TeamDeathmatch, MatchMode.Channel5Team],
        [MatchMode.Jessium],
        [MatchMode.FreeForAll],
        [MatchMode.Survival],
        [MatchMode.Bsr],
    ];

    private static readonly object HistoryGate = new();
    private static readonly Dictionary<(byte Type, byte Mode, byte Column, uint Id), uint> Previous = [];
    private static string _historyDay = "";

    public MsgCategory Category => MsgCategory.cRank;

    public Task HandleAsync(ClientSession session, PacketReader reader, CancellationToken cancellationToken)
    {
        byte sub = reader.ReadByte();
        switch (sub)
        {
            case OwnStanding:
                return session.SendAsync(OwnStandingPacket(session), cancellationToken);

            case Page:
            {
                byte type = reader.ReadByte();
                byte mode = reader.ReadByte();
                uint first = reader.Remaining >= 4 ? reader.ReadUInt32() : 1;
                if (reader.Remaining >= 1) reader.ReadByte();
                uint count = reader.Remaining >= 4 ? reader.ReadUInt32() : MaxRows;
                reader.ReadToEnd();
                return session.SendAsync(PagePacket(session, type, mode, first, count), cancellationToken);
            }

            default:
                Log.Warn(session.Tag, $"cRank 0x{sub:X2} (not implemented): {Log.Hex(reader.ReadToEnd())}");
                return Task.CompletedTask;
        }
    }

    private readonly record struct Entry(uint Id, string Nickname, int Record);

    public static PacketWriter OwnStandingPacket(ClientSession session)
    {
        RememberRanks(session);
        var writer = new PacketWriter(MsgCategory.sRank, OwnStandingReply).WriteByte((byte)(Types.Length * RankingModes.Length * Columns.Length));
        foreach (byte type in Types)
        {
            for (byte mode = 1; mode <= RankingModes.Length; mode++)
            {
                foreach (byte column in Columns)
                {
                    List<Entry> ranked = Ranked(session, type, mode, column);
                    int index = ranked.FindIndex(entry => entry.Id == session.Account.Id);
                    uint rank = (uint)(index + 1);
                    writer.WriteByte((byte)(type + 1)).WriteByte(mode).WriteByte(column)
                        .WriteUInt32(rank)
                        .WriteUInt32((uint)RecordOf(session.Account, type, mode, column))
                        .WriteUInt32(rank == 0 ? 0 : PreviousRank(type, mode, column, session.Account.Id, rank));
                }
            }
        }
        return writer;
    }

    private static PacketWriter PagePacket(ClientSession session, byte type, byte mode, uint first, uint count)
    {
        RememberRanks(session);
        if (type > HallOfFame) type = HallOfFame;
        int skip = first > 1 ? (int)Math.Min(first - 1, int.MaxValue) : 0;
        int take = count is > 0 and <= MaxRows ? (int)count : MaxRows;

        var rows = new List<(uint Rank, byte Column, Entry Entry)>();
        if (mode >= 1 && mode <= RankingModes.Length)
        {
            foreach (byte column in Columns)
            {
                List<Entry> ranked = Ranked(session, type, mode, column);
                rows.AddRange(ranked.Skip(skip).Take(take).Select((entry, index) => ((uint)(skip + index + 1), column, entry)));
            }
        }

        var writer = new PacketWriter(MsgCategory.sRank, PageReply)
            .WriteUInt32((uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds())
            .WriteByte(type)
            .WriteByte(mode)
            .WriteByte((byte)rows.Count);

        foreach ((uint rank, byte column, Entry entry) in rows)
        {
            writer.WriteUInt32(rank)
                .WriteByte(column)
                .WriteWideString(entry.Nickname)
                .WriteUInt32(rank)
                .WriteUInt32((uint)entry.Record)
                .WriteUInt32(PreviousRank(type, mode, column, entry.Id, rank));
        }

        Log.Info(session.Tag, $"Leaderboard {(type == Daily ? "daily" : "hall of fame")}, mode {mode}, from rank {skip + 1}: {rows.Count} cells");
        return writer;
    }

    private static int RecordOf(MatchStats? stats, byte column) => stats is null
        ? 0
        : column switch
        {
            ColumnWins => stats.Wins,
            ColumnKills => stats.Score,
            _ => stats.Assists,
        };

    private static int RecordOf(Account account, byte type, byte mode, byte column)
    {
        if (mode < 1 || mode > RankingModes.Length) return 0;
        bool today = account.DailyDay == Account.DayOf(DateTime.UtcNow);
        return RankingModes[mode - 1].Sum(matchMode => RecordOf(
            type == Daily ? (today ? account.DailyStats.GetValueOrDefault((byte)matchMode) : null) : account.ModeStats.GetValueOrDefault((byte)matchMode),
            column));
    }

    private static List<Entry> Ranked(ClientSession session, byte type, byte mode, byte column)
    {
        IEnumerable<Entry> entries = session.Accounts.All()
            .Where(account => account.HasNickname)
            .Select(account => new Entry(account.Id, account.Nickname, RecordOf(account, type, mode, column)));

        if (session.Settings.Bots.ShowOnLeaderboard)
        {
            entries = entries.Concat(BotDirector.Roster().Select(bot =>
            {
                int seed = (int)((bot.Id * 31 + mode * 7 + column * 13) % 17);
                int record = (bot.Level + seed) * (column == ColumnWins ? 1 : 6) / (type == Daily ? 6 : 1);
                return new Entry(bot.Id, bot.Name, record);
            }));
        }

        return entries
            .Where(entry => entry.Record > 0)
            .OrderByDescending(entry => entry.Record)
            .ThenBy(entry => entry.Id)
            .ToList();
    }

    private static uint PreviousRank(byte type, byte mode, byte column, uint id, uint current = 0)
    {
        lock (HistoryGate) return Previous.GetValueOrDefault((type, mode, column, id));
    }

    private static void RememberRanks(ClientSession session)
    {
        string today = Account.DayOf(DateTime.UtcNow);
        lock (HistoryGate)
        {
            if (_historyDay == today) return;
            _historyDay = today;
            Previous.Clear();
            for (byte mode = 1; mode <= RankingModes.Length; mode++)
            {
                foreach (byte column in Columns)
                {
                    List<Entry> ranked = Ranked(session, HallOfFame, mode, column);
                    for (int i = 0; i < ranked.Count; i++) Previous[(HallOfFame, mode, column, ranked[i].Id)] = (uint)(i + 1);
                }
            }
        }
    }
}
