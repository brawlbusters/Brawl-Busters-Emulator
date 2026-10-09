using BrawlBusters.Core.Data;
using BrawlBusters.Core.Logging;
using BrawlBusters.Core.Network;
using BrawlBusters.Core.Protocol;
using BrawlBusters.Core.Protocol.Packets;
using BrawlBusters.Core.Sessions.Handlers;

namespace BrawlBusters.Core.Sessions;

public static partial class GameFlow
{
    private const byte GameResult = 0x03;
    private const byte GameResultObserver = 0x04;

    private const int LadderWinPoints = 20;
    private const int LadderLossPoints = 10;

    private const int StarsFast = 4;
    private const double StarFastShare = 50;
    private const double StarGoodShare = 80;
    private const double StarInTimeShare = 100;

    private static readonly TimeSpan HostReportWait = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan ResultScreenLimit = TimeSpan.FromSeconds(45);

    public static void HostEventReported(ClientSession session, Room room, byte code, byte[] body)
    {
        if (!room.IsHost(session)) return;

        string? what = room.Log.Apply((HostEvent)code, body);
        if (what is null) Log.Debug(session.Tag, $"Match event 0x{code:X2}: {Log.Hex(body)}");
        else Log.Info(session.Tag, $"Room {room.Id}: {what}");
    }

    public static Task GameEndedAsync(ClientSession session, CancellationToken cancellationToken)
    {
        Room? room = session.Room;
        if (room is null || !room.IsHost(session)) return Task.CompletedTask;

        MatchLog log = room.Log;
        log.EndedUtc ??= DateTime.UtcNow;
        Log.Info(session.Tag, $"Room {room.Id}: the match on map {room.PlayedMapId} is over - waiting for the host's report");
        World.Later(HostReportWait, async () =>
        {
            if (log.Finished || room.Log != log) return;

            Log.Warn($"Room {room.Id}", "The host did not close the match - results are made from what was reported");
            await ShowResultsAsync(room, CancellationToken.None);
        });
        return Task.CompletedTask;
    }

    public static async Task MatchResultsAsync(ClientSession session, Room room, byte[] report, CancellationToken cancellationToken)
    {
        if (!room.IsHost(session)) return;

        List<PlayerStatistics>? statistics = HostStatistics.Parse(report);
        if (statistics is null)
        {
            Log.Warn(session.Tag, $"Room {room.Id}: host statistics of {report.Length} byte(s) do not have the expected shape - not used: {Log.Hex(report)}");
            return;
        }

        GameData.Instance.Rules.TryGetValue(room.RuleId, out RuleInfo? ruleInfo);

        foreach (RoomMember member in room.Members.Where(member => !member.IsObserver))
        {
            PlayerStatistics? own = statistics.FirstOrDefault(player => player.UserId == member.Session.Account.Id);
            if (own is null) continue;

            (bool Changed, List<uint> RewardIds, List<InventoryItem> Items) missions = (false, [], []);
            member.Session.Accounts.Update(member.Session.Account.Id, account =>
            {
                missions = DailyMissions.Progress(account, new MissionFacts(room.Mode) { Difficulty = ruleInfo?.Difficulty ?? 0, Statistics = own });
                RecordBook book = account.Records;
                if (!book.HasHostStatistics)
                {
                    book.HasHostStatistics = true;
                    Array.Clear(book.ClassSeconds);
                    Array.Clear(book.ClassKills);
                }

                for (int i = 0; i < PlayerStatistics.Classes; i++)
                {
                    book.ClassSeconds[i] += own.SecondsAs(i);
                    book.ClassKills[i] += own.KillsAs(i);
                    book.ClassAssists[i] += own.AssistsAs(i);
                    book.ClassSlays[i] += own.MobKillsAs(i);
                    book.ClassRevives[i] += own.RevivesAs(i);
                    book.ClassTries[i] += own.TriesAs(i);
                    book.ClassHits[i] += own.HitsAs(i);
                    book.ClassKillsOf[i] += own.KillsOf(i);
                    book.ClassDeathsBy[i] += own.DeathsBy(i);
                }
                for (int cause = 0; cause < PlayerStatistics.Causes; cause++)
                {
                    book.KillsByCause[cause] += own.KillsByCause(cause);
                    book.MobKillsByCause[cause] += own.MobKillsByCause(cause);
                }
                book.Deaths += own.Deaths;
            });
            member.Session.RefreshAccount();
            Log.Info(member.Session.Tag, $"Match statistics: {own.Kills} kill(s), {own.Assists} assist(s), {own.Deaths} death(s), {own.MobKills} mob kill(s), {own.Revives} revive(s)");
            await SendMissionProgressAsync(member.Session, missions, cancellationToken);
        }
    }

