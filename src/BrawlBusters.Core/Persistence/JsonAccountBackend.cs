using System.Text.Json;
using BrawlBusters.Core.Data;

namespace BrawlBusters.Core.Persistence;

/// <summary>Keeps every account in one JSON file. Meant for development without a database server.</summary>
public sealed class JsonAccountBackend : IAccountBackend
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _path;
    private readonly string _purchasePath;
    private readonly Dictionary<uint, Account> _accounts = [];

    public JsonAccountBackend(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        _path = Path.Combine(dataDirectory, "accounts.json");
        _purchasePath = Path.Combine(dataDirectory, "store-transactions.log");
    }

    public string Name => $"JSON file {_path}";

    public Task<List<Account>> LoadAllAsync(CancellationToken cancellationToken)
    {
        List<Account> loaded = File.Exists(_path)
            ? JsonSerializer.Deserialize<List<Account>>(File.ReadAllText(_path)) ?? []
            : [];
        _accounts.Clear();
        foreach (Account account in loaded) _accounts[account.Id] = account;
        return Task.FromResult(loaded);
    }

    public async Task SaveAsync(IReadOnlyList<Account> changed, CancellationToken cancellationToken)
    {
        foreach (Account account in changed) _accounts[account.Id] = account;

        string temporary = _path + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(_accounts.Values.OrderBy(account => account.Id), JsonOptions), cancellationToken);
        File.Move(temporary, _path, overwrite: true);
    }

    public Task RecordPurchaseAsync(StoreTransaction purchase, CancellationToken cancellationToken)
        => File.AppendAllTextAsync(
            _purchasePath,
            $"{purchase.AtUtc:O}\t{purchase.UserId}\t{purchase.Kind}\t{purchase.ItemId}\t{purchase.Gold}\t{purchase.Cash}{Environment.NewLine}",
            cancellationToken);

    public Task<IReadOnlyList<AccountAuthority>> LoadAuthorityAsync(CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<AccountAuthority>>([]);

    public Task SaveAuthorityAsync(AccountAuthority authority, CancellationToken cancellationToken) => Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
