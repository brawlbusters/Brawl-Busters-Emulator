using BrawlBusters.Core.Security;
using BrawlBusters.Core.Sessions;

namespace BrawlBusters.Core.Commands;

public sealed class HelpCommand : ChatCommand
{
    public override string Name => "help";
    public override string[] Aliases => ["commands", "?"];
    public override Permission Required => Permission.None;
    public override string Usage => "help";
    public override string Description => "lists the commands you may use";

    public override async Task ExecuteAsync(CommandContext context, string[] arguments, CancellationToken cancellationToken)
    {
        List<IChatCommand> allowed = CommandRegistry.Instance.Commands
            .Where(command => command is not HelpCommand && context.Can(command.Required))
            .ToList();
        string grade = context.Caller is { } caller ? AuthorityNotifier.NameOf(caller.Grade) : "Console";
        if (allowed.Count == 0)
        {
            await context.Reply($"[System] Your grade is {grade}. There are no commands for this grade.");
            return;
        }

        await context.Reply($"[System] Your grade is {grade}. Commands you may use:");
        foreach (IChatCommand command in allowed)
            await context.Reply($"{CommandRegistry.Prefix}{command.Usage} - {command.Description}");
    }
}
