using System.Runtime.InteropServices;
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
        try { NativeMethods.LockWorkStation(); }
        catch { /* ignore */ }

        if (!turnOffDisplay) return;
        try
        {
            // 等 SessionLock / 安全桌面就绪后再灭屏，避免关屏抢在锁屏之前
            Thread.Sleep(350);
            TryTurnOffDisplay();
        }
        catch { /* ignore */ }
    }

    public static void TryTurnOffDisplay()
    {
        // lParam=2：关闭显示器；键鼠输入可唤醒
        SendMessage(HwndBroadcast, WmSysCommand, (IntPtr)ScMonitorPower, (IntPtr)2);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
}
