using BrawlBusters.Core.Data;
using BrawlBusters.Core.Logging;
using BrawlBusters.Core.Network;
using BrawlBusters.Core.Protocol;

namespace BrawlBusters.Core.Chat;

public sealed class BuddyService
{
    public const byte Whisper = 0x26;
    public const byte ListRequest = 0x27;
    public const byte Add = 0x28;
    public const byte AnswerRequest = 0x29;
    public const byte Remove = 0x2A;
    public const byte Search = 0x2E;
    public const byte WhisperById = 0x25;

    private const byte PrivateMessage = 0x0E;
    private const byte List = 0x11;
    private const byte Added = 0x12;
    private const byte Updated = 0x13;
    private const byte Removed = 0x14;
    private const byte AddResult = 0x15;
    private const byte Requests = 0x17;
    private const byte SearchResult = 0x09;
    private const int MaxSearchResults = 30;

    private const byte ResultSuccess = (byte)NetError.Success;
    // Recorded from the real server for a nickname that does not exist: 2, which the client shows as its general
    // "request failed" text. (The client only has own texts for 89, 96 and 97.)
    private const byte ResultNotExist = 2;
    private const byte ResultTargetListFull = (byte)NetError.Chat_TargetBuddyListFull;
    private const byte ResultSelf = (byte)NetError.Chat_SelfBuddy;
    private const byte ResultAlready = (byte)NetError.Chat_AlreadyBuddy;
    private const byte ResultMyListFull = (byte)NetError.Chat_MyBuddyListFull;

    // The client's eBuddyState (PbActionScriptLib): 0 nothing, 1 new request, 2 waiting for accept, 3 buddy, 4 offline,
    // 5 in game, 6 ready, 7 in room, 8 in single play, 9 in ladder room, 10 in ladder room finding a match.
    private const byte StateNewRequest = 1;
    private const byte StateWaitingForAccept = 2;
    private const byte StateOffline = 4;
    private const byte StateInGame = 5;
    private const byte StateReady = 6;
    private const byte StateInRoom = 7;
    private const byte StateInSinglePlay = 8;

    private const byte PresenceInGame = 0x2B;
    private const byte PresenceSingleStart = 0x31;

    private const int MaxBuddies = 50;

    private readonly AccountRepository _accounts;
    private readonly Func<uint, ChatSession?> _findOnline;
    private readonly Func<uint[], ChatSession[]> _onlineAmong;

    public BuddyService(AccountRepository accounts, Func<uint, ChatSession?> findOnline, Func<uint[], ChatSession[]> onlineAmong)
    {
        _accounts = accounts;
        _findOnline = findOnline;
        _onlineAmong = onlineAmong;
    }

    // A adds B: A sees B as "waiting for accept", B sees A as a "new request". B accepts: both see each other with the
    // real state. B declines, or one of them removes the other: the entry disappears on both sides.
    // An entry of mine is a friendship once the other side has me too; who has me without my having him is a request.

    public async Task SendListAsync(ChatSession session, CancellationToken cancellationToken)
    {
        Account? account = _accounts.FindById(session.UserId);
        List<BuddyEntry> buddies = account?.Buddies ?? [];
        List<BuddyEntry> requests = RequestsFor(session.UserId, buddies);

        var writer = new PacketWriter(MsgCategory.sChat, List).WriteUInt16((ushort)(buddies.Count + requests.Count));
        foreach (BuddyEntry buddy in buddies)
        {
            writer.WriteByte(1);
            WriteRecord(writer, buddy, StateOf(session.UserId, buddy.Id));
        }
        foreach (BuddyEntry request in requests)
        {
            writer.WriteByte(1);
            WriteRecord(writer, request, StateNewRequest);
        }
        await session.SendAsync(writer, cancellationToken);

        if (requests.Count > 0) await SendRequestsAsync(session, requests, cancellationToken);
    }

