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

    /// <summary>Registers a session and closes any older connection of the same account, so one account has one session.</summary>
    public static void Replace(ClientSession session)
    {
        List<ClientSession> older;
        lock (Gate)
        {
            older = Sessions.Where(other => other != session && other.Account.Id == session.Account.Id).ToList();
            Sessions.Add(session);
        }

        foreach (ClientSession other in older)
        {
            if (session.ServerChangeChannel is not null)
            {
                Log.Info(LogChannel.Session, other.Tag, "The player changed server - the connection to the old one is closed");
                other.Connection.Close();
                continue;
            }

            Log.Warn(LogChannel.Session, other.Tag, "The account logged in again from another connection - this one is closed");
            _ = other.SendAsync(Protocol.Packets.ErrorPacket.Show(Protocol.NetError.ID_AnotherLogin));
            World.Later(TimeSpan.FromSeconds(1), () =>
            {
                other.Connection.Close();
                return Task.CompletedTask;
            });
        }
    }

    public static List<ClientSession> All()
    {
        lock (Gate) return [.. Sessions];
    }

    public static void Remove(ClientSession session)
    {
        lock (Gate) Sessions.Remove(session);
    }

    public static ClientSession? Find(uint userId)
    {
        lock (Gate) return Sessions.FirstOrDefault(session => session.Account.Id == userId);
    }

    public static int InChannel(ushort channelId)
    {
        lock (Gate) return Sessions.Count(session => session.ChannelId == channelId && session.Room is null);
    }

    public static List<ClientSession> InLobbies()
    {
        lock (Gate) return Sessions.Where(session => session.ChannelId != 0 && session.Room is null && !session.InMatch).ToList();
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
