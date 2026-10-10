using System.Net;
using BrawlBusters.Core.Data;
using BrawlBusters.Core.Logging;
using BrawlBusters.Core.Network;
using BrawlBusters.Core.Protocol;
using BrawlBusters.Core.Protocol.Packets;
using BrawlBusters.Core.Sessions.Handlers;

namespace BrawlBusters.Core.Sessions;

public static partial class GameFlow
{
    private const ushort LadderChannel = ushort.MaxValue;
    private const byte LadderPartySize = 4;
    private static readonly TimeSpan LadderRoomPause = TimeSpan.FromSeconds(2);

    private static readonly object LadderGate = new();
    /// <summary>A ranked room started or stopped searching: the friend lists of its players show it (chat server).</summary>
    public static event Action<uint>? LadderSearchChanged;

    private static void LadderSearchChangedFor(Room room)
    {
        foreach (RoomMember member in room.Members) LadderSearchChanged?.Invoke(member.Session.Account.Id);
    }

    /// <summary>
    /// How long a ranked search will probably take, from the players searching right now: nobody - long, fewer
    /// than a match needs - medium, enough for a match - short. The original rule is unknown.
    /// </summary>
    public static byte LadderWaitState(ClientSession session)
    {
        int searching;
        lock (LadderGate)
        {
            searching = LadderSolo.Count(other => other != session)
                + LadderParties.Where(room => room != session.Room).Sum(room => room.Members.Count);
        }

        int needed = 2 * Math.Max(1, session.Settings.LadderTeamSize) - 1;
        return searching == 0 ? LadderPacket.StateLow : searching < needed ? LadderPacket.StateMedium : LadderPacket.StateHigh;
    }

    private static readonly List<ClientSession> LadderSolo = [];
    private static readonly List<Room> LadderParties = [];

    private static GameMode WaitingMode(Room room) => room.LadderType switch
    {
        Room.LadderSingle => GameMode.WaitingRoomLadderSingle,
        Room.LadderMulti => GameMode.WaitingRoomLadderMulti,
        _ => GameMode.WaitingRoom,
    };

    public static async Task LadderCreateRoomAsync(ClientSession session, CancellationToken cancellationToken)
    {
        LadderForget(session);
        if (session.Room is not null) await LeaveQuietlyAsync(session);

        Room room = NewLadderRoom(session, Room.LadderMulti, LadderPartySize);
        room.Join(session);
        session.Room = room;
        Log.Info(session.Tag, $"Ladder room {room.Id} created");

        // The ranked screen (client state Ladder) registers no handler for sRoom: the room can only be read once
        // the client is in the ranked waiting room, so the screen change goes first. (A normal room is the other
        // way round - the lobby reads sRoom 05 itself.) The client's own log shows the wrong order as
        // "server message process failed [Ladder(18)/ sRoom(16)/ 5]".
        await session.SendAsync(ModePacket.Build(WaitingMode(room)), cancellationToken);
        await SendCanUnlockQuietlyAsync(session, cancellationToken);
        await session.SendAsync(RoomPacket.Entered(room, Relay(session), Slots(room), []), cancellationToken);
        await BroadcastStateAsync(room, RoomPhase.Created, cancellationToken);
    }

    public static async Task LadderStartSoloAsync(ClientSession session, CancellationToken cancellationToken)
    {
        if (session.Room is not null) await LeaveQuietlyAsync(session);
        lock (LadderGate)
        {
            if (!LadderSolo.Contains(session)) LadderSolo.Add(session);
        }

        Log.Info(session.Tag, "Ladder: searching for a match");
        await session.SendAsync(LadderPacket.State(LadderWaitState(session)), cancellationToken);
        await session.SendAsync(LadderPacket.Matching(true), cancellationToken);
        await LadderTryMatchAsync(cancellationToken);
    }

    public static Task LadderCancelSoloAsync(ClientSession session, CancellationToken cancellationToken)
    {
        LadderForget(session);
        Log.Info(session.Tag, "Ladder: search cancelled");
        return session.SendAsync(LadderPacket.Matching(false), cancellationToken);
    }

