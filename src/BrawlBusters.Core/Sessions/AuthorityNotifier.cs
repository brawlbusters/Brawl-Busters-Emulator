using BrawlBusters.Core.Data;
using BrawlBusters.Core.Logging;
using BrawlBusters.Core.Protocol.Packets;
using BrawlBusters.Core.Security;

namespace BrawlBusters.Core.Sessions;

/// <summary>
/// Brings a change of grade or a ban to the player the moment it happens: the session gets the new rights,
/// the game client is told, and the player reads a system message about it.
/// </summary>
public static class AuthorityNotifier
{
    public static void Attach(AccountRepository accounts)
        => accounts.AuthorityChanged += (account, gradeBefore) => _ = World.RunAsync(() => ApplyAsync(account, gradeBefore));

    public static string NameOf(AccountGrade grade) => grade switch
    {
        AccountGrade.Moderator => "Moderator",
        AccountGrade.GameMaster => "Game Master",
        AccountGrade.Developer => "Developer",
        _ => "Player",
    };

    private static async Task ApplyAsync(Account account, AccountGrade gradeBefore)
    {
        if (SessionRegistry.Find(account.Id) is not { } session) return;

        if (account.IsBanned)
        {
            Log.Info(LogChannel.Session, session.Tag, $"Banned until {account.BannedUntilUtc:u} - disconnected");
            await session.SendAsync(UserMsgPacket.SystemMessage(
                $"[System] Your account has been suspended until {account.BannedUntilUtc:yyyy-MM-dd HH:mm} UTC. You will be disconnected."));
            World.Later(TimeSpan.FromSeconds(3), () =>
            {
                session.Connection.Close();
                return Task.CompletedTask;
            });
            return;
        }

        if (account.Grade == gradeBefore) return;

        bool couldObserve = session.Account.Can(Permission.ObserveMatches);
        session.RefreshAccount();
        bool canObserve = session.Account.Can(Permission.ObserveMatches);
        Log.Info(LogChannel.Session, session.Tag, $"Grade changed: {gradeBefore} -> {account.Grade}");

        string direction = account.Grade > gradeBefore ? "promoted" : "changed";
        await session.SendAsync(UserMsgPacket.SystemMessage(
            $"[System] Your account has been {direction} to {NameOf(account.Grade)}. Your new permissions are active now."));
        await session.SendAsync(UserInfoPacket.FlagChanged(UserInfoPacket.GradeFlag, (byte)account.Grade));
        if (canObserve != couldObserve)
            await session.SendAsync(UserInfoPacket.FlagChanged(UserInfoPacket.GameMasterFlag, (byte)(canObserve ? 1 : 0)));
        if (session.Room is null && session.ChannelId != 0)
            await GameFlow.RefreshLobbyAsync(session, session.ChannelId, CancellationToken.None);
    }
}
