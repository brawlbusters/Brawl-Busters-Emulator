using System.Net;
using BrawlBusters.Core.Data;
using BrawlBusters.Core.Logging;
using BrawlBusters.Core.Network;
using BrawlBusters.Core.Protocol;
using BrawlBusters.Core.Protocol.Packets;
using BrawlBusters.Core.Security;

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

        if (RoomRegistry.Instance.InChannel(session.ChannelId).Count >= session.Settings.MaxRoomsPerChannel)
        {
            Log.Info(LogChannel.Lobby, session.Tag, $"Room not created: channel {session.ChannelId} already has {session.Settings.MaxRoomsPerChannel} rooms");
            await session.SendAsync(LobbyPacket.Error(NetError.Lobby_FullRoomList), cancellationToken);
            return;
        }

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

    public static async Task RoomPingAsync(ClientSession session, Room room, ushort ping, CancellationToken cancellationToken)
    {
        RoomMember? member = room.Find(session);
        if (member is null || member.IsObserver) return;

        member.Ping = ping;
        Log.Debug(session.Tag, $"Room {room.Id}: ping to the host {ping} ms");
        await BroadcastAsync(room, target => RoomPacket.SlotPing(room, Relay(target), SlotOf(member)), cancellationToken);
    }

    public static async Task JoinInvitedAsync(ClientSession session, ushort channelId, ushort roomId, CancellationToken cancellationToken)
    {
        Room? room = RoomRegistry.Instance.Find(roomId);
        if (room is null || room == session.Room || room.IsBotRoom)
        {
            Log.Info(session.Tag, $"Invitation to room {roomId}: that room cannot be entered");
            await session.SendAsync(RoomPacket.InviteResult(NetError.Room_NotExistInviter), cancellationToken);
            return;
        }

        if (session.Room is not null) await LeaveQuietlyAsync(session);
        LadderForget(session);
        if (!room.IsLadder) session.ChannelId = room.ChannelId;
        Log.Info(session.Tag, $"Follows an invitation to room {roomId} (channel {channelId})");
        await JoinRoomAsync(session, roomId, room.Password, cancellationToken, invited: true);
    }

    public static async Task JoinRoomAsync(ClientSession session, ushort roomId, string password, CancellationToken cancellationToken, bool invited = false)
    {
        Room? room = RoomRegistry.Instance.Find(roomId);
        NetError? refusal = room switch
        {
            null => NetError.Lobby_NotExistRoom,
            _ when room.ChannelId != session.ChannelId && !invited => NetError.Lobby_NotExistRoom,
            _ when room.Password.Length > 0 && room.Password != password => NetError.Lobby_WrongPassword,
            _ when room.State != RoomPacket.StateOf(RoomPhase.Created) && !CanIntrude(room) => NetError.Lobby_CannotJoinRoom,
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
            await session.SendAsync(
                invited ? RoomPacket.InviteResult(refusal ?? NetError.Lobby_CannotJoinRoom) : LobbyPacket.Error(refusal ?? NetError.Lobby_CannotJoinRoom),
                cancellationToken);
            return;
        }

        if (invited) await session.SendAsync(RoomPacket.InviteResult(NetError.Success), cancellationToken);

        session.Room = room;
        List<RoomMember> others = room.Members.Where(member => member.Session != session).ToList();
        Log.Info(session.Tag, CanIntrude(room)
            ? $"Joined room {room.Id} in slot {joined!.Slot} while its match is running ({room.PlayerCount}/{room.MaxPlayers})"
            : $"Joined room {room.Id} in slot {joined!.Slot} ({room.PlayerCount}/{room.MaxPlayers})");

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
        await session.SendAsync(ModePacket.Build(WaitingMode(room)), cancellationToken);
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
            return RefuseSettingAsync(session, room, NetError.Room_InvalidLevelID, cancellationToken);
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
            return RefuseSettingAsync(session, room, NetError.Room_InvalidRuleParamID, cancellationToken);
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
        await StartMatchAsync(session, room, cancellationToken);
    }

    private static async Task StartMatchAsync(ClientSession session, Room room, CancellationToken cancellationToken)
    {
        var hostLocal = HolePunchServer.FindPlayerLocalEndPoint(room.HostUserId) ?? room.HostEndPoint;
        ushort playedMap = PlayedMap(room);
        room.PlayedMapId = playedMap;
        room.MatchStartedUtc = null;
        room.Log = new MatchLog();
        foreach (RoomMember member in room.Members)
        {
            member.Payout = null;
            member.InResult = false;
        }
        IPEndPoint relay = Relay(session);
        room.HostLoaded = false;
        Log.Info(session.Tag, $"Room {room.Id}: playing map {playedMap} with {room.Members.Count} player(s)");

        await BroadcastAsync(room, _ => RoomPacket.GameStarting(room, hostLocal, relay, playedMap), cancellationToken);
        await BroadcastStateAsync(room, RoomPhase.Starting, cancellationToken);
        await BroadcastAsync(room, _ => ModePacket.Build(GameMode.ReadyRoom), cancellationToken);
    }

    private static readonly TimeSpan HostHeadStart = TimeSpan.FromMilliseconds(700);

    private static bool CanIntrude(Room room)
        => !room.IsBotRoom && room.HostLoaded && room.MatchStartedUtc is not null && room.State == RoomPacket.StateOf(RoomPhase.Playing);

    public static async Task RoomIntrudeAsync(ClientSession session, Room room, bool asObserver, CancellationToken cancellationToken)
    {
        RoomMember? member = room.Find(session);
        string what = asObserver ? "observe" : "join";
        if (member is null || session.InMatch || member.Intruding || !CanIntrude(room) || member.IsObserver != asObserver)
        {
            Log.Info(session.Tag, $"Room {room.Id}: request to {what} the running match refused");
            await session.SendAsync(RoomPacket.Error(asObserver ? NetError.Room_CanNotObserve : NetError.Room_CanNotIntrude), cancellationToken);
            return;
        }

        member.Intruding = true;
        var hostLocal = HolePunchServer.FindPlayerLocalEndPoint(room.HostUserId) ?? room.HostEndPoint;
        Log.Info(session.Tag, $"Room {room.Id}: entering the running match on map {room.PlayedMapId} to {what}");

        await session.SendAsync(RoomPacket.GameStarting(room, hostLocal, Relay(session), room.PlayedMapId), cancellationToken);
        await session.SendAsync(ModePacket.Build(GameMode.ReadyRoom), cancellationToken);
    }

    private static async Task IntruderLoadedAsync(ClientSession session, Room room, RoomMember member, CancellationToken cancellationToken)
    {
        RoomMember? host = room.Members.FirstOrDefault(other => room.IsHost(other.Session));
        if (host is null || !CanIntrude(room))
        {
            member.Intruding = false;
            Log.Info(session.Tag, $"Room {room.Id}: the match ended while loading - back to the waiting room");
            await session.SendAsync(ModePacket.Build(GameMode.WaitingRoom), cancellationToken);
            return;
        }

        await AnnounceToHostAsync(session, room, member, host, cancellationToken);

        await World.PauseAsync(HostHeadStart, cancellationToken);
        await TrySendAsync(session, ModePacket.Build(GameMode.LoadingGame), cancellationToken);
    }

    /// <summary>Tells the host of a running match about a member who is on his way in (sHost: player added).</summary>
    private static async Task AnnounceToHostAsync(ClientSession session, Room room, RoomMember member, RoomMember host, CancellationToken cancellationToken)
    {
        HashSet<int> used = room.Members.Where(other => other != member && other.HostIndex >= 0).Select(other => other.HostIndex).ToHashSet();
        int index = member.IsObserver || used.Contains(member.Slot)
            ? Enumerable.Range(0, 256).First(free => !used.Contains(free) && room.Members.All(other => other.Slot != free))
            : member.Slot;
        member.HostIndex = index;
        member.Status = RoomPacket.StatusLoaded;

        var entry = new RoomPacket.HostPlayerEntry(
            index,
            session.Account.Id,
            session.Account.Nickname,
            session.Account.Character?.Class ?? 0,
            member.Team,
            Loadout.ClassSlots(session.Account),
            IsPlayer: !member.IsObserver);

        Log.Info(session.Tag, $"Room {room.Id}: loaded - the host is told about the new {(member.IsObserver ? "observer" : "player")} (index {index}, team {member.Team})");
        await TrySendAsync(host.Session, RoomPacket.HostPlayerAdded(room, entry), cancellationToken);
        if (!member.IsObserver)
            await BroadcastAsync(room, target => RoomPacket.SlotChanged(room, Relay(target), SlotOf(member)), cancellationToken);
    }

    public static async Task GameLoadedAsync(ClientSession session, Room room, CancellationToken cancellationToken)
    {
        RoomMember? member = room.Find(session);
        if (member is { Intruding: true })
        {
            await IntruderLoadedAsync(session, room, member, cancellationToken);
            return;
        }

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

        for (int i = 0; i < everyone.Count; i++)
        {
            everyone[i].HostIndex = players[i].Slot;
            everyone[i].Intruding = false;
        }

        await session.SendAsync(RoomPacket.HostPlayers(room, players), cancellationToken);
        await ReleaseLoadedPlayerAsync(room, session, member, cancellationToken);
        room.HostLoaded = true;

        List<RoomMember> waiting = room.Members
            .Where(other => other.Session != session && other.Status == RoomPacket.StatusLoaded)
            .ToList();
        if (waiting.Count == 0) return;

        await World.PauseAsync(HostHeadStart, cancellationToken);
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
        member.Intruding = false;
        member.Status = RoomPacket.StatusPlaying;
        room.MatchStartedUtc ??= DateTime.UtcNow;
        room.Log.Of(playerId).LifeStartedUtc = DateTime.UtcNow;
        Log.Info(session.Tag, $"Room {room.Id}: player {playerId} is in the match");

        await BroadcastStateAsync(room, RoomPhase.Playing, cancellationToken, member.Slot);
        if (!member.GmObserver) await TrySendAsync(member.Session, ModePacket.Build(GameMode.InGame), cancellationToken);
    }

    public static async Task GmObserveAsync(ClientSession session, ushort roomId, CancellationToken cancellationToken)
    {
        session.RefreshAccount();
        if (!session.Account.Can(Permission.ObserveMatches))
        {
            Log.Warn(LogChannel.Commands, session.Tag, $"/gm_observe {roomId} refused: grade {session.Account.Grade}");
            await session.SendAsync(LobbyPacket.Error(NetError.Lobby_CannotJoinRoom), cancellationToken);
            return;
        }

        Room? room = RoomRegistry.Instance.Find(roomId);
        if (room is null || room.IsBotRoom || room == session.Room)
        {
            Log.Info(session.Tag, $"/gm_observe {roomId}: no such room to watch");
            await session.SendAsync(LobbyPacket.Error(NetError.Lobby_NotExistRoom), cancellationToken);
            return;
        }

        if (session.Room is not null) await LeaveQuietlyAsync(session);
        RoomMember? member = room.JoinAsObserver(session);
        if (member is null)
        {
            await session.SendAsync(LobbyPacket.Error(NetError.Lobby_CannotJoinRoom), cancellationToken);
            return;
        }

        session.Room = room;
        List<RoomMember> others = room.Members.Where(other => other.Session != session).ToList();
        byte[] ownRecord = RecordPart(session);
        foreach (RoomMember other in others)
            await TrySendAsync(other.Session, RoomPacket.PlayerJoined(session.Account.Id, ownRecord), cancellationToken);

        if (CanIntrude(room) && room.Members.FirstOrDefault(other => room.IsHost(other.Session)) is { } host)
        {
            // The original path: the "GM observer" screen (sMode 13) gets the match record (sObserver 00), loads the
            // map by itself and reports cGame 08. There is no room screen and no cGame 09, so the host is told at once.
            member.GmObserver = true;
            member.Intruding = true;
            var hostLocal = HolePunchServer.FindPlayerLocalEndPoint(room.HostUserId) ?? room.HostEndPoint;
            Log.Info(session.Tag, $"/gm_observe {roomId}: match on map {room.PlayedMapId} is running - sent to the GM observer screen");
            await session.SendAsync(ModePacket.Build(GameMode.GmObserver), cancellationToken);
            await session.SendAsync(RoomPacket.ObserverGameStarting(room, hostLocal, Relay(session), room.PlayedMapId), cancellationToken);
            await AnnounceToHostAsync(session, room, member, host, cancellationToken);
            return;
        }

        Log.Info(session.Tag, $"/gm_observe {roomId}: no match is running - in the room as an observer");

        var records = others.Select(other => (other.Session.Account.Id, RecordPart(other.Session))).ToList();
        await session.SendAsync(RoomPacket.Entered(room, Relay(session), Slots(room), records), cancellationToken);
        await session.SendAsync(RoomPacket.State(room, RoomPhase.Created, Relay(session), Slots(room)), cancellationToken);
        await session.SendAsync(ModePacket.Build(WaitingMode(room)), cancellationToken);
        if (CanIntrude(room)) await RoomIntrudeAsync(session, room, asObserver: true, cancellationToken);
    }

    private static async Task SendMissionProgressAsync(ClientSession player,
        (bool Changed, List<uint> RewardIds, List<InventoryItem> Items) missions, CancellationToken cancellationToken)
    {
        if (!missions.Changed) return;

        await TrySendAsync(player, UserInfoPacket.MissionsChanged(DailyMissions.Block(player.Account)), cancellationToken);
        foreach (uint rewardId in missions.RewardIds)
        {
            Log.Info(player.Tag, $"Daily mission completed: reward {rewardId}");
            await TrySendAsync(player, RoomPacket.Reward(rewardId), cancellationToken);
        }
        if (missions.Items.Count > 0) await TrySendAsync(player, InventoryPacket.Added(missions.Items), cancellationToken);
        if (missions.RewardIds.Count > 0)
            await TrySendAsync(player, UserInfoPacket.Gold((uint)player.Account.Gold), cancellationToken);
    }

    public static (int Exp, int Gold) Boosters(Account account)
    {
        uint now = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        int exp = 0;
        int gold = 0;
        foreach (InventoryItem item in account.Items)
        {
            if (item.Expiry <= now || !GameData.Instance.Upgrades.Misc.TryGetValue(item.ItemId, out MiscItem? misc)) continue;
            exp = Math.Max(exp, misc.BonusExp);
            gold = Math.Max(gold, misc.BonusGold);
        }
        return (exp, gold);
    }

    public static async Task LeaveRoomAsync(ClientSession session, bool afterMatch, CancellationToken cancellationToken)
    {
        bool ladder = session.Room?.IsLadder == true;
        await LeaveQuietlyAsync(session, afterMatch);
        session.InMatch = false;

        try
        {
            if (ladder) await EnterLadderAsync(session, cancellationToken);
            else await SendLobbyReturnAsync(session, afterMatch, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
        }
    }

    public static void Disconnected(ClientSession session)
    {
        if (session.Account is null) return;
        LadderForget(session);
        _ = LeaveQuietlyAsync(session, lostConnection: true);
    }

    private static async Task LeaveQuietlyAsync(ClientSession session, bool afterMatch = false, bool lostConnection = false)
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
            LadderForget(room);
            bool ladder = room.IsLadder;
            bool running = room.MatchStartedUtc is not null && !afterMatch;
            room.Clear();
            room.ClearBots();
            BotDirector.Release(room);
            HashSet<RoomMember> inMatch = guests.Where(guest => guest.Session.InMatch).ToHashSet();
            foreach (RoomMember guest in guests)
            {
                guest.Session.Room = null;
                guest.Session.InMatch = false;
                Log.Info(guest.Session.Tag, $"Room {room.Id} closed by its host - back to the lobby");
                try
                {
                    if (running && inMatch.Contains(guest) && !guest.InResult)
                        await guest.Session.SendAsync(RoomPacket.GameCanceled(), CancellationToken.None);
                    await guest.Session.SendAsync(NoticePacket.RoomDisappeared(room.Id), CancellationToken.None);
                    if (ladder) await EnterLadderAsync(guest.Session, CancellationToken.None);
                    else await SendLobbyReturnAsync(guest.Session, afterMatch, CancellationToken.None);
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

        if (left.HostIndex >= 0 && room.HostLoaded && room.MatchStartedUtc is not null
            && room.Members.FirstOrDefault(member => room.IsHost(member.Session)) is { } host)
            await TrySendAsync(host.Session, RoomPacket.HostPlayerRemoved(room, left.HostIndex), CancellationToken.None);

        bool loading = room.State == RoomPacket.StateOf(RoomPhase.Starting) || room.State == RoomPacket.StateOf(RoomPhase.Loaded);
        if (loading && room.MatchStartedUtc is null && !left.IsObserver)
            await CancelLoadingMatchAsync(room, lostConnection ? NetError.Room_NetworkError : NetError.Room_UserOut_GameCancel);
    }

    /// <summary>A player left while everybody was still loading: the match is called off and the room waits again.</summary>
    private static async Task CancelLoadingMatchAsync(Room room, NetError reason)
    {
        Log.Info(LogChannel.Room, $"Room {room.Id}", $"Match cancelled while loading: {reason}");
        room.HostLoaded = false;
        foreach (RoomMember member in room.Members)
        {
            member.Session.InMatch = false;
            member.Intruding = false;
            member.HostIndex = -1;
            member.Status = room.IsHost(member.Session) ? RoomPacket.StatusWaiting : RoomPacket.StatusNotReady;
        }

        await BroadcastAsync(room, _ => RoomPacket.Error(reason), CancellationToken.None);
        await BroadcastStateAsync(room, RoomPhase.Created, CancellationToken.None);
        await BroadcastAsync(room, _ => ModePacket.Build(WaitingMode(room)), CancellationToken.None);
    }

    private static async Task RefuseSettingAsync(ClientSession session, Room room, NetError reason, CancellationToken cancellationToken)
    {
        await session.SendAsync(RoomPacket.Error(reason), cancellationToken);
        await RoomSettingsChangedAsync(session, room, cancellationToken);
    }

    private static async Task SendLobbyReturnAsync(ClientSession session, bool afterMatch, CancellationToken cancellationToken)
    {
        if (afterMatch)
            await session.SendAsync(UserInfoPacket.Gold((uint)session.Account.Gold), cancellationToken);
        await session.SendAsync(ModePacket.Build(GameMode.Lobby), cancellationToken);
        await session.SendAsync(LobbyPacket.Opened(), cancellationToken);
        await session.SendAsync(UserInfoPacket.PartialOnLobby(AllEquippedTables(session.Account)), cancellationToken);
    }

    private static IPEndPoint Relay(ClientSession session) => session.Settings.RelayEndPoint;

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
            await session.SendAsync(RoomPacket.Error(NetError.Room_SwitchToObserver), cancellationToken);
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
            await session.SendAsync(RoomPacket.Error(NetError.Room_SwitchToPlayer), cancellationToken);
            return;
        }

        member.Status = RoomPacket.StatusNotReady;
        Log.Info(session.Tag, $"Room {room.Id}: playing again in slot {member.Slot}");
        await BroadcastAsync(room, target => RoomPacket.State(room, RoomPhase.Created, Relay(target), Slots(room)), cancellationToken);
    }

    public static async Task RoomChatAsync(ClientSession session, string text, byte kind, CancellationToken cancellationToken)
    {
        Room? room = session.Room;
        if (room is null)
        {
            await session.SendAsync(UserMsgPacket.RoomChat(session.Account.Id, text), cancellationToken);
            return;
        }

        RoomMember? sender = room.Find(session);
        if (kind == UserMsgPacket.ChatToTeam && room.HasTeams && sender is { IsObserver: false })
        {
            // Team chat: only the sender's own team reads it.
            foreach (RoomMember mate in room.Members.Where(member => !member.IsObserver && member.Team == sender.Team))
                await TrySendAsync(mate.Session, UserMsgPacket.RoomChat(session.Account.Id, text, UserMsgPacket.ChatToTeam), cancellationToken);
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
        => new(member.Slot, member.Session.Account.Id, member.Session.Account.Character?.Class ?? 0, member.Status, member.Team, member.Ping,
            (sbyte)Math.Min(member.WinStreak, sbyte.MaxValue));

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

    public static Task RoomClassChangedAsync(ClientSession session, CancellationToken cancellationToken)
    {
        Room? room = session.Room;
        return room is null
            ? Task.CompletedTask
            : BroadcastAsync(room, target => RoomPacket.State(room, RoomPhase.Created, Relay(target), Slots(room)), cancellationToken);
    }

    public static async Task RoomAdjustAsync(ClientSession session, Room room, CancellationToken cancellationToken)
    {
        List<RoomMember> players = room.Members.Where(member => !member.IsObserver).ToList();
        if (!room.IsHost(session) || !room.HasTeams || players.Count < 3 || room.State == RoomPacket.StateOf(RoomPhase.Playing))
        {
            Log.Info(session.Tag, $"Room {room.Id}: team balancing refused");
            await session.SendAsync(RoomPacket.Error(NetError.Room_CantChangeTeam), cancellationToken);
            return;
        }

        int[] strength = new int[2];
        int[] size = new int[2];
        foreach (RoomBot bot in room.Bots.Where(bot => bot.Team <= 1))
        {
            strength[bot.Team] += bot.Level;
            size[bot.Team]++;
        }

        var moved = new List<RoomMember>();
        foreach (RoomMember member in players.OrderByDescending(member => member.Session.Account.DisplayLevel).ThenBy(member => member.Slot))
        {
            byte team = size[0] != size[1]
                ? (byte)(size[0] < size[1] ? 0 : 1)
                : (byte)(strength[0] <= strength[1] ? 0 : 1);
            strength[team] += member.Session.Account.DisplayLevel;
            size[team]++;
            if (member.Team == team) continue;
            member.Team = team;
            moved.Add(member);
        }

        Log.Info(session.Tag, $"Room {room.Id}: teams balanced by level - {size[0]} against {size[1]}, {moved.Count} player(s) moved");
        foreach (RoomMember member in moved)
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
        await session.SendAsync(RoomPacket.Error(NetError.Room_StartHost), cancellationToken);
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
        UserInfoPacket.WriteFirstPart(writer, bot.Name, bot.CharacterClass, Loadout.ClassSlots(standIn), nowhere, nowhere, bot.Level, 0, default);
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
            account.GemRank,
            LadderRating.FromPoints(account.LadderPoints),
            account.OwnedClassMask());
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
