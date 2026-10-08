using System.Net;
using BrawlBusters.Core.Configuration;
using BrawlBusters.Core.Data;
using BrawlBusters.Core.Network;
using BrawlBusters.Core.Sessions;
using BrawlBusters.Core.Sessions.Handlers;
using BrawlBusters.MainServer;

return await ServerHost.RunAsync("MainServer", async (settings, cancellationToken) =>
{
    var accounts = new AccountRepository(EmulatorSettings.DataDirectory);
    MessageRouter router = StandardRouter.Create();

    var lobby = new GameServer(
        "MainServer",
        new IPEndPoint(IPAddress.Any, settings.MainPort),
        (connection, token) => new MainSession(connection, settings, accounts, router).RunAsync(token));

    var chatHub = new ChatHub(accounts);
    var chat = new GameServer(
        "ChatServer",
        new IPEndPoint(IPAddress.Any, settings.ChatPort),
        (connection, token) => new ChatSession(connection, settings, chatHub).RunAsync(token));

    var puncher = new HolePunchServer("MainServer", new IPEndPoint(IPAddress.Any, settings.MainPort));

    await Task.WhenAll(
        lobby.RunAsync(cancellationToken),
        chat.RunAsync(cancellationToken),
        puncher.RunAsync(cancellationToken),
        ServerBus.ListenAsync("MainServer", cancellationToken));
});
