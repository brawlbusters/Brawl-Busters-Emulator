using System.Net;
using BrawlBusters.Core.Configuration;
using BrawlBusters.Core.Data;
using BrawlBusters.Core.Logging;
using BrawlBusters.Core.Protocol.Packets;

namespace BrawlBusters.Core.Sessions;

public sealed class BotDirector
{
    private const uint FirstBotId = 0x7F000000;

    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(2);

    private static readonly TimeSpan PlayerRoomGrace = TimeSpan.FromSeconds(6);

    private static readonly string[] Names =
    [
        "Rocko", "Mina", "Tobi", "Kessler", "Juno", "Brick", "Pixel", "Nova", "Dash", "Rumble",
        "Sable", "Vex", "Moxie", "Tank", "Lumi", "Zed", "Koda", "Fizz", "Bolt", "Echo",
        "Rook", "Tango", "Wren", "Ajax", "Pogo", "Skye", "Grim", "Hex", "Lark", "Onyx",
        "Piper", "Quill", "Riot", "Sumo", "Trix", "Ursa", "Volt", "Wisp", "Yeti", "Zuko",
    ];

    private static readonly string[] Titles =
    [
        "Brawl Busters!", "Come on in", "Fun only", "No rush pls", "Everyone welcome",
        "Fast games", "Let's go!", "Chill room", "Pros only", "Just for fun", "Zombie time",
        "One more round", "Newbies ok", "gogo", "Warm up",
    ];

    private static readonly MatchMode[] RoomModes =
    [
        MatchMode.TeamDeathmatch, MatchMode.Survival, MatchMode.Jessium, MatchMode.Szm,
        MatchMode.Zim, MatchMode.FreeForAll, MatchMode.Bsr, MatchMode.Channel5Team,
    ];

    private static BotDirector? _instance;

    private readonly object _gate = new();
    private readonly Random _random = new();
    private readonly List<Bot> _bots = [];
    private readonly List<BotRoom> _rooms = [];
    private readonly ushort[] _channels;
    private readonly string _serverName;

    private readonly bool _joinPlayerRooms;

    private BotDirector(string serverName, BotSettings settings, ushort[] channels)
    {
        _joinPlayerRooms = settings.JoinPlayerRooms;
        _serverName = serverName;
        _channels = channels;
        for (int i = 0; i < settings.Count; i++)
        {
            string name = i < Names.Length ? Names[i] : $"{Names[i % Names.Length]}{i / Names.Length + 1}";
            _bots.Add(new Bot
            {
                Id = FirstBotId + (uint)i,
                Name = name,
                Channel = channels[i % channels.Length],
                CharacterClass = (byte)_random.Next(1, 6),
                Level = (byte)_random.Next(1, 40),
            });
        }
    }

    public static Task RunAsync(string serverName, EmulatorSettings settings, CancellationToken cancellationToken)
    {
        ushort[] channels = settings.Channels.Select(channel => channel.Id).ToArray();
        if (!settings.Bots.Enabled || settings.Bots.Count <= 0 || channels.Length == 0 || GameData.Instance.Maps.Count == 0)
            return Task.CompletedTask;

        var director = new BotDirector(serverName, settings.Bots, channels);
        _instance = director;
        Log.Info(serverName, $"Bots: {settings.Bots.Count} bots across {channels.Length} channel(s)");
        return director.LoopAsync(cancellationToken);
    }

    public static bool Claim(Room room, out List<(uint Id, string Name, byte CharacterClass, byte Level)> bots)
    {
        bots = [];
        BotDirector? director = _instance;
        if (director is null || !director._joinPlayerRooms) return false;

        lock (director._gate)
        {
            BotRoom? botRoom = director._rooms.FirstOrDefault(candidate => candidate.Room == room);
            if (botRoom is null || botRoom.Claimed || botRoom.Phase != RoomPhase.Created) return false;

            botRoom.Claimed = true;
            bots = botRoom.Members.Select(bot => (bot.Id, bot.Name, bot.CharacterClass, bot.Level)).ToList();
            return true;
        }
    }

