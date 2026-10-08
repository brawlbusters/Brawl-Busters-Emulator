using System.Text.Json;

namespace BrawlBusters.Core.Data;

public sealed class AccountRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _path;
    private readonly string _mutexName;

    public AccountRepository(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        _path = Path.Combine(dataDirectory, "accounts.json");
        _mutexName = "BrawlBustersEmu_" + Convert.ToHexString(
            System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(_path.ToLowerInvariant())));
    }

    public Account? FindByLoginId(string loginId)
        => Read(accounts => accounts.Find(a => a.LoginId.Equals(loginId, StringComparison.OrdinalIgnoreCase)));

    public List<Account> All() => Read(accounts => accounts);

    public Account? FindById(uint id)
        => Read(accounts => accounts.Find(a => a.Id == id));

    public Account? FindByNickname(string nickname)
        => nickname.Length == 0 ? null : Read(accounts => accounts.Find(a => a.Nickname.Equals(nickname, StringComparison.OrdinalIgnoreCase)));

    public uint[] FindWhoHasBuddy(uint buddyId)
        => Read(accounts => accounts.Where(a => a.Buddies.Any(buddy => buddy.Id == buddyId)).Select(a => a.Id).ToArray());

    public bool NicknameExists(string nickname)
        => Read(accounts => accounts.Exists(a => a.Nickname.Equals(nickname, StringComparison.OrdinalIgnoreCase)));

    public Account? Create(string loginId, string passwordHash)
    {
        return Mutate(accounts =>
        {
            if (accounts.Exists(a => a.LoginId.Equals(loginId, StringComparison.OrdinalIgnoreCase))) return null;

            var account = new Account
            {
                Id = accounts.Count == 0 ? 1001 : accounts.Max(a => a.Id) + 1,
                LoginId = loginId,
                PasswordHash = passwordHash,
            };
            accounts.Add(account);
            return account;
        });
    }

    public Account? Update(uint id, Action<Account> change)
    {
        return Mutate(accounts =>
        {
            Account? account = accounts.Find(a => a.Id == id);
            if (account is not null) change(account);
            return account;
        });
    }

    public bool TrySetNickname(uint id, string nickname)
    {
        return Mutate(accounts =>
        {
            if (accounts.Exists(a => a.Id != id && a.Nickname.Equals(nickname, StringComparison.OrdinalIgnoreCase)))
                return false;

            Account? account = accounts.Find(a => a.Id == id);
            if (account is null) return false;
            account.Nickname = nickname;
            return true;
        });
    }

    private T Read<T>(Func<List<Account>, T> query) => Locked(() => query(Load()));

    private T Mutate<T>(Func<List<Account>, T> change)
    {
        return Locked(() =>
        {
            List<Account> accounts = Load();
            T result = change(accounts);
            string temporary = _path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(accounts, JsonOptions));
            ReplaceFile(temporary, _path);
            return result;
        });
    }

    private List<Account> Load()
    {
        if (!File.Exists(_path)) return [];
        return JsonSerializer.Deserialize<List<Account>>(Retry(() => File.ReadAllText(_path))) ?? [];
    }

    private static void ReplaceFile(string source, string target)
        => Retry(() =>
        {
            File.Move(source, target, overwrite: true);
            return true;
        });

    private static T Retry<T>(Func<T> action)
    {
        const int attempts = 40;
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                return action();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException && attempt < attempts)
            {
                Thread.Sleep(25);
            }
        }
    }

    private T Locked<T>(Func<T> action)
    {
        using var mutex = new Mutex(false, _mutexName);
        try
        {
            mutex.WaitOne();
        }
        catch (AbandonedMutexException)
        {
        }

        try
        {
            return action();
        }
        finally
        {
            mutex.ReleaseMutex();
        }
    }
}
