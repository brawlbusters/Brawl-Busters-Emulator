using System.Net;
using BrawlBusters.AuthServer;
using BrawlBusters.Core.Configuration;
using BrawlBusters.Core.Data;
using BrawlBusters.Core.Network;
using BrawlBusters.Core.Sessions;
using BrawlBusters.Core.Sessions.Handlers;

return await ServerHost.RunAsync("AuthServer", async (settings, cancellationToken) =>
{
    var accounts = new AccountRepository(EmulatorSettings.DataDirectory);
    MessageRouter router = StandardRouter.Create();

    IEnumerable<Task> listeners = settings.AuthPorts.Select(port => new GameServer(
        "AuthServer",
        new IPEndPoint(IPAddress.Any, port),
        (connection, token) => new AuthSession(connection, settings, accounts, router).RunAsync(token)).RunAsync(cancellationToken));

    IEnumerable<Task> punchers = settings.AuthPorts.Select(port =>
        new HolePunchServer("AuthServer", new IPEndPoint(IPAddress.Any, port)).RunAsync(cancellationToken));

    Task bus = ServerBus.ListenAsync("AuthServer", cancellationToken);

    Task bots = BotDirector.RunAsync("AuthServer", settings, cancellationToken);

    await Task.WhenAll(listeners.Concat(punchers).Append(bus).Append(bots));
});
