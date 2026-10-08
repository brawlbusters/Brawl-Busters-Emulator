using System.Net;
using BrawlBusters.Core.Data;

namespace BrawlBusters.Core.Sessions;

public sealed class RoomMember
{
    public required ClientSession Session { get; init; }

    public required int Slot { get; set; }

    public bool IsObserver => Slot < 0;

    public byte Status { get; set; } = 2;

    public byte Team { get; set; }
}

public sealed record RoomBot(uint Id, string Name, byte CharacterClass, byte Level, int Slot, byte Team = 0);

public sealed class Room
{
    private readonly List<RoomMember> _members = [];
    private readonly List<RoomBot> _bots = [];

    public List<RoomBot> Bots
    {
        get
        {
            lock (_members) return _bots.OrderBy(bot => bot.Slot).ToList();
        }
    }

    public void SeatBots(IEnumerable<(uint Id, string Name, byte CharacterClass, byte Level)> bots)
    {
        lock (_members)
        {
            _bots.Clear();
            int slot = 1;
            foreach (var bot in bots)
                _bots.Add(new RoomBot(bot.Id, bot.Name, bot.CharacterClass, bot.Level, slot++, SmallerTeamLocked()));
            PlayerCount = (byte)Math.Max(1, _members.Count + _bots.Count);
        }
    }

    public RoomBot? AddBot(uint id, string name, byte characterClass, byte level)
    {
        lock (_members)
        {
            if (PlayersLocked() >= MaxPlayers || _bots.Any(bot => bot.Id == id)) return null;

            int slot = FreeSlotLocked();
            var seated = new RoomBot(id, name, characterClass, level, slot, SmallerTeamLocked());
            _bots.Add(seated);
            PlayerCount = (byte)PlayersLocked();
            return seated;
        }
    }

    private int PlayersLocked() => _members.Count(member => !member.IsObserver) + _bots.Count;

    private int FreeSlotLocked()
    {
        int slot = 0;
        while (_members.Any(member => member.Slot == slot) || _bots.Any(bot => bot.Slot == slot)) slot++;
        return slot;
    }

    public int MakeObserver(RoomMember member)
    {
        lock (_members)
        {
            if (member.IsObserver) return -1;
            int old = member.Slot;
            member.Slot = -1;
            PlayerCount = (byte)Math.Max(1, PlayersLocked());
            return old;
        }
    }

    public bool MakePlayer(RoomMember member)
    {
        lock (_members)
        {
            if (!member.IsObserver) return true;
            if (PlayersLocked() >= MaxPlayers) return false;
            member.Slot = FreeSlotLocked();
            member.Team = SmallerTeamLocked();
            PlayerCount = (byte)PlayersLocked();
            return true;
        }
    }

    public bool HasTeams => Mode is MatchMode.TeamDeathmatch or MatchMode.Jessium or MatchMode.Szm or MatchMode.Channel5Team;

    private byte SmallerTeamLocked()
    {
        if (!HasTeams) return 0;
        int second = _members.Count(member => !member.IsObserver && member.Team == 1) + _bots.Count(bot => bot.Team == 1);
        int first = PlayersLocked() - second;
        return (byte)(second < first ? 1 : 0);
    }

    public void ClearBots()
    {
        lock (_members)
        {
            _bots.Clear();
            PlayerCount = (byte)Math.Max(1, _members.Count);
        }
    }

    public List<RoomMember> Members
    {
        get
        {
            lock (_members) return _members.OrderBy(member => member.Slot).ToList();
        }
    }

    public bool IsBotRoom
    {
        get
        {
            lock (_members) return _members.Count == 0;
        }
    }

    public bool IsHost(ClientSession session) => session.Account is not null && session.Account.Id == HostUserId;

    public ClientSession? HostSession => FindUser(HostUserId)?.Session;

    public RoomMember? Find(ClientSession session)
    {
        lock (_members) return _members.FirstOrDefault(member => member.Session == session);
    }