    public static async Task MatchClosedAsync(ClientSession session, CancellationToken cancellationToken)
    {
        Room? room = session.Room;
        if (room is null || !room.IsHost(session)) return;

        Log.Info(session.Tag, $"Room {room.Id}: match closed by its host");
        await ShowResultsAsync(room, cancellationToken);
    }

    private static async Task ShowResultsAsync(Room room, CancellationToken cancellationToken)
    {
        MatchLog log = room.Log;
        lock (log)
        {
            if (log.Finished) return;
            log.Finished = true;
        }

        List<RoomMember> members = room.Members;
        List<RoomMember> played = members.Where(member => member.Session.InMatch).ToList();
        MatchSummary summary = Summarize(room, played.Where(member => !member.IsObserver).ToList());
        await PayAsync(room, summary, played, cancellationToken);

        room.HostLoaded = false;
        room.MatchStartedUtc = null;
        room.State = RoomPacket.StateOf(RoomPhase.Created);
        foreach (RoomMember member in members)
        {
            member.Session.InMatch = false;
            member.Intruding = false;
            member.HostIndex = -1;
            member.Status = room.IsHost(member.Session) ? RoomPacket.StatusWaiting : RoomPacket.StatusNotReady;
        }

        byte[] shared = ResultMessage.Public(summary);
        bool extraByte = ResultMessage.HasExtraPrivateByte(room.Mode);
        foreach (RoomMember member in members)
        {
            if (member.GmObserver)
            {
                Log.Info(member.Session.Tag, $"Room {room.Id}: the watched match is over - the game master goes back to the lobby");
                await LeaveRoomAsync(member.Session, afterMatch: false, cancellationToken);
                continue;
            }

            if (!played.Contains(member) || summary.Rows.Count == 0)
            {
                await ReturnMemberAsync(room, member, cancellationToken);
                continue;
            }

            member.InResult = true;
            var message = new PacketWriter(MsgCategory.sGame, member.IsObserver ? GameResultObserver : GameResult).WriteBytes(shared);
            if (!member.IsObserver)
            {
                Account account = member.Session.Account;
                MatchPayout payout = member.Payout ?? new MatchPayout(account.Experience, 0, 0, account.Gold, 0, 0, false, 0, 0);
                message.WriteBytes(payout.ToPrivateRecord(extraByte));
            }
            await TrySendAsync(member.Session, ModePacket.Build(GameMode.GameResult), cancellationToken);
            await TrySendAsync(member.Session, message, cancellationToken);
        }

        World.Later(ResultScreenLimit, async () =>
        {
            foreach (RoomMember member in room.Members.Where(member => member.InResult && room.Log == log))
            {
                Log.Info(member.Session.Tag, $"Room {room.Id}: still on the result screen - sent back to the waiting room");
                await ReturnMemberAsync(room, member, CancellationToken.None);
            }
        });
    }

    public static async Task ReturnToRoomAsync(ClientSession session, CancellationToken cancellationToken)
    {
        Room? room = session.Room;
        RoomMember? member = room?.Find(session);
        if (room is null || member is null || !member.InResult)
        {
            Log.Debug(session.Tag, "Return to the room asked outside a result screen");
            return;
        }

        Log.Info(session.Tag, $"Room {room.Id}: result screen closed - back in the waiting room");
        await ReturnMemberAsync(room, member, cancellationToken);
    }

    private static async Task ReturnMemberAsync(Room room, RoomMember member, CancellationToken cancellationToken)
    {
        member.InResult = false;
        await TrySendAsync(member.Session, RoomPacket.State(room, RoomPhase.Created, Relay(member.Session), Slots(room)), cancellationToken);
        await TrySendAsync(member.Session, ModePacket.Build(WaitingMode(room)), cancellationToken);
    }

