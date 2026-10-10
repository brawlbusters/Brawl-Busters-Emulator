using BrawlBusters.Core.Data;

namespace BrawlBusters.Core.Commands;

public sealed class ExpCommand : CurrencyCommand
{
    public override string Name => "exp";
    protected override string Unit => "exp";

    protected override void Apply(Account account, long amount)
    {
        account.Experience = AddCapped(account.Experience, amount);
        account.Level = GameData.Instance.LevelForExp(account.Experience, account.Level);
    }
}