    public RoomMember? FindUser(uint userId)
    {
        lock (_members) return _members.FirstOrDefault(member => member.Session.Account.Id == userId);
    }

    public RoomMember? Join(ClientSession session)
    {
        lock (_members)
        {
            RoomMember? already = _members.FirstOrDefault(member => member.Session == session);
            if (already is not null) return already;
            if (PlayersLocked() >= MaxPlayers) return null;

            int slot = FreeSlotLocked();
            bool isHost = session.Account.Id == HostUserId;
            var joined = new RoomMember { Session = session, Slot = slot, Team = SmallerTeamLocked(), Status = isHost ? (byte)2 : (byte)0 };
            _members.Add(joined);
            PlayerCount = (byte)PlayersLocked();
            return joined;
        }
    }

    public RoomMember? Leave(ClientSession session)
    {
        lock (_members)
        {
            RoomMember? member = _members.FirstOrDefault(candidate => candidate.Session == session);
            if (member is null) return null;
            _members.Remove(member);
            PlayerCount = (byte)Math.Max(1, PlayersLocked());
            return member;
        }
    }

    public void Clear()
    {
        lock (_members) _members.Clear();
    }

    public required ushort Id { get; init; }
    public required ushort ChannelId { get; init; }
    public required string Title { get; init; }

    public string Password { get; set; } = "";
    public required uint HostUserId { get; set; }

    public required IPEndPoint HostEndPoint { get; set; }

    public DateTime CreatedUtc { get; } = DateTime.UtcNow;

    public ushort PlayedMapId { get; set; }

    public DateTime? MatchStartedUtc { get; set; }

    public bool HostLoaded { get; set; }

    public byte PlayerCount { get; set; } = 1;
    public byte MaxPlayers { get; init; } = 6;

    public MatchMode Mode { get; init; } = MatchMode.Survival;

    public byte State { get; set; }

    public ushort LevelId { get; set; } = 0x1F41;

    public ushort RuleId { get; set; } = 0x1F42;
}

public sealed class RoomRegistry
{
    public static RoomRegistry Instance { get; } = new();

    private readonly object _gate = new();
    private readonly Dictionary<ushort, Room> _rooms = [];
    private ushort _nextId;

    public Room Create(ushort channelId, string title, uint hostUserId, IPEndPoint hostEndPoint,
        MatchMode mode, byte maxPlayers, ushort levelId, ushort ruleId)
    {
        lock (_gate)
        {
            RemoveHostedByLocked(hostUserId);
            var room = new Room
            {
                Id = ++_nextId,
                ChannelId = channelId,
                Title = title,
                HostUserId = hostUserId,
                HostEndPoint = hostEndPoint,
                Mode = mode,
                MaxPlayers = maxPlayers,
                LevelId = levelId,
                RuleId = ruleId,
            };
            _rooms[room.Id] = room;
            return room;
        }
    }

    public void RemoveHostedBy(uint hostUserId)
    {
        lock (_gate)
        {
            RemoveHostedByLocked(hostUserId);
        }
    }

    public Room? Find(ushort roomId)
    {
        lock (_gate)
        {
            return _rooms.GetValueOrDefault(roomId);
        }
    }

    public void Remove(ushort roomId)
    {
        lock (_gate)
        {
            _rooms.Remove(roomId);
        }
    }

    public IReadOnlyList<Room> All()
    {
        lock (_gate)
        {
            return _rooms.Values.ToList();
        }
    }

    public Room? FindHostedBy(uint hostUserId)
    {
        lock (_gate)
        {
            return _rooms.Values.FirstOrDefault(room => room.HostUserId == hostUserId);
        }
    }

    public IReadOnlyList<Room> InChannel(ushort channelId)
    {
        lock (_gate)
        {
            return _rooms.Values.Where(room => room.ChannelId == channelId).OrderBy(room => room.Id).ToList();
        }
    }

    private void RemoveHostedByLocked(uint hostUserId)
    {
        foreach (Room room in _rooms.Values.Where(room => room.HostUserId == hostUserId).ToList())
            _rooms.Remove(room.Id);
    }
}