    private static bool IsStageMode(MatchMode mode) => mode is MatchMode.Survival or MatchMode.Bsr;

    private static MatchSummary Summarize(Room room, List<RoomMember> players)
    {
        GameData data = GameData.Instance;
        MatchLog log = room.Log;
        DateTime end = log.EndedUtc ?? DateTime.UtcNow;
        int seconds = room.MatchStartedUtc is { } started ? Math.Max(0, (int)(end - started).TotalSeconds) : 0;
        data.Rules.TryGetValue(room.RuleId, out RuleInfo? rule);
        bool stage = IsStageMode(room.Mode);
        bool formal = stage ? players.Count >= 1 : players.Count >= 2;

        var rows = new List<ResultRow>();
        foreach (RoomMember member in players)
        {
            Account account = member.Session.Account;
            PlayerLog own = log.Of(account.Id);
            rows.Add(new ResultRow
            {
                UserId = account.Id,
                Nickname = account.Nickname,
                Level = account.DisplayLevel,
                LadderLevel = account.GemRank,
                Team = member.Team,
                Kills = own.Kills,
                Assists = own.Assists,
                Deaths = own.Deaths,
                Revenges = own.Revenges,
                Attack = own.AttackPercent,
                MaxCombo = own.MaxCombo,
                Items = own.Items,
                Chargers = own.Chargers,
                Slays = own.Slays,
                Revives = own.Revives,
                Jessium = own.Jessium,
                JessiumExtra = own.JessiumExtra,
                Points = own.Points,
                Survival = own.Survival,
                LongestLife = own.LongestLifeAt(end),
            });
        }

        int red = 0, blue = 0;
        if (room.HasTeams)
        {
            if (log.HasTeamScore) (red, blue) = (log.RedScore, log.BlueScore);
            else if (room.Mode == MatchMode.Jessium) (red, blue) = (TeamSum(rows, 0, row => row.Jessium), TeamSum(rows, 1, row => row.Jessium));
            else if (room.Mode == MatchMode.Szm) (red, blue) = (TeamSum(rows, 0, row => row.Points), TeamSum(rows, 1, row => row.Points));
            else (red, blue) = (TeamSum(rows, 0, row => row.Kills), TeamSum(rows, 1, row => row.Kills));
        }

        int lastWave = log.LastWave;
        int maxWave = Math.Max(rule?.Waves ?? 0, lastWave);
        bool success = room.Mode switch
        {
            MatchMode.Survival => rule is { Waves: > 0 } ? lastWave >= rule.Waves : log.StageCleared && lastWave > 0,
            MatchMode.Bsr => log.StageCleared,
            _ => false,
        };
        int stars = room.Mode == MatchMode.Bsr ? (success ? TimeStars(seconds, rule?.Time ?? 0) : 0) : log.Stars;
        int grade = room.Mode == MatchMode.Bsr ? (success ? 1 + stars : ResultMessage.BossFailed) : 0;
        bool perfect = room.Mode switch
        {
            MatchMode.Survival => success && lastWave > 0 && stars >= StarsFast * lastWave,
            MatchMode.Bsr => success && stars >= StarsFast,
            _ => room.HasTeams && red != blue && Math.Min(red, blue) == 0,
        };

        Func<ResultRow, int>[] columns = Columns(room.Mode);
        List<ResultRow> ranking = rows
            .OrderByDescending(columns[0]).ThenByDescending(columns[1]).ThenByDescending(columns[2])
            .ToList();
        for (int i = 0; i < ranking.Count; i++) ranking[i].Rank = i;

        foreach (ResultRow row in rows)
        {
            row.Outcome = room.Mode switch
            {
                _ when !formal => 0,
                MatchMode.Survival or MatchMode.Bsr => success ? 1 : -1,
                MatchMode.FreeForAll => row.Rank == 0 ? 1 : -1,
                _ when room.HasTeams => red == blue ? 0 : ((row.Team == 0 ? red : blue) > (row.Team == 0 ? blue : red) ? 1 : -1),
                _ => 0,
            };
        }

        if (formal) GiveTitles(room, log, rows, columns, perfect, stage);

        data.Maps.TryGetValue(room.PlayedMapId, out MapInfo? map);
        Log.Info($"Room {room.Id}", room.Mode switch
        {
            MatchMode.Survival => $"Result: survival {(success ? "cleared" : "failed")} at wave {lastWave}/{maxWave} with {stars} star(s) after {seconds} s",
            MatchMode.Bsr => $"Result: boss battle {(success ? $"cleared with grade {grade}" : "failed")} after {seconds} s",
            _ when room.HasTeams => $"Result: teams {red} - {blue}{(perfect ? " (perfect)" : "")} after {seconds} s",
            _ => $"Result: {rows.Count} player(s) after {seconds} s",
        });

        return new MatchSummary
        {
            Mode = room.Mode,
            Ladder = room.IsLadder,
            Formal = formal,
            Rows = rows,
            Red = red,
            Blue = blue,
            Perfect = perfect,
            Success = success,
            Seconds = seconds,
            LastWave = lastWave,
            MaxWave = maxWave,
            Stars = stars,
            Grade = grade,
            BossName = map?.Name ?? "",
        };
    }

