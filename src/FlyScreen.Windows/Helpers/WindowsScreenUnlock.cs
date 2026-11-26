using System.Runtime.InteropServices;
using FlyScreen.LockInput;

namespace FlyScreen.Helpers;

/// <summary>
/// Windows 本机解锁：先亮屏 / 退出屏保 / 唤醒锁屏欢迎页，再注入密码并回车（一次完成）。
/// </summary>
public static class WindowsScreenUnlock
{
    private const ushort VkReturn = 0x0D;
    private const ushort VkSpace = 0x20;
    private const ushort VkEscape = 0x1B;
    private const ushort VkBack = 0x08;
    private const ushort VkDelete = 0x2E;
    private const ushort VkA = 0x41;
    private const ushort VkControl = 0x11;
    private const uint SpiGetScreenSaverRunning = 0x0072;

    public static void TryUnlock(string password)
    {
        if (string.IsNullOrEmpty(password)) return;
        LockScreenSession.EnsureStarted();
        LockScreenSession.SyncFromOs();

        // 息屏锁定后：先强制亮屏，再 nudge；仅靠微移常常打不亮 DPMS
        WindowsScreenLock.TryWakeDisplay();
        Thread.Sleep(400);
        NudgeSecureDesktop(sendKey: true, preferEscape: false);
        Thread.Sleep(250);
        WindowsScreenLock.TryWakeDisplay();
        Thread.Sleep(200);

        // 屏保态：任意键/微移即可退出；勿在此时打密码（会被屏保吃掉，只留下登录页）
        if (IsScreenSaverRunning())
        {
            NudgeSecureDesktop(sendKey: true, preferEscape: true);
            WaitWhileScreenSaver(timeoutMs: 2500);
            Thread.Sleep(350);
            LockScreenSession.SyncFromOs();
        }

        // 屏保关闭后若策略要求登录，SessionLock / 桌面切换可能稍晚才到
        if (!LockScreenSession.IsLocked)
            WaitUntilLocked(timeoutMs: 4000);

        LockScreenSession.SyncFromOs();
        if (!LockScreenSession.IsLocked) return;

        // 欢迎页：只点鼠标，勿再发空格——密码框已聚焦时 Space 会进密码导致「密码错误」
        NudgeSecureDesktop(sendKey: false, preferEscape: false);
        Thread.Sleep(500);

        ClearPasswordField();
        Thread.Sleep(80);
        InjectPasswordAndEnter(password);
    }

    private static void InjectPasswordAndEnter(string password)
    {
        // 优先走锁屏助手管道；失败再回退本机 SendInput
        if (LockScreenInputClient.TryInsertText(password))
        {
            Thread.Sleep(120);
            LockScreenInputClient.TryKey(VkReturn, down: true, extended: false);
            LockScreenInputClient.TryKey(VkReturn, down: false, extended: false);
            return;
        }

        try
        {
            SecureDesktopInjector.InsertText(password);
            Thread.Sleep(120);
            SecureDesktopInjector.Key(VkReturn, down: true, extended: false);
            SecureDesktopInjector.Key(VkReturn, down: false, extended: false);
        }
        catch
        {
            // ignore
        }
    }

    /// <summary>清空密码框残留（上次失败 / 误入的空格），避免叠字。</summary>
    private static void ClearPasswordField()
    {
        if (LockScreenInputClient.TryKey(VkControl, down: true, extended: false))
        {
            LockScreenInputClient.TryKey(VkA, down: true, extended: false);
            LockScreenInputClient.TryKey(VkA, down: false, extended: false);
            LockScreenInputClient.TryKey(VkControl, down: false, extended: false);
            LockScreenInputClient.TryKey(VkBack, down: true, extended: false);
            LockScreenInputClient.TryKey(VkBack, down: false, extended: false);
            LockScreenInputClient.TryKey(VkDelete, down: true, extended: true);
            LockScreenInputClient.TryKey(VkDelete, down: false, extended: true);
            return;
        }

        try
        {
            SecureDesktopInjector.Key(VkControl, down: true, extended: false);
            SecureDesktopInjector.Key(VkA, down: true, extended: false);
            SecureDesktopInjector.Key(VkA, down: false, extended: false);
            SecureDesktopInjector.Key(VkControl, down: false, extended: false);
            SecureDesktopInjector.Key(VkBack, down: true, extended: false);
            SecureDesktopInjector.Key(VkBack, down: false, extended: false);
            SecureDesktopInjector.Key(VkDelete, down: true, extended: true);
            SecureDesktopInjector.Key(VkDelete, down: false, extended: true);
        }
        catch
        {
            // ignore
        }
    }

    /// <summary>微移鼠标 + 可选空格/Esc + 点击，用于退出屏保或唤醒锁屏密码框。</summary>
    private static void NudgeSecureDesktop(bool sendKey, bool preferEscape)
    {
        var vk = preferEscape ? VkEscape : VkSpace;
        if (LockScreenInputClient.TryMoveRelative(1, 0))
        {
            LockScreenInputClient.TryMoveRelative(-1, 0);
            if (sendKey)
            {
                LockScreenInputClient.TryKey(vk, down: true, extended: false);
                LockScreenInputClient.TryKey(vk, down: false, extended: false);
            }

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
            if (sendKey)
            {
                SecureDesktopInjector.Key(vk, down: true, extended: false);
                SecureDesktopInjector.Key(vk, down: false, extended: false);
            }

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
        while (Environment.TickCount64 < until)
        {
            LockScreenSession.SyncFromOs();
            if (LockScreenSession.IsLocked) return;
            Thread.Sleep(100);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, ref bool pvParam, uint fWinIni);
}
