using System.Net;
using BrawlBusters.CastServer;
using BrawlBusters.Core.Network;
using BrawlBusters.Core.Sessions;

return await ServerHost.RunAsync("CastServer", async (settings, cancellationToken) =>
{
    var server = new GameServer(
        "CastServer",
        new IPEndPoint(IPAddress.Any, settings.CastPort),
        (connection, token) => new MatchSession(connection, settings).RunAsync(token));

    await server.RunAsync(cancellationToken);
});