    private static int TeamSum(List<ResultRow> rows, byte team, Func<ResultRow, int> value) => rows.Where(row => row.Team == team).Sum(value);

    private static int TimeStars(int seconds, int limit)
    {
        if (limit <= 0) return 1;
        double share = seconds * 100.0 / limit;
        return share < StarFastShare ? 4 : share < StarGoodShare ? 3 : share < StarInTimeShare ? 2 : 1;
    }

    private static Func<ResultRow, int>[] Columns(MatchMode mode) => mode switch
    {
        MatchMode.Survival => [row => row.Slays, row => row.Revives, row => row.Attack],
        MatchMode.Bsr => [row => row.Attack, row => row.Revives, row => row.Slays],
        MatchMode.Jessium => [row => row.Jessium, row => row.Kills, row => row.Attack],
        MatchMode.Zim => [row => row.Survival, row => row.Kills, row => row.Assists],
        MatchMode.Szm => [row => row.Points, row => row.Kills, row => row.Assists],
        _ => [row => row.Kills, row => row.Assists, row => row.Attack],
    };

    private static ResultFlag[] ColumnCrowns(MatchMode mode) => mode switch
    {
        MatchMode.Survival => [ResultFlag.SlayCrown, ResultFlag.ReviveCrown, ResultFlag.AttackCrown],
        MatchMode.Bsr => [ResultFlag.AttackCrown, ResultFlag.ReviveCrown, ResultFlag.SlayCrown],
        MatchMode.Jessium => [ResultFlag.JessiumCrown, ResultFlag.KillCrown, ResultFlag.AttackCrown],
        MatchMode.Zim or MatchMode.Szm => [ResultFlag.None, ResultFlag.KillCrown, ResultFlag.AssistCrown],
        _ => [ResultFlag.KillCrown, ResultFlag.AssistCrown, ResultFlag.AttackCrown],
    };

    private static void GiveTitles(Room room, MatchLog log, List<ResultRow> rows, Func<ResultRow, int>[] columns, bool perfect, bool stage)
    {
        static void Best(List<ResultRow> rows, Func<ResultRow, int> value, ResultFlag flag)
        {
            if (flag == ResultFlag.None || rows.Count == 0) return;
            int top = rows.Max(value);
            if (top <= 0) return;
            foreach (ResultRow row in rows.Where(row => value(row) == top)) row.Flags |= flag;
        }

        ResultFlag[] crowns = ColumnCrowns(room.Mode);
        for (int i = 0; i < columns.Length; i++) Best(rows, columns[i], crowns[i]);
        ResultFlag columnMask = crowns.Aggregate(ResultFlag.None, (mask, flag) => mask | flag);
        foreach (ResultRow row in rows)
        {
            int count = System.Numerics.BitOperations.PopCount((uint)(row.Flags & columnMask));
            row.Flags |= count switch
            {
                1 => ResultFlag.Crown1,
                2 => ResultFlag.Crown2,
                >= 3 => ResultFlag.Crown3,
                _ => ResultFlag.None,
            };
        }

        Best(rows, row => row.Items, ResultFlag.ItemMania);
        Best(rows, row => row.Chargers, ResultFlag.ChargerMania);
        Best(rows, row => row.MaxCombo, ResultFlag.InfiniteCombo);

        foreach (ResultRow row in rows)
        {
            if (row.UserId == log.FirstKiller) row.Flags |= ResultFlag.FirstKill;
            if (row.UserId == log.LastKiller) row.Flags |= ResultFlag.LastKill;
            if (!stage && row.Revenges > 0) row.Flags |= ResultFlag.Revenge;
            if (row.Deaths == 0) row.Flags |= ResultFlag.Immortal;
            if (perfect && row.Outcome > 0) row.Flags |= ResultFlag.PerfectWin;
        }

        List<ResultRow> mortal = rows.Where(row => row.Deaths > 0).ToList();
        if (mortal.Count > 0 && rows.Count > 1)
            mortal.MaxBy(row => row.LongestLife)!.Flags |= ResultFlag.LongLife;
    }

