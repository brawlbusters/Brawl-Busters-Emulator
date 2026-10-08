using BrawlBusters.Core.Configuration;
using BrawlBusters.Core.Data;
using BrawlBusters.Core.Network;
using BrawlBusters.Core.Sessions;

namespace BrawlBusters.AuthServer;

public sealed class AuthSession : ClientSession
{
    public AuthSession(GameConnection connection, EmulatorSettings settings, AccountRepository accounts, MessageRouter router)
        : base(connection, settings, accounts, router)
    {
    }

    protected override bool AcceptsLogin => true;
    protected override bool AcceptsTransfer => false;

    protected override Task OnSessionStartedAsync(CancellationToken cancellationToken)
        => GameFlow.StartAsync(this, cancellationToken);
}
