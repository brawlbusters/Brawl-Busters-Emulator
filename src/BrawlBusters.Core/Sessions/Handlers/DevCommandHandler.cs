using BrawlBusters.Core.Data;
using BrawlBusters.Core.Logging;
using BrawlBusters.Core.Network;
using BrawlBusters.Core.Protocol;
using BrawlBusters.Core.Protocol.Packets;

namespace BrawlBusters.Core.Sessions.Handlers;

public sealed class DevCommandHandler : IMessageHandler
{
    private const AccountGrade RequiredGrade = AccountGrade.Developer;
    private const byte AddExperience = 0;
    private const byte AddGold = 1;

    public MsgCategory Category => MsgCategory.cDevCommand;

    public Task HandleAsync(ClientSession session, PacketReader reader, CancellationToken cancellationToken)
    {
        byte command = reader.ReadByte();
        uint amount = reader.ReadUInt32();

        session.RefreshAccount();
        AccountGrade grade = session.Account.Grade;

        if (grade < RequiredGrade)
        {
            Log.Warn(session.Tag, $"Dev command {command} ({amount}) refused: grade {grade}, needs {RequiredGrade}");
            return Task.CompletedTask;
        }

        if (command is not (AddExperience or AddGold))
        {
            Log.Warn(session.Tag, $"Dev command {command} ({amount}) is not known");
            return Task.CompletedTask;
        }

        StaffCommands.ApplyAmount(session.Accounts, session.Account.Id, gold: command == AddGold, amount);
        session.RefreshAccount();

        Account updated = session.Account;
        Log.Info(session.Tag, command == AddExperience
            ? $"Dev command /exp {amount}: experience {updated.Experience}, level {updated.DisplayLevel}"
            : $"Dev command /gold {amount}: BP {updated.Gold}");

        return session.SendAsync(
            UserInfoPacket.ExpAndGold((uint)updated.Experience, (uint)updated.Gold), cancellationToken);
    }
}
