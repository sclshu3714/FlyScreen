namespace FlyScreen.AppMaui.Services;

/// <summary>
/// 程序编辑器（Monaco）闪烁光标的屏幕像素，供组字浮层锚定。
/// 优先于远程鼠标位置。
/// </summary>
public static class CompositionCaretAnchor
{
    private static readonly object Gate = new();
    private static int _x;
    private static int _y;
    private static bool _valid;
    private static long _updatedAt;

    /// <summary>编辑器页注册：刷新 Monaco 光标坐标。</summary>
    public static Action? RequestRefresh { get; set; }

    public static void Clear()
    {
        lock (Gate)
            _valid = false;
    }

    /// <summary>WebView 内 CSS 像素（相对 WebView 左上）→ 屏幕像素。</summary>
    public static void UpdateFromWebView(double cssX, double cssY, View? webView)
    {
#if ANDROID
        try
        {
            if (webView?.Handler?.PlatformView is not global::Android.Views.View native)
                return;
            var loc = new int[2];
            native.GetLocationOnScreen(loc);
            var density = native.Resources?.DisplayMetrics?.Density ?? 2f;
            if (density <= 0) density = 2f;
            lock (Gate)
            {
                _x = loc[0] + (int)Math.Round(cssX * density);
                _y = loc[1] + (int)Math.Round(cssY * density);
                _valid = true;
                _updatedAt = Environment.TickCount64;
            }
        }
        catch
        {
            /* ignore */
        }
#else
        _ = cssX;
        _ = cssY;
        _ = webView;
#endif
    }

    public static bool TryGetScreenPixels(out int x, out int y)
    {
        lock (Gate)
        {
            // 超过 8 秒未更新视为过期（页面已关）
            if (!_valid || Environment.TickCount64 - _updatedAt > 8000)
            {
                x = y = 0;
                return false;
            }
            x = _x;
            y = _y;
            return true;
        }
    }
}
