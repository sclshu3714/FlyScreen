using System.Diagnostics;
using FlyScreen.Mac.Permissions;

namespace FlyScreen.Mac.ScreenExtend;

public static class MacScreenRecording
{
    public static bool IsGranted()
    {
        if (!AccessibilityPermission.IsMacOS) return false;
        try
        {
            return MacDisplayNative.CGPreflightScreenCaptureAccess();
        }
        catch (EntryPointNotFoundException)
        {
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static void Request()
    {
        if (!AccessibilityPermission.IsMacOS) return;
        try { MacDisplayNative.CGRequestScreenCaptureAccess(); }
        catch { /* 旧系统没有这个入口 */ }
    }

    public static void OpenSettings()
    {
        if (!AccessibilityPermission.IsMacOS) return;
        try
        {
            Process.Start("open", "x-apple.systempreferences:com.apple.preference.security?Privacy_ScreenCapture");
        }
        catch
        {
            /* ignore */
        }
    }
}
