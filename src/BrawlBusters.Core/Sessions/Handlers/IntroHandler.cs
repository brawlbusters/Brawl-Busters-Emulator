using BrawlBusters.Core.Data;
using BrawlBusters.Core.Logging;
using BrawlBusters.Core.Network;
using BrawlBusters.Core.Protocol;
using BrawlBusters.Core.Protocol.Packets;

namespace BrawlBusters.Core.Sessions.Handlers;

public sealed class IntroHandler : IMessageHandler
{
    public MsgCategory Category => MsgCategory.cIntro;

    public Task HandleAsync(ClientSession session, PacketReader reader, CancellationToken cancellationToken)
    {
        var request = (IntroRequest)reader.ReadByte();
        switch (request)
        {
            case IntroRequest.CheckNickname:
                return CheckNicknameAsync(session, reader.ReadWideString(), cancellationToken);
            case IntroRequest.CreateNickname:
                return CreateNicknameAsync(session, reader.ReadWideString(), cancellationToken);
            case IntroRequest.CreateCharacter:
                return CreateCharacterAsync(session, reader, cancellationToken);
            default:
                Log.Warn(session.Tag, $"cIntro sub {(byte)request} (not implemented): {Log.Hex(reader.ReadToEnd())}");
                return Task.CompletedTask;
        }
    }

    private static Task CheckNicknameAsync(ClientSession session, string nickname, CancellationToken cancellationToken)
    {
        NetError result = AccountRules.ValidateNickname(nickname)
            ?? (session.Accounts.NicknameExists(nickname) ? NetError.Nick_AlreadyExist : NetError.Success);

        Log.Info(session.Tag, $"Nickname check '{nickname}' -> {result}");
        return session.SendAsync(IntroPacket.CheckNicknameResult(result), cancellationToken);
    }

    private static async Task CreateNicknameAsync(ClientSession session, string nickname, CancellationToken cancellationToken)
    {
        NetError result = AccountRules.ValidateNickname(nickname)
            ?? (session.Accounts.TrySetNickname(session.Account.Id, nickname) ? NetError.Success : NetError.Nick_AlreadyExist);

        Log.Info(session.Tag, $"Nickname create '{nickname}' -> {result}");
        await session.SendAsync(IntroPacket.CreateNicknameResult(result), cancellationToken);

        if (result != NetError.Success) return;
        session.RefreshAccount();
        await GameFlow.NicknameCreatedAsync(session, cancellationToken);
    }

    private static Task CreateCharacterAsync(ClientSession session, PacketReader reader, CancellationToken cancellationToken)
    {
        var shape = new CharacterShape { Class = reader.ReadByte() };
        var values = new List<ushort>();
        while (reader.Remaining >= 2) values.Add(reader.ReadUInt16());
        shape.Values = [.. values];

        session.Accounts.Update(session.Account.Id, account => account.Character = shape);
        session.RefreshAccount();

        Log.Info(session.Tag, $"Character created: class {shape.Class}, shape [{string.Join(", ", shape.Values)}]");
        return GameFlow.CharacterCreatedAsync(session, cancellationToken);
    }
}
