using BrawlBusters.Core.Data;
using BrawlBusters.Core.Protocol.Packets;
using BrawlBusters.Core.Security;
using BrawlBusters.Core.Sessions;

namespace BrawlBusters.Core.Commands;

public sealed class ItemCommand : ChatCommand
{
    public override string Name => "item";
    public override Permission Required => Permission.GiveItems;
    public override string Usage => "item <item id> [quantity] [nickname]";
    public override string Description => "puts an item into a player's inventory";

    public override async Task ExecuteAsync(CommandContext context, string[] arguments, CancellationToken cancellationToken)
    {
        long itemId = NeedNumber(arguments, 0);
        long quantity = arguments.Length > 1 ? NeedNumber(arguments, 1) : 1;
        Account? target = context.Target(Optional(arguments, 2));
        GameData data = GameData.Instance;
        if (target is null || itemId <= 0 || quantity is < 1 or > ushort.MaxValue || !data.Items.ContainsKey((uint)itemId))
        {
            await context.Reply("No such player or item.");
            return;
        }

        InventoryItem? granted = null;
        context.Accounts.Update(target.Id, account =>
        {
            granted = new InventoryItem
            {
                Slot = account.FreeSlot(),
                ItemId = (uint)itemId,
                Type = data.TypeOf((uint)itemId),
                Quantity = (ushort)quantity,
            };
            account.Items.Add(granted);
        });

        if (granted is not null && SessionRegistry.Find(target.Id) is { } session)
        {
            session.RefreshAccount();
            await session.SendAsync(InventoryPacket.Added([granted]), cancellationToken);
        }
        await context.Reply($"{target.Nickname} received item {itemId} x{quantity}.");
    }
}
