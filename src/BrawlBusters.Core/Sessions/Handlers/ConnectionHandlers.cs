using BrawlBusters.Core.Logging;
using BrawlBusters.Core.Network;
using BrawlBusters.Core.Protocol;
using BrawlBusters.Core.Protocol.Packets;

namespace BrawlBusters.Core.Sessions.Handlers;

public sealed class KeepAliveHandler : IMessageHandler
{
    public MsgCategory Category => MsgCategory.Start;

    public Task HandleAsync(ClientSession session, PacketReader reader, CancellationToken cancellationToken)
    {
        bool isRequest = !reader.EndOfData && reader.ReadBool();
        return isRequest ? session.SendAsync(KeepAlivePacket.Idle(), cancellationToken) : Task.CompletedTask;
    }
}

public sealed class UdpHandler : IMessageHandler
{
    public MsgCategory Category => MsgCategory.cUDP;

    public Task HandleAsync(ClientSession session, PacketReader reader, CancellationToken cancellationToken)
    {
        byte[] body = reader.ReadToEnd();
        string outcome = body is [0, 0, ..] ? "server hole punch FAILED"
            : body is [0, 1, ..] ? "server hole punch succeeded"
            : body is [1, 0, ..] ? "relay hole punch FAILED"
            : body is [1, 1, ..] ? "relay hole punch succeeded"
            : body is [3, 0, ..] ? "connection to another player through the relay FAILED"
            : body is [3, 1, ..] ? "connection to another player through the relay succeeded"
            : "not decoded";
        if (body is [2, ..])
            Log.Debug(session.Tag, $"cUDP {Log.Hex(body)} (hole punch to another player {(body.Length > 1 && body[1] == 1 ? "succeeded" : "failed")})");
        else
            Log.Info(session.Tag, $"cUDP {Log.Hex(body)} ({outcome})");
        return Task.CompletedTask;
    }
}

public sealed class SilentHandler : IMessageHandler
{
    public SilentHandler(MsgCategory category) => Category = category;

    public MsgCategory Category { get; }

    public Task HandleAsync(ClientSession session, PacketReader reader, CancellationToken cancellationToken)
        => Task.CompletedTask;
}

public sealed class NotImplementedHandler : IMessageHandler
{
    public NotImplementedHandler(MsgCategory category) => Category = category;

    public MsgCategory Category { get; }

    public Task HandleAsync(ClientSession session, PacketReader reader, CancellationToken cancellationToken)
    {
        Log.Warn(session.Tag, $"{Category} (not implemented): {Log.Hex(reader.ReadToEnd())}");
        return Task.CompletedTask;
    }
}
