using BrawlBusters.Core.Protocol;

namespace BrawlBusters.Core.Data;

/// <summary>
/// What a login id, password and nickname must look like. Every refusal is one of the client's own error
/// codes, so the player sees the matching dialog ("You cannot use your LoginID as your password", ...).
/// </summary>
public static class AccountRules
{
    public const int MinLoginIdLength = 4;
    public const int MaxLoginIdLength = 10;
    public const int MinPasswordLength = 4;
    public const int MaxPasswordLength = 16;
    public const int MinNicknameLength = 2;
    public const int MaxNicknameLength = 10;
    public const int MaxWrongPasswords = 3;

    private const int MaxSameCharactersInARow = 2;

    public static NetError? ValidateNewAccount(string loginId, string password, bool requirePunctuation = false)
    {
        if (loginId.Length < MinLoginIdLength) return NetError.ID_TooShort;
        if (loginId.Length > MaxLoginIdLength) return NetError.ID_TooLong;
        if (!loginId.All(char.IsAsciiLetterOrDigit)) return NetError.ID_InvalidChar;
        if (HasRun(loginId, MaxSameCharactersInARow + 1)) return NetError.ID_ExceedCharCount;
        if (IsProhibited(loginId)) return NetError.ID_Prohibited;

        if (password.Length < MinPasswordLength) return NetError.PW_TooShort;
        if (password.Length > MaxPasswordLength) return NetError.PW_TooLong;
        if (password.Equals(loginId, StringComparison.OrdinalIgnoreCase)) return NetError.PW_SameAsID;
        if (password.Distinct().Count() == 1) return NetError.PW_AllSameChars;
        if (IsLinear(password)) return NetError.PW_LinearString;
        if (requirePunctuation && !password.Any(c => char.IsPunctuation(c) || char.IsSymbol(c))) return NetError.PW_NoSpecialChar;
        return null;
    }

    public static NetError? ValidateNickname(string nickname)
    {
        if (nickname.Length < MinNicknameLength) return NetError.Nick_TooShort;
        if (nickname.Length > MaxNicknameLength) return NetError.Nick_TooLong;
        if (nickname.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)) || nickname[0] == '#') return NetError.Nick_InvalidChar;
        if (nickname.All(char.IsDigit)) return NetError.Nick_DigitChar;
        if (IsProhibited(nickname)) return NetError.Nick_Prohibited;
        return null;
    }

    private static bool HasRun(string text, int length)
    {
        int run = 1;
        for (int i = 1; i < text.Length; i++)
        {
            run = char.ToLowerInvariant(text[i]) == char.ToLowerInvariant(text[i - 1]) ? run + 1 : 1;
            if (run >= length) return true;
        }
        return false;
    }

    /// <summary>True for "abcd", "4321" and the like: every character is one step from the one before, all in one direction.</summary>
    private static bool IsLinear(string text)
    {
        if (text.Length < 2 || !text.All(char.IsAsciiLetterOrDigit)) return false;

        string lower = text.ToLowerInvariant();
        int step = lower[1] - lower[0];
        if (step is not (1 or -1)) return false;
        for (int i = 2; i < lower.Length; i++)
            if (lower[i] - lower[i - 1] != step) return false;
        return true;
    }

    private static bool IsProhibited(string text)
        => GameData.Instance.CensoredWords.Any(word => text.Contains(word, StringComparison.OrdinalIgnoreCase));
}
