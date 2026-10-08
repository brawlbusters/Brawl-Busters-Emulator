using System.Net;
using BrawlBusters.Core.Data;
using BrawlBusters.Core.Logging;
using BrawlBusters.Core.Network;
using BrawlBusters.Core.Protocol;
using BrawlBusters.Core.Protocol.Packets;

namespace BrawlBusters.Core.Sessions;

public static partial class GameFlow
{
    private const ushort RecordedLevelId = 0x1F41;
    private const ushort RecordedRuleId = 0x1F42;

    private const ushort RecordedPlayedMapId = 0x20D2;

    private static ushort PlayedMap(Room room)
    {
        GameData data = GameData.Instance;
        if (!data.Maps.TryGetValue(room.LevelId, out MapInfo? map)) return data.Maps.Count == 0 ? RecordedPlayedMapId : room.LevelId;

        List<ushort> choices = map.RandomMaps
            .Where(id => data.Maps.TryGetValue(id, out MapInfo? candidate) && candidate.IsReleased)
            .ToList();
        return choices.Count == 0 ? map.Id : choices[Random.Shared.Next(choices.Count)];
    }

    public static async Task CreateRoomAsync(ClientSession session, string title, string password, MatchMode mode, byte maxPlayers, CancellationToken cancellationToken)
    {
        if (session.Room is not null) await LeaveQuietlyAsync(session);

        uint userId = session.Account.Id;

        GameData data = GameData.Instance;
        MapInfo? map = data.DefaultMap(mode, maxPlayers);
        ushort levelId = map?.Id ?? RecordedLevelId;
        ushort ruleId = map is null ? RecordedRuleId : data.DefaultRule(map);
        if (map is null)
            Log.Warn(session.Tag, $"No map for mode {(byte)mode}; using the recorded survival map");
        else
            maxPlayers = Math.Clamp(maxPlayers, map.MinPlayers, map.MaxPlayers);
        if (maxPlayers == 0) maxPlayers = 6;

        var player = HolePunchServer.FindPlayerEndPoint(userId) ?? session.Connection.RemoteEndPoint;
        Room room = RoomRegistry.Instance.Create(session.ChannelId, title, userId, player, mode, maxPlayers, levelId, ruleId);
        room.Password = password;
        room.Join(session);
        session.Room = room;
        Log.Info(session.Tag, $"Room {room.Id}: {mode}, {maxPlayers} players, map {levelId} ({map?.Name ?? "?"}), rule {ruleId}");

        await session.SendAsync(RoomPacket.Entered(room, Relay(session), Slots(room), []), cancellationToken);
        await BroadcastStateAsync(room, RoomPhase.Created, cancellationToken);
        await session.SendAsync(ModePacket.Build(GameMode.WaitingRoom), cancellationToken);
    }

