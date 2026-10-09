using BrawlBusters.Core.Commands;
using BrawlBusters.Core.Logging;
using BrawlBusters.Core.Network;
using BrawlBusters.Core.Protocol;
using BrawlBusters.Core.Protocol.Packets;

namespace BrawlBusters.Core.Sessions.Handlers;

public sealed class UserMsgHandler : IMessageHandler
{
    public MsgCategory Category => MsgCategory.cUserMsg;

    public async Task HandleAsync(ClientSession session, PacketReader reader, CancellationToken cancellationToken)
    {
        var request = (UserMsgRequest)reader.ReadByte();
        if (request != UserMsgRequest.RoomChat)
        {
            Log.Warn(session.Tag, $"cUserMsg 0x{(byte)request:X2} (not implemented): {Log.Hex(reader.ReadToEnd())}");
            return;
        }

        string text = reader.ReadWideString();
        session.RefreshAccount();
        var context = new CommandContext
        {
            Accounts = session.Accounts,
            Tag = session.Tag,
            Caller = session.Account,
            Session = session,
            Reply = line => session.SendAsync(UserMsgPacket.SystemMessage(line), cancellationToken),
        };
        if (await CommandRegistry.Instance.TryExecuteAsync(context, text, cancellationToken)) return;

        if (text.Length > session.Settings.MaxChatLength)
        {
            Log.Info(LogChannel.Chat, session.Tag, $"Room chat line of {text.Length} characters refused");
            await session.SendAsync(RoomPacket.Error(NetError.Room_ExceedMaxChatLength), cancellationToken);
            return;
        }

        // cUserMsg 05 (client 0x5AB300): wstr text, u8 kind - 0 to everybody, 1 to the own team.
        byte kind = reader.EndOfData ? UserMsgPacket.ChatToAll : reader.ReadByte();
        Log.Info(LogChannel.Chat, session.Tag, $"[{(kind == UserMsgPacket.ChatToTeam ? "team chat" : "room chat")}] {text}");
        await GameFlow.RoomChatAsync(session, text, kind, cancellationToken);
    }
}