    public async Task AddAsync(ChatSession session, string nickname, CancellationToken cancellationToken)
    {
        Account? target = _accounts.FindByNickname(nickname);
        byte result = ResultSuccess;
        BuddyEntry? added = null;

        if (target is null) result = ResultNotExist;
        else if (target.Id == session.UserId) result = ResultSelf;
        else if (target.Buddies.Count >= MaxBuddies) result = ResultTargetListFull;
        else
        {
            _accounts.Update(session.UserId, account =>
            {
                if (account.Buddies.Any(buddy => buddy.Id == target.Id)) { result = ResultAlready; return; }
                if (account.Buddies.Count >= MaxBuddies) { result = ResultMyListFull; return; }

                added = new BuddyEntry { Id = target.Id, Nickname = target.Nickname, AddedUnix = Now() };
                account.Buddies.Add(added);
                account.BuddyRequests.Remove(target.Id);
            });
        }

        Log.Info(session.Tag, $"Add buddy '{nickname}' -> result {result}");

        bool mutual = added is not null && target is not null && target.Buddies.Any(buddy => buddy.Id == session.UserId);
        if (added is not null)
        {
            // He had asked me before: adding him is the same as accepting. The request entry I have turns into a buddy.
            PacketWriter own = WriteRecord(new PacketWriter(MsgCategory.sChat, mutual ? Updated : Added), added, StateOf(session.UserId, added.Id));
            await session.SendAsync(own, cancellationToken);
        }
        await session.SendAsync(new PacketWriter(MsgCategory.sChat, AddResult).WriteByte(result), cancellationToken);

        if (added is null || target is null) return;

        var me = new BuddyEntry { Id = session.UserId, Nickname = session.Nickname, AddedUnix = added.AddedUnix };
        if (mutual)
        {
            if (_findOnline(target.Id) is { } friend)
                await TrySendAsync(friend, () => friend.SendAsync(WriteRecord(new PacketWriter(MsgCategory.sChat, Updated), me, StateOf(target.Id, me.Id)), cancellationToken));
            return;
        }

        _accounts.Update(target.Id, account =>
        {
            if (!account.BuddyRequests.Contains(session.UserId)) account.BuddyRequests.Add(session.UserId);
        });
        if (_findOnline(target.Id) is { } online)
        {
            await TrySendAsync(online, async () =>
            {
                await online.SendAsync(WriteRecord(new PacketWriter(MsgCategory.sChat, Added), me, StateNewRequest), cancellationToken);
                await SendRequestsAsync(online, [me], cancellationToken);
            });
        }
    }

    public async Task AnswerAsync(ChatSession session, bool accept, uint playerId, CancellationToken cancellationToken)
    {
        Account? other = _accounts.FindById(playerId);
        bool asked = other is not null && other.Buddies.Any(buddy => buddy.Id == session.UserId);
        BuddyEntry? mine = null;
        bool full = false;

        _accounts.Update(session.UserId, account =>
        {
            account.BuddyRequests.Remove(playerId);
            mine = account.Buddies.FirstOrDefault(buddy => buddy.Id == playerId);
            if (!accept || !asked || other is null || mine is not null) return;
            if (account.Buddies.Count >= MaxBuddies) { full = true; return; }

            mine = new BuddyEntry { Id = other.Id, Nickname = other.Nickname, AddedUnix = Now() };
            account.Buddies.Add(mine);
        });

        if (other is null || !asked)
        {
            Log.Info(session.Tag, $"Buddy request of {playerId}: {(accept ? "accepted" : "declined")}, but there is no such request");
            return;
        }

        var me = new BuddyEntry { Id = session.UserId, Nickname = session.Nickname, AddedUnix = Now() };
        ChatSession? requester = _findOnline(playerId);

        if (accept && !full && mine is not null)
        {
            Log.Info(session.Tag, $"Buddy request of {other.Nickname}: accepted - they are buddies now");
            await session.SendAsync(WriteRecord(new PacketWriter(MsgCategory.sChat, Updated), mine, StateOf(session.UserId, playerId)), cancellationToken);
            if (requester is not null)
                await TrySendAsync(requester, () => requester.SendAsync(WriteRecord(new PacketWriter(MsgCategory.sChat, Updated), me, StateOf(playerId, me.Id)), cancellationToken));
            return;
        }

        // Declined (or my list is full): the waiting entry of the other side goes, and so does the request here.
        Log.Info(session.Tag, $"Buddy request of {other.Nickname}: {(full ? "could not be accepted, the list is full" : "declined")}");
        _accounts.Update(playerId, account => account.Buddies.RemoveAll(buddy => buddy.Id == session.UserId));
        var asker = new BuddyEntry { Id = other.Id, Nickname = other.Nickname, AddedUnix = Now() };
        await session.SendAsync(WriteRecord(new PacketWriter(MsgCategory.sChat, Removed), asker, StateOffline), cancellationToken);
        if (full) await session.SendAsync(new PacketWriter(MsgCategory.sChat, AddResult).WriteByte(ResultMyListFull), cancellationToken);
        if (requester is not null)
            await TrySendAsync(requester, () => requester.SendAsync(WriteRecord(new PacketWriter(MsgCategory.sChat, Removed), me, StateOffline), cancellationToken));
    }

