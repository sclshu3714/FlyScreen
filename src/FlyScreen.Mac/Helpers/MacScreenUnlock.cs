using FlyScreen.Mac.LockInput;

namespace FlyScreen.Mac.Helpers;

/// <summary>macOS 本机解锁：锁屏助手可用时注入密码并回车（loginwindow 仍可能拦截）。</summary>
public static class MacScreenUnlock
{
    private const ushort VkReturn = 0x0D;

    public static void TryUnlock(string password)
    {
        if (string.IsNullOrEmpty(password)) return;
        MacLockScreenSession.EnsureStarted();
        if (!MacLockScreenSession.IsLocked) return;
        // 先唤醒锁屏 UI，再注入密码（一次完成）
        MacLockScreenInputBridge.TryKey(0x20, down: true, extended: false);
        MacLockScreenInputBridge.TryKey(0x20, down: false, extended: false);
        Thread.Sleep(300);
        if (!MacLockScreenInputBridge.TryInsertText(password)) return;
        Thread.Sleep(80);
        MacLockScreenInputBridge.TryKey(VkReturn, down: true, extended: false);
        MacLockScreenInputBridge.TryKey(VkReturn, down: false, extended: false);
    }
}