    private static int PayoutOf(PayoutRule? rule, MatchSummary summary, ResultRow row, RuleInfo? ruleInfo)
    {
        if (rule is null || summary.Seconds < rule.TimeMin) return 0;

        bool stage = IsStageMode(summary.Mode);
        int players = summary.Rows.Count;
        double amount = 0;
        if (rule.Outcome.Count > 0)
        {
            int index = summary.Mode == MatchMode.FreeForAll
                ? row.Rank
                : row.Outcome > 0 ? 0 : row.Outcome < 0 ? 1 : 2;
            amount += rule.Outcome[Math.Clamp(index, 0, rule.Outcome.Count - 1)];
        }

        amount += rule.Kill * (stage ? row.Slays : row.Kills);
        amount += rule.Assist * row.Assists;
        amount += rule.Attack * row.Attack;
        amount += rule.Revive * row.Revives;
        amount += rule.Jessium * row.Jessium;
        amount += rule.Survival * row.Survival;
        amount += rule.Wave * summary.LastWave;
        amount += rule.Star * summary.Stars;

        int crown = row.Has(ResultFlag.Crown3) ? 3 : row.Has(ResultFlag.Crown2) ? 2 : row.Has(ResultFlag.Crown1) ? 1 : 0;
        if (crown < rule.Crown.Count) amount += rule.Crown[crown];
        if (row.Has(ResultFlag.PerfectWin)) amount += rule.Perfect;
        if (row.Has(ResultFlag.Immortal)) amount += rule.Immortal;
        if (row.Has(ResultFlag.LongLife)) amount += rule.LongLife;
        if (row.Has(ResultFlag.LastKill)) amount += rule.LastKill;
        if (row.Has(ResultFlag.FirstKill)) amount += rule.FirstKill;
        if (row.Has(ResultFlag.InfiniteCombo)) amount += rule.Combo;
        if (row.Has(ResultFlag.ItemMania)) amount += rule.Item;
        if (row.Has(ResultFlag.ChargerMania)) amount += rule.Charger;
        if (row.Has(ResultFlag.Revenge)) amount += rule.Revenge;

        if (rule.Member.Count > 0) amount *= rule.Member[Math.Clamp(players, 0, rule.Member.Count - 1)];
        if (rule.Difficulty.Count > 1) amount *= rule.Difficulty[Math.Clamp(ruleInfo?.Difficulty ?? 0, 0, rule.Difficulty.Count - 1)];
        return (int)Math.Round(Math.Max(0, amount));
    }