    public async Task RemoveAsync(ChatSession session, uint buddyId, CancellationToken cancellationToken)
    {
        BuddyEntry? removed = null;
        _accounts.Update(session.UserId, account =>
        {
            removed = account.Buddies.FirstOrDefault(buddy => buddy.Id == buddyId);
            if (removed is not null) account.Buddies.Remove(removed);
            account.BuddyRequests.Remove(buddyId);
        });

        // A friendship ends on both sides; a request that was still waiting is withdrawn.
        bool hadMe = false;
        _accounts.Update(buddyId, account =>
        {
            hadMe = account.Buddies.RemoveAll(buddy => buddy.Id == session.UserId) > 0;
            hadMe |= account.BuddyRequests.Remove(session.UserId);
        });

        Log.Info(session.Tag, $"Remove buddy {buddyId} -> {(removed is null ? "not a buddy" : "removed")}");
        if (removed is not null)
            await session.SendAsync(WriteRecord(new PacketWriter(MsgCategory.sChat, Removed), removed, StateOffline), cancellationToken);

        if (hadMe && _findOnline(buddyId) is { } other)
        {
            var me = new BuddyEntry { Id = session.UserId, Nickname = session.Nickname, AddedUnix = Now() };
            await TrySendAsync(other, () => other.SendAsync(WriteRecord(new PacketWriter(MsgCategory.sChat, Removed), me, StateOffline), cancellationToken));
        }
    }

    public Task SearchAsync(ChatSession session, string text, CancellationToken cancellationToken)
    {
        List<Account> found = text.Length == 0
            ? []
            : _accounts.All()
                .Where(account => account.HasNickname && account.Id != session.UserId
                    && account.Nickname.StartsWith(text, StringComparison.OrdinalIgnoreCase))
                .OrderBy(account => account.Nickname, StringComparer.OrdinalIgnoreCase)
                .Take(MaxSearchResults)
                .ToList();

        Log.Info(session.Tag, $"Player search '{text}': {found.Count} found");
        var writer = new PacketWriter(MsgCategory.sChat, SearchResult).WriteUInt32((uint)found.Count);
        foreach (Account account in found)
            writer.WriteUInt32(account.Id).WriteWideString(account.Nickname);
        return session.SendAsync(writer, cancellationToken);
    }

    public Task<NetError?> WhisperByIdAsync(ChatSession sender, uint targetId, string text, CancellationToken cancellationToken)
    {
        Account? target = _accounts.FindById(targetId);
        return target is null
            ? Task.FromResult<NetError?>(NetError.Chat_NotExistNickName)
            : WhisperAsync(sender, target.Nickname, text, cancellationToken);
    }

