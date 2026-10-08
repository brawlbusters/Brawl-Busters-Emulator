using System.Net;
using System.Net.Sockets;
using BrawlBusters.Core.Logging;

namespace BrawlBusters.Core.Network;

public sealed class GameServer
{
    private readonly string _name;
    private readonly IPEndPoint _endPoint;
    private readonly Func<GameConnection, CancellationToken, Task> _handleClient;

    public GameServer(string name, IPEndPoint endPoint, Func<GameConnection, CancellationToken, Task> handleClient)
    {
        _name = name;
        _endPoint = endPoint;
        _handleClient = handleClient;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var listener = new TcpListener(_endPoint);
        listener.Start();
        Log.Info(_name, $"Listening on {_endPoint}");

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TcpClient client = await listener.AcceptTcpClientAsync(cancellationToken);
                _ = ServeAsync(client, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            listener.Stop();
            Log.Info(_name, "Stopped.");
        }
    }

    private async Task ServeAsync(TcpClient client, CancellationToken cancellationToken)
    {
        await using var connection = new GameConnection(client);
        Log.Info(_name, $"Connected    {connection.Tag}");
        try
        {
            await _handleClient(connection, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or SocketException or OperationCanceledException)
        {
            Log.Debug(_name, $"Connection {connection.Tag} dropped: {exception.Message}");
        }
        catch (Exception exception)
        {
            Log.Error(_name, exception);
        }
        Log.Info(_name, $"Disconnected {connection.Tag}");
    }
}