    public static async Task LadderFindMatchAsync(ClientSession session, Room room, CancellationToken cancellationToken)
    {
        bool everyoneReady = room.Members.All(member => room.IsHost(member.Session) || member.Status == RoomPacket.StatusWaiting);
        if (!room.IsLadder || !room.IsHost(session) || !everyoneReady || room.State != RoomPacket.StateOf(RoomPhase.Created))
        {
            Log.Info(session.Tag, $"Room {room.Id}: find match refused");
            await session.SendAsync(RoomPacket.Error(NetError.Room_StartHost), cancellationToken);
            return;
        }

        lock (LadderGate)
        {
            if (!LadderParties.Contains(room)) LadderParties.Add(room);
        }

        room.State = RoomPacket.StateMatching;
        Log.Info(session.Tag, $"Ladder room {room.Id}: searching for a match with {room.Members.Count} player(s)");
        await BroadcastAsync(room, target => RoomPacket.StateOnly(room, Relay(target)), cancellationToken);
        await BroadcastAsync(room, _ => LadderPacket.Matching(true), cancellationToken);
        LadderSearchChangedFor(room);
        await LadderTryMatchAsync(cancellationToken);
    }

    public static async Task LadderCancelFindAsync(ClientSession session, Room room, CancellationToken cancellationToken)
    {
        if (!room.IsLadder || !room.IsHost(session) || room.State != RoomPacket.StateMatching)
        {
            Log.Info(session.Tag, $"Room {room.Id}: cancel find match ignored");
            return;
        }

        lock (LadderGate)
        {
            LadderParties.Remove(room);
        }

        room.State = RoomPacket.StateOf(RoomPhase.Created);
        Log.Info(session.Tag, $"Ladder room {room.Id}: search cancelled");
        await BroadcastAsync(room, target => RoomPacket.StateOnly(room, Relay(target)), cancellationToken);
        await BroadcastAsync(room, _ => LadderPacket.Matching(false), cancellationToken);
        LadderSearchChangedFor(room);
    }

    /// <summary>Takes the player out of the solo queue; true when he was in it.</summary>
    private static bool LadderForget(ClientSession session)
    {
        lock (LadderGate)
        {
            return LadderSolo.Remove(session);
        }
    }

    /// <summary>
    /// Leaving a ranked round that is being played costs gem score that nobody receives, and makes the round
    /// unofficial for those who stay.
    /// </summary>
    private static void LadderLeftRound(ClientSession session, Room room)
    {
        RoomMember? member = room.Find(session);
        if (room.MatchStartedUtc is null || member is null || member.IsObserver || member.InResult) return;

        room.LadderUnofficial = true;
        int penalty = Math.Max(0, session.Settings.LadderLeavePenalty);
        session.Accounts.Update(session.Account.Id, account => account.LadderPoints = Math.Max(0, account.LadderPoints - penalty));
        Log.Info(session.Tag, $"Ladder: left room {room.Id} during the round - {penalty} gem score lost, the round is unofficial");
    }

    /// <summary>
    /// Gem score a player wins or loses: the share of <paramref name="factor"/> by which the result beat what the
    /// own score promised against the other team's average. Winners never lose, losers never win; in a draw the
    /// players above the other team's average lose and those below it win.
    /// </summary>
    public static int LadderGemChange(int own, double otherAverage, int outcome, int factor)
    {
        double expected = 1.0 / (1.0 + Math.Pow(10.0, (otherAverage - own) / 400.0));
        double scored = outcome > 0 ? 1.0 : outcome < 0 ? 0.0 : 0.5;
        int change = (int)Math.Round(factor * (scored - expected), MidpointRounding.AwayFromZero);
        change = outcome > 0 ? Math.Max(0, change) : outcome < 0 ? Math.Min(0, change) : change;
        return Math.Max(-own, change);
    }

