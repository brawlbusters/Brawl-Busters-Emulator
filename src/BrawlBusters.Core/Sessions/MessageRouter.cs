using BrawlBusters.Core.Logging;
using BrawlBusters.Core.Network;
using BrawlBusters.Core.Protocol;

namespace BrawlBusters.Core.Sessions;

public sealed class MessageRouter
{
    private readonly Dictionary<MsgCategory, IMessageHandler> _handlers = [];

    public MessageRouter Add(IMessageHandler handler)
    {
        _handlers[handler.Category] = handler;
        return this;
    }

    public async Task DispatchAsync(ClientSession session, byte[] message, CancellationToken cancellationToken)
    {
        if (message.Length == 0) return;

        var category = (MsgCategory)message[0];
        string name = Enum.IsDefined(category) ? category.ToString() : $"0x{message[0]:X2}";

        if (session.Settings.LogPackets)
            Log.Debug(session.Tag, $"RECV {name,-16} {Log.Hex(message.AsSpan(1))}");

        if (!_handlers.TryGetValue(category, out IMessageHandler? handler))
        {
            Log.Warn(session.Tag, $"No handler for {name} - body {Log.Hex(message.AsSpan(1))}");
            return;
        }

        try
        {
            await handler.HandleAsync(session, new PacketReader(message, offset: 1), cancellationToken);
        }
        catch (EndOfStreamException exception)
        {
            Log.Warn(session.Tag, $"Malformed {name}: {exception.Message}");
        }
    }
}
