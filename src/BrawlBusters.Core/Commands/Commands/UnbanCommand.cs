using BrawlBusters.Core.Data;
using BrawlBusters.Core.Security;

namespace BrawlBusters.Core.Commands;

public sealed class UnbanCommand : ChatCommand
{
    public override string Name => "unban";
    public override Permission Required => Permission.Ban;
    public override string Usage => "unban <nickname>";
    public override string Description => "lifts a ban";

    public override Task ExecuteAsync(CommandContext context, string[] arguments, CancellationToken cancellationToken)
    {
        Account? target = context.Target(Need(arguments, 0));
        if (target is null) return context.Reply("No such player.");

        context.Accounts.Update(target.Id, account => account.BannedUntilUtc = null);
        return context.Reply($"{target.Nickname} may log in again.");
    }
}