    public static async Task JoinRoomAsync(ClientSession session, ushort roomId, string password, CancellationToken cancellationToken)
    {
        Room? room = RoomRegistry.Instance.Find(roomId);
        NetError? refusal = room switch
        {
            null => NetError.Lobby_NotExistRoom,
            _ when room.ChannelId != session.ChannelId => NetError.Lobby_NotExistRoom,
            _ when room.Password.Length > 0 && room.Password != password => NetError.Lobby_WrongPassword,
            _ when room.State != RoomPacket.StateOf(RoomPhase.Created) => NetError.Lobby_CannotJoinRoom,
            _ when room.PlayerCount >= room.MaxPlayers => NetError.Lobby_CannotJoinRoom,
            _ => null,
        };

        if (refusal is null && room!.IsBotRoom)
        {
            if (BotDirector.Claim(room, out var bots))
            {
                room.SeatBots(bots);
                room.HostUserId = session.Account.Id;
                room.HostEndPoint = HolePunchServer.FindPlayerEndPoint(session.Account.Id) ?? session.Connection.RemoteEndPoint;
                Log.Info(session.Tag, $"Took over bot room {room.Id} with {bots.Count} bot(s)");
            }
            else
            {
                refusal = NetError.Lobby_CannotJoinRoom;
            }
        }

        RoomMember? joined = refusal is null ? room!.Join(session) : null;
        if (refusal is null && joined is null) refusal = NetError.Lobby_CannotJoinRoom;
        if (refusal is not null || room is null)
        {
            Log.Info(session.Tag, $"Join room {roomId} refused: {refusal}");
            await session.SendAsync(LobbyPacket.Error(refusal ?? NetError.Lobby_CannotJoinRoom), cancellationToken);
            return;
        }

        session.Room = room;
        List<RoomMember> others = room.Members.Where(member => member.Session != session).ToList();
        Log.Info(session.Tag, $"Joined room {room.Id} in slot {joined!.Slot} ({room.PlayerCount}/{room.MaxPlayers})");

        byte[] ownRecord = RecordPart(session);
        foreach (RoomMember other in others)
        {
            await TrySendAsync(other.Session, RoomPacket.PlayerJoined(session.Account.Id, ownRecord), cancellationToken);
            await TrySendAsync(other.Session, RoomPacket.State(room, RoomPhase.Created, Relay(session), Slots(room)), cancellationToken);
        }

        var records = others.Select(other => (other.Session.Account.Id, RecordPart(other.Session))).ToList();
        records.AddRange(room.Bots.Select(bot => (bot.Id, BotRecordPart(bot))));
        await session.SendAsync(RoomPacket.Entered(room, Relay(session), Slots(room), records), cancellationToken);
        await session.SendAsync(RoomPacket.State(room, RoomPhase.Created, Relay(session), Slots(room)), cancellationToken);
        await session.SendAsync(ModePacket.Build(GameMode.WaitingRoom), cancellationToken);
    }

    public static Task RoomInfoRequestedAsync(ClientSession session, ushort roomId, CancellationToken cancellationToken)
    {
        Room? room = RoomRegistry.Instance.Find(roomId);
        if (room is null)
            return session.SendAsync(LobbyPacket.Error(NetError.Lobby_NotExistRoomInfo), cancellationToken);

        var players = room.Members
            .Where(member => !member.IsObserver)
            .Select(member => (member.Team, member.Session.Account.Nickname))
            .Concat(room.Bots.Select(bot => (bot.Team, bot.Name)))
            .ToList();
        return session.SendAsync(LobbyPacket.RoomInfo(room, players), cancellationToken);
    }

    public static Task RoomLevelRequestedAsync(ClientSession session, Room room, ushort levelId, CancellationToken cancellationToken)
    {
        GameData data = GameData.Instance;
        if (!room.IsHost(session))
        {
            Log.Warn(session.Tag, $"Room {room.Id}: only the host can change the map");
        }
        else if (data.Maps.Count == 0)
        {
            room.LevelId = levelId;
        }
        else if (data.Maps.TryGetValue(levelId, out MapInfo? map) && map.Mode == room.Mode && map.IsReleased)
        {
            room.LevelId = levelId;
            if (!map.Rules.Contains(room.RuleId)) room.RuleId = data.DefaultRule(map);
            Log.Info(session.Tag, $"Room {room.Id}: map {levelId} ({map.Name})");
        }
        else
        {
            Log.Warn(session.Tag, $"Room {room.Id}: map {levelId} is not a {room.Mode} map - keeping {room.LevelId}");
        }
        return RoomSettingsChangedAsync(session, room, cancellationToken);
    }

    public static Task RoomRuleRequestedAsync(ClientSession session, Room room, ushort ruleId, CancellationToken cancellationToken)
    {
        GameData data = GameData.Instance;
        if (!room.IsHost(session))
        {
            Log.Warn(session.Tag, $"Room {room.Id}: only the host can change the rule");
        }
        else if (!data.Maps.TryGetValue(room.LevelId, out MapInfo? map) || map.Rules.Contains(ruleId))
        {
            room.RuleId = ruleId;
            Log.Info(session.Tag, $"Room {room.Id}: rule {ruleId}");
        }
        else
        {
            room.RuleId = data.DefaultRule(map);
            Log.Warn(session.Tag, $"Room {room.Id}: rule {ruleId} is not allowed on map {map.Id} - using {room.RuleId}");
        }
        return RoomSettingsChangedAsync(session, room, cancellationToken);
    }

