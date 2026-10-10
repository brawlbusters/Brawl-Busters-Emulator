using BrawlBusters.Core.Data;

namespace BrawlBusters.Core.Commands;

public sealed class CashCommand : CurrencyCommand
{
    public override string Name => "cash";
    protected override string Unit => "RT";

    protected override void Apply(Account account, long amount) => account.Cash = AddCapped(account.Cash, amount);
}
