using BrawlBusters.Core.Sessions;
using BrawlBusters.Core.Data;
using BrawlBusters.Core.Logging;
using BrawlBusters.Core.Network;
using BrawlBusters.Core.Protocol;

namespace BrawlBusters.Core.Chat;

public sealed class InviteService
{
    public const byte Invite = 0x1D;
    public const byte AutoReject = 0x1E;
    public const byte AnswerInvite = 0x1F;
    public const byte Follow = 0x20;
    public const byte AnswerFollow = 0x21;

    private const byte InviteReceived = 0x03;
    private const byte EnterRoom = 0x04;
    private const byte Result = 0x05;
    private const byte FollowReceived = 0x06;
    private const byte FollowGranted = 0x07;

    private const byte PresenceInGame = 0x2B;

    private readonly Func<uint, ChatSession?> _findById;
    private readonly Func<string, ChatSession?> _findByNickname;

    public InviteService(Func<uint, ChatSession?> findById, Func<string, ChatSession?> findByNickname)
    {
        _findById = findById;
        _findByNickname = findByNickname;
    }

    /// <summary>Whether the player's level is inside the level range of <paramref name="channelId"/> (the client's channel table).</summary>
    private static bool LevelFits(uint userId, ushort channelId)
    {
        if (SessionRegistry.Find(userId) is not { } game || !GameData.Instance.Channels.TryGetValue(channelId, out ChannelData? channel)) return true;
        byte level = game.Account.DisplayLevel;
        return level >= channel.LevelMin && level <= channel.LevelMax;
    }

    public Task InviteAsync(ChatSession session, uint targetId, string targetNickname, CancellationToken cancellationToken)
    {
        ChatSession? target = _findById(targetId) ?? _findByNickname(targetNickname);
        NetError? refusal = target switch
        {
            null => NetError.Chat_TagetOffLine,
            _ when session.GameRoom is null => NetError.Room_NotExistInviter,
            _ when target.GameRoom == session.GameRoom => NetError.Chat_Taget_Same_Room,
            _ when target.Presence == PresenceInGame => NetError.Chat_TagetIngame,
            _ when session.GameRoom is { } place && !LevelFits(target.UserId, place.Channel) => NetError.Chat_Taget_LevelLimit,
            _ => null,
        };
        if (refusal is not null || target is null)
        {
            Log.Info(session.Tag, $"Invite of '{targetNickname}' refused: {refusal}");
            return session.SendAsync(ResultPacket(refusal ?? NetError.Chat_TagetOffLine, targetNickname), cancellationToken);
        }

        (ushort channel, ushort room) = session.GameRoom!.Value;
        Log.Info(session.Tag, $"Invites {target.Nickname} to room {room} of channel {channel}");
        return Task.WhenAll(
            target.SendAsync(new PacketWriter(MsgCategory.sChat, InviteReceived)
                .WriteUInt32(session.UserId)
                .WriteWideString(session.Nickname)
                .WriteUInt16(channel)
                .WriteUInt16(room)
                .WriteUInt32(session.GameRoomKey), cancellationToken),
            session.SendAsync(ResultPacket(NetError.Chat_SendInvitation_Result, target.Nickname), cancellationToken));
    }

    public Task AnswerInviteAsync(ChatSession session, string inviterNickname, uint inviterId, ushort channel, ushort room, uint key, bool accept,
        CancellationToken cancellationToken)
    {
        ChatSession? inviter = _findById(inviterId) ?? _findByNickname(inviterNickname);
        if (!accept)
        {
            Log.Info(session.Tag, $"Declined the invitation of {inviterNickname}");
            return inviter is null
                ? Task.CompletedTask
                : inviter.SendAsync(ResultPacket(NetError.Chat_Taget_Rejection, session.Nickname), cancellationToken);
        }

        if (inviter?.GameRoom is not { } place)
        {
            Log.Info(session.Tag, $"Accepted the invitation of {inviterNickname}, but that room is gone");
            return session.SendAsync(ResultPacket(NetError.Room_NotExistInviter, inviterNickname), cancellationToken);
        }

        if (RoomRegistry.Instance.Find(place.Room) is { } invited && invited.Members.Count(member => !member.IsObserver) + invited.Bots.Count >= invited.MaxPlayers)
        {
            Log.Info(session.Tag, $"Accepted the invitation of {inviterNickname}, but room {place.Room} is full");
            return session.SendAsync(ResultPacket(NetError.Chat_System_Rejection, inviterNickname), cancellationToken);
        }

        Log.Info(session.Tag, $"Accepted the invitation of {inviterNickname}: room {place.Room} of channel {place.Channel}");
        return session.SendAsync(new PacketWriter(MsgCategory.sChat, EnterRoom)
            .WriteWideString(inviter.Nickname)
            .WriteUInt32(inviter.UserId)
            .WriteUInt16(place.Channel)
            .WriteUInt16(place.Room)
            .WriteUInt32(inviter.GameRoomKey), cancellationToken);
    }

