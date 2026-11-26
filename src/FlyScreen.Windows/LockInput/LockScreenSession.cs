using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace FlyScreen.LockInput;

/// <summary>本机会话锁屏状态（SessionSwitch + 输入桌面探测）。</summary>
public static class LockScreenSession
{
    private static int _started;
    private static volatile bool _locked;

    public static bool IsLocked => _locked;

    public static event Action<bool>? Changed;

    public static void EnsureStarted()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            // 已订阅时仍同步一次，避免启动前已锁屏漏事件
            SyncFromOs();
            return;
        }

        try
        {
            SystemEvents.SessionSwitch += OnSessionSwitch;
            SyncFromOs();
        }
        catch
        {
            Interlocked.Exchange(ref _started, 0);
        }
    }

    /// <summary>
    /// 用当前输入桌面校正锁屏标志。SessionSwitch 在订阅前已发生时 <see cref="IsLocked"/> 会一直为 false。
    /// </summary>
    public static void SyncFromOs()
    {
        try
        {
            var locked = ProbeOsLocked();
            if (_locked == locked) return;
            _locked = locked;
            try { Changed?.Invoke(locked); } catch { /* ignore */ }
            if (!locked)
                LockScreenInputClient.Reset();
        }
        catch
        {
            // ignore
        }
    }

    private static void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        var locked = _locked;
        if (e.Reason == SessionSwitchReason.SessionLock)
            locked = true;
        else if (e.Reason is SessionSwitchReason.SessionUnlock
                 or SessionSwitchReason.ConsoleConnect
                 or SessionSwitchReason.SessionLogon)
            locked = false;
        else
            return;

        if (_locked == locked) return;
        _locked = locked;
        try { Changed?.Invoke(locked); } catch { /* ignore */ }
        if (!locked)
            LockScreenInputClient.Reset();
    }

    /// <summary>
    /// 锁屏时输入桌面多为 Winlogon，用户进程 OpenInputDesktop 常失败或拿到非 Default。
    /// </summary>
    private static bool ProbeOsLocked()
    {
        var desktop = OpenInputDesktop(0, false, DesktopReadObjects);
        if (desktop == IntPtr.Zero)
            return true;

        try
        {
            var name = GetDesktopName(desktop);
            if (string.IsNullOrEmpty(name))
                return _locked;
            return name.Equals("Winlogon", StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { CloseDesktop(desktop); } catch { /* ignore */ }
        }
    }

    private static string GetDesktopName(IntPtr desktop)
    {
        var needed = 0;
        GetUserObjectInformation(desktop, UoiName, IntPtr.Zero, 0, ref needed);
        if (needed <= 0) return "";

        var buffer = Marshal.AllocHGlobal(needed);
        try
        {
            if (!GetUserObjectInformation(desktop, UoiName, buffer, needed, ref needed))
                return "";
            return Marshal.PtrToStringUni(buffer)?.TrimEnd('\0') ?? "";
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private const int UoiName = 2;
    private const uint DesktopReadObjects = 0x0001;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr OpenInputDesktop(uint dwFlags, bool fInherit, uint dwDesiredAccess);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseDesktop(IntPtr hDesktop);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool GetUserObjectInformation(
        IntPtr hObj, int nIndex, IntPtr pvInfo, int nLength, ref int lpnLengthNeeded);
}