    public static void Release(Room room)
    {
        BotDirector? director = _instance;
        if (director is null) return;

        lock (director._gate)
        {
            DateTime now = DateTime.UtcNow;
            foreach (Bot bot in director._bots.Where(bot => bot.GuestOf == room))
            {
                bot.GuestOf = null;
                bot.BusyUntil = now + director.Seconds(5, 25);
            }

            BotRoom? botRoom = director._rooms.FirstOrDefault(candidate => candidate.Room == room);
            if (botRoom is null) return;

            director._rooms.Remove(botRoom);
            foreach (Bot bot in botRoom.Members)
            {
                bot.Room = null;
                bot.BusyUntil = now + director.Seconds(5, 25);
            }
        }
    }

    public static int InLobby(ushort channelId)
    {
        BotDirector? director = _instance;
        if (director is null) return 0;
        lock (director._gate) return director._bots.Count(bot => bot.Room is null && bot.GuestOf is null && bot.Channel == channelId);
    }

    private async Task LoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(Tick);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                lock (_gate)
                {
                    DateTime now = DateTime.UtcNow;
                    foreach (BotRoom room in _rooms.ToList()) Advance(room, now);
                    ReleaseGuestsOfClosedRooms(now);
                    FillPlayerRooms(now);
                    foreach (Bot bot in _bots.Where(bot => bot.Room is null && bot.GuestOf is null && bot.BusyUntil <= now).ToList()) Act(bot, now);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void Act(Bot bot, DateTime now)
    {
        int roll = _random.Next(100);
        if (roll < 55)
        {
            BotRoom? open = _rooms
                .Where(room => !room.Claimed && room.Phase == RoomPhase.Created && room.Room.ChannelId == bot.Channel && room.Members.Count < room.Room.MaxPlayers)
                .OrderBy(_ => _random.Next())
                .FirstOrDefault();
            if (open is not null)
            {
                Join(bot, open);
                return;
            }
        }

        if (roll < 80) OpenRoom(bot, now);
        else
        {
            bot.Channel = _channels[_random.Next(_channels.Length)];
            bot.BusyUntil = now + Seconds(4, 15);
        }
    }

    private void FillPlayerRooms(DateTime now)
    {
        if (!_joinPlayerRooms) return;

        List<Room> rooms = RoomRegistry.Instance.All()
            .Where(candidate => !candidate.IsBotRoom
                && now - candidate.CreatedUtc > PlayerRoomGrace
                && candidate.State == RoomPacket.StateOf(RoomPhase.Created)
                && candidate.PlayerCount < candidate.MaxPlayers)
            .ToList();

        foreach (Room room in rooms)
        {
            if (_random.Next(100) >= 60) continue;

            Bot? bot = _bots.Where(candidate => candidate.Room is null && candidate.GuestOf is null)
                           .OrderBy(_ => _random.Next()).FirstOrDefault()
                       ?? RecruitFromWaitingRoom();
            if (bot is null) continue;

            RoomBot? seated = room.AddBot(bot.Id, bot.Name, bot.CharacterClass, bot.Level);
            if (seated is null) continue;

            bot.Channel = room.ChannelId;
            bot.GuestOf = room;
            Log.Info(_serverName, $"Bot {bot.Name} joined room {room.Id} ({room.PlayerCount}/{room.MaxPlayers})");
            _ = GameFlow.BotJoinedAsync(room, seated);
        }
    }

    private Bot? RecruitFromWaitingRoom()
    {
        BotRoom? source = _rooms
            .Where(room => !room.Claimed && room.Phase == RoomPhase.Created && room.Members.Count > 0)
            .OrderByDescending(room => room.Members.Count)
            .FirstOrDefault();
        if (source is null) return null;

        Bot bot = source.Members[^1];
        source.Members.Remove(bot);
        bot.Room = null;
        if (source.Members.Count == 0)
        {
            RoomRegistry.Instance.Remove(source.Room.Id);
            _rooms.Remove(source);
        }
        else
        {
            source.Room.PlayerCount = (byte)source.Members.Count;
        }
        return bot;
    }

    private void ReleaseGuestsOfClosedRooms(DateTime now)
    {
        foreach (Bot bot in _bots.Where(bot => bot.GuestOf is not null))
        {
            Room room = bot.GuestOf!;
            if (RoomRegistry.Instance.Find(room.Id) == room && room.Bots.Any(seated => seated.Id == bot.Id)) continue;
            bot.GuestOf = null;
            bot.BusyUntil = now + Seconds(5, 25);
        }
    }

    private void OpenRoom(Bot bot, DateTime now)
    {
        GameData data = GameData.Instance;

        var choices = RoomModes
            .Select(mode => (Mode: mode, Maps: data.MapsOf(mode).Where(map => map.Channels.Count == 0 || map.Channels.Contains(bot.Channel)).ToList()))
            .Where(choice => choice.Maps.Count > 0)
            .ToList();
        if (choices.Count == 0) return;

        var (mode, maps) = choices[_random.Next(choices.Count)];
        MapInfo map = maps[_random.Next(maps.Count)];
        ushort rule = map.Rules.Count > 0 && _random.Next(3) == 0 ? map.Rules[_random.Next(map.Rules.Count)] : data.DefaultRule(map);
        byte maxPlayers = (byte)Math.Clamp(_random.Next(map.MinPlayers, map.MaxPlayers + 1), Math.Max((byte)2, map.MinPlayers), map.MaxPlayers);
        if (map.MaxPlayers < 2) maxPlayers = map.MaxPlayers;

        Room room = RoomRegistry.Instance.Create(
            bot.Channel, Titles[_random.Next(Titles.Length)], bot.Id, FakeEndPoint(bot), mode, maxPlayers, map.Id, rule);

        var botRoom = new BotRoom
        {
            Room = room,
            Map = map,
            Phase = RoomPhase.Created,
            NextStep = now + Seconds(15, 45),
            GiveUpAt = now + Seconds(90, 150),
        };
        _rooms.Add(botRoom);
        Join(bot, botRoom);
        Log.Info(_serverName, $"Bot {bot.Name} opened room {room.Id} in channel {room.ChannelId}: {mode}, map {map.Id} ({map.Name}), rule {rule}, max {maxPlayers}");
    }

    private static void Join(Bot bot, BotRoom room)
    {
        bot.Room = room;
        room.Members.Add(bot);
        room.Room.PlayerCount = (byte)room.Members.Count;
    }

    private void Advance(BotRoom room, DateTime now)
    {
        if (room.Claimed || now < room.NextStep) return;

        switch (room.Phase)
        {
            case RoomPhase.Created:
                if (room.Members.Count >= room.Map.MinPlayers) Enter(room, RoomPhase.Ready, now + Seconds(3, 4));
                else if (now >= room.GiveUpAt) Close(room, now);
                break;
            case RoomPhase.Ready:
                Enter(room, RoomPhase.Starting, now + Seconds(4, 7));
                break;
            case RoomPhase.Starting:
                Enter(room, RoomPhase.Loaded, now + Seconds(4, 6));
                break;
            case RoomPhase.Loaded:
                Enter(room, RoomPhase.Playing, now + Seconds(60, 240));
                Log.Info(_serverName, $"Bot room {room.Room.Id}: match started with {room.Members.Count} bot(s) on map {room.Map.Id}");
                break;
            default:
                Close(room, now);
                break;
        }
    }

    private static void Enter(BotRoom room, RoomPhase phase, DateTime nextStep)
    {
        room.Phase = phase;
        room.Room.State = RoomPacket.StateOf(phase);
        room.NextStep = nextStep;
    }

    private void Close(BotRoom room, DateTime now)
    {
        RoomRegistry.Instance.Remove(room.Room.Id);
        _rooms.Remove(room);
        foreach (Bot bot in room.Members)
        {
            bot.Room = null;
            bot.BusyUntil = now + Seconds(3, 20);
        }
    }

    private TimeSpan Seconds(int from, int to) => TimeSpan.FromSeconds(_random.Next(from, to + 1));

    private static IPEndPoint FakeEndPoint(Bot bot)
        => new(new IPAddress([10, 99, (byte)(bot.Id >> 8), (byte)bot.Id]), 27000 + (int)(bot.Id & 0x3FF));

    private sealed class Bot
    {
        public required uint Id { get; init; }
        public required string Name { get; init; }
        public ushort Channel { get; set; }
        public BotRoom? Room { get; set; }

        public Room? GuestOf { get; set; }
        public DateTime BusyUntil { get; set; }
        public byte CharacterClass { get; init; } = 1;
        public byte Level { get; init; } = 1;
    }

    private sealed class BotRoom
    {
        public required Room Room { get; init; }
        public required MapInfo Map { get; init; }
        public RoomPhase Phase { get; set; }
        public DateTime NextStep { get; set; }
        public DateTime GiveUpAt { get; set; }

        public bool Claimed { get; set; }
        public List<Bot> Members { get; } = [];
    }
}
