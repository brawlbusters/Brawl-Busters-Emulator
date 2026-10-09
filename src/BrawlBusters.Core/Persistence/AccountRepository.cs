using System.Text.Json;
using System.Threading.Channels;
using BrawlBusters.Core.Logging;
using BrawlBusters.Core.Persistence;

namespace BrawlBusters.Core.Data;

/// <summary>
/// The one place accounts live while the server runs.
/// <para>
/// Every account is held in memory. Readers get a private copy, so a session can never observe another
/// session's half-finished change; writers go through <see cref="Update"/>, which runs under the repository
/// lock. Changed accounts are queued and written to the backend (MariaDB or a JSON file) by a single
/// background task, so game logic never waits for the database.
/// </para>
/// </summary>
public sealed class AccountRepository : IAsyncDisposable
{
    private const uint FirstAccountId = 1001;

    private readonly object _gate = new();
    private readonly Dictionary<uint, Account> _byId = [];
    private readonly Dictionary<string, uint> _byLogin = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, uint> _byNickname = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<uint> _dirty = [];
    private readonly Channel<StoreTransaction> _purchases = Channel.CreateUnbounded<StoreTransaction>();
    private readonly Dictionary<uint, AccountAuthority> _authorityToSave = [];
    private static readonly TimeSpan AuthorityPollInterval = TimeSpan.FromSeconds(2);
    private DateTime _nextAuthorityPoll = DateTime.UtcNow;

    /// <summary>
    /// Raised when an account's grade or ban changed - by a command, by the config, or by somebody editing the
    /// <c>users</c> table while the server runs. Arguments: the account after the change and its grade before.
    /// </summary>
    public event Action<Account, AccountGrade>? AuthorityChanged;
    private readonly SemaphoreSlim _wake = new(0);
    private readonly IAccountBackend _backend;
    private readonly CancellationTokenSource _stop = new();
    private Task _writer = Task.CompletedTask;

    private AccountRepository(IAccountBackend backend) => _backend = backend;

    public static async Task<AccountRepository> OpenAsync(IAccountBackend backend, CancellationToken cancellationToken)
    {
        var repository = new AccountRepository(backend);
        List<Account> accounts = await backend.LoadAllAsync(cancellationToken);
        foreach (Account account in accounts) repository.Index(account);
        Log.Info(LogChannel.Database, "Accounts", $"Loaded {accounts.Count} account(s) from {backend.Name}");
        repository._writer = Task.Run(repository.WriteLoopAsync);
        return repository;
    }

    public Account? FindById(uint id)
    {
        lock (_gate) return _byId.TryGetValue(id, out Account? account) ? Copy(account) : null;
    }

    public Account? FindByLoginId(string loginId)
    {
        lock (_gate) return _byLogin.TryGetValue(loginId, out uint id) ? Copy(_byId[id]) : null;
    }

    public Account? FindByNickname(string nickname)
    {
        if (nickname.Length == 0) return null;
        lock (_gate) return _byNickname.TryGetValue(nickname, out uint id) ? Copy(_byId[id]) : null;
    }

    public bool NicknameExists(string nickname)
    {
        lock (_gate) return _byNickname.ContainsKey(nickname);
    }

    public List<Account> All()
    {
        lock (_gate) return _byId.Values.Select(Copy).ToList();
    }

    public uint[] FindWhoHasBuddy(uint buddyId)
    {
        lock (_gate)
            return _byId.Values.Where(account => account.Buddies.Any(buddy => buddy.Id == buddyId)).Select(account => account.Id).ToArray();
    }

    public Account? Create(string loginId, string passwordHash)
    {
        lock (_gate)
        {
            if (_byLogin.ContainsKey(loginId)) return null;

            var account = new Account
            {
                Id = _byId.Count == 0 ? FirstAccountId : Math.Max(FirstAccountId, _byId.Keys.Max() + 1),
                LoginId = loginId,
                PasswordHash = passwordHash,
            };
            Index(account);
            MarkDirty(account.Id);
            return Copy(account);
        }
    }

    public Account? Update(uint id, Action<Account> change)
    {
        lock (_gate)
        {
            if (!_byId.TryGetValue(id, out Account? account)) return null;

            string nicknameBefore = account.Nickname;
            AccountGrade gradeBefore = account.Grade;
            DateTime? bannedBefore = account.BannedUntilUtc;
            change(account);
            if (account.Grade != gradeBefore || account.BannedUntilUtc != bannedBefore)
            {
                _authorityToSave[id] = new AccountAuthority(id, account.Grade, account.BannedUntilUtc);
                Announce(Copy(account), gradeBefore);
            }
            if (!string.Equals(nicknameBefore, account.Nickname, StringComparison.OrdinalIgnoreCase))
            {
                if (nicknameBefore.Length > 0) _byNickname.Remove(nicknameBefore);
                if (account.Nickname.Length > 0) _byNickname[account.Nickname] = id;
            }
            MarkDirty(id);
            return Copy(account);
        }
    }