    public static Task RoomSettingsChangedAsync(ClientSession session, Room room, CancellationToken cancellationToken)
        => BroadcastStateAsync(room, RoomPhase.Settings, cancellationToken);

    public static async Task RoomReadyAsync(ClientSession session, Room room, CancellationToken cancellationToken)
    {
        if (!room.IsHost(session))
        {
            Log.Info(session.Tag, $"Room {room.Id}: guest ready");
            await BroadcastStateAsync(room, RoomPhase.Settings, cancellationToken);
            return;
        }

        if (await RefuseStartAsync(session, room, cancellationToken)) return;

        await DismissBotsAsync(room);

        await BroadcastStateAsync(room, RoomPhase.Ready, cancellationToken);
        await BroadcastAsync(room, _ => RoomPacket.ReadyAccepted(), cancellationToken);
    }

    public static async Task RoomStartAsync(ClientSession session, Room room, CancellationToken cancellationToken)
    {
        if (!room.IsHost(session))
        {
            Log.Warn(session.Tag, $"Room {room.Id}: only the host can start");
            return;
        }

        if (await RefuseStartAsync(session, room, cancellationToken)) return;

        await DismissBotsAsync(room);

        var hostLocal = HolePunchServer.FindPlayerLocalEndPoint(room.HostUserId) ?? room.HostEndPoint;
        ushort playedMap = PlayedMap(room);
        room.PlayedMapId = playedMap;
        room.MatchStartedUtc = null;
        IPEndPoint relay = Relay(session);
        room.HostLoaded = false;
        Log.Info(session.Tag, $"Room {room.Id}: playing map {playedMap} with {room.Members.Count} player(s)");

        await BroadcastAsync(room, _ => RoomPacket.GameStarting(room, hostLocal, relay, playedMap), cancellationToken);
        await BroadcastStateAsync(room, RoomPhase.Starting, cancellationToken);
        await BroadcastAsync(room, _ => ModePacket.Build(GameMode.ReadyRoom), cancellationToken);
    }

    private static readonly TimeSpan HostHeadStart = TimeSpan.FromMilliseconds(700);

    public static async Task GameLoadedAsync(ClientSession session, Room room, CancellationToken cancellationToken)
    {
        RoomMember? member = room.Find(session);
        if (member is not null) member.Status = RoomPacket.StatusLoaded;

        if (!room.IsHost(session))
        {
            if (room.HostLoaded) await ReleaseLoadedPlayerAsync(room, session, member, cancellationToken);
            else Log.Info(session.Tag, $"Room {room.Id}: loaded before the host - waiting for it");
            return;
        }

        List<RoomMember> everyone = room.Members;
        int nextIndex = everyone.Where(other => !other.IsObserver).Select(other => other.Slot).DefaultIfEmpty(-1).Max() + 1;
        List<RoomPacket.HostPlayerEntry> players = everyone
            .Select(other => new RoomPacket.HostPlayerEntry(
                other.IsObserver ? nextIndex++ : other.Slot,
                other.Session.Account.Id,
                other.Session.Account.Nickname,
                other.Session.Account.Character?.Class ?? 0,
                other.Team,
                Loadout.ClassSlots(other.Session.Account),
                IsPlayer: !other.IsObserver))
            .ToList();

        await session.SendAsync(RoomPacket.HostPlayers(room, players), cancellationToken);
        await ReleaseLoadedPlayerAsync(room, session, member, cancellationToken);
        room.HostLoaded = true;

        List<RoomMember> waiting = room.Members
            .Where(other => other.Session != session && other.Status == RoomPacket.StatusLoaded)
            .ToList();
        if (waiting.Count == 0) return;

        await Task.Delay(HostHeadStart, cancellationToken);
        foreach (RoomMember guest in waiting)
            await ReleaseLoadedPlayerAsync(room, guest.Session, guest, cancellationToken);
    }

    private static async Task ReleaseLoadedPlayerAsync(Room room, ClientSession player, RoomMember? member, CancellationToken cancellationToken)
    {
        await BroadcastStateAsync(room, RoomPhase.Loaded, cancellationToken, member?.Slot ?? 0);
        await TrySendAsync(player, ModePacket.Build(GameMode.LoadingGame), cancellationToken);
    }

