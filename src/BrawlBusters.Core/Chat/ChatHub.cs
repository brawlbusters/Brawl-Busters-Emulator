using BrawlBusters.Core.Commands;
using BrawlBusters.Core.Data;
using BrawlBusters.Core.Logging;
using BrawlBusters.Core.Network;
using BrawlBusters.Core.Protocol;
using BrawlBusters.Core.Protocol.Packets;
using BrawlBusters.Core.Sessions;

namespace BrawlBusters.Core.Chat;

public sealed class ChatHub
{
    /// <summary>Set from the settings at start-up (EmulatorSettings.MaxChatLength).</summary>
    public static int MaxChatLength { get; set; } = 70;

    private const byte PresenceMenus = 0x1A;
    private const byte PresenceInGame = 0x2B;
    private const byte PresenceSingleStart = 0x31;
    private const byte PresenceSingleEnd = 0x32;

    // cChat 33 (client 0x593480, no body): one more state report of the same family as 31 / 32 / 34.
    private const byte PresenceOther = 0x33;
    private const byte ChatLogin = 0x35;
    private const byte ChatRoomMessage = 0x24;
    private const byte PresenceInRoom = 0x2F;
    private const byte PresenceDetail = 0x34;
    private const byte RoomJoin = 0x00;
    private const byte RoomLeave = 0x01;

    private const byte PrivateMessage = 0x0E;

    private const string GmSenderName = "#GMMessage";

    private const byte ChatRoomBroadcast = 0x0D;
    private const byte RoomJoined = 0x04;
    private const byte RoomLeft = 0x05;

    private readonly AccountRepository _accounts;
    private readonly HashSet<ChatSession> _online = [];

    private readonly BuddyService _buddies;
    private readonly InviteService _invites;

    public ChatHub(AccountRepository accounts)
    {
        _accounts = accounts;
        _buddies = new BuddyService(accounts, FindOnline, OnlineAmong);
        _invites = new InviteService(FindOnline, FindOnlineByNickname);
    }

    private ChatSession? FindOnline(uint userId)
    {
        lock (_gate) return _online.FirstOrDefault(session => session.UserId == userId);
    }

    private ChatSession? FindOnlineByNickname(string nickname)
    {
        lock (_gate) return _online.FirstOrDefault(session => session.Nickname.Equals(nickname, StringComparison.OrdinalIgnoreCase));
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

                case MsgCategory.cChat when sub is PresenceMenus or PresenceInGame or PresenceSingleStart or PresenceSingleEnd or PresenceOther or ChatLogin:
                    session.Presence = sub;
                    if (sub == PresenceMenus) session.GameRoom = null;
                    return _buddies.PresenceChangedAsync(session.UserId, cancellationToken);

                case MsgCategory.cChat when sub == ChatRoomMessage:
                {
                    ushort room = reader.ReadUInt16();
                    string text = reader.ReadWideString();
                    if (text.Length > MaxChatLength && SessionRegistry.Find(session.UserId) is { } game)
                    {
                        // The chat server has no error packet of its own; the dialog comes over the game connection.
                        Log.Info(LogChannel.Chat, session.Tag, $"Chat line of {text.Length} characters refused");
                        return game.SendAsync(game.Room is null
                            ? LobbyPacket.Error(NetError.Lobby_ExceedMaxChatLength)
                            : RoomPacket.Error(NetError.Room_ExceedMaxChatLength), cancellationToken);
                    }

                    return text.StartsWith('/')
                        ? CommandAsync(session, room, text, cancellationToken)
                        : BroadcastAsync(session, room, text, cancellationToken);
                }

                case MsgCategory.cChat when sub == PresenceInRoom:
                {
                    session.GameRoomKey = reader.ReadUInt32();
                    ushort channel = reader.ReadUInt16();
                    ushort gameRoom = reader.ReadUInt16();
                    session.GameRoom = (channel, gameRoom);
                    Log.Info(session.Tag, $"Presence: in game room {gameRoom} of channel {channel}");
                    return _buddies.PresenceChangedAsync(session.UserId, cancellationToken);
                }

                case MsgCategory.cChat when sub == InviteService.Invite:
                {
                    uint targetId = reader.ReadUInt32();
                    return _invites.InviteAsync(session, targetId, reader.ReadWideString(), cancellationToken);
                }

                case MsgCategory.cChat when sub == InviteService.AutoReject:
                {
                    byte reason = reader.ReadByte();
                    string own = reader.ReadWideString();
                    return _invites.AutoRejectAsync(session, reason, own, reader.ReadWideString(), cancellationToken);
                }

                case MsgCategory.cChat when sub == InviteService.AnswerInvite:
                {
                    string inviter = reader.ReadWideString();
                    uint inviterId = reader.ReadUInt32();
                    ushort channel = reader.ReadUInt16();
                    ushort gameRoom = reader.ReadUInt16();
                    uint key = reader.ReadUInt32();
                    return _invites.AnswerInviteAsync(session, inviter, inviterId, channel, gameRoom, key, reader.ReadByte() != 0, cancellationToken);
                }

                case MsgCategory.cChat when sub == InviteService.Follow:
                {
                    uint targetId = reader.ReadUInt32();
                    return _invites.FollowAsync(session, targetId, reader.ReadWideString(), cancellationToken);
                }

                case MsgCategory.cChat when sub == InviteService.AnswerFollow:
                {
                    string follower = reader.ReadWideString();
                    uint followerId = reader.ReadUInt32();
                    bool accept = reader.ReadByte() != 0;
                    reader.ReadByte();
                    return _invites.AnswerFollowAsync(session, follower, followerId, accept, cancellationToken);
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
        if (await _buddies.WhisperByIdAsync(session, targetId, text, cancellationToken) is { } failure)
        {
            Log.Info(session.Tag, $"Whisper to player {targetId} not delivered: {failure}");
            await session.SendAsync(InviteService.ResultPacket(failure, ""), cancellationToken);
        }
    }

    private async Task WhisperAsync(ChatSession session, string target, string text, CancellationToken cancellationToken)
    {
        if (target.Equals(GmSenderName, StringComparison.OrdinalIgnoreCase)) return;

        if (await _buddies.WhisperAsync(session, target, text, cancellationToken) is { } failure)
        {
            Log.Info(session.Tag, $"Whisper to '{target}' not delivered: {failure}");
            await session.SendAsync(InviteService.ResultPacket(failure, target), cancellationToken);
        }
    }

    private async Task CommandAsync(ChatSession session, ushort room, string text, CancellationToken cancellationToken)
    {
        var context = new CommandContext
        {
            Accounts = _accounts,
            Tag = session.Tag,
            Caller = _accounts.FindById(session.UserId),
            Session = SessionRegistry.Find(session.UserId),
            Reply = line => GmMessageAsync(session, line, cancellationToken),
        };
        if (await CommandRegistry.Instance.TryExecuteAsync(context, text, cancellationToken)) return;

        await BroadcastAsync(session, room, text, cancellationToken);
    }

    private static Task GmMessageAsync(ChatSession target, string text, CancellationToken cancellationToken)
        => target.SendAsync(
            new PacketWriter(MsgCategory.sChat, PrivateMessage)
                .WriteUInt32(0)
                .WriteWideString(GmSenderName)
                .WriteUInt32(target.UserId)
                .WriteWideString(target.Nickname)
                .WriteWideString(text),
            cancellationToken);

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
