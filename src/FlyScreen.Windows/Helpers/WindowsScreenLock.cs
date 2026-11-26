using System.Runtime.InteropServices;
using FlyScreen.LockInput;
using FlyScreen.Native;

namespace FlyScreen.Helpers;

/// <summary>Windows 本机锁屏；可选随后关闭显示器。</summary>
public static class WindowsScreenLock
{
    private const int WmSysCommand = 0x0112;
    private const int ScMonitorPower = 0xF170;
    private static readonly IntPtr HwndBroadcast = new(0xFFFF);

    public static void TryLock(bool turnOffDisplay = false)
    {
        LockScreenSession.EnsureStarted();
        try { NativeMethods.LockWorkStation(); }
        catch { /* ignore */ }

        if (!turnOffDisplay) return;
        try
        {
            // 等 SessionLock / 安全桌面就绪后再灭屏，避免关屏抢在锁屏之前
            WaitUntilLocked(timeoutMs: 3000);
            Thread.Sleep(200);
            TryTurnOffDisplay();
        }
        catch { /* ignore */ }
    }

    public static void TryTurnOffDisplay()
    {
        // lParam=2：关闭显示器；键鼠输入或 TryWakeDisplay 可唤醒
        SendMessage(HwndBroadcast, WmSysCommand, (IntPtr)ScMonitorPower, (IntPtr)2);
    }

    /// <summary>强制点亮显示器（息屏锁定后远程解锁前置步骤）。</summary>
    public static void TryWakeDisplay()
    {
        // lParam=-1：打开显示器
        SendMessage(HwndBroadcast, WmSysCommand, (IntPtr)ScMonitorPower, (IntPtr)(-1));
    }

    private static void WaitUntilLocked(int timeoutMs)
    {
        var until = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < until)
        {
            LockScreenSession.SyncFromOs();
            if (LockScreenSession.IsLocked) return;
            Thread.Sleep(50);
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
}
