using FlyScreen.Native;

namespace FlyScreen.Helpers;

/// <summary>Windows 本机锁屏。</summary>
public static class WindowsScreenLock
{
    public static void TryLock()
    {
        try { NativeMethods.LockWorkStation(); }
        catch { /* ignore */ }
    }
}
