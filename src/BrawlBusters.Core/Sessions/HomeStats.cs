using BrawlBusters.Core.Data;

namespace BrawlBusters.Core.Sessions;

/// <summary>
/// The 86 bytes of the user record (second part, offset 20) behind the five "My Stats" pages of the home screen.
/// Layout from the client's page builder (0x7A7811, value names tr_*_ego):
///
///   +00 u32 x 5   rounds played: team deathmatch, jessium, free for all, survival, boss battle
///   +14 u32       not read
///   +18 u32       team wins (TDM + jessium)        +1C u32  team losses
///   +20 u32       free for all first places        +24 u32  co-op clears (survival + boss battle)
///   +28 u32 x 5   play time per class, seconds     (class 1-5: boxer, guitarist, baseball, football, firefighter)
///   +3C u32 x 4   kills, assists, slays, revives   (all classes)
///   +4C i16 x 5   superiority over each enemy class, in tenths of a percent: kills of / (kills of + deaths by)
///
/// Page 1 "Game Record" shows rounds / wins / losses and the rates the client works out itself, page 2 the rounds per
/// mode, page 3 the play times, page 4 the four totals, page 5 the superiority rates.
/// </summary>
public static class HomeStats
{
    public const int Size = 86;

    private const int RoundsAt = 0x00;
    private const int TeamWinsAt = 0x18;
    private const int TeamLossesAt = 0x1C;
    private const int FirstPlacesAt = 0x20;
    private const int ClearsAt = 0x24;
    private const int PlayTimeAt = 0x28;
    private const int TotalsAt = 0x3C;
    private const int SuperiorityAt = 0x4C;

    private static readonly MatchMode[] RoundModes =
        [MatchMode.TeamDeathmatch, MatchMode.Jessium, MatchMode.FreeForAll, MatchMode.Survival, MatchMode.Bsr];

    public static byte[] Of(Account account)
    {
        var block = new byte[Size];
        RecordBook book = account.Records;

        void Put(int at, int value) => BitConverter.TryWriteBytes(block.AsSpan(at), Math.Max(0, value));

        for (int i = 0; i < RoundModes.Length; i++)
            Put(RoundsAt + i * sizeof(int), Played(account, RoundModes[i]).Matches);

        MatchStats tdm = Played(account, MatchMode.TeamDeathmatch), jessium = Played(account, MatchMode.Jessium);
        Put(TeamWinsAt, tdm.Wins + jessium.Wins);
        Put(TeamLossesAt, tdm.Losses + jessium.Losses);
        Put(FirstPlacesAt, Played(account, MatchMode.FreeForAll).Wins);
        Put(ClearsAt, Played(account, MatchMode.Survival).Wins + Played(account, MatchMode.Bsr).Wins);

        for (int index = 0; index < RecordBook.Classes; index++)
        {
            Put(PlayTimeAt + index * sizeof(int), book.ClassSeconds.ElementAtOrDefault(index));

            int kills = book.ClassKillsOf.ElementAtOrDefault(index), deaths = book.ClassDeathsBy.ElementAtOrDefault(index);
            short rate = (short)(kills + deaths == 0 ? 0 : Math.Clamp(kills * 1000L / (kills + deaths), 0, 1000));
            BitConverter.TryWriteBytes(block.AsSpan(SuperiorityAt + index * sizeof(short)), rate);
        }

        Put(TotalsAt, book.ClassKills.Sum());
        Put(TotalsAt + 4, book.ClassAssists.Sum());
        Put(TotalsAt + 8, book.ClassSlays.Sum());
        Put(TotalsAt + 12, book.ClassRevives.Sum());
        return block;
    }

    /// <summary>The counters of one mode; the team channel variant counts as team deathmatch.</summary>
    private static MatchStats Played(Account account, MatchMode mode)
    {
        MatchStats own = account.ModeStats.GetValueOrDefault((byte)mode) ?? new MatchStats();
        if (mode != MatchMode.TeamDeathmatch || !account.ModeStats.TryGetValue((byte)MatchMode.Channel5Team, out MatchStats? other)) return own;
        return new MatchStats { Matches = own.Matches + other.Matches, Wins = own.Wins + other.Wins, Losses = own.Losses + other.Losses, Draws = own.Draws + other.Draws };
    }
}
