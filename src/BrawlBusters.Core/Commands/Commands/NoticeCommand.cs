using BrawlBusters.Core.Protocol.Packets;
using BrawlBusters.Core.Security;
using BrawlBusters.Core.Sessions;

namespace BrawlBusters.Core.Commands;

public sealed class NoticeCommand : ChatCommand
{
    public override string Name => "notice";
    public override string[] Aliases => ["gm"];
    public override Permission Required => Permission.Notice;
    public override string Usage => "notice <text>";
    public override string Description => "shows a system message to every player";

    public override async Task ExecuteAsync(CommandContext context, string[] arguments, CancellationToken cancellationToken)
    {
        if (arguments.Length == 0) throw new CommandUsageException();
        string text = string.Join(' ', arguments);
        int delivered = await SessionRegistry.BroadcastAsync(() => UserMsgPacket.SystemMessage(text), cancellationToken);
        await context.Reply($"Notice sent to {delivered} player(s).");
    }
}
