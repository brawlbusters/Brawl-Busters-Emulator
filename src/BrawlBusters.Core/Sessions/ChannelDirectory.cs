using BrawlBusters.Core.Configuration;
using BrawlBusters.Core.Data;
using BrawlBusters.Core.Protocol.Packets;
using BrawlBusters.Core.Security;

namespace BrawlBusters.Core.Sessions;

/// <summary>
/// The channels this server offers. Ids, name keys, type and level range come from the client's own data
/// (<c>serverconfigdb</c>); which of them are open is decided by the config file or the <c>channels</c> table.
/// </summary>
public static class ChannelDirectory
{
    private static IReadOnlyList<ChannelSettings> _channels = [];

    public static IReadOnlyList<ChannelSettings> Channels => _channels;

    public static void Configure(IReadOnlyList<ChannelSettings> channels) => _channels = channels;

    public static bool Exists(ushort channelId) => _channels.Any(channel => channel.Id == channelId);

    public static bool MayEnter(Account account, ushort channelId)
        => _channels.FirstOrDefault(channel => channel.Id == channelId) is not { StaffOnly: true } || account.Can(Permission.SeeAllChannels);

    /// <summary>
    /// The client's own refusal when the player's level is outside the level range of the channel (its channel table);
    /// staff may enter every channel.
    /// </summary>
    public static Protocol.NetError? LevelRefusal(Account account, ushort channelId)
    {
        if (account.Can(Permission.SeeAllChannels) || !GameData.Instance.Channels.TryGetValue(channelId, out ChannelData? data)) return null;
        byte level = account.DisplayLevel;
        if (level < data.LevelMin) return Protocol.NetError.Lobby_ChannelTooLowLevel;
        if (level > data.LevelMax) return Protocol.NetError.Lobby_ChannelTooHighLevel;
        return null;
    }

    /// <summary>The lobby port a channel is bound to, or null when it is served on every port.</summary>
    public static int? PortOf(ushort channelId)
        => _channels.FirstOrDefault(channel => channel.Id == channelId) is { LobbyPort: > 0 } bound ? bound.LobbyPort : null;

    /// <summary>The channel list as <paramref name="viewer"/> may see it: staff-only channels are left out for players.</summary>
    public static IReadOnlyList<ChannelInfo> Build(EmulatorSettings settings, Account viewer)
    {
        bool staff = viewer.Can(Permission.SeeAllChannels);
        return _channels
            .Where(channel => staff || !channel.StaffOnly)
            .Select(channel =>
            {
                ChannelData data = GameData.Instance.Channels.GetValueOrDefault(channel.Id) ?? new ChannelData { Id = channel.Id, Name = channel.Name };
                return new ChannelInfo
                {
                    Server = channel.LobbyPort > 0
                        ? new System.Net.IPEndPoint(System.Net.IPAddress.Parse(settings.PublicAddress), channel.LobbyPort)
                        : settings.RelayEndPoint,
                    Id = channel.Id,
                    Name = channel.Name.Length > 0 ? channel.Name : data.Name,
                    Type = data.Type,
                    LevelMin = data.LevelMin,
                    LevelMax = data.LevelMax,
                    Country = data.Area,
                    Status = BotDirector.StatusOf(channel.Id, SessionRegistry.InChannel(channel.Id)),
                    Secondary = settings.RelayEndPoint,
                };
            })
            .ToList();
    }
}
