using BrawlBusters.Core.Data;

namespace BrawlBusters.Core.Persistence;

/// <summary>
/// Durable storage behind <see cref="AccountRepository"/>. The repository keeps every account in memory and
/// hands changed accounts to the backend from a single writer task, so a backend never sees concurrent calls.
/// </summary>
public interface IAccountBackend : IAsyncDisposable
{
    string Name { get; }

    Task<List<Account>> LoadAllAsync(CancellationToken cancellationToken);

    Task SaveAsync(IReadOnlyList<Account> changed, CancellationToken cancellationToken);

    Task RecordPurchaseAsync(StoreTransaction purchase, CancellationToken cancellationToken);

    /// <summary>
    /// Grade and ban of every account as the storage has them now. A backend whose data can be edited from
    /// outside while the server runs (a database) returns them so that such edits reach the players at once;
    /// other backends return an empty list.
    /// </summary>
    Task<IReadOnlyList<AccountAuthority>> LoadAuthorityAsync(CancellationToken cancellationToken);

    Task SaveAuthorityAsync(AccountAuthority authority, CancellationToken cancellationToken);
}

/// <summary>The columns an administrator may change directly in the database: who is staff and who is locked out.</summary>
public sealed record AccountAuthority(uint UserId, AccountGrade Grade, DateTime? BannedUntilUtc);

public sealed record StoreTransaction(uint UserId, uint ItemId, int Gold, int Cash, string Kind, DateTime AtUtc);
