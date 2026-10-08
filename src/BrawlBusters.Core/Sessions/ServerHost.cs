using BrawlBusters.Core.Configuration;
using BrawlBusters.Core.Logging;

namespace BrawlBusters.Core.Sessions;

public static class ServerHost
{
    private static async Task ReadConsoleCommandsAsync(string serverName, CancellationToken cancellationToken)
    {
        const string noticeCommand = "notice ";

        while (!cancellationToken.IsCancellationRequested)
        {
            string? line;
            try
            {
                line = await Console.In.ReadLineAsync(cancellationToken);
            }
            catch (Exception exception) when (exception is OperationCanceledException or IOException)
            {
                return;
            }

            if (line is null) return;
            line = line.Trim();
            if (line.Length == 0) continue;

            if (line.StartsWith(noticeCommand, StringComparison.OrdinalIgnoreCase))
            {
                string text = line[noticeCommand.Length..].Trim();
                ServerBus.PostNotice(text);
            }
            else
            {
                Log.Info(serverName, "Commands: notice <text>");
            }
        }
    }

    public static async Task<int> RunAsync(string serverName, Func<EmulatorSettings, CancellationToken, Task> run)
    {
        Console.Title = $"Brawl Busters - {serverName}";
        Log.ToFile(EmulatorSettings.LogDirectory, serverName);

        using var shutdown = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            shutdown.Cancel();
        };

        try
        {
            EmulatorSettings settings = EmulatorSettings.Load();
            if (Enum.TryParse(settings.ConsoleLogLevel, ignoreCase: true, out LogLevel consoleLevel))
                Log.ConsoleMinimumLevel = consoleLevel;
            Log.Info(serverName, $"Root: {EmulatorSettings.RootDirectory}");
            _ = Task.Run(() => ReadConsoleCommandsAsync(serverName, shutdown.Token));
            await run(settings, shutdown.Token);
            return 0;
        }
        catch (Exception exception)
        {
            Log.Error(serverName, exception);
            return 1;
        }
    }
}
