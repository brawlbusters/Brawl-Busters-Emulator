using BrawlBusters.Core.Data;

namespace BrawlBusters.Core.Commands;

public sealed class GoldCommand : CurrencyCommand
{
    public override string Name => "gold";
    protected override string Unit => "BP";

    protected override void Apply(Account account, long amount) => account.Gold = AddCapped(account.Gold, amount);
}
