using BrawlBusters.Core.Configuration;
using BrawlBusters.Core.Data;
using BrawlBusters.Core.Logging;

namespace BrawlBusters.Core.Sessions;

/// <summary>
/// What the server refuses to take a client's word for. Matches run on the host player's own computer and single
/// play runs on the player's own, so their outcome is reported, not observed: a changed client (edited data
/// files, a packet tool) can report whatever it likes. Nothing a client sends is a price, a balance or a reward -
/// those come from the server's own tables - but kills, waves, stars and "I won" do feed the rewards. This class
/// holds the limits those reports are checked against: what could have happened in the time that really passed,
/// measured by the server's clock.
/// </summary>
public static class MatchGuard
{
    /// <summary>Set from the settings at start-up (EmulatorSettings.AntiCheat).</summary>
    public static AntiCheatSettings Limits { get; set; } = new();

    private const int StarsPerWave = 4;

    /// <summary>The seconds a per-minute limit is worked out from: what really passed, no more than the mode pays for.</summary>
    private static double Minutes(int seconds, int timeMax)
        => Math.Max(0, timeMax > 0 ? Math.Min(seconds, timeMax) : seconds) / 60.0;

    private static int Allowed(int perMinute, int seconds, int timeMax)
        => perMinute <= 0 ? int.MaxValue : Limits.Allowance + (int)Math.Ceiling(perMinute * Minutes(seconds, timeMax));

    /// <summary>
    /// Brings one reported number down to what the match's length allows. Returns the number to use; a number that
    /// was too high is logged with the player it was reported for.
    /// </summary>
    private static int Limit(string what, int reported, int allowed, string tag, uint userId)
    {
        if (!Limits.Enabled || reported <= allowed) return Math.Max(0, reported);

        Log.Warn(LogChannel.Match, tag, $"Not plausible: {reported} {what} reported for player {userId}, at most {allowed} possible in that time - counted as {allowed}");
        return allowed;
    }

    /// <summary>The row values of the result, checked against the length of the match.</summary>
    public static void Check(ResultRow row, PlayerLog own, int seconds, int timeMax, string tag, out int kills, out int assists, out int slays,
        out int revives, out int attack)
    {
        kills = Limit("kill(s)", own.Kills, Allowed(Limits.MaxKillsPerMinute, seconds, timeMax), tag, row.UserId);
        assists = Limit("assist(s)", own.Assists, Allowed(Limits.MaxKillsPerMinute, seconds, timeMax), tag, row.UserId);
        slays = Limit("slay(s)", own.Slays, Allowed(Limits.MaxSlaysPerMinute, seconds, timeMax), tag, row.UserId);
        revives = Limit("revive(s)", own.Revives, Allowed(Limits.MaxRevivesPerMinute, seconds, timeMax), tag, row.UserId);
        attack = Math.Clamp(own.AttackPercent, 0, 100);
    }

    /// <summary>Whether the host's end-of-match counters of one player could have happened in the time the match took.</summary>
    public static bool Plausible(PlayerStatistics statistics, int seconds, string tag)
    {
        if (!Limits.Enabled) return true;

        int kills = Allowed(Limits.MaxKillsPerMinute, seconds, 0);
        int slays = Allowed(Limits.MaxSlaysPerMinute, seconds, 0);
        if (statistics.Kills <= kills && statistics.Assists <= kills && statistics.MobKills <= slays) return true;

        Log.Warn(LogChannel.Match, tag, $"Not plausible: statistics of player {statistics.UserId} report {statistics.Kills} kill(s), {statistics.Assists} assist(s), "
            + $"{statistics.MobKills} mob kill(s) for a match of {seconds} s - not used");
        return false;
    }

    /// <summary>
    /// A "wave cleared" report: waves come one after the other, each takes time, a wave gives at most four stars and
    /// there are no more waves than the rule has.
    /// </summary>
    public static bool WaveAllowed(MatchLog log, int wave, int stars, RuleInfo? rule, DateTime now, string tag)
    {
        if (!Limits.Enabled) return true;

        string? refusal =
            wave != log.LastWave + 1 ? $"the last wave was {log.LastWave}"
            : rule is { Waves: > 0 } && wave > rule.Waves ? $"the rule has {rule.Waves} wave(s)"
            : stars > StarsPerWave ? $"{stars} star(s) for one wave"
            : (now - (log.WaveClearedUtc ?? log.StartedUtc ?? now)).TotalSeconds < Limits.MinSecondsPerWave
                ? $"less than {Limits.MinSecondsPerWave} s after the wave before"
            : null;
        if (refusal is null) return true;

        Log.Warn(LogChannel.Match, tag, $"Not plausible: wave {wave} reported as cleared - {refusal}; ignored");
        return false;
    }

    /// <summary>The kill and death reports name players of this match (or its bots), and nobody kills himself.</summary>
    public static bool KillAllowed(Room room, uint killer, uint victim, string tag)
    {
        if (!Limits.Enabled) return true;
        if (InMatch(room, killer) && (victim == 0 || (victim != killer && InMatch(room, victim)))) return true;

        Log.Warn(LogChannel.Match, tag, $"Not plausible: kill of {victim} by {killer} reported, who are not both players of this match - ignored");
        return false;
    }

    public static bool InMatch(Room room, uint playerId)
        => room.FindUser(playerId) is { IsObserver: false, Session.InMatch: true } || room.Bots.Any(bot => bot.Id == playerId);
}
