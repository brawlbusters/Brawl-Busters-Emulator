using BrawlBusters.Core.Commands;
using BrawlBusters.Core.Data;
using BrawlBusters.Core.Logging;
using BrawlBusters.Core.Sessions;

namespace BrawlBusters.Server;

/// <summary>Reads commands typed into the server window. The console has every permission; a leading slash is optional.</summary>
public static class ServerConsole
{
    private const string Scope = "Console";

    public static async Task RunAsync(AccountRepository accounts, CancellationTokenSource shutdown)
    {
        CancellationToken cancellationToken = shutdown.Token;
        await Task.Yield();

        while (!cancellationToken.IsCancellationRequested)
        {
            string? line;
            try
            {
                line = await Console.In.ReadLineAsync(cancellationToken);
            }
            catch (Exception exception) when (exception is OperationCanceledException or IOException)
            {
                break;
            }

            if (line is null) break;
            line = line.Trim();
            if (line.Length == 0) continue;

            if (line.TrimStart(CommandRegistry.Prefix) is "stop" or "quit" or "exit")
            {
                Log.Info(Scope, "Stopping.");
                shutdown.Cancel();
                break;
            }

            string text = line[0] == CommandRegistry.Prefix ? line : CommandRegistry.Prefix + line;
            var context = new CommandContext
            {
                Accounts = accounts,
                Tag = Scope,
                Reply = answer =>
                {
                    Log.Info(Scope, answer);
                    return Task.CompletedTask;
                },
            };

            bool known = false;
            await World.RunAsync(async () => known = await CommandRegistry.Instance.TryExecuteAsync(context, text, cancellationToken));
            if (!known) Log.Info(Scope, "Unknown command - type help. 'stop' shuts the server down.");
        }

        try
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
    }
}
