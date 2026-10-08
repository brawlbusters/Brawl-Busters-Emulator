using BrawlBusters.Core.Configuration;
using BrawlBusters.Core.Protocol.Packets;

namespace BrawlBusters.Core.Sessions;

public static class ChannelDirectory
{
    public static IReadOnlyList<ChannelInfo> Build(EmulatorSettings settings)
    {
        return settings.Channels
            .Select(channel => new ChannelInfo
            {
                Server = settings.MainEndPoint,
                Id = channel.Id,
                Name = channel.Name,
                Secondary = settings.MainEndPoint,
            })
            .ToList();
    }

    public static bool Exists(EmulatorSettings settings, ushort channelId)
        => settings.Channels.Exists(channel => channel.Id == channelId);
}
