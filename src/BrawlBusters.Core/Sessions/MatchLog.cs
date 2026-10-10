using System.Buffers.Binary;

namespace BrawlBusters.Core.Sessions;

public sealed class PlayerLog
{
    public int Kills { get; set; }

    public int Assists { get; set; }

    public int Deaths { get; set; }

    public int Revenges { get; set; }

    public int Items { get; set; }

    public int Chargers { get; set; }

    public int AttackPercent { get; set; }

    public int MaxCombo { get; set; }

    public int ExtraA { get; set; }

    public int ExtraB { get; set; }

    public int Slays { get; set; }

    public int Revives { get; set; }

    public int Jessium { get; set; }

    public int JessiumExtra { get; set; }

    public int Points { get; set; }

    public int Survival { get; set; }

    public DateTime LifeStartedUtc { get; set; } = DateTime.UtcNow;

    public TimeSpan LongestLife { get; set; }

    public void Died(DateTime now)
    {
        Deaths++;
        TimeSpan life = now - LifeStartedUtc;
        if (life > LongestLife) LongestLife = life;
        LifeStartedUtc = now;
    }

    public TimeSpan LongestLifeAt(DateTime now)
    {
        TimeSpan life = now - LifeStartedUtc;
        return life > LongestLife ? life : LongestLife;
    }
}

public enum HostEvent : byte
{
    PlayerEntered = 0x00,
    Kill = 0x01,
    Death = 0x02,
    ChargerUsed = 0x03,
    CommonResults = 0x04,
    SurvivalResults = 0x05,
    JessiumResults = 0x06,
    BossResults = 0x07,
    ZimResults = 0x08,
    SzmResults = 0x09,
    ItemUsed = 0x0A,
    InventoryItemUsed = 0x0B,
    RoundStarted = 0x0C,
    RoundEnded = 0x0D,
    WaveCleared = 0x0E,
    SurvivalFirstKill = 0x0F,
    SurvivalLastKill = 0x10,
    SurvivalDeath = 0x11,
    BossFirstKill = 0x12,
    BossLastKill = 0x13,
    BossDeath = 0x14,
    JessiumScore = 0x15,
    Zim16 = 0x16,
    Zim17 = 0x17,
    SzmScore = 0x18,
}

public sealed class MatchLog
{
    private readonly Dictionary<uint, PlayerLog> _players = [];

    public IReadOnlyDictionary<uint, PlayerLog> Players => _players;

    public uint FirstKiller { get; private set; }

    public uint LastKiller { get; private set; }

    public int RedScore { get; private set; }

    public int BlueScore { get; private set; }

    public bool HasTeamScore { get; private set; }

    public int LastWave { get; private set; }

    public int Stars { get; private set; }

    public bool StageCleared { get; private set; }

    public bool HasCommonResults { get; private set; }

    public int ZimHeaderA { get; private set; }

    public int ZimHeaderB { get; private set; }

    public bool Finished { get; set; }

    /// <summary>When the first player was in the match (server clock) - what every report is measured against.</summary>
    public DateTime? StartedUtc { get; set; }

    public DateTime? WaveClearedUtc { get; set; }

    /// <summary>The host's end-of-match statistics are taken once per match.</summary>
    public bool StatisticsApplied { get; set; }

    public DateTime? EndedUtc { get; set; }

    public PlayerLog Of(uint playerId)
    {
        if (!_players.TryGetValue(playerId, out PlayerLog? log)) _players[playerId] = log = new PlayerLog();
        return log;
    }

    public PlayerLog? Find(uint playerId) => _players.GetValueOrDefault(playerId);

