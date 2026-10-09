using System.Security.Cryptography;

namespace BrawlBusters.Core.Sessions;

/// <summary>
/// Players who were told to change server (sTransServer) and have not arrived on the other port yet.
/// The entry is taken when the new connection presents the account's session key.
/// </summary>
public static class ServerChanges
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(30);
    private static readonly Dictionary<uint, (ushort ChannelId, uint Key, DateTime Expires)> Pending = [];

    public static uint Begin(uint accountId, ushort channelId)
    {
        uint key = BitConverter.ToUInt32(RandomNumberGenerator.GetBytes(4));
        lock (Pending) Pending[accountId] = (channelId, key, DateTime.UtcNow + Lifetime);
        return key;
    }

    public static (ushort ChannelId, uint Key)? Take(uint accountId)
    {
        lock (Pending)
        {
            if (!Pending.Remove(accountId, out var entry) || entry.Expires < DateTime.UtcNow) return null;
            return (entry.ChannelId, entry.Key);
        }
    }
}
