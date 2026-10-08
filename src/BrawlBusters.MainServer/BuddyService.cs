using BrawlBusters.Core.Data;
using BrawlBusters.Core.Logging;
using BrawlBusters.Core.Network;
using BrawlBusters.Core.Protocol;

namespace BrawlBusters.MainServer;

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
    private const byte ResultNotExist = 2;
    private const byte ResultSelf = (byte)NetError.Chat_SelfBuddy;
    private const byte ResultAlready = (byte)NetError.Chat_AlreadyBuddy;
    private const byte ResultMyListFull = (byte)NetError.Chat_MyBuddyListFull;

    private const byte StateOnline = 1;
    private const byte StateOffline = 2;

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

    public async Task SendListAsync(ChatSession session, CancellationToken cancellationToken)
    {
        Account? account = _accounts.FindById(session.UserId);
        List<BuddyEntry> buddies = account?.Buddies ?? [];

        var writer = new PacketWriter(MsgCategory.sChat, List).WriteUInt16((ushort)buddies.Count);
        foreach (BuddyEntry buddy in buddies)
        {
            writer.WriteByte(1);
            WriteRecord(writer, buddy);
        }
        await session.SendAsync(writer, cancellationToken);

        if (account is { BuddyRequests.Count: > 0 })
            await SendRequestsAsync(session, account, cancellationToken);
    }

    public async Task AddAsync(ChatSession session, string nickname, CancellationToken cancellationToken)
    {
        Account? target = _accounts.FindByNickname(nickname);
        byte result = ResultSuccess;
        BuddyEntry? added = null;

        if (target is null) result = ResultNotExist;
        else if (target.Id == session.UserId) result = ResultSelf;
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

        if (added is not null)
            await session.SendAsync(WriteRecord(new PacketWriter(MsgCategory.sChat, Added), added), cancellationToken);
        await session.SendAsync(new PacketWriter(MsgCategory.sChat, AddResult).WriteByte(result), cancellationToken);

        if (added is null || target is null) return;

        Account? updatedTarget = _accounts.Update(target.Id, account =>
        {
            if (account.Buddies.All(buddy => buddy.Id != session.UserId) && !account.BuddyRequests.Contains(session.UserId))
                account.BuddyRequests.Add(session.UserId);
        });
        if (updatedTarget is { BuddyRequests.Count: > 0 } && _findOnline(target.Id) is { } online)
            await TrySendAsync(online, () => SendRequestsAsync(online, updatedTarget, cancellationToken));
    }

    public async Task AnswerAsync(ChatSession session, bool accept, uint playerId, CancellationToken cancellationToken)
    {
        Account? other = _accounts.FindById(playerId);
        BuddyEntry? added = null;

        _accounts.Update(session.UserId, account =>
        {
            bool asked = account.BuddyRequests.Remove(playerId);
            if (!accept || !asked || other is null) return;
            if (account.Buddies.Any(buddy => buddy.Id == playerId) || account.Buddies.Count >= MaxBuddies) return;

            added = new BuddyEntry { Id = other.Id, Nickname = other.Nickname, AddedUnix = Now() };
            account.Buddies.Add(added);
        });

        Log.Info(session.Tag, $"Buddy request of {playerId}: {(accept ? "accepted" : "declined")}");
        if (added is not null)
            await session.SendAsync(WriteRecord(new PacketWriter(MsgCategory.sChat, Added), added), cancellationToken);
    }

    public async Task RemoveAsync(ChatSession session, uint buddyId, CancellationToken cancellationToken)
    {
        BuddyEntry? removed = null;
        _accounts.Update(session.UserId, account =>
        {
            removed = account.Buddies.FirstOrDefault(buddy => buddy.Id == buddyId);
            if (removed is not null) account.Buddies.Remove(removed);
        });

        Log.Info(session.Tag, $"Remove buddy {buddyId} -> {(removed is null ? "not a buddy" : "removed")}");
        if (removed is not null)
            await session.SendAsync(WriteRecord(new PacketWriter(MsgCategory.sChat, Removed), removed), cancellationToken);
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

    public Task<bool> WhisperByIdAsync(ChatSession sender, uint targetId, string text, CancellationToken cancellationToken)
    {
        Account? target = _accounts.FindById(targetId);
        return target is null ? Task.FromResult(false) : WhisperAsync(sender, target.Nickname, text, cancellationToken);
    }

    public async Task<bool> WhisperAsync(ChatSession sender, string targetNickname, string text, CancellationToken cancellationToken)
    {
        Account? target = _accounts.FindByNickname(targetNickname);
        ChatSession? online = target is null ? null : _findOnline(target.Id);
        if (target is null || online is null) return false;

        PacketWriter Message() => new PacketWriter(MsgCategory.sChat, PrivateMessage)
            .WriteUInt32(sender.UserId)
            .WriteWideString(sender.Nickname)
            .WriteUInt32(target.Id)
            .WriteWideString(target.Nickname)
            .WriteWideString(text);

        Log.Info(sender.Tag, $"[whisper to {target.Nickname}] {text}");
        await sender.SendAsync(Message(), cancellationToken);
        if (online != sender) await TrySendAsync(online, () => online.SendAsync(Message(), cancellationToken));
        return true;
    }

    public async Task PresenceChangedAsync(uint userId, CancellationToken cancellationToken)
    {
        uint[] watchers = _accounts.FindWhoHasBuddy(userId);
        foreach (ChatSession watcher in _onlineAmong(watchers))
        {
            BuddyEntry? entry = _accounts.FindById(watcher.UserId)?.Buddies.FirstOrDefault(buddy => buddy.Id == userId);
            if (entry is null) continue;
            await TrySendAsync(watcher, () => watcher.SendAsync(WriteRecord(new PacketWriter(MsgCategory.sChat, Updated), entry), cancellationToken));
        }
    }

    private Task SendRequestsAsync(ChatSession session, Account account, CancellationToken cancellationToken)
    {
        var known = account.BuddyRequests
            .Select(id => _accounts.FindById(id))
            .Where(requester => requester is not null)
            .ToList();

        var writer = new PacketWriter(MsgCategory.sChat, Requests).WriteUInt32((uint)known.Count);
        foreach (Account? requester in known)
            writer.WriteUInt32(requester!.Id).WriteWideString(requester.Nickname);
        return session.SendAsync(writer, cancellationToken);
    }

    private PacketWriter WriteRecord(PacketWriter writer, BuddyEntry buddy)
        => writer.WriteUInt32(buddy.Id)
            .WriteUInt32(buddy.Id)
            .WriteWideString(buddy.Nickname)
            .WriteByte(_findOnline(buddy.Id) is null ? StateOffline : StateOnline)
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
