using BrawlBusters.Core.Configuration;
using BrawlBusters.Core.Data;
using BrawlBusters.Core.Network;
using BrawlBusters.Core.Sessions;

namespace BrawlBusters.MainServer;

public sealed class MainSession : ClientSession
{
    public MainSession(GameConnection connection, EmulatorSettings settings, AccountRepository accounts, MessageRouter router)
        : base(connection, settings, accounts, router)
    {
    }

    protected override bool AcceptsLogin => false;
    protected override bool AcceptsTransfer => true;

    protected override Task OnSessionStartedAsync(CancellationToken cancellationToken)
        => GameFlow.StartAsync(this, cancellationToken);
}
