#if ANDROID
using Android.Content;
using Android.Views;
using AgPoint = Android.Graphics.Point;

namespace FlyScreen.AppMaui.Platforms.Android;

/// <summary>物理分辨率读取：API 30+ 用 WindowMetrics，低版本回退 GetRealSize。</summary>
internal static class AndroidDisplaySize
{
    public static bool TryGetPixels(Context? ctx, out int width, out int height)
    {
        width = height = 0;
        if (ctx == null) return false;
        try
        {
            if (OperatingSystem.IsAndroidVersionAtLeast(30))
            {
                var wm = ctx.GetSystemService(Context.WindowService) as IWindowManager;
                var bounds = wm?.CurrentWindowMetrics?.Bounds;
                if (bounds != null)
                {
                    width = bounds.Width();
                    height = bounds.Height();
                    if (width > 0 && height > 0)
                        return true;
                }

                var display = ctx.Display;
                if (TryGetRealSizeLegacy(display, out width, out height))
                    return true;
            }

            var legacyWm = ctx.GetSystemService(Context.WindowService) as IWindowManager;
            return TryGetRealSizeLegacy(legacyWm?.DefaultDisplay, out width, out height);
        }
        catch
        {
            return false;
        }
    }

    public static bool TryGetPixels(Display? display, out int width, out int height)
        => TryGetRealSizeLegacy(display, out width, out height);

    private static bool TryGetRealSizeLegacy(Display? display, out int width, out int height)
    {
        width = height = 0;
        if (display == null) return false;
#pragma warning disable CA1422 // GetRealSize：API31+ 弃用，仅作回退
        var size = new AgPoint();
        display.GetRealSize(size);
        width = size.X;
        height = size.Y;
#pragma warning restore CA1422
        return width > 0 && height > 0;
    }
}
#endif
