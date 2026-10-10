using BrawlBusters.Core.Data;
using BrawlBusters.Core.Security;
using BrawlBusters.Core.Sessions;

namespace BrawlBusters.Core.Commands;

public sealed class GradeCommand : ChatCommand
{
    public override string Name => "grade";
    public override Permission Required => Permission.SetGrade;
    public override string Usage => "grade <nickname> <player|mod|gm|dev>";
    public override string Description => "changes an account's grade; the player is told and gets the rights at once";

    public override Task ExecuteAsync(CommandContext context, string[] arguments, CancellationToken cancellationToken)
    {
        Account? target = context.Target(Need(arguments, 0));
        if (target is null || !Permissions.TryParseGrade(Need(arguments, 1), out AccountGrade grade)) throw new CommandUsageException();

        context.Accounts.Update(target.Id, account => account.Grade = grade);
        return context.Reply($"{target.Nickname} is now {AuthorityNotifier.NameOf(grade)}.");
    }
}
