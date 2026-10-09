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

        await session.SendAsync(RoomPacket.Entered(room, Relay(session), Slots(room), []), cancellationToken);
        await BroadcastStateAsync(room, RoomPhase.Created, cancellationToken);
        await session.SendAsync(ModePacket.Build(WaitingMode(room)), cancellationToken);
    }

    public static async Task LadderStartSoloAsync(ClientSession session, CancellationToken cancellationToken)
    {
        if (session.Room is not null) await LeaveQuietlyAsync(session);
        lock (LadderGate)
        {
            if (!LadderSolo.Contains(session)) LadderSolo.Add(session);
        }

        Log.Info(session.Tag, "Ladder: searching for a match");
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
    }

    private static void LadderForget(ClientSession session)
    {
        lock (LadderGate)
        {
            LadderSolo.Remove(session);
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
        MapInfo? map = data.DefaultMap(MatchMode.TeamDeathmatch, maxPlayers);
        ushort levelId = map?.Id ?? RecordedLevelId;
        ushort ruleId = map is null ? RecordedRuleId : data.DefaultRule(map);
        IPEndPoint endPoint = HolePunchServer.FindPlayerEndPoint(host.Account.Id) ?? host.Connection.RemoteEndPoint;

        Room room = RoomRegistry.Instance.Create(LadderChannel, "Ladder", host.Account.Id, endPoint, MatchMode.TeamDeathmatch, maxPlayers, levelId, ruleId);
        room.LadderType = ladderType;
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

            sideA = groups[0];
            sideB = groups.Skip(1).OrderBy(group => Math.Abs(group.Count - sideA.Count)).First();
            foreach (ClientSession session in sideA.Concat(sideB)) LadderSolo.Remove(session);
            LadderParties.RemoveAll(room => room.Members.Any(member => sideA.Contains(member.Session) || sideB.Contains(member.Session)));
        }

        bool solo = sideA.Count == 1 && sideB.Count == 1;
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
            await TrySendAsync(session, RoomPacket.Entered(room, Relay(session), Slots(room), others), cancellationToken);
            await TrySendAsync(session, RoomPacket.State(room, RoomPhase.Created, Relay(session), Slots(room)), cancellationToken);
            await TrySendAsync(session, ModePacket.Build(WaitingMode(room)), cancellationToken);
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
