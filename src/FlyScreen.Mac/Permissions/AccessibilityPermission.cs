using System.Diagnostics;
using System.Runtime.InteropServices;
using FlyScreen.Mac.Native;

namespace FlyScreen.Mac.Permissions;

/// <summary>辅助功能权限检测与引导（键鼠捕获/注入所需）。</summary>
public static class AccessibilityPermission
{
    public static bool IsMacOS => RuntimeInformation.IsOSPlatform(OSPlatform.OSX);

    public static bool IsTrusted()
    {
        if (!IsMacOS) return false;
        try
        {
            return CgEventNative.AXIsProcessTrusted();
        }
        catch
        {
            return false;
        }
    }

    /// <summary>打开系统设置 → 隐私与安全性 → 辅助功能。</summary>
    public static void OpenSystemSettings()
    {
        if (!IsMacOS) return;
        try
        {
            Process.Start("open", "x-apple.systempreferences:com.apple.preference.security?Privacy_Accessibility");
        }
        catch
        {
            try
            {
                Process.Start("open", "/System/Library/PreferencePanes/Security.prefPane");
            }
            catch
            {
                // ignore
            }
        }
    }
}
