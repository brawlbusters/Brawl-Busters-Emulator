using BrawlBusters.Core.Logging;
using BrawlBusters.Core.Network;

namespace BrawlBusters.Core.Sessions;

public static class SessionRegistry
{
    private static readonly object Gate = new();
    private static readonly HashSet<ClientSession> Sessions = [];

    public static void Add(ClientSession session)
    {
        lock (Gate) Sessions.Add(session);
    }

    public static void Remove(ClientSession session)
    {
        lock (Gate) Sessions.Remove(session);
    }

    public static ClientSession? Find(uint userId)
    {
        lock (Gate) return Sessions.FirstOrDefault(session => session.Account.Id == userId);
    }

    public static int Count
    {
        get
        {
            lock (Gate) return Sessions.Count;
        }
    }

    public static async Task<int> BroadcastAsync(Func<PacketWriter> build, CancellationToken cancellationToken = default)
    {
        ClientSession[] targets;
        lock (Gate) targets = [.. Sessions];

        int delivered = 0;
        foreach (ClientSession session in targets)
        {
            try
            {
                await session.SendAsync(build(), cancellationToken);
                delivered++;
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException)
            {
                Log.Debug(session.Tag, $"Broadcast skipped: {exception.Message}");
            }
        }
        return delivered;
    }
}
