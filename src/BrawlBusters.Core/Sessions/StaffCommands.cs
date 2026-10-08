using BrawlBusters.Core.Data;
using BrawlBusters.Core.Logging;

namespace BrawlBusters.Core.Sessions;

public static class StaffCommands
{
    private const string NoticePrefix = "/notice ";
    private const string GoldPrefix = "/gold ";
    private const string ExpPrefix = "/exp ";

    public static bool TryExecute(AccountRepository accounts, uint userId, string who, string text)
    {
        if (!text.StartsWith('/')) return false;

        Account? account = accounts.FindById(userId);
        if (account is null) return false;

        if (HasPrefix(text, NoticePrefix, out string notice))
        {
            if (!Allowed(account, AccountGrade.GameMaster, who, text) || notice.Length == 0) return false;

            Log.Info(who, $"/notice: {notice}");
            ServerBus.PostNotice(notice);
            return true;
        }

        bool gold = HasPrefix(text, GoldPrefix, out string amountText);
        if (!gold && !HasPrefix(text, ExpPrefix, out amountText)) return false;
        if (!uint.TryParse(amountText, out uint amount)) return false;
        if (!Allowed(account, AccountGrade.Developer, who, text)) return false;

        ApplyAmount(accounts, userId, gold, amount);
        Log.Info(who, gold ? $"/gold {amount}" : $"/exp {amount}");
        ServerBus.PostRefresh(userId);
        return true;
    }

    public static void ApplyAmount(AccountRepository accounts, uint userId, bool gold, uint amount)
    {
        accounts.Update(userId, account =>
        {
            if (gold)
            {
                account.Gold = AddCapped(account.Gold, amount);
            }
            else
            {
                account.Experience = AddCapped(account.Experience, amount);
                account.Level = GameData.Instance.LevelForExp(account.Experience, account.Level);
            }
        });
    }

    private static bool HasPrefix(string text, string prefix, out string rest)
    {
        bool match = text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        rest = match ? text[prefix.Length..].Trim() : "";
        return match;
    }

    private static bool Allowed(Account account, AccountGrade required, string who, string text)
    {
        if (account.Grade >= required) return true;
        Log.Warn(who, $"Staff command refused (grade {account.Grade}, needs {required}): {text}");
        return false;
    }

    private static int AddCapped(int current, uint amount)
        => (int)Math.Min(int.MaxValue, current + (long)amount);
}
