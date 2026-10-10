using BrawlBusters.Core.Data;
using BrawlBusters.Core.Security;
using BrawlBusters.Core.Sessions;

namespace BrawlBusters.Core.Commands;

/// <summary>Who runs a command and how the answer gets back to them (room chat, chat server or the server console).</summary>
public sealed class CommandContext
{
    public required AccountRepository Accounts { get; init; }

    public required string Tag { get; init; }

    public required Func<string, Task> Reply { get; init; }

    /// <summary>The account of the caller; <c>null</c> for the server console, which may do everything.</summary>
    public Account? Caller { get; init; }

    /// <summary>The caller's game connection, when the command came from a logged-in player.</summary>
    public ClientSession? Session { get; init; }

    public bool Can(Permission permission) => Caller is null || Caller.Can(permission);

    /// <summary>The player a command is aimed at: the named one, or the caller when no name was given.</summary>
    public Account? Target(string? nickname)
        => string.IsNullOrEmpty(nickname)
            ? (Caller is null ? null : Accounts.FindById(Caller.Id))
            : Accounts.FindByNickname(nickname) ?? Accounts.FindByLoginId(nickname);
}