    public string? Apply(HostEvent code, ReadOnlySpan<byte> body)
    {
        DateTime now = DateTime.UtcNow;
        switch (code)
        {
            case HostEvent.Kill when body.Length >= 13:
            {
                uint killer = U32(body, 0), assister = U32(body, 4), victim = U32(body, 8);
                bool revenge = body[12] != 0;
                PlayerLog log = Of(killer);
                log.Kills++;
                if (revenge) log.Revenges++;
                if (assister != 0) Of(assister).Assists++;
                if (victim != 0) Of(victim).Died(now);
                if (FirstKiller == 0) FirstKiller = killer;
                LastKiller = killer;
                return $"kill: {killer} killed {victim}{(assister != 0 ? $", assist {assister}" : "")}{(revenge ? ", revenge" : "")}";
            }
            case HostEvent.Death when body.Length >= 8:
            {
                uint assister = U32(body, 0), victim = U32(body, 4);
                if (assister != 0) Of(assister).Assists++;
                if (victim != 0) Of(victim).Died(now);
                return $"death: {victim} died without a killer{(assister != 0 ? $", assist {assister}" : "")}";
            }
            case HostEvent.ChargerUsed when body.Length >= 4:
                Of(U32(body, 0)).Chargers++;
                return $"charger used by {U32(body, 0)}";
            case HostEvent.ItemUsed when body.Length >= 6:
                Of(U32(body, 0)).Items++;
                return $"item {U16(body, 4)} used by {U32(body, 0)}";
            case HostEvent.InventoryItemUsed when body.Length >= 5:
                return $"inventory item of type {body[4]} used by {U32(body, 0)}";
            case HostEvent.CommonResults when body.Length >= 1:
            {
                int count = body[0];
                for (int i = 0, at = 1; i < count && at + 9 <= body.Length; i++, at += 9)
                {
                    PlayerLog log = Of(U32(body, at));
                    log.AttackPercent = body[at + 4];
                    log.MaxCombo = U16(body, at + 5);
                    log.ExtraA = (sbyte)body[at + 7];
                    log.ExtraB = (sbyte)body[at + 8];
                }
                HasCommonResults = true;
                return $"results of {count} player(s): attack share and best combo";
            }
            case HostEvent.SurvivalResults or HostEvent.BossResults when body.Length >= 1:
            {
                int count = body[0];
                for (int i = 0, at = 1; i < count && at + 7 <= body.Length; i++, at += 7)
                {
                    PlayerLog log = Of(U32(body, at));
                    log.Slays = U16(body, at + 4);
                    log.Revives = body[at + 6];
                }
                return $"stage results of {count} player(s): slays and revives";
            }
            case HostEvent.JessiumResults when body.Length >= 1:
            {
                int count = body[0];
                for (int i = 0, at = 1; i < count && at + 6 <= body.Length; i++, at += 6)
                {
                    PlayerLog log = Of(U32(body, at));
                    log.Jessium = body[at + 4];
                    log.JessiumExtra = body[at + 5];
                }
                return $"jessium results of {count} player(s)";
            }
            case HostEvent.ZimResults when body.Length >= 3:
            {
                ZimHeaderA = (sbyte)body[0];
                ZimHeaderB = (sbyte)body[1];
                int count = body[2];
                for (int i = 0, at = 3; i < count && at + 5 <= body.Length; i++, at += 5)
                    Of(U32(body, at)).Survival = body[at + 4];
                return $"zombie mode results of {count} player(s)";
            }
            case HostEvent.SzmResults when body.Length >= 5:
            {
                RedScore = U16(body, 0);
                BlueScore = U16(body, 2);
                HasTeamScore = true;
                int count = body[4];
                for (int i = 0, at = 5; i < count && at + 6 <= body.Length; i++, at += 6)
                    Of(U32(body, at)).Points = U16(body, at + 4);
                return $"point results of {count} player(s), teams {RedScore} - {BlueScore}";
            }
            case HostEvent.WaveCleared when body.Length >= 2:
                LastWave = Math.Max(LastWave, body[0]);
                Stars += Math.Max(0, (int)(sbyte)body[1]);
                return $"wave {body[0]} cleared with {(sbyte)body[1]} star(s)";
            case HostEvent.SurvivalFirstKill or HostEvent.BossFirstKill when body.Length >= 4:
                if (FirstKiller == 0) FirstKiller = U32(body, 0);
                return $"first slay by {U32(body, 0)}";
            case HostEvent.SurvivalLastKill or HostEvent.BossLastKill when body.Length >= 4:
                LastKiller = U32(body, 0);
                StageCleared = true;
                return $"last slay by {U32(body, 0)}";
            case HostEvent.SurvivalDeath or HostEvent.BossDeath when body.Length >= 4:
                Of(U32(body, 0)).Died(now);
                return $"player {U32(body, 0)} died";
            case HostEvent.JessiumScore when body.Length >= 2:
                RedScore = (sbyte)body[0];
                BlueScore = (sbyte)body[1];
                HasTeamScore = true;
                return $"jessium score {RedScore} - {BlueScore}";
            case HostEvent.SzmScore when body.Length >= 4:
                RedScore = U16(body, 0);
                BlueScore = U16(body, 2);
                HasTeamScore = true;
                return $"points {RedScore} - {BlueScore}";
            case HostEvent.RoundStarted:
                return "round started";
            case HostEvent.RoundEnded:
                return "round ended";
            case HostEvent.Zim16 or HostEvent.Zim17 when body.Length >= 4:
                return $"zombie mode event {(byte)code:X2} for {U32(body, 0)}";
            default:
                return null;
        }
    }

    private static uint U32(ReadOnlySpan<byte> body, int at) => BinaryPrimitives.ReadUInt32LittleEndian(body[at..]);

    private static ushort U16(ReadOnlySpan<byte> body, int at) => BinaryPrimitives.ReadUInt16LittleEndian(body[at..]);
}
