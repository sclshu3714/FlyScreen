using FlyScreen.Linux.LockInput;

namespace FlyScreen.Linux.Helpers;

/// <summary>Linux 本机解锁：uinput 助手可用时注入密码并回车。</summary>
public static class LinuxScreenUnlock
{
    private const ushort VkReturn = 0x0D;

    public static void TryUnlock(string password)
    {
        if (string.IsNullOrEmpty(password)) return;
        LinuxLockScreenSession.EnsureStarted();
        if (!LinuxLockScreenSession.IsLocked) return;
        // 先唤醒锁屏 UI，再注入密码（一次完成）
        LinuxLockScreenInputBridge.TryKey(0x20, down: true, extended: false);
        LinuxLockScreenInputBridge.TryKey(0x20, down: false, extended: false);
        Thread.Sleep(300);
        if (!LinuxLockScreenInputBridge.TryInsertText(password)) return;
        Thread.Sleep(80);
        LinuxLockScreenInputBridge.TryKey(VkReturn, down: true, extended: false);
        LinuxLockScreenInputBridge.TryKey(VkReturn, down: false, extended: false);
    }
}
