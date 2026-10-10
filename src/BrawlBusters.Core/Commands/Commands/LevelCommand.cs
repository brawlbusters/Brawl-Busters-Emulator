using BrawlBusters.Core.Data;
using BrawlBusters.Core.Security;

namespace BrawlBusters.Core.Commands;

public sealed class LevelCommand : ChatCommand
{
    public override string Name => "level";
    public override Permission Required => Permission.GiveCurrency;
    public override string Usage => "level <1-99> [nickname]";
    public override string Description => "sets a player's level";

    public override async Task ExecuteAsync(CommandContext context, string[] arguments, CancellationToken cancellationToken)
    {
        long level = NeedNumber(arguments, 0);
        Account? target = context.Target(Optional(arguments, 1));
        if (target is null || level is < 1 or > 99)
        {
            await context.Reply("No such player, or the level is out of range.");
            return;
        }

        context.Accounts.Update(target.Id, account =>
        {
            account.Level = (byte)level;
            account.Experience = GameData.Instance.ExpForLevel((byte)level);
        });
        await RefreshClientAsync(target.Id, cancellationToken);
        await context.Reply($"{target.Nickname} is now level {level}.");
    }
}
