using BrawlBusters.Core.Data;
using BrawlBusters.Core.Logging;
using BrawlBusters.Core.Network;
using BrawlBusters.Core.Protocol;
using BrawlBusters.Core.Protocol.Packets;
using BrawlBusters.Core.Sessions;

namespace BrawlBusters.MainServer;

public sealed class ChatHub
{
    private const byte PresenceMenus = 0x1A;
    private const byte PresenceInGame = 0x2B;
    private const byte PresenceSingleStart = 0x31;
    private const byte PresenceSingleEnd = 0x32;
    private const byte ChatLogin = 0x35;
    private const byte ChatRoomMessage = 0x24;
    private const byte PresenceInRoom = 0x2F;
    private const byte PresenceDetail = 0x34;
    private const byte RoomJoin = 0x00;
    private const byte RoomLeave = 0x01;

    private const byte PrivateMessage = 0x0E;

    private const string GmSenderName = "#GMMessage";

    private static readonly string[] JoinRoomCommands = ["/j ", "/join "];
    private static readonly string[] ObserveRoomCommands = ["/gmo ", "/gm_observe "];
    private const string GmSayCommand = "/gm ";
    private const byte ChatRoomBroadcast = 0x0D;
    private const byte RoomJoined = 0x04;
    private const byte RoomLeft = 0x05;

    private readonly AccountRepository _accounts;
    private readonly HashSet<ChatSession> _online = [];

    private readonly BuddyService _buddies;

    public ChatHub(AccountRepository accounts)
    {
        _accounts = accounts;
        _buddies = new BuddyService(accounts, FindOnline, OnlineAmong);
    }

    private ChatSession? FindOnline(uint userId)
    {
        lock (_gate) return _online.FirstOrDefault(session => session.UserId == userId);
    }

    private ChatSession[] OnlineAmong(uint[] userIds)
    {
        lock (_gate) return _online.Where(session => userIds.Contains(session.UserId)).ToArray();
    }

    private readonly object _gate = new();
    private readonly Dictionary<ushort, HashSet<ChatSession>> _rooms = [];

    public Task HandleAsync(ChatSession session, byte[] message, CancellationToken cancellationToken)
    {
        if (message.Length < 2) return Task.CompletedTask;

        var category = (MsgCategory)message[0];
        byte sub = message[1];
        var reader = new PacketReader(message, offset: 2);

        try
        {
            switch (category)
            {
                case MsgCategory.Start:
                    return session.SendAsync(KeepAlivePacket.Idle(), cancellationToken);

                case MsgCategory.cChat when sub == BuddyService.ListRequest:
                    return _buddies.SendListAsync(session, cancellationToken);

                case MsgCategory.cChat when sub == BuddyService.Add:
                    return _buddies.AddAsync(session, reader.ReadWideString(), cancellationToken);

                case MsgCategory.cChat when sub == BuddyService.AnswerRequest:
                {
                    bool accept = reader.ReadByte() != 0;
                    return _buddies.AnswerAsync(session, accept, reader.ReadUInt32(), cancellationToken);
                }

                case MsgCategory.cChat when sub == BuddyService.Remove:
                    return _buddies.RemoveAsync(session, reader.ReadUInt32(), cancellationToken);

                case MsgCategory.cChat when sub == BuddyService.Search:
                    return _buddies.SearchAsync(session, reader.ReadWideString(), cancellationToken);

                case MsgCategory.cChat when sub == BuddyService.WhisperById:
                {
                    uint targetId = reader.ReadUInt32();
                    string text = reader.ReadWideString();
                    return WhisperByIdAsync(session, targetId, text, cancellationToken);
                }

                case MsgCategory.cChat when sub == BuddyService.Whisper:
                {
                    string target = reader.ReadWideString();
                    string text = reader.ReadWideString();
                    return WhisperAsync(session, target, text, cancellationToken);
                }

                case MsgCategory.cChat when sub is PresenceMenus or PresenceInGame or PresenceSingleStart or PresenceSingleEnd or ChatLogin:
                    session.Presence = sub;
                    if (sub == PresenceMenus) session.GameRoom = null;
                    return Task.CompletedTask;

                case MsgCategory.cChat when sub == ChatRoomMessage:
                {
                    ushort room = reader.ReadUInt16();
                    string text = reader.ReadWideString();
                    return text.StartsWith('/')
                        ? CommandAsync(session, room, text, cancellationToken)
                        : BroadcastAsync(session, room, text, cancellationToken);
                }

                case MsgCategory.cChat when sub == PresenceInRoom:
                {
                    reader.ReadUInt32();
                    ushort channel = reader.ReadUInt16();
                    ushort gameRoom = reader.ReadUInt16();
                    session.GameRoom = (channel, gameRoom);
                    Log.Info(session.Tag, $"Presence: in game room {gameRoom} of channel {channel}");
                    return Task.CompletedTask;
                }

                case MsgCategory.cChat when sub == PresenceDetail:
                    session.PresenceDetail = reader.ReadUInt16();
                    return Task.CompletedTask;

                case MsgCategory.cChatRoom when sub == RoomJoin:
                    return JoinAsync(session, reader.ReadUInt16(), cancellationToken);

                case MsgCategory.cChatRoom when sub == RoomLeave:
                    Leave(session);
                    return session.SendAsync(new PacketWriter(MsgCategory.sChatRoom, RoomLeft), cancellationToken);

                default:
                    Log.Warn(session.Tag, $"CHAT RECV {category} 0x{sub:X2} (not implemented) {Log.Hex(reader.ReadToEnd())}");
                    return Task.CompletedTask;
            }
        }
        catch (EndOfStreamException exception)
        {
            Log.Warn(session.Tag, $"Malformed chat message {category} 0x{sub:X2}: {exception.Message}");
            return Task.CompletedTask;
        }
    }

