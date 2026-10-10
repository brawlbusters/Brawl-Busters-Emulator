using BrawlBusters.Core.Logging;
using BrawlBusters.Core.Security;

namespace BrawlBusters.Core.Commands;

public sealed class LogCommand : ChatCommand
{
    public override string Name => "log";
    public override Permission Required => Permission.ManageServer;
    public override string Usage => "log [channel] [on|off]";
    public override string Description => "shows or switches the log channels (packets, bots, database, ...)";

    public override Task ExecuteAsync(CommandContext context, string[] arguments, CancellationToken cancellationToken)
    {
        if (arguments.Length == 0)
            return context.Reply(string.Join(", ", Log.Channels().Select(entry => $"{entry.Channel} {(entry.Enabled ? "on" : "off")}")));

        if (!Enum.TryParse(arguments[0], ignoreCase: true, out LogChannel channel)) throw new CommandUsageException();
        bool enabled = arguments.Length > 1 ? arguments[1].Equals("on", StringComparison.OrdinalIgnoreCase) : !Log.IsEnabled(channel);
        Log.SetEnabled(channel, enabled);
        return context.Reply($"Log channel {channel} is {(enabled ? "on" : "off")}.");
    }
}
