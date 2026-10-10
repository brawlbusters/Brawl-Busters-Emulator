using BrawlBusters.Core.Logging;
using BrawlBusters.Core.Protocol.Packets;

namespace BrawlBusters.Core.Sessions;

/// <summary>
/// Keeps the room list of every player in a lobby up to date without a refresh: a new room is announced
/// (sRoomList 01), a closed one removed (02) and a changed one updated field by field (03). The player count of the
/// lobby follows the same way (sLobby 01), and so do the population bars of the channel list (sServer 01).
/// Each session remembers the list it was last told about, so it only ever hears what is new to it.
/// </summary>
public static class LobbyFeed
{
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(1);

    public static async Task RunAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(Tick);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                try
                {
                    await World.RunAsync(() => PushAsync(cancellationToken));
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    // One bad tick must never end the loop - or, with it, the server.
                    Log.Error("Lobby", $"Lobby update failed: {exception}");
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>The list the session has just been sent in full.</summary>
    public static void Remember(ClientSession session, IReadOnlyList<Room> rooms)
        => session.LobbyRooms = rooms.ToDictionary(room => room.Id, RoomListEntry.Of);

    private static async Task PushAsync(CancellationToken cancellationToken)
    {
        foreach (ClientSession session in SessionRegistry.InLobbies())
        {
            if (session.LobbyRooms is not { } known) continue;

            Dictionary<ushort, RoomListEntry> now = RoomRegistry.Instance.InChannel(session.ChannelId).ToDictionary(room => room.Id, RoomListEntry.Of);
            try
            {
                // The population bars of the channel list: sent again whenever one of them moves.
                IReadOnlyList<ChannelInfo> channels = ChannelDirectory.Build(session.Settings, session.Account);
                string statuses = string.Join(",", channels.Select(channel => $"{channel.Id}:{(byte)channel.Status}"));
                if (statuses != session.ChannelStatuses)
                {
                    bool first = session.ChannelStatuses.Length == 0;
                    session.ChannelStatuses = statuses;
                    if (!first) await session.SendAsync(ServerPacket.ChannelStates(channels), cancellationToken);
                }

                ushort players = GameFlow.LobbyPlayerCount(session.ChannelId);
                if (players != session.LobbyPlayers)
                {
                    session.LobbyPlayers = players;
                    await session.SendAsync(LobbyPacket.PlayerCountChanged(players), cancellationToken);
                }

                foreach (ushort gone in known.Keys.Where(id => !now.ContainsKey(id)).ToList())
                    await session.SendAsync(LobbyPacket.RoomRemoved(gone), cancellationToken);

                foreach ((ushort id, RoomListEntry entry) in now)
                {
                    if (!known.TryGetValue(id, out RoomListEntry? before))
                        await session.SendAsync(LobbyPacket.RoomAdded(id, entry), cancellationToken);
                    else if (before != entry)
                        await session.SendAsync(LobbyPacket.RoomChanged(id, before, entry), cancellationToken);
                }
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException or InvalidOperationException)
            {
                Log.Debug(LogChannel.Lobby, session.Tag, $"Room list update not delivered: {exception.Message}");
            }

            session.LobbyRooms = now;
        }
    }
}