    private static async Task PayAsync(Room room, MatchSummary summary, List<RoomMember> played, CancellationToken cancellationToken)
    {
        GameData data = GameData.Instance;
        data.Results.Payouts.TryGetValue((byte)room.Mode, out ModePayout? payout);
        data.Results.Bonus.TryGetValue(room.PlayedMapId, out BonusRule? bonus);
        data.Rules.TryGetValue(room.RuleId, out RuleInfo? ruleInfo);
        MatchLog log = room.Log;
        string? key = RecordBook.KeyOf(room.Mode, room.IsLadder);

        foreach (RoomMember member in played)
        {
            ClientSession player = member.Session;
            ResultRow? row = summary.Of(player.Account.Id);
            if (member.IsObserver || row is null)
            {
                await TrySendAsync(player, UserInfoPacket.PartialEmpty(), cancellationToken);
                continue;
            }

            member.WinStreak = row.Outcome > 0 ? member.WinStreak + 1 : row.Outcome < 0 ? 0 : member.WinStreak;

            (int expBoost, int goldBoost) = Boosters(player.Account);
            int baseExp = summary.Formal ? PayoutOf(payout?.Exp, summary, row, ruleInfo) : 0;
            int baseGold = summary.Formal ? PayoutOf(payout?.Gold, summary, row, ruleInfo) : 0;
            int exp = baseExp * (100 + expBoost) / 100;
            int gold = baseGold * (100 + goldBoost) / 100;
            int expBefore = player.Account.Experience;
            int goldBefore = player.Account.Gold;
            int ladderPoints = room.IsLadder && row.Outcome > 0 ? LadderWinPoints : 0;
            uint rewardId = summary.Formal && bonus is not null && summary.Seconds >= bonus.TimeMin && bonus.Rewards.Count > 0
                ? bonus.Rewards[Math.Min(Dice.Weighted(bonus.Prob), bonus.Rewards.Count - 1)]
                : 0;
            int score = IsStageMode(room.Mode) ? row.Slays : row.Kills;

            InventoryItem? rewardItem = null;
            List<InventoryItem> levelItems = [];
            (bool Changed, List<uint> RewardIds, List<InventoryItem> Items) missions = (false, [], []);
            byte levelBefore = player.Account.DisplayLevel;
            player.Accounts.Update(player.Account.Id, account =>
            {
                account.Experience += exp;
                account.Gold += gold;
                account.Records.Played(room.Mode, account.Character?.Class ?? 0, summary.Seconds, score, row.Rank, summary.Formal);
                if (summary.Formal)
                {
                    account.Stats.Add(row.Outcome, score);
                    foreach (MatchStats stats in new[] { account.StatsOf(room.Mode), account.TodayOf(room.Mode) })
                    {
                        stats.Add(row.Outcome, score);
                        stats.Assists += row.Assists;
                    }
                    if (room.IsLadder)
                    {
                        account.Ladder.Add(row.Outcome, score);
                        account.LadderPoints = Math.Max(0, account.LadderPoints + (row.Outcome > 0 ? LadderWinPoints : row.Outcome < 0 ? -LadderLossPoints : 0));
                    }
                    if (key is not null) ApplyToRecords(account.Records, key, room.Mode, summary, row);
                }
                else
                {
                    account.Stats.Matches++;
                    foreach (MatchStats stats in new[] { account.StatsOf(room.Mode), account.TodayOf(room.Mode) }) stats.Matches++;
                }
                account.Level = data.LevelForExp(account.Experience, account.Level);
                if (room.IsLadder) account.GemRank = LadderGrades.GradeOf(account.LadderPoints, LadderGrades.Table(player));

                missions = DailyMissions.Progress(account, new MissionFacts(room.Mode)
                {
                    Difficulty = ruleInfo?.Difficulty ?? 0,
                    Played = true,
                    Won = row.Outcome > 0,
                    Kills = summary.Formal ? row.Kills : 0,
                    Assists = summary.Formal ? row.Assists : 0,
                    Slays = summary.Formal ? row.Slays : 0,
                    Stars = summary.Formal ? summary.Stars : 0,
                    Medals = summary.Formal ? row.Flags : ResultFlag.None,
                });
                levelItems = Rewards.GrantForLevels(account);
                if (rewardId != 0) rewardItem = Rewards.Grant(account, rewardId);
            });
            player.RefreshAccount();
            await TrySendAsync(player, UserInfoPacket.HomeStatsChanged(HomeStats.Of(player.Account)), cancellationToken);

            bool levelUp = player.Account.DisplayLevel != levelBefore;
            row.Level = player.Account.DisplayLevel;
            if (levelUp) row.Flags |= ResultFlag.LevelUp;
            if (expBoost > 0 || goldBoost > 0) row.Flags |= ResultFlag.ItemBonus;
            PlayerLog own = log.Of(player.Account.Id);
            member.Payout = new MatchPayout(expBefore, baseExp, exp - baseExp, goldBefore, baseGold, gold - baseGold, levelUp, expBoost, goldBoost)
            {
                LadderPoints = ladderPoints,
                WinCount = room.Mode == MatchMode.Zim ? own.ExtraA : 0,
                LoseCount = room.Mode == MatchMode.Zim ? own.ExtraB : 0,
            };
            Log.Info(player.Tag, $"Match payout: +{exp} exp (booster +{expBoost}%), +{gold} BP (booster +{goldBoost}%), reward {rewardId}{(rewardItem is null ? "" : $" (item {rewardItem.ItemId})")}; "
                + $"{row.Kills} kill(s), {row.Assists} assist(s), {row.Deaths} death(s), attack {row.Attack}%, titles {row.Flags}");

            if (rewardId != 0) await TrySendAsync(player, RoomPacket.Reward(rewardId), cancellationToken);
            if (rewardItem is not null) await TrySendAsync(player, InventoryPacket.Added([rewardItem]), cancellationToken);
            if (levelItems.Count > 0) await TrySendAsync(player, InventoryPacket.Added(levelItems), cancellationToken);
            await SendMissionProgressAsync(player, missions, cancellationToken);
            await TrySendAsync(
                player,
                exp > 0 || gold > 0
                    ? UserInfoPacket.ExpAndGold((uint)player.Account.Experience, (uint)player.Account.Gold)
                    : UserInfoPacket.PartialEmpty(),
                cancellationToken);
            if (levelUp) await TrySendAsync(player, UserInfoPacket.Level(player.Account.DisplayLevel), cancellationToken);
        }
    }

