using System.Runtime.InteropServices;
using FlyScreen.LockInput;

namespace FlyScreen.Helpers;

/// <summary>
/// Windows 本机解锁：先退出屏保 / 唤醒锁屏欢迎页，再注入密码并回车（一次完成）。
/// </summary>
public static class WindowsScreenUnlock
{
    private const ushort VkReturn = 0x0D;
    private const ushort VkSpace = 0x20;
    private const ushort VkEscape = 0x1B;
    private const uint SpiGetScreenSaverRunning = 0x0072;

    public static void TryUnlock(string password)
    {
        if (string.IsNullOrEmpty(password)) return;
        LockScreenSession.EnsureStarted();

        // 屏保态：任意键/微移即可退出；勿在此时打密码（会被屏保吃掉，只留下登录页）
        if (IsScreenSaverRunning())
        {
            NudgeSecureDesktop(preferEscape: true);
            WaitWhileScreenSaver(timeoutMs: 2500);
            Thread.Sleep(350);
        }

        // 屏保关闭后若策略要求登录，SessionLock 可能稍晚才到
        if (!LockScreenSession.IsLocked)
            WaitUntilLocked(timeoutMs: 1500);

        if (!LockScreenSession.IsLocked) return;

        // 锁屏欢迎页（点按/空格后才出现密码框）
        NudgeSecureDesktop(preferEscape: false);
        Thread.Sleep(400);

        InjectPasswordAndEnter(password);
    }

    private static void InjectPasswordAndEnter(string password)
    {
        if (LockScreenInputClient.TryInsertText(password))
        {
            Thread.Sleep(100);
            LockScreenInputClient.TryKey(VkReturn, down: true, extended: false);
            LockScreenInputClient.TryKey(VkReturn, down: false, extended: false);
            return;
        }

        try
        {
            SecureDesktopInjector.InsertText(password);
            Thread.Sleep(100);
            SecureDesktopInjector.Key(VkReturn, down: true, extended: false);
            SecureDesktopInjector.Key(VkReturn, down: false, extended: false);
        }
        catch
        {
            // ignore
        }
    }

    /// <summary>微移鼠标 + 空格/Esc，用于退出屏保或唤醒锁屏密码框。</summary>
    private static void NudgeSecureDesktop(bool preferEscape)
    {
        var vk = preferEscape ? VkEscape : VkSpace;
        if (LockScreenInputClient.TryMoveRelative(1, 0))
        {
            LockScreenInputClient.TryMoveRelative(-1, 0);
            LockScreenInputClient.TryKey(vk, down: true, extended: false);
            LockScreenInputClient.TryKey(vk, down: false, extended: false);
            // 再点一下屏幕中部，兼容需点击的锁屏欢迎页
            LockScreenInputClient.TryMoveAbsoluteNormalized(0.5, 0.55);
            LockScreenInputClient.TryMouseButton(0, down: true);
            LockScreenInputClient.TryMouseButton(0, down: false);
            return;
        }

        try
        {
            SecureDesktopInjector.MoveRelative(1, 0);
            SecureDesktopInjector.MoveRelative(-1, 0);
            SecureDesktopInjector.Key(vk, down: true, extended: false);
            SecureDesktopInjector.Key(vk, down: false, extended: false);
            SecureDesktopInjector.MoveAbsoluteNormalized(0.5, 0.55);
            SecureDesktopInjector.MouseButton(0, down: true);
            SecureDesktopInjector.MouseButton(0, down: false);
        }
        catch
        {
            // ignore
        }
    }

    private static bool IsScreenSaverRunning()
    {
        try
        {
            var running = false;
            if (SystemParametersInfo(SpiGetScreenSaverRunning, 0, ref running, 0))
                return running;
        }
        catch
        {
            // ignore
        }

        return false;
    }

    private static void WaitWhileScreenSaver(int timeoutMs)
    {
        var until = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < until && IsScreenSaverRunning())
            Thread.Sleep(100);
    }

    private static void WaitUntilLocked(int timeoutMs)
    {
        var until = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < until && !LockScreenSession.IsLocked)
            Thread.Sleep(100);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, ref bool pvParam, uint fWinIni);
}
