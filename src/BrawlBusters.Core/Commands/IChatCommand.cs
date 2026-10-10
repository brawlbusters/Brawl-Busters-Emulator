using BrawlBusters.Core.Security;

namespace BrawlBusters.Core.Commands;

public interface IChatCommand
{
    string Name { get; }

    string[] Aliases => [];

    Permission Required { get; }

    string Usage { get; }

    string Description { get; }

    Task ExecuteAsync(CommandContext context, string[] arguments, CancellationToken cancellationToken);
}

/// <summary>Thrown by a command whose arguments are missing or wrong: the caller is shown its usage line.</summary>
public sealed class CommandUsageException : Exception;
