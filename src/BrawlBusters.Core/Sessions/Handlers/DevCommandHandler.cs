using BrawlBusters.Core.Data;
using BrawlBusters.Core.Logging;
using BrawlBusters.Core.Network;
using BrawlBusters.Core.Protocol;
using BrawlBusters.Core.Protocol.Packets;
using BrawlBusters.Core.Security;

namespace BrawlBusters.Core.Sessions.Handlers;

public sealed class DevCommandHandler : IMessageHandler
{
    private const byte AddExperience = 0;
    private const byte AddGold = 1;

    public MsgCategory Category => MsgCategory.cDevCommand;

    public Task HandleAsync(ClientSession session, PacketReader reader, CancellationToken cancellationToken)
    {
        byte command = reader.ReadByte();
        uint amount = reader.ReadUInt32();

        session.RefreshAccount();
        if (!session.Account.Can(Permission.GiveCurrency))
        {
            Log.Warn(LogChannel.Commands, session.Tag, $"Dev command {command} ({amount}) refused: grade {session.Account.Grade}");
            return Task.CompletedTask;
        }

        if (command is not (AddExperience or AddGold))
        {
            Log.Warn(session.Tag, $"Dev command {command} ({amount}) is not known");
            return Task.CompletedTask;
        }

        session.Accounts.Update(session.Account.Id, account =>
        {
            if (command == AddGold)
            {
                account.Gold = (int)Math.Min(int.MaxValue, account.Gold + (long)amount);
            }
            else
            {
                account.Experience = (int)Math.Min(int.MaxValue, account.Experience + (long)amount);
                account.Level = GameData.Instance.LevelForExp(account.Experience, account.Level);
            }
        });
        session.RefreshAccount();

        Account updated = session.Account;
        Log.Info(session.Tag, command == AddExperience
            ? $"Dev command /exp {amount}: experience {updated.Experience}, level {updated.DisplayLevel}"
            : $"Dev command /gold {amount}: BP {updated.Gold}");

        return session.SendAsync(
            UserInfoPacket.ExpAndGold((uint)updated.Experience, (uint)updated.Gold), cancellationToken);
    }
}
