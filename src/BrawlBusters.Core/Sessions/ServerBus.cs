using System.Text;
using BrawlBusters.Core.Configuration;
using BrawlBusters.Core.Logging;
using BrawlBusters.Core.Protocol.Packets;

namespace BrawlBusters.Core.Sessions;

public static class ServerBus
{
    private const string Notice = "notice";
    private const string Refresh = "refresh";
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);
    private static readonly object WriteGate = new();

    private static string FilePath => Path.Combine(EmulatorSettings.DataDirectory, "server-bus.log");

    public static void PostNotice(string text) => Append(Notice, text.Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' '));

    public static void PostRefresh(uint userId) => Append(Refresh, userId.ToString());

    public static async Task ListenAsync(string serverName, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(EmulatorSettings.DataDirectory);
        long position = File.Exists(FilePath) ? new FileInfo(FilePath).Length : 0;

        try
        {
            using var timer = new PeriodicTimer(PollInterval);
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                foreach (string line in ReadNewLines(ref position))
                    await HandleAsync(serverName, line, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static void Append(string command, string argument)
    {
        Directory.CreateDirectory(EmulatorSettings.DataDirectory);
        lock (WriteGate)
        {
            using var stream = new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            stream.Write(Encoding.UTF8.GetBytes($"{command}\t{argument}\n"));
        }
    }

    private static List<string> ReadNewLines(ref long position)
    {
        var lines = new List<string>();
        if (!File.Exists(FilePath)) return lines;

        using var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (stream.Length < position) position = 0;
        if (stream.Length == position) return lines;

        stream.Seek(position, SeekOrigin.Begin);
        var buffer = new byte[stream.Length - position];
        int read = stream.Read(buffer, 0, buffer.Length);

        int end = Array.LastIndexOf(buffer, (byte)'\n', read - 1);
        if (end < 0) return lines;

        position += end + 1;
        lines.AddRange(Encoding.UTF8.GetString(buffer, 0, end).Split('\n', StringSplitOptions.RemoveEmptyEntries));
        return lines;
    }

    private static async Task HandleAsync(string serverName, string line, CancellationToken cancellationToken)
    {
        string[] parts = line.Split('\t', 2);
        if (parts.Length != 2) return;

        switch (parts[0])
        {
            case Notice:
            {
                int delivered = await SessionRegistry.BroadcastAsync(() => UserMsgPacket.SystemMessage(parts[1]), cancellationToken);
                Log.Info(serverName, $"System message to {delivered} player(s): {parts[1]}");
                break;
            }
            case Refresh when uint.TryParse(parts[1], out uint userId):
            {
                ClientSession? session = SessionRegistry.Find(userId);
                if (session is null) break;

                session.RefreshAccount();
                await session.SendAsync(
                    UserInfoPacket.ExpAndGold((uint)session.Account.Experience, (uint)session.Account.Gold), cancellationToken);
                break;
            }
        }
    }
}