    public Task AutoRejectAsync(ChatSession session, byte reason, string ownNickname, string otherNickname, CancellationToken cancellationToken)
    {
        ChatSession? other = _findByNickname(otherNickname);
        Log.Info(session.Tag, $"Cannot accept the request of {otherNickname}: {(NetError)reason}");
        return other is null
            ? Task.CompletedTask
            : other.SendAsync(ResultPacket((NetError)reason, ownNickname.Length > 0 ? ownNickname : session.Nickname), cancellationToken);
    }

    public Task FollowAsync(ChatSession session, uint targetId, string targetNickname, CancellationToken cancellationToken)
    {
        ChatSession? target = _findById(targetId) ?? _findByNickname(targetNickname);
        NetError? refusal = target switch
        {
            null => NetError.Chat_TagetOffLine,
            _ when target.GameRoom is null => NetError.Chat_Taget_Not_IngameWaitingRoom,
            _ when target.GameRoom == session.GameRoom => NetError.Chat_Taget_Same_Room,
            _ when target.GameRoom is { } place && !LevelFits(session.UserId, place.Channel) => NetError.Chat_My_LevelLimit,
            _ => null,
        };
        if (refusal is not null || target is null)
        {
            Log.Info(session.Tag, $"Follow of '{targetNickname}' refused: {refusal}");
            return session.SendAsync(ResultPacket(refusal ?? NetError.Chat_TagetOffLine, targetNickname), cancellationToken);
        }

        Log.Info(session.Tag, $"Asks to follow {target.Nickname}");
        return Task.WhenAll(
            target.SendAsync(new PacketWriter(MsgCategory.sChat, FollowReceived)
                .WriteUInt32(session.UserId)
                .WriteWideString(session.Nickname), cancellationToken),
            session.SendAsync(ResultPacket(NetError.Chat_SendFollow_Result, target.Nickname), cancellationToken));
    }

    public Task AnswerFollowAsync(ChatSession session, string followerNickname, uint followerId, bool accept, CancellationToken cancellationToken)
    {
        ChatSession? follower = _findById(followerId) ?? _findByNickname(followerNickname);
        if (follower is null) return Task.CompletedTask;

        if (!accept || session.GameRoom is not { } place)
        {
            Log.Info(session.Tag, $"Declined the follow request of {followerNickname}");
            NetError reason = accept ? NetError.Chat_Taget_Not_IngameWaitingRoom : NetError.Chat_Taget_Rejection;
            return follower.SendAsync(ResultPacket(reason, session.Nickname), cancellationToken);
        }

        Log.Info(session.Tag, $"Lets {followerNickname} follow into room {place.Room} of channel {place.Channel}");
        return follower.SendAsync(new PacketWriter(MsgCategory.sChat, FollowGranted)
            .WriteWideString(session.Nickname)
            .WriteUInt32(session.UserId)
            .WriteUInt16(place.Channel)
            .WriteUInt16(place.Room)
            .WriteWideString(""), cancellationToken);
    }

    /// <summary>sChat 05: <c>u8 error, wstr nickname</c> - the client shows the text of that error with the name filled in.</summary>
    public static PacketWriter ResultPacket(NetError result, string nickname)
        => new PacketWriter(MsgCategory.sChat, Result).WriteByte((byte)result).WriteWideString(nickname);
}
