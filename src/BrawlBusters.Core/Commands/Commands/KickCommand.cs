using BrawlBusters.Core.Data;
using BrawlBusters.Core.Security;
using BrawlBusters.Core.Sessions;

namespace BrawlBusters.Core.Commands;

public sealed class KickCommand : ChatCommand
{
    public override string Name => "kick";
    public override Permission Required => Permission.Kick;
    public override string Usage => "kick <nickname>";
    public override string Description => "disconnects a player";

    public override Task ExecuteAsync(CommandContext context, string[] arguments, CancellationToken cancellationToken)
    {
        Account? target = context.Target(Need(arguments, 0));
        if (target is null || SessionRegistry.Find(target.Id) is not { } session) return context.Reply("That player is not online.");
        if (context.Caller is { } caller && target.Grade > caller.Grade) return context.Reply("You cannot kick a higher grade.");

        session.Connection.Close();
        return context.Reply($"{target.Nickname} was disconnected.");
    }
}
