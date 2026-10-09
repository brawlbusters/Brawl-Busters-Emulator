using BrawlBusters.Core.Logging;
using BrawlBusters.Core.Network;
using BrawlBusters.Core.Protocol;

namespace BrawlBusters.Core.Sessions.Handlers;

public sealed class CommunityHandler : IMessageHandler
{
    private const byte EnterInvitedRoom = 0x00;

    public MsgCategory Category => MsgCategory.cCommunity;

    public Task HandleAsync(ClientSession session, PacketReader reader, CancellationToken cancellationToken)
    {
        byte sub = reader.ReadByte();
        if (sub != EnterInvitedRoom)
        {
            Log.Warn(session.Tag, $"cCommunity 0x{sub:X2} (not implemented): {Log.Hex(reader.ReadToEnd())}");
            return Task.CompletedTask;
        }

        ushort channel = reader.ReadUInt16();
        ushort room = reader.ReadUInt16();
        reader.ReadUInt32();
        return GameFlow.JoinInvitedAsync(session, channel, room, cancellationToken);
    }
}
