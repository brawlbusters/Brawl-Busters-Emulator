using BrawlBusters.Core.Data;

namespace BrawlBusters.Core.Sessions;

/// <summary>
/// The gem ranks of ranked play. The client gets seven score boundaries in sLadder 00 and works the rank out itself
/// (UI LadderPoint2Grade): rank i when table[i] &lt;= score &lt; table[i + 1], so table[0] is 0 and the six values behind
/// it are the scores the ranked screen lists (mcRank0..mcRank5). The same rank goes into the player record as the
/// "ladder level" (user +0x3C1) for the icon other players see.
///
/// The original boundaries were never fixed: the client's own description says they are "adjusted according to the
/// player population in each rank". The server therefore takes them from the scores of everyone who has played ranked
/// (the shares below), and from the configured fixed scores while too few have. Both the shares and the fixed scores
/// are this emulator's choice - no original values exist in the client.
/// </summary>
public static class LadderGrades
{
    public const int Boundaries = 7;

    /// <summary>Share of the ranked players that stays below each rank, lowest rank first.</summary>
    private static readonly double[] ShareBelow = [0.20, 0.45, 0.65, 0.80, 0.92, 0.98];

    private static readonly TimeSpan CacheTime = TimeSpan.FromMinutes(1);
    private static readonly object Gate = new();
    private static ushort[]? _table;
    private static DateTime _tableAt;

    public static ushort[] Table(ClientSession session)
    {
        lock (Gate)
        {
            if (_table is not null && DateTime.UtcNow - _tableAt < CacheTime) return _table;

            List<int> scores = session.Accounts.All()
                .Where(account => account.Ladder.Matches > 0)
                .Select(account => account.LadderPoints)
                .Order()
                .ToList();
            _table = Build(scores, session.Settings.LadderGradePoints, session.Settings.LadderGradeMinPlayers);
            _tableAt = DateTime.UtcNow;
            return _table;
        }
    }

    public static ushort[] Build(IReadOnlyList<int> sortedScores, IReadOnlyList<int> fixedScores, int minPlayers)
    {
        var table = new ushort[Boundaries];
        bool byPopulation = minPlayers > 0 && sortedScores.Count >= minPlayers;
        for (int rank = 1; rank < Boundaries; rank++)
        {
            int score = byPopulation
                ? sortedScores[Math.Min((int)(sortedScores.Count * ShareBelow[rank - 1]), sortedScores.Count - 1)]
                : fixedScores.ElementAtOrDefault(rank - 1);
            table[rank] = (ushort)Math.Clamp(score, table[rank - 1] + 1, short.MaxValue - (Boundaries - rank));
        }
        return table;
    }

    /// <summary>The client's LadderPoint2Grade.</summary>
    public static byte GradeOf(int points, ushort[] table)
    {
        byte grade = 0;
        for (int i = 1; i < table.Length && points >= table[i]; i++) grade = (byte)i;
        return grade;
    }
}