    public void Connected(ChatSession session)
    {
        lock (_gate) _online.Add(session);
        _ = _buddies.PresenceChangedAsync(session.UserId, CancellationToken.None);
    }

    public void Disconnected(ChatSession session)
    {
        Leave(session);
        lock (_gate) _online.Remove(session);
        _ = _buddies.PresenceChangedAsync(session.UserId, CancellationToken.None);
    }

    private async Task WhisperByIdAsync(ChatSession session, uint targetId, string text, CancellationToken cancellationToken)
    {
        if (!await _buddies.WhisperByIdAsync(session, targetId, text, cancellationToken))
            await GmMessageAsync(session, "That player is not online.", cancellationToken);
    }

    private async Task WhisperAsync(ChatSession session, string target, string text, CancellationToken cancellationToken)
    {
        if (target.Equals(GmSenderName, StringComparison.OrdinalIgnoreCase)) return;

        if (!await _buddies.WhisperAsync(session, target, text, cancellationToken))
            await GmMessageAsync(session, $"{target} is not online.", cancellationToken);
    }

    private Task CommandAsync(ChatSession session, ushort room, string text, CancellationToken cancellationToken)
    {
        if (StaffCommands.TryExecute(_accounts, session.UserId, session.Tag, text))
            return Task.CompletedTask;

        if (text.StartsWith(GmSayCommand, StringComparison.OrdinalIgnoreCase))
        {
            Account? account = _accounts.FindById(session.UserId);
            string message = text[GmSayCommand.Length..].Trim();
            if (account is { Grade: >= AccountGrade.GameMaster } && message.Length > 0)
            {
                Log.Info(session.Tag, $"/gm: {message}");
                return GmMessageToAllAsync(message, cancellationToken);
            }
        }

        if (StartsWithAny(text, JoinRoomCommands))
            return GmMessageAsync(session, "Joining a room by number is not available on this server yet.", cancellationToken);

        if (StartsWithAny(text, ObserveRoomCommands))
            return GmMessageAsync(session, "Observing a room is not available on this server yet.", cancellationToken);

        return BroadcastAsync(session, room, text, cancellationToken);
    }

    private static bool StartsWithAny(string text, string[] prefixes)
        => prefixes.Any(prefix => text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    private static Task GmMessageAsync(ChatSession target, string text, CancellationToken cancellationToken)
        => target.SendAsync(
            new PacketWriter(MsgCategory.sChat, PrivateMessage)
                .WriteUInt32(0)
                .WriteWideString(GmSenderName)
                .WriteUInt32(target.UserId)
                .WriteWideString(target.Nickname)
                .WriteWideString(text),
            cancellationToken);

    private async Task GmMessageToAllAsync(string text, CancellationToken cancellationToken)
    {
        ChatSession[] targets;
        lock (_gate) targets = [.. _online];

        foreach (ChatSession target in targets)
        {
            try
            {
                await GmMessageAsync(target, text, cancellationToken);
            }
            catch (IOException)
            {
            }
        }
    }

    public void Leave(ChatSession session)
    {
        lock (_gate)
        {
            if (session.Room is { } room && _rooms.TryGetValue(room, out HashSet<ChatSession>? members))
            {
                members.Remove(session);
                if (members.Count == 0) _rooms.Remove(room);
            }
            session.Room = null;
        }
    }

    private Task JoinAsync(ChatSession session, ushort room, CancellationToken cancellationToken)
    {
        Leave(session);
        session.GameRoom = null;
        lock (_gate)
        {
            if (!_rooms.TryGetValue(room, out HashSet<ChatSession>? members)) _rooms[room] = members = [];
            members.Add(session);
            session.Room = room;
        }

        Log.Info(session.Tag, $"Joined chat room {room}");
        return session.SendAsync(
            new PacketWriter(MsgCategory.sChatRoom, RoomJoined).WriteUInt16(room).WriteUInt16(1), cancellationToken);
    }

    private async Task BroadcastAsync(ChatSession sender, ushort room, string text, CancellationToken cancellationToken)
    {
        ChatSession[] members;
        lock (_gate)
        {
            members = _rooms.TryGetValue(room, out HashSet<ChatSession>? set) ? [.. set] : [];
        }

        Log.Info(sender.Tag, $"[room {room}] {text}");
        foreach (ChatSession member in members)
        {
            try
            {
                await member.SendAsync(
                    new PacketWriter(MsgCategory.sChat, ChatRoomBroadcast)
                        .WriteUInt16(0)
                        .WriteWideString(sender.Nickname)
                        .WriteWideString(text),
                    cancellationToken);
            }
            catch (IOException)
            {
            }
        }
    }
}
