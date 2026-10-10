using BrawlBusters.Core.Logging;
using BrawlBusters.Core.Network;
using BrawlBusters.Core.Protocol;
using BrawlBusters.Core.Protocol.Packets;

namespace BrawlBusters.Core.Sessions.Handlers;

public sealed class LadderHandler : IMessageHandler
{
    private const byte StartMatch = 0x03;
    private const byte CancelMatch = 0x04;
    private const byte CreateRoom = 0x05;

    public MsgCategory Category => MsgCategory.cLadder;

    public async Task HandleAsync(ClientSession session, PacketReader reader, CancellationToken cancellationToken)
    {
        byte sub = reader.ReadByte();
        switch (sub)
        {
            case StartMatch:
                await GameFlow.LadderStartSoloAsync(session, cancellationToken);
                break;

            case CancelMatch:
                await GameFlow.LadderCancelSoloAsync(session, cancellationToken);
                break;

            case CreateRoom:
                reader.ReadToEnd();
                await GameFlow.LadderCreateRoomAsync(session, cancellationToken);
                break;

            default:
                Log.Warn(session.Tag, $"cLadder 0x{sub:X2} (not implemented): {Log.Hex(reader.ReadToEnd())}");
                break;
        }
    }
}

public static class LadderPacket
{
    private const byte DataSub = 0x00;
    private const byte MatchingSub = 0x02;
    private const int TableValues = 7;
    private const int RecordValues = 6;

    /// <summary>
    /// sLadder 00: `u32 time the boundaries were last updated (the screen shows "Last updated: N m ago" from it,
    /// UI m_iUpdateElapsedTimeSec), u16 x 7 rank boundaries (see LadderGrades), u32 matches, wins, losses, draws,
    /// score, gem score`.
    /// </summary>
    public static PacketWriter Data(Data.Account account, ushort[] table)
    {
        Data.MatchStats ladder = account.Ladder;
        var writer = new PacketWriter(MsgCategory.sLadder, DataSub)
            .WriteUInt32((uint)new DateTimeOffset(LadderGrades.UpdatedUtc, TimeSpan.Zero).ToUnixTimeSeconds());
        for (int i = 0; i < TableValues; i++) writer.WriteUInt16(table.ElementAtOrDefault(i));
        return writer
            .WriteUInt32((uint)ladder.Matches)
            .WriteUInt32((uint)ladder.Wins)
            .WriteUInt32((uint)ladder.Losses)
            .WriteUInt32((uint)ladder.Draws)
            .WriteUInt32((uint)ladder.Score)
            .WriteUInt32((uint)account.LadderPoints);
    }

    public static PacketWriter Matching(bool searching)
        => new PacketWriter(MsgCategory.sLadder, MatchingSub).WriteByte((byte)(searching ? 1 : 0));
}
