using BrawlBusters.Core.Data;
using BrawlBusters.Core.Security;

namespace BrawlBusters.Core.Commands;

/// <summary>Base of the commands that add to one of a player's numbers (/gold, /cash, /exp).</summary>
public abstract class CurrencyCommand : ChatCommand
{
    /// <summary>What the amount is called in the answer ("BP", "RT", "exp").</summary>
    protected abstract string Unit { get; }

    protected abstract void Apply(Account account, long amount);

    public override Permission Required => Permission.GiveCurrency;
    public override string Usage => $"{Name} <amount> [nickname]";
    public override string Description => $"adds {Unit} (a negative amount takes it away)";

    public override async Task ExecuteAsync(CommandContext context, string[] arguments, CancellationToken cancellationToken)
    {
        long amount = NeedNumber(arguments, 0);
        Account? target = context.Target(Optional(arguments, 1));
        if (target is null)
        {
            await context.Reply("No such player.");
            return;
        }

        context.Accounts.Update(target.Id, account => Apply(account, amount));
        await RefreshClientAsync(target.Id, cancellationToken);
        await context.Reply($"{target.Nickname}: {amount:+#;-#;0} {Unit}.");
    }
}
