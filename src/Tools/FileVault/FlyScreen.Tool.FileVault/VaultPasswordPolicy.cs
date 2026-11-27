namespace FlyScreen.Tool.FileVault;

/// <summary>主密码强度策略（创建保险箱时强制）。</summary>
public static class VaultPasswordPolicy
{
    public const int MinLength = 8;

    public static bool TryValidate(string? password, out string errorKey)
    {
        errorKey = "";
        if (string.IsNullOrEmpty(password))
        {
            errorKey = "Tool.FileVault.PasswordEmpty";
            return false;
        }

        if (password.Length < MinLength)
        {
            errorKey = "Tool.FileVault.PasswordTooShort";
            return false;
        }

        // 全相同字符（如 aaaaaaaa）视为过弱
        if (password.Distinct().Count() == 1)
        {
            errorKey = "Tool.FileVault.PasswordTooWeak";
            return false;
        }

        return true;
    }
}