    public bool TrySetNickname(uint id, string nickname)
    {
        lock (_gate)
        {
            if (!_byId.TryGetValue(id, out Account? account)) return false;
            if (_byNickname.TryGetValue(nickname, out uint owner) && owner != id) return false;

            if (account.Nickname.Length > 0) _byNickname.Remove(account.Nickname);
            account.Nickname = nickname;
            _byNickname[nickname] = id;
            MarkDirty(id);
            return true;
        }
    }

    public int RaiseAll(byte level, int experience, int gold, int cash)
    {
        lock (_gate)
        {
            int changed = 0;
            foreach (Account account in _byId.Values)
            {
                if (account.Level >= level && account.Gold >= gold && account.Cash >= cash) continue;
                if (account.Level < level)
                {
                    account.Level = level;
                    account.Experience = Math.Max(account.Experience, experience);
                }
                account.Gold = Math.Max(account.Gold, gold);
                account.Cash = Math.Max(account.Cash, cash);
                MarkDirty(account.Id);
                changed++;
            }
            return changed;
        }
    }

    public void RecordPurchase(uint userId, uint itemId, int gold, int cash, string kind)
        => _purchases.Writer.TryWrite(new StoreTransaction(userId, itemId, gold, cash, kind, DateTime.UtcNow));

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _wake.Release();
        await _writer;
        await FlushAsync(CancellationToken.None);
        await _backend.DisposeAsync();
    }

    private void Announce(Account account, AccountGrade gradeBefore)
    {
        Action<Account, AccountGrade>? handlers = AuthorityChanged;
        if (handlers is not null) _ = Task.Run(() => handlers(account, gradeBefore));
    }

    private async Task PollAuthorityAsync(CancellationToken cancellationToken)
    {
        if (DateTime.UtcNow < _nextAuthorityPoll) return;
        _nextAuthorityPoll = DateTime.UtcNow + AuthorityPollInterval;

        IReadOnlyList<AccountAuthority> stored = await _backend.LoadAuthorityAsync(cancellationToken);
        lock (_gate)
        {
            foreach (AccountAuthority row in stored)
            {
                if (_authorityToSave.ContainsKey(row.UserId) || !_byId.TryGetValue(row.UserId, out Account? account)) continue;
                if (account.Grade == row.Grade && account.BannedUntilUtc == row.BannedUntilUtc) continue;

                AccountGrade gradeBefore = account.Grade;
                account.Grade = row.Grade;
                account.BannedUntilUtc = row.BannedUntilUtc;
                Log.Info(LogChannel.Database, "Accounts", $"'{account.LoginId}' was changed in the database: grade {gradeBefore} -> {row.Grade}"
                    + (row.BannedUntilUtc is { } until ? $", banned until {until:u}" : ""));
                Announce(Copy(account), gradeBefore);
            }
        }
    }

    private void Index(Account account)
    {
        _byId[account.Id] = account;
        _byLogin[account.LoginId] = account.Id;
        if (account.Nickname.Length > 0) _byNickname[account.Nickname] = account.Id;
    }

    private void MarkDirty(uint id)
    {
        if (_dirty.Add(id) && _dirty.Count == 1) _wake.Release();
    }

    private static Account Copy(Account account)
        => JsonSerializer.Deserialize<Account>(JsonSerializer.SerializeToUtf8Bytes(account))!;

    private async Task WriteLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                await _wake.WaitAsync(TimeSpan.FromSeconds(1), _stop.Token);
                await FlushAsync(_stop.Token);
                await PollAuthorityAsync(_stop.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception exception)
            {
                Log.Error(LogChannel.Database, "Accounts", $"Saving failed, will retry: {exception.Message}");
                await Task.Delay(TimeSpan.FromSeconds(2));
            }
        }
    }

    private async Task FlushAsync(CancellationToken cancellationToken)
    {
        List<Account> changed;
        lock (_gate)
        {
            changed = _dirty.Select(id => Copy(_byId[id])).ToList();
            _dirty.Clear();
        }

        if (changed.Count > 0)
        {
            try
            {
                await _backend.SaveAsync(changed, cancellationToken);
                Log.Debug(LogChannel.Database, "Accounts", $"Saved {changed.Count} account(s)");
            }
            catch
            {
                lock (_gate)
                    foreach (Account account in changed) _dirty.Add(account.Id);
                throw;
            }
        }

        List<AccountAuthority> authority;
        lock (_gate) authority = [.. _authorityToSave.Values];
        foreach (AccountAuthority row in authority)
        {
            await _backend.SaveAuthorityAsync(row, cancellationToken);
            lock (_gate)
                if (_authorityToSave.TryGetValue(row.UserId, out AccountAuthority? latest) && latest == row) _authorityToSave.Remove(row.UserId);
        }

        while (_purchases.Reader.TryRead(out StoreTransaction? purchase))
            await _backend.RecordPurchaseAsync(purchase, cancellationToken);
    }
}
