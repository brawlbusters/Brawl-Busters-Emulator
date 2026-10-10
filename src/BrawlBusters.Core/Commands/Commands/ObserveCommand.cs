using BrawlBusters.Core.Security;
using BrawlBusters.Core.Sessions;

namespace BrawlBusters.Core.Commands;

public sealed class ObserveCommand : ChatCommand
{
    public override string Name => "observe";
    public override string[] Aliases => ["gmo", "gm_observe"];
    public override Permission Required => Permission.ObserveMatches;
    public override string Usage => "observe <room number>";
    public override string Description => "enters a room as an observer";

    public override Task ExecuteAsync(CommandContext context, string[] arguments, CancellationToken cancellationToken)
    {
        if (!ushort.TryParse(Need(arguments, 0), out ushort roomId)) throw new CommandUsageException();
        return context.Session is { } session
            ? GameFlow.GmObserveAsync(session, roomId, cancellationToken)
            : context.Reply("Only a logged-in player can observe a room.");
    }
}