    private const double GreatMarginShare = 0.5;
    private const double CloseMarginShare = 0.1;

    private static void ApplyToRecords(RecordBook book, string key, MatchMode mode, MatchSummary summary, ResultRow row)
    {
        ModeRecord record = book.ModeOf(key);
        uint flags = (uint)row.Flags;
        IReadOnlyList<int> bits = RecordsPacket.TitleBitsOf(key);
        switch (key)
        {
            case "jes":
                record.Add([row.Kills, row.Assists, row.Attack, row.Jessium], flags, bits, [row.Kills, row.Assists, row.Attack]);
                break;
            case "suv":
            case "bsr":
                record.Add([row.Slays, row.Revives, row.Attack], flags, bits, [row.Slays, row.Revives, row.Attack]);
                break;
            case "ladder":
                record.Add([row.Kills, row.Assists, row.Attack, row.Deaths], flags, bits, [row.Kills, row.Assists, row.Attack]);
                break;
            default:
                record.Add([row.Kills, row.Assists, row.Attack], flags, bits, [row.Kills, row.Assists, row.Attack]);
                break;
        }

        int crown = row.Has(ResultFlag.Crown3) ? 2 : row.Has(ResultFlag.Crown2) ? 1 : row.Has(ResultFlag.Crown1) ? 0 : -1;
        if (crown >= 0) book.CrownsOf(mode)[crown]++;

        if (key == "ffa")
        {
            book.Placements[Math.Clamp(row.Rank, 0, RecordBook.Places - 1)]++;
            if (row.Rank == 0 && row.Assists == 0 && row.Kills > 0) record.Extra[0]++;
            if (row.Kills == 0 && row.Has(ResultFlag.AssistCrown)) record.Extra[1]++;
            if (row.Kills == 0 && row.Has(ResultFlag.AttackCrown)) record.Extra[2]++;
        }

        if (key == "bsr" && summary.Success)
            book.BossClears[Math.Clamp(summary.Grade - 2, 0, RecordBook.BossGrades - 1)]++;

        if (key is "tdm" or "jes" or "ladder")
        {
            record.Streak(row.Outcome);
            int own = row.Team == 0 ? summary.Red : summary.Blue;
            int other = row.Team == 0 ? summary.Blue : summary.Red;
            int margin = Math.Abs(own - other);
            int top = Math.Max(own, other);
            bool perfect = margin > 0 && Math.Min(own, other) == 0;
            bool great = !perfect && top > 0 && margin >= top * GreatMarginShare;
            bool close = margin > 0 && margin <= Math.Max(1, top * CloseMarginShare);
            if (row.Outcome > 0)
            {
                if (perfect) record.PerfectWins++;
                if (great) record.GreatWins++;
                if (close) record.CloseWins++;
            }
            else if (row.Outcome < 0)
            {
                if (perfect) record.PerfectLosses++;
                if (great) record.GreatLosses++;
                if (close) record.CloseLosses++;
            }
        }
    }
}
