using BrawlBusters.Core.Network;
using BrawlBusters.Core.Protocol;

namespace BrawlBusters.Core.Sessions;

public interface IMessageHandler
{
    MsgCategory Category { get; }

    Task HandleAsync(ClientSession session, PacketReader reader, CancellationToken cancellationToken);
}
