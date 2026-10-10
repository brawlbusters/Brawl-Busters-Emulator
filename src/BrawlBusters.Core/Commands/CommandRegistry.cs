using System.Reflection;
using BrawlBusters.Core.Data;
using BrawlBusters.Core.Logging;
using BrawlBusters.Core.Security;
using BrawlBusters.Core.Sessions;

namespace BrawlBusters.Core.Commands;

/// <summary>
/// Slash commands typed in chat or on the server console. A command must run while the caller holds the
/// <see cref="World"/> gate, because most of them touch sessions and rooms.
/// </summary>
public sealed class CommandRegistry
{
    public const char Prefix = '/';

    private readonly Dictionary<string, IChatCommand> _byName = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<IChatCommand> _commands = [];

    public static CommandRegistry Instance { get; } = Discover();

    public IReadOnlyList<IChatCommand> Commands => _commands;

    /// <summary>
    /// Every command class of this assembly (the files of the Commands folder), lowest required grade first and by
    /// name within a grade - the order /help lists them in.
    /// </summary>
    private static CommandRegistry Discover()
    {
        var registry = new CommandRegistry();
        IEnumerable<IChatCommand> found = Assembly.GetExecutingAssembly().GetTypes()
            .Where(type => type is { IsClass: true, IsAbstract: false } && typeof(IChatCommand).IsAssignableFrom(type)
                && type.GetConstructor(Type.EmptyTypes) is not null)
            .Select(type => (IChatCommand)Activator.CreateInstance(type)!)
            .OrderBy(command => Permissions.MinimumGrade(command.Required))
            .ThenBy(command => command.Name, StringComparer.Ordinal);
        foreach (IChatCommand command in found) registry.Add(command);
        return registry;
    }

    public CommandRegistry Add(IChatCommand command)
    {
        if (_byName.TryGetValue(command.Name, out IChatCommand? taken))
            throw new InvalidOperationException($"Two commands are called '{command.Name}': {taken.GetType().Name} and {command.GetType().Name}");

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