    /// <summary>
    /// Tells the client again whether it holds a Class Unlock ticket. The in-match class picker only offers to open a
    /// locked class when the client has raised its "can unlock" event (30983), and it raises it in two places: on
    /// entering a normal waiting room (0x5D270E) and whenever the flag block arrives (0x59E27B). A ranked room is not
    /// a normal waiting room, so the flag has to come from here.
    /// </summary>
    private static async Task SendCanUnlockQuietlyAsync(ClientSession session, CancellationToken cancellationToken)
    {
        try
        {
            await SendCanUnlockAsync(session, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
        }
    }

    private static void LadderForget(Room room)
    {
        lock (LadderGate)
        {
            LadderParties.Remove(room);
        }
    }

    private static Room NewLadderRoom(ClientSession host, byte ladderType, byte maxPlayers)
    {
        GameData data = GameData.Instance;
        // Ranked play has its own maps (mode 10, the "Ranked" channel type): the default one is RANDOM, which
        // PlayedMap turns into one of its list when the match starts. An ordinary team deathmatch map is only the
        // fallback for game data without them.
        MapInfo? map = data.DefaultMap(MatchMode.Channel5Team, maxPlayers) ?? data.DefaultMap(MatchMode.TeamDeathmatch, maxPlayers);
        ushort levelId = map?.Id ?? RecordedLevelId;
        ushort ruleId = map is null ? RecordedRuleId : data.DefaultRule(map);
        IPEndPoint endPoint = HolePunchServer.FindPlayerEndPoint(host.Account.Id) ?? host.Connection.RemoteEndPoint;

        Room room = RoomRegistry.Instance.Create(LadderChannel, "Ladder", host.Account.Id, endPoint, MatchMode.TeamDeathmatch, maxPlayers, levelId, ruleId);
        room.LadderType = ladderType;
        room.AllowIntrusion = false;      // a ranked round can be neither joined nor watched once it runs
        room.AllowObservation = false;
        return room;
    }

    private static async Task LadderTryMatchAsync(CancellationToken cancellationToken)
    {
        List<ClientSession> sideA;
        List<ClientSession> sideB;
        lock (LadderGate)
        {
            var groups = LadderParties.Select(room => room.Members.Select(member => member.Session).ToList())
                .Concat(LadderSolo.Select(session => new List<ClientSession> { session }))
                .Where(group => group.Count > 0)
                .ToList();
            if (groups.Count < 2) return;

            // Two full teams: parties stay together, the largest first; each group goes to the team that has room
            // for it and the lower gem score so far, so the teams come out close in strength.
            int teamSize = Math.Clamp(groups[0][0].Settings.LadderTeamSize, 1, 8);
            sideA = [];
            sideB = [];
            static int Score(List<ClientSession> team) => team.Sum(player => player.Account.LadderPoints);
            foreach (List<ClientSession> group in groups.Where(group => group.Count <= teamSize).OrderByDescending(group => group.Count))
            {
                bool fitsA = sideA.Count + group.Count <= teamSize, fitsB = sideB.Count + group.Count <= teamSize;
                if (!fitsA && !fitsB) continue;
                (fitsA && (!fitsB || Score(sideA) <= Score(sideB)) ? sideA : sideB).AddRange(group);
                if (sideA.Count == teamSize && sideB.Count == teamSize) break;
            }
            if (sideA.Count < teamSize || sideB.Count < teamSize) return;

            foreach (ClientSession session in sideA.Concat(sideB)) LadderSolo.Remove(session);
            LadderParties.RemoveAll(room => room.Members.Any(member => sideA.Contains(member.Session) || sideB.Contains(member.Session)));
        }

        bool solo = sideA.Concat(sideB).All(session => session.Room is null);
        ClientSession host = sideA[0];
        List<ClientSession> everyone = sideA.Concat(sideB).ToList();
        foreach (ClientSession session in everyone)
        {
            Room? old = session.Room;
            session.Room = null;
            if (old is null) continue;
            old.Clear();
            RoomRegistry.Instance.Remove(old.Id);
        }

        Room room = NewLadderRoom(host, solo ? Room.LadderSingle : Room.LadderMulti, (byte)Math.Max(2, everyone.Count));
        foreach (ClientSession session in everyone)
        {
            RoomMember? member = room.Join(session);
            if (member is null) continue;
            member.Team = (byte)(sideA.Contains(session) ? 0 : 1);
            member.Status = RoomPacket.StatusWaiting;
            session.Room = room;
        }

        Log.Info(host.Tag, $"Ladder: match found - room {room.Id}, {sideA.Count} against {sideB.Count}");
        foreach (ClientSession session in everyone)
        {
            var others = room.Members.Where(member => member.Session != session)
                .Select(member => (member.Session.Account.Id, RecordPart(member.Session)))
                .ToList();
            await TrySendAsync(session, LadderPacket.Matching(false), cancellationToken);
            await TrySendAsync(session, ModePacket.Build(WaitingMode(room)), cancellationToken);
            await SendCanUnlockQuietlyAsync(session, cancellationToken);
            // The other players come as "player joined" (sRoom 06), not in the room's own list: that message is the
            // only thing that makes a client in a ranked room open its UDP path to another player (client 0x5CD1F0).
            // A normal room does it for everybody listed when its waiting room opens (0x5D83C0), but that code is
            // for the normal room class only - and without it the two firewalls never let the match connect.
            await TrySendAsync(session, RoomPacket.Entered(room, Relay(session), Slots(room), []), cancellationToken);
            foreach ((uint id, byte[] record) in others)
                await TrySendAsync(session, RoomPacket.PlayerJoined(id, record), cancellationToken);
            await TrySendAsync(session, RoomPacket.State(room, RoomPhase.Created, Relay(session), Slots(room)), cancellationToken);
        }

        await World.PauseAsync(LadderRoomPause, cancellationToken);
        if (room.Members.Count < 2)
        {
            Log.Info(host.Tag, $"Ladder room {room.Id}: a player left before the start");
            return;
        }

        await StartMatchAsync(host, room, cancellationToken);
    }
}
