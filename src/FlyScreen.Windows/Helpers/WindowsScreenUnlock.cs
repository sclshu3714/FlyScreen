using FlyScreen.LockInput;

namespace FlyScreen.Helpers;

/// <summary>Windows 本机解锁：向登录界面注入密码并回车。</summary>
public static class WindowsScreenUnlock
{
    private const ushort VkReturn = 0x0D;

    public static void TryUnlock(string password)
    {
        if (string.IsNullOrEmpty(password)) return;
        LockScreenSession.EnsureStarted();
        if (!LockScreenSession.IsLocked) return;

        // 优先锁屏助手管道（可进 Winlogon）
        if (LockScreenInputClient.TryInsertText(password))
        {
            Thread.Sleep(80);
            LockScreenInputClient.TryKey(VkReturn, down: true, extended: false);
            LockScreenInputClient.TryKey(VkReturn, down: false, extended: false);
            return;
        }

        try
        {
            SecureDesktopInjector.InsertText(password);
            Thread.Sleep(80);
            SecureDesktopInjector.Key(VkReturn, down: true, extended: false);
            SecureDesktopInjector.Key(VkReturn, down: false, extended: false);
        }
        catch
        {
            // ignore
        }
    }
}
