using BrawlBusters.Core.Data;
using BrawlBusters.Core.Logging;
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

public interface IChatCommand
{
    string Name { get; }

    string[] Aliases => [];

    Permission Required { get; }

    string Usage { get; }

    string Description { get; }

    Task ExecuteAsync(CommandContext context, string[] arguments, CancellationToken cancellationToken);
}

/// <summary>
/// Slash commands typed in chat or on the server console. A command must run while the caller holds the
/// <see cref="World"/> gate, because most of them touch sessions and rooms.
/// </summary>
public sealed class CommandRegistry
{
    public const char Prefix = '/';

    private readonly Dictionary<string, IChatCommand> _byName = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<IChatCommand> _commands = [];

    public static CommandRegistry Instance { get; } = StandardCommands.Create();

    public IReadOnlyList<IChatCommand> Commands => _commands;

    public CommandRegistry Add(IChatCommand command)
    {
        _commands.Add(command);
        _byName[command.Name] = command;
        foreach (string alias in command.Aliases) _byName[alias] = command;
        return this;
    }

    public bool Knows(string text)
        => text.Length > 1 && text[0] == Prefix && _byName.ContainsKey(NameOf(text));

    /// <summary>Runs <paramref name="text"/> if it is a known command. Returns false when it is ordinary chat.</summary>
    public async Task<bool> TryExecuteAsync(CommandContext context, string text, CancellationToken cancellationToken)
    {
        if (!Knows(text)) return false;

        string[] parts = text[1..].Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        IChatCommand command = _byName[parts[0]];
        if (!context.Can(command.Required))
        {
            string needed = AuthorityNotifier.NameOf(Permissions.MinimumGrade(command.Required));
            string has = AuthorityNotifier.NameOf(context.Caller?.Grade ?? AccountGrade.Player);
            Log.Warn(LogChannel.Commands, context.Tag, $"Command refused: {text} - the caller is {has}, the command needs {needed} or higher");
            await context.Reply($"[System] You are not allowed to use {CommandRegistry.Prefix}{command.Name}. It requires the grade {needed} or higher.");
            return true;
        }

        Log.Info(LogChannel.Commands, context.Tag, $"Command: {text}");
        try
        {
            await command.ExecuteAsync(context, parts[1..], cancellationToken);
        }
        catch (CommandUsageException)
        {
            await context.Reply($"Usage: {Prefix}{command.Usage}");
        }
        return true;
    }

    private static string NameOf(string text)
    {
        int end = text.IndexOf(' ');
        return end < 0 ? text[1..] : text[1..end];
    }
}

public sealed class CommandUsageException : Exception;
