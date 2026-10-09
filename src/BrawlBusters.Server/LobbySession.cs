using BrawlBusters.Core.Configuration;
using BrawlBusters.Core.Data;
using BrawlBusters.Core.Network;
using BrawlBusters.Core.Sessions;

namespace BrawlBusters.Server;

/// <summary>A game client on one of the lobby ports. It may log in, or arrive with the session key of an earlier login.</summary>
public sealed class LobbySession : ClientSession
{
    public LobbySession(GameConnection connection, EmulatorSettings settings, AccountRepository accounts, MessageRouter router)
        : base(connection, settings, accounts, router)
    {
    }

    protected override bool AcceptsLogin => true;

    protected override bool AcceptsTransfer => true;

    protected override Task OnSessionStartedAsync(CancellationToken cancellationToken)
        => GameFlow.StartAsync(this, cancellationToken);
}
