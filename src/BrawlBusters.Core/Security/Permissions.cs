using BrawlBusters.Core.Data;

namespace BrawlBusters.Core.Security;

[Flags]
public enum Permission
{
    None = 0,
    SeeAllChannels = 1 << 0,
    EnterFullChannel = 1 << 1,
    ObserveMatches = 1 << 2,
    StaffChat = 1 << 3,
    ListPlayers = 1 << 4,
    Kick = 1 << 5,
    Notice = 1 << 6,
    Ban = 1 << 7,
    GiveCurrency = 1 << 8,
    GiveItems = 1 << 9,
    SetGrade = 1 << 10,
    ManageServer = 1 << 11,
}

/// <summary>What each account grade may do. Every staff-only action in the server asks here.</summary>
public static class Permissions
{
    private const Permission Moderator =
        Permission.SeeAllChannels | Permission.EnterFullChannel | Permission.ObserveMatches
        | Permission.StaffChat | Permission.ListPlayers | Permission.Kick;

    private const Permission GameMaster = Moderator | Permission.Notice | Permission.Ban;

    private const Permission Developer =
        GameMaster | Permission.GiveCurrency | Permission.GiveItems | Permission.SetGrade | Permission.ManageServer;

    public static Permission Of(AccountGrade grade) => grade switch
    {
        AccountGrade.Moderator => Moderator,
        AccountGrade.GameMaster => GameMaster,
        AccountGrade.Developer => Developer,
        _ => Permission.None,
    };

    /// <summary>The lowest grade that holds <paramref name="permission"/>.</summary>
    public static AccountGrade MinimumGrade(Permission permission)
        => Enum.GetValues<AccountGrade>().Where(grade => (Of(grade) & permission) == permission).DefaultIfEmpty(AccountGrade.Developer).Min();

    public static bool Can(this Account account, Permission permission) => (Of(account.Grade) & permission) == permission;

    public static bool IsStaff(this Account account) => account.Grade >= AccountGrade.Moderator;

    public static bool TryParseGrade(string text, out AccountGrade grade)
    {
        grade = text.ToLowerInvariant() switch
        {
            "player" or "user" or "0" => AccountGrade.Player,
            "mod" or "moderator" or "1" => AccountGrade.Moderator,
            "gm" or "gamemaster" or "2" => AccountGrade.GameMaster,
            "dev" or "developer" or "3" => AccountGrade.Developer,
            _ => (AccountGrade)(-1),
        };
        return (int)grade >= 0;
    }
}
