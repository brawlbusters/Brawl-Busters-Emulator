using BrawlBusters.Core.Configuration;
using BrawlBusters.Core.Logging;
using BrawlBusters.Core.Network;

namespace BrawlBusters.CastServer;

public sealed class MatchSession
{
    private readonly GameConnection _connection;
    private readonly EmulatorSettings _settings;

    public MatchSession(GameConnection connection, EmulatorSettings settings)
    {
        _connection = connection;
        _settings = settings;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (await _connection.ReceiveRawAsync(cancellationToken) is { } data)
        {
            if (_settings.LogPackets) Log.Debug(_connection.Tag, $"HOST RECV (not implemented) {Log.Hex(data)}");
        }
    }
}