    public static async Task GamePlayerEnteredAsync(ClientSession session, Room room, uint playerId, CancellationToken cancellationToken)
    {
        RoomMember? member = room.FindUser(playerId);
        if (member is null || member.Session.InMatch) return;

        member.Session.InMatch = true;
        member.Status = RoomPacket.StatusPlaying;
        room.MatchStartedUtc ??= DateTime.UtcNow;
        Log.Info(session.Tag, $"Room {room.Id}: player {playerId} is in the match");

        await BroadcastStateAsync(room, RoomPhase.Playing, cancellationToken, member.Slot);
        await TrySendAsync(member.Session, ModePacket.Build(GameMode.InGame), cancellationToken);
    }

    public static async Task GameEndedAsync(ClientSession session, CancellationToken cancellationToken)
    {
        Room? room = session.Room;
        List<RoomMember> members = room?.Members ?? [];
        if (room is null || members.Count == 0)
        {
            session.InMatch = false;
            await session.SendAsync(UserInfoPacket.PartialEmpty(), cancellationToken);
            return;
        }

        GameData data = GameData.Instance;
        int seconds = room.MatchStartedUtc is { } started ? (int)(DateTime.UtcNow - started).TotalSeconds : 0;
        int players = members.Count(member => !member.IsObserver);
        room.MatchStartedUtc = null;
        Log.Info(session.Tag, $"Room {room.Id}: match on map {room.PlayedMapId} ended after {seconds} s with {players} player(s)");

        data.Results.Payouts.TryGetValue((byte)room.Mode, out ModePayout? payout);
        data.Results.Bonus.TryGetValue(room.PlayedMapId, out BonusRule? bonus);

        foreach (RoomMember member in members)
        {
            ClientSession player = member.Session;
            player.InMatch = false;
            if (member.IsObserver)
            {
                await TrySendAsync(player, UserInfoPacket.PartialEmpty(), cancellationToken);
                continue;
            }

            int exp = Amount(payout?.Exp, players, seconds);
            int gold = Amount(payout?.Gold, players, seconds);
            uint rewardId = bonus is not null && seconds >= bonus.TimeMin && bonus.Rewards.Count > 0
                ? bonus.Rewards[Math.Min(Handlers.Dice.Weighted(bonus.Prob), bonus.Rewards.Count - 1)]
                : 0;

            InventoryItem? rewardItem = null;
            byte levelBefore = player.Account.DisplayLevel;
            player.Accounts.Update(player.Account.Id, account =>
            {
                account.Experience += exp;
                account.Gold += gold;
                account.Level = data.LevelForExp(account.Experience, account.Level);

                if (rewardId != 0 && data.Results.Rewards.TryGetValue(rewardId, out int[]? reward) && reward.Length >= 2 && reward[0] == 1
                    && data.Packages.Fixed.TryGetValue((uint)reward[1], out FixedItem? item))
                {
                    rewardItem = new InventoryItem
                    {
                        Slot = account.FreeSlot(),
                        ItemId = item.ItemId,
                        Type = item.Type,
                        Options = (ushort[])item.Options.Clone(),
                        Quantity = item.Count,
                        State = item.State,
                        Expiry = item.Expire > 0 ? (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds() + (uint)item.Expire : uint.MaxValue,
                    };
                    account.Items.Add(rewardItem);
                }
            });
            player.RefreshAccount();
            Log.Info(player.Tag, $"Match payout: +{exp} exp, +{gold} BP, reward {rewardId}{(rewardItem is null ? "" : $" (item {rewardItem.ItemId})")}");

            if (rewardId != 0) await TrySendAsync(player, RoomPacket.Reward(rewardId), cancellationToken);
            if (rewardItem is not null) await TrySendAsync(player, InventoryPacket.Added([rewardItem]), cancellationToken);
            await TrySendAsync(
                player,
                exp > 0 || gold > 0
                    ? UserInfoPacket.ExpAndGold((uint)player.Account.Experience, (uint)player.Account.Gold)
                    : UserInfoPacket.PartialEmpty(),
                cancellationToken);
            if (player.Account.DisplayLevel != levelBefore)
                await TrySendAsync(player, UserInfoPacket.Level(player.Account.DisplayLevel), cancellationToken);
        }
    }

    private static int Amount(PayoutRule? rule, int players, int seconds)
    {
        if (rule is null || rule.Outcome.Count == 0 || seconds < rule.TimeMin) return 0;
        double factor = rule.Member.Count == 0 ? 1 : rule.Member[Math.Clamp(players, 0, rule.Member.Count - 1)];
        return (int)Math.Round(rule.Outcome.Min() * factor);
    }

    public static async Task MatchClosedAsync(ClientSession session, CancellationToken cancellationToken)
    {
        Room? room = session.Room;
        if (room is null) return;

        Log.Info(session.Tag, $"Room {room.Id}: match closed by its host - back to the waiting room");
        room.HostLoaded = false;
        foreach (RoomMember member in room.Members)
        {
            member.Session.InMatch = false;
            member.Status = room.IsHost(member.Session) ? RoomPacket.StatusWaiting : RoomPacket.StatusNotReady;
        }

        await BroadcastStateAsync(room, RoomPhase.Created, cancellationToken);
        await BroadcastAsync(room, _ => ModePacket.Build(GameMode.WaitingRoom), cancellationToken);
    }

    public static async Task LeaveRoomAsync(ClientSession session, bool afterMatch, CancellationToken cancellationToken)
    {
        await LeaveQuietlyAsync(session, afterMatch);
        session.InMatch = false;

        try
        {
            await SendLobbyReturnAsync(session, afterMatch, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
        }
    }

    public static void Disconnected(ClientSession session)
    {
        if (session.Account is null) return;
        _ = LeaveQuietlyAsync(session);
    }

    private static async Task LeaveQuietlyAsync(ClientSession session, bool afterMatch = false)
    {
        Room? room = session.Room;
        session.Room = null;
        if (room is null)
        {
            RoomRegistry.Instance.RemoveHostedBy(session.Account.Id);
            return;
        }

        if (room.IsHost(session))
        {
            List<RoomMember> guests = room.Members.Where(member => member.Session != session).ToList();
            RoomRegistry.Instance.Remove(room.Id);
            room.Clear();
            room.ClearBots();
            BotDirector.Release(room);
            foreach (RoomMember guest in guests)
            {
                guest.Session.Room = null;
                guest.Session.InMatch = false;
                Log.Info(guest.Session.Tag, $"Room {room.Id} closed by its host - back to the lobby");
                try
                {
                    await SendLobbyReturnAsync(guest.Session, afterMatch, CancellationToken.None);
                }
                catch (Exception exception) when (exception is IOException or ObjectDisposedException)
                {
                }
            }
            return;
        }

        RoomMember? left = room.Leave(session);
        if (left is null) return;

        Log.Info(session.Tag, $"Left room {room.Id} ({room.PlayerCount}/{room.MaxPlayers})");
        IPEndPoint relay = Relay(session);
        foreach (RoomMember member in room.Members)
        {
            await TrySendAsync(member.Session, RoomPacket.PlayerLeft(session.Account.Id), CancellationToken.None);
            if (!left.IsObserver)
                await TrySendAsync(member.Session, RoomPacket.SlotRemoved(room, relay, left.Slot), CancellationToken.None);
        }
    }

    private static async Task SendLobbyReturnAsync(ClientSession session, bool afterMatch, CancellationToken cancellationToken)
    {
        if (afterMatch)
            await session.SendAsync(UserInfoPacket.Gold((uint)session.Account.Gold), cancellationToken);
        await session.SendAsync(ModePacket.Build(GameMode.Lobby), cancellationToken);
        await session.SendAsync(LobbyPacket.Opened(), cancellationToken);
        await session.SendAsync(UserInfoPacket.PartialOnLobby(AllEquippedTables(session.Account)), cancellationToken);
    }

    private static IPEndPoint Relay(ClientSession session) => session.Settings.MainEndPoint;

    private static List<RoomSlot> Slots(Room room)
        => room.Members
            .Where(member => !member.IsObserver)
            .Select(SlotOf)
            .Concat(room.Bots.Select(bot => new RoomSlot(bot.Slot, bot.Id, bot.CharacterClass, RoomPacket.StatusWaiting, bot.Team)))
            .ToList();

    public static async Task RoomObserveRequestedAsync(ClientSession session, Room room, CancellationToken cancellationToken)
    {
        RoomMember? member = room.Find(session);
        if (member is null) return;

        int oldSlot = room.IsHost(session) ? -1 : room.MakeObserver(member);
        if (oldSlot < 0)
        {
            Log.Info(session.Tag, $"Room {room.Id}: switch to observer refused");
            await session.SendAsync(RoomPacket.Error(NetError.Room_CanNotObserve), cancellationToken);
            return;
        }

        Log.Info(session.Tag, $"Room {room.Id}: now observing ({room.PlayerCount}/{room.MaxPlayers} players)");
        await BroadcastAsync(room, target => RoomPacket.SlotRemoved(room, Relay(target), oldSlot), cancellationToken);
    }

    public static async Task RoomPlayRequestedAsync(ClientSession session, Room room, CancellationToken cancellationToken)
    {
        RoomMember? member = room.Find(session);
        if (member is null || !member.IsObserver) return;

        if (!room.MakePlayer(member))
        {
            Log.Info(session.Tag, $"Room {room.Id}: no free slot for the observer");
            await session.SendAsync(RoomPacket.Error(NetError.Lobby_CannotJoinRoom), cancellationToken);
            return;
        }

        member.Status = RoomPacket.StatusNotReady;
        Log.Info(session.Tag, $"Room {room.Id}: playing again in slot {member.Slot}");
        await BroadcastAsync(room, target => RoomPacket.State(room, RoomPhase.Created, Relay(target), Slots(room)), cancellationToken);
    }

    public static async Task RoomChatAsync(ClientSession session, string text, ushort kind, CancellationToken cancellationToken)
    {
        Room? room = session.Room;
        if (room is null)
        {
            await session.SendAsync(UserMsgPacket.RoomChat(session.Account.Id, text), cancellationToken);
            return;
        }

        await BroadcastAsync(room, _ => UserMsgPacket.RoomChat(session.Account.Id, text), cancellationToken);
    }

    public static Task RoomReadyCancelledAsync(ClientSession session, Room room, CancellationToken cancellationToken)
    {
        if (!room.IsHost(session)) return Task.CompletedTask;
        Log.Info(session.Tag, $"Room {room.Id}: ready cancelled");
        return BroadcastStateAsync(room, RoomPhase.Settings, cancellationToken);
    }

    public static async Task RoomKickAsync(ClientSession session, Room room, uint playerId, CancellationToken cancellationToken)
    {
        RoomMember? target = room.FindUser(playerId);
        if (!room.IsHost(session) || target is null || target.Session == session)
        {
            Log.Info(session.Tag, $"Room {room.Id}: kick of {playerId} ignored");
            return;
        }

        Log.Info(session.Tag, $"Room {room.Id}: kicked player {playerId}");
        await LeaveRoomAsync(target.Session, afterMatch: false, cancellationToken);
    }

    private static RoomSlot SlotOf(RoomMember member)
        => new(member.Slot, member.Session.Account.Id, member.Session.Account.Character?.Class ?? 0, member.Status, member.Team);

    public static async Task RoomTeamRequestedAsync(ClientSession session, Room room, byte team, CancellationToken cancellationToken)
    {
        RoomMember? member = room.Find(session);
        if (member is null || member.IsObserver) return;

        bool readyGuest = !room.IsHost(session) && member.Status == RoomPacket.StatusWaiting;
        if (!room.HasTeams || team > 1 || readyGuest)
        {
            Log.Info(session.Tag, $"Room {room.Id}: team change to {team} refused");
            await session.SendAsync(RoomPacket.Error(NetError.Room_CantChangeTeam), cancellationToken);
            return;
        }

        member.Team = team;
        Log.Info(session.Tag, $"Room {room.Id}: now in team {team}");
        await BroadcastAsync(room, target => RoomPacket.SlotChanged(room, Relay(target), SlotOf(member)), cancellationToken);
    }

    public static async Task RoomGuestReadyAsync(ClientSession session, Room room, bool ready, CancellationToken cancellationToken)
    {
        RoomMember? member = room.Find(session);
        if (member is null || room.IsHost(session) || member.IsObserver) return;

        member.Status = ready ? RoomPacket.StatusWaiting : RoomPacket.StatusNotReady;
        Log.Info(session.Tag, $"Room {room.Id}: {(ready ? "ready" : "not ready")}");
        await BroadcastAsync(room, target => RoomPacket.SlotChanged(room, Relay(target), SlotOf(member)), cancellationToken);
    }

    private static async Task<bool> RefuseStartAsync(ClientSession session, Room room, CancellationToken cancellationToken)
    {
        int players = room.Members.Count(member => !member.IsObserver);
        int needed = GameData.Instance.Maps.TryGetValue(room.LevelId, out MapInfo? map) ? map.MinPlayers : 1;
        if (players >= needed) return false;

        Log.Warn(session.Tag, $"Room {room.Id}: start refused - map {room.LevelId} needs {needed} real players, the room has {players}");
        string notice = $"This mode needs {needed} players. The room has {players} (bots and observers do not count).";
        await BroadcastAsync(room, _ => UserMsgPacket.SystemMessage(notice), cancellationToken);
        await BroadcastStateAsync(room, RoomPhase.Settings, cancellationToken);
        return true;
    }

    public static async Task BotJoinedAsync(Room room, RoomBot bot)
    {
        byte[] record = BotRecordPart(bot);
        foreach (RoomMember member in room.Members)
        {
            await TrySendAsync(member.Session, RoomPacket.PlayerJoined(bot.Id, record), CancellationToken.None);
            await TrySendAsync(
                member.Session, RoomPacket.State(room, RoomPhase.Created, Relay(member.Session), Slots(room)), CancellationToken.None);
        }
    }

    private static async Task DismissBotsAsync(Room room)
    {
        List<RoomBot> bots = room.Bots;
        if (bots.Count == 0) return;

        room.ClearBots();
        BotDirector.Release(room);
        foreach (RoomBot bot in bots)
        {
            foreach (RoomMember member in room.Members)
            {
                await TrySendAsync(member.Session, RoomPacket.PlayerLeft(bot.Id), CancellationToken.None);
                await TrySendAsync(member.Session, RoomPacket.SlotRemoved(room, Relay(member.Session), bot.Slot), CancellationToken.None);
            }
        }
    }

    private static byte[] BotRecordPart(RoomBot bot)
    {
        var standIn = new Account { Character = new CharacterShape { Class = bot.CharacterClass, Values = [0x1F9, 1, 0x34, 0, 0] } };
        var writer = new PacketWriter();
        var nowhere = new IPEndPoint(IPAddress.Any, 0);
        UserInfoPacket.WriteFirstPart(writer, bot.Name, bot.CharacterClass, Loadout.ClassSlots(standIn), nowhere, nowhere, bot.Level, 0);
        return writer.ToArray();
    }

    private static byte[] RecordPart(ClientSession session)
    {
        Account account = session.Account;
        var writer = new PacketWriter();
        UserInfoPacket.WriteFirstPart(
            writer,
            account.Nickname,
            account.Character?.Class ?? 0,
            Loadout.ClassSlots(account),
            HolePunchServer.FindPlayerEndPoint(account.Id),
            HolePunchServer.FindPlayerLocalEndPoint(account.Id),
            account.DisplayLevel,
            account.GemRank);
        return writer.ToArray();
    }

    private static Task BroadcastStateAsync(Room room, RoomPhase phase, CancellationToken cancellationToken, int actor = -1)
    {
        room.State = RoomPacket.StateOf(phase);
        List<RoomSlot> slots = Slots(room);
        return BroadcastAsync(room, member => RoomPacket.State(room, phase, Relay(member), slots, actor), cancellationToken);
    }

    private static async Task BroadcastAsync(Room room, Func<ClientSession, PacketWriter> message, CancellationToken cancellationToken)
    {
        foreach (RoomMember member in room.Members)
            await TrySendAsync(member.Session, message(member.Session), cancellationToken);
    }

    private static async Task TrySendAsync(ClientSession target, PacketWriter message, CancellationToken cancellationToken)
    {
        try
        {
            await target.SendAsync(message, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            Log.Debug(target.Tag, "Send skipped: the connection is closing");
        }
    }
}
