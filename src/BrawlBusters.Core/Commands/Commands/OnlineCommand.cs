using BrawlBusters.Core.Configuration;
using BrawlBusters.Core.Data;
using BrawlBusters.Core.Security;
using BrawlBusters.Core.Sessions;

namespace BrawlBusters.Core.Commands;

public sealed class OnlineCommand : ChatCommand
{
    public override string Name => "online";
    public override string[] Aliases => ["who"];
    public override Permission Required => Permission.ListPlayers;
    public override string Usage => "online";
    public override string Description => "shows who is connected and where";

    public override async Task ExecuteAsync(CommandContext context, string[] arguments, CancellationToken cancellationToken)
    {
        List<ClientSession> sessions = SessionRegistry.All();
        await context.Reply($"{sessions.Count} real player(s) connected");
        foreach (ChannelSettings channel in ChannelDirectory.Channels)
        {
            int real = SessionRegistry.InChannel(channel.Id);
            int simulated = BotDirector.InLobby(channel.Id);
            string name = GameData.Instance.Channels.TryGetValue(channel.Id, out ChannelData? data) ? data.Text : channel.Name;
            await context.Reply($"Channel {channel.Id} ({name}): {real} real player(s) in the lobby"
                + (simulated > 0 ? $", {simulated} simulated (bot fill, not connections)" : ""));
        }
        foreach (ClientSession session in sessions.OrderBy(session => session.Account.Nickname))
        {
            string place = session.Room is { } room
                ? $"room {room.Id} ({room.Title}){(session.InMatch ? ", in a match" : "")}"
                : session.ChannelId != 0 ? $"lobby of channel {session.ChannelId}" : "menus";
            await context.Reply($"{session.Account.Nickname} [{session.Account.Grade}, level {session.Account.DisplayLevel}] - {place}");
        }
    }
}
