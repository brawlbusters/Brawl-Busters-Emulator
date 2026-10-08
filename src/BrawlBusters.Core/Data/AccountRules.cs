using BrawlBusters.Core.Protocol;

namespace BrawlBusters.Core.Data;

public static class AccountRules
{
    public const int MinLoginIdLength = 4;
    public const int MaxLoginIdLength = 10;
    public const int MinPasswordLength = 4;
    public const int MaxPasswordLength = 16;
    public const int MinNicknameLength = 2;
    public const int MaxNicknameLength = 10;

    public static NetError? ValidateNewAccount(string loginId, string password)
    {
        if (loginId.Length < MinLoginIdLength) return NetError.ID_TooShort;
        if (loginId.Length > MaxLoginIdLength) return NetError.ID_TooLong;
        if (!loginId.All(char.IsAsciiLetterOrDigit)) return NetError.ID_InvalidChar;
        if (password.Length < MinPasswordLength) return NetError.PW_TooShort;
        if (password.Length > MaxPasswordLength) return NetError.PW_TooLong;
        return null;
    }

    public static NetError? ValidateNickname(string nickname)
    {
        if (nickname.Length < MinNicknameLength) return NetError.Nick_TooShort;
        if (nickname.Length > MaxNicknameLength) return NetError.Nick_TooLong;
        if (nickname.Any(c => char.IsWhiteSpace(c) || char.IsControl(c))) return NetError.Nick_InvalidChar;
        return null;
    }
}