    /// <summary>Delivers a whisper; the result is the client's error code when it could not be delivered.</summary>
    public async Task<NetError?> WhisperAsync(ChatSession sender, string targetNickname, string text, CancellationToken cancellationToken)
    {
        Account? target = _accounts.FindByNickname(targetNickname);
        if (target is null) return NetError.Chat_NotExistNickName;
        ChatSession? online = _findOnline(target.Id);
        if (online is null) return NetError.Chat_TagetOffLine;

        PacketWriter Message() => new PacketWriter(MsgCategory.sChat, PrivateMessage)
            .WriteUInt32(sender.UserId)
            .WriteWideString(sender.Nickname)
            .WriteUInt32(target.Id)
            .WriteWideString(target.Nickname)
            .WriteWideString(text);

        Log.Info(sender.Tag, $"[whisper to {target.Nickname}] {text}");
        await sender.SendAsync(Message(), cancellationToken);
        if (online != sender) await TrySendAsync(online, () => online.SendAsync(Message(), cancellationToken));
        return null;
    }

    /// <summary>Tells everybody who is a buddy of <paramref name="userId"/> what he is doing now (sChat 13).</summary>
    public async Task PresenceChangedAsync(uint userId, CancellationToken cancellationToken)
    {
        uint[] watchers = _accounts.FindWhoHasBuddy(userId);
        foreach (ChatSession watcher in _onlineAmong(watchers))
        {
            BuddyEntry? entry = _accounts.FindById(watcher.UserId)?.Buddies.FirstOrDefault(buddy => buddy.Id == userId);
            if (entry is null) continue;
            byte state = StateOf(watcher.UserId, userId);
            await TrySendAsync(watcher, () => watcher.SendAsync(WriteRecord(new PacketWriter(MsgCategory.sChat, Updated), entry, state), cancellationToken));
        }
    }

    /// <summary>Who has asked <paramref name="userId"/> and is still waiting: they have him, he does not have them.</summary>
    private List<BuddyEntry> RequestsFor(uint userId, List<BuddyEntry> buddies)
        => _accounts.FindWhoHasBuddy(userId)
            .Where(id => buddies.All(buddy => buddy.Id != id))
            .Select(id => _accounts.FindById(id))
            .Where(requester => requester is { HasNickname: true })
            .Select(requester => new BuddyEntry { Id = requester!.Id, Nickname = requester.Nickname, AddedUnix = Now() })
            .ToList();

    /// <summary>What <paramref name="ownerId"/> sees next to <paramref name="buddyId"/> in his list.</summary>
    private byte StateOf(uint ownerId, uint buddyId)
    {
        Account? buddy = _accounts.FindById(buddyId);
        if (buddy is null || buddy.Buddies.All(entry => entry.Id != ownerId)) return StateWaitingForAccept;

        ChatSession? online = _findOnline(buddyId);
        if (online is null) return StateOffline;
        if (online.Presence == PresenceInGame) return StateInGame;
        if (online.Presence == PresenceSingleStart) return StateInSinglePlay;
        return online.GameRoom is null ? StateReady : StateInRoom;
    }

    private Task SendRequestsAsync(ChatSession session, List<BuddyEntry> requests, CancellationToken cancellationToken)
    {
        var writer = new PacketWriter(MsgCategory.sChat, Requests).WriteUInt32((uint)requests.Count);
        foreach (BuddyEntry requester in requests)
            writer.WriteUInt32(requester.Id).WriteWideString(requester.Nickname);
        return session.SendAsync(writer, cancellationToken);
    }

    private static PacketWriter WriteRecord(PacketWriter writer, BuddyEntry buddy, byte state)
        => writer.WriteUInt32(buddy.Id)
            .WriteUInt32(buddy.Id)
            .WriteWideString(buddy.Nickname)
            .WriteByte(state)
            .WriteUInt32(buddy.AddedUnix);

    private static uint Now() => (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private static async Task TrySendAsync(ChatSession target, Func<Task> send)
    {
        try
        {
            await send();
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
            _ = target;
        }
    }
}
