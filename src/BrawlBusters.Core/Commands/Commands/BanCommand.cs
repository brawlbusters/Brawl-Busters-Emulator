using BrawlBusters.Core.Data;
using BrawlBusters.Core.Security;

namespace BrawlBusters.Core.Commands;

public sealed class BanCommand : ChatCommand
{
    public override string Name => "ban";
    public override Permission Required => Permission.Ban;
    public override string Usage => "ban <nickname> <minutes>";
    public override string Description => "locks an account for a while and disconnects it";

    public override Task ExecuteAsync(CommandContext context, string[] arguments, CancellationToken cancellationToken)
    {
        Account? target = context.Target(Need(arguments, 0));
        long minutes = NeedNumber(arguments, 1);
        if (target is null || minutes <= 0) return context.Reply("No such player.");
        if (context.Caller is { } caller && target.Grade >= caller.Grade) return context.Reply("You cannot ban that grade.");

        DateTime until = DateTime.UtcNow.AddMinutes(minutes);
        context.Accounts.Update(target.Id, account => account.BannedUntilUtc = until);
        return context.Reply($"{target.Nickname} is banned until {until:yyyy-MM-dd HH:mm} UTC.");
    }
}
