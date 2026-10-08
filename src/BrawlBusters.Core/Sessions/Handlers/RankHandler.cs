using BrawlBusters.Core.Data;
using BrawlBusters.Core.Logging;
using BrawlBusters.Core.Network;
using BrawlBusters.Core.Protocol;

namespace BrawlBusters.Core.Sessions.Handlers;

public sealed class RankHandler : IMessageHandler
{
    private const byte OwnStanding = 0x0B;
    private const byte Page = 0x0C;
    private const byte OwnStandingReply = 0x07;
    private const byte PageReply = 0x08;
    private const int MaxRows = 50;
    private const byte ClassCount = 5;
    private const byte Period = 1;
    private const byte Kind = 1;

    public MsgCategory Category => MsgCategory.cRank;

    public Task HandleAsync(ClientSession session, PacketReader reader, CancellationToken cancellationToken)
    {
        byte sub = reader.ReadByte();
        switch (sub)
        {
            case OwnStanding:
                return session.SendAsync(OwnStandingPacket(session), cancellationToken);

            case Page:
            {
                reader.ReadByte();
                byte characterClass = reader.ReadByte();
                reader.ReadToEnd();
                return SendPageAsync(session, characterClass, cancellationToken);
            }

            default:
                Log.Warn(session.Tag, $"cRank 0x{sub:X2} (not implemented): {Log.Hex(reader.ReadToEnd())}");
                return Task.CompletedTask;
        }
    }

    public static PacketWriter OwnStandingPacket(ClientSession session)
    {
        var writer = new PacketWriter(MsgCategory.sRank, OwnStandingReply).WriteByte(ClassCount);
        for (byte characterClass = 1; characterClass <= ClassCount; characterClass++)
        {
            List<Account> ranked = Ranked(session, characterClass);
            int index = ranked.FindIndex(account => account.Id == session.Account.Id);
            writer.WriteByte(Kind).WriteByte(characterClass).WriteByte(Period)
                .WriteUInt32((uint)(index + 1))
                .WriteUInt32(index < 0 ? 0 : (uint)session.Account.Experience)
                .WriteUInt32((uint)ranked.Count);
        }
        return writer;
    }

    private static Task SendPageAsync(ClientSession session, byte characterClass, CancellationToken cancellationToken)
    {
        if (characterClass is < 1 or > ClassCount)
        {
            Log.Warn(session.Tag, $"Leaderboard for class {characterClass} does not exist");
            return Task.CompletedTask;
        }

        List<Account> ranked = Ranked(session, characterClass).Take(MaxRows).ToList();

        var writer = new PacketWriter(MsgCategory.sRank, PageReply)
            .WriteUInt32((uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds())
            .WriteByte(characterClass)
            .WriteByte(Period)
            .WriteByte((byte)ranked.Count);

        for (int i = 0; i < ranked.Count; i++)
        {
            Account account = ranked[i];
            writer.WriteUInt32((uint)(i + 1))
                .WriteByte(Period)
                .WriteWideString(account.Nickname)
                .WriteUInt32((uint)account.Experience)
                .WriteUInt32((uint)account.Gem)
                .WriteUInt32((uint)account.Gold);
        }

        Log.Info(session.Tag, $"Leaderboard of class {characterClass}: {ranked.Count} rows");
        return session.SendAsync(writer, cancellationToken);
    }

    private static List<Account> Ranked(ClientSession session, byte characterClass)
        => session.Accounts.All()
            .Where(account => account.HasNickname && account.Character?.Class == characterClass)
            .OrderByDescending(account => account.DisplayLevel)
            .ThenByDescending(account => account.Experience)
            .ThenBy(account => account.Id)
            .ToList();
}
