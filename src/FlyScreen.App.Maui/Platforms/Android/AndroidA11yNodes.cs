#if ANDROID
using Android.Views.Accessibility;

namespace FlyScreen.AppMaui.Platforms.Android;

/// <summary>
/// AccessibilityNodeInfo 池化兼容：API 33+ Obtain/Recycle 已弃用，低版本仍须使用。
/// </summary>
internal static class AndroidA11yNodes
{
    public static AccessibilityNodeInfo? ObtainCopy(AccessibilityNodeInfo? node)
    {
        if (node == null) return null;
        if (OperatingSystem.IsAndroidVersionAtLeast(33))
            return new AccessibilityNodeInfo(node);
#pragma warning disable CA1422 // Obtain：API33+ 弃用，低版本必须池化
        return AccessibilityNodeInfo.Obtain(node);
#pragma warning restore CA1422
    }

    public static void RecycleQuiet(AccessibilityNodeInfo? node)
    {
        if (node == null) return;
        if (OperatingSystem.IsAndroidVersionAtLeast(33))
            return;
#pragma warning disable CA1422 // Recycle：API33+ 弃用为空操作，低版本须归还池
        try { node.Recycle(); }
        catch { /* ignore */ }
#pragma warning restore CA1422
    }
}
#endif
