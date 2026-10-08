using BrawlBusters.Core.Logging;
using BrawlBusters.Core.Network;
using BrawlBusters.Core.Protocol;
using BrawlBusters.Core.Protocol.Packets;

namespace BrawlBusters.Core.Sessions.Handlers;

public sealed class UserMsgHandler : IMessageHandler
{
    public MsgCategory Category => MsgCategory.cUserMsg;

    public Task HandleAsync(ClientSession session, PacketReader reader, CancellationToken cancellationToken)
    {
        var request = (UserMsgRequest)reader.ReadByte();
        if (request != UserMsgRequest.RoomChat)
        {
            Log.Warn(session.Tag, $"cUserMsg 0x{(byte)request:X2} (not implemented): {Log.Hex(reader.ReadToEnd())}");
            return Task.CompletedTask;
        }

        string text = reader.ReadWideString();
        if (StaffCommands.TryExecute(session.Accounts, session.Account.Id, session.Tag, text))
            return Task.CompletedTask;

        ushort kind = reader.Remaining >= 2 ? reader.ReadUInt16() : (ushort)0;
        Log.Info(session.Tag, $"[room chat{(kind == 0 ? "" : " " + kind)}] {text}");
        return GameFlow.RoomChatAsync(session, text, kind, cancellationToken);
    }
}
