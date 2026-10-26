#if ANDROID
using FlyScreen.Services.Programs;

namespace FlyScreen.AppMaui.Platforms.Android;

/// <summary>Android：把无障碍能力挂到用户程序 <see cref="Fly"/> API。</summary>
public sealed class AndroidProgramDeviceControl : IProgramDeviceControl
{
    public static AndroidProgramDeviceControl Instance { get; } = new();

    private static FlyScreenInputAccessibilityService? Svc
        => FlyScreenInputAccessibilityService.Instance;

    public bool IsReady => FlyScreenInputAccessibilityService.IsReady;

    public string? ForegroundPackage
        => Svc?.TryGetForegroundPackage();

    public (int Width, int Height) ScreenSize
    {
        get
        {
            if (Svc == null) return (0, 0);
            return Svc.GetScreenPixels();
        }
    }

    public bool ContainsText(string text)
        => Svc?.ContainsVisibleText(text) == true;

    public bool ContainsViewId(string viewId)
        => Svc?.ContainsViewId(viewId) == true;

    public bool ContainsDesc(string desc)
        => Svc?.ContainsDesc(desc) == true;

    public IReadOnlyList<string> CollectTexts(int max = 80)
        => Svc?.CollectVisibleTexts(max) ?? (IReadOnlyList<string>)Array.Empty<string>();

    public int[]? FindTextBounds(string text)
        => Svc?.FindTextBounds(text);

    public string OcrRegion(int left, int top, int right, int bottom)
        => Svc?.TryOcrRegion(left, top, right, bottom) ?? "";

    public bool DrawRect(int left, int top, int right, int bottom, uint argb, int durationMs)
        => Svc?.TryDrawRect(left, top, right, bottom, argb, durationMs) == true;

    public bool TapText(string text)
        => Svc?.TryTapText(text) == true;

    public bool LongPressText(string text, int durationMs = 700)
        => Svc?.TryLongPressText(text, durationMs) == true;

    public bool TapViewId(string viewId)
        => Svc?.TryTapViewId(viewId) == true;

    public bool TapDesc(string desc)
        => Svc?.TryTapDesc(desc) == true;

    public bool Tap(double nx, double ny)
        => Svc?.TryTapNormalized(nx, ny) == true;

    public bool TapPx(int x, int y)
        => Svc?.TryTapPx(x, y) == true;

    public bool DoubleTap(double nx, double ny)
        => Svc?.TryDoubleTapNormalized(nx, ny) == true;

    public bool LongPress(double nx, double ny, int durationMs = 700)
        => Svc?.TryLongPressNormalized(nx, ny, durationMs) == true;

    public bool Swipe(double x1, double y1, double x2, double y2, int durationMs = 320)
        => Svc?.TrySwipeNormalized(x1, y1, x2, y2, durationMs) == true;

    public bool SwipeUp(double distance = 0.45, int durationMs = 320)
    {
        var d = Math.Clamp(distance, 0.1, 0.9);
        var (w, h) = ScreenSize;
        var landscape = w > h && h > 0;
        // 偏左中轴，避开短剧/漫剧右下「查看详情」广告热区
        var mid = landscape ? 0.30 : 0.34;
        if (landscape)
            d = Math.Clamp(Math.Max(d, 0.58), 0.1, 0.85);
        // 起点略偏上，减少擦到底部广告条
        var y1 = Math.Clamp(0.48 + d / 2, 0.08, 0.78);
        var y2 = Math.Clamp(0.48 - d / 2, 0.08, 0.92);
        return Swipe(mid, y1, mid, y2, durationMs);
    }

    public bool SwipeDown(double distance = 0.45, int durationMs = 320)
    {
        var d = Math.Clamp(distance, 0.1, 0.9);
        var y1 = Math.Clamp(0.5 - d / 2, 0.05, 0.95);
        var y2 = Math.Clamp(0.5 + d / 2, 0.05, 0.95);
        return Swipe(0.5, y1, 0.5, y2, durationMs);
    }

    public bool SwipeLeft(double distance = 0.45, int durationMs = 320)
    {
        var d = Math.Clamp(distance, 0.1, 0.9);
        var x1 = Math.Clamp(0.5 + d / 2, 0.05, 0.95);
        var x2 = Math.Clamp(0.5 - d / 2, 0.05, 0.95);
        return Swipe(x1, 0.5, x2, 0.5, durationMs);
    }

    public bool SwipeRight(double distance = 0.45, int durationMs = 320)
    {
        var d = Math.Clamp(distance, 0.1, 0.9);
        var x1 = Math.Clamp(0.5 - d / 2, 0.05, 0.95);
        var x2 = Math.Clamp(0.5 + d / 2, 0.05, 0.95);
        return Swipe(x1, 0.5, x2, 0.5, durationMs);
    }

    public bool ScrollForward()
        => Svc?.TryScrollForward() == true;

    public bool ScrollBackward()
        => Svc?.TryScrollBackward() == true;

    public bool WaitText(string text, int timeoutMs = 8000, int pollMs = 250)
        => Svc?.WaitForText(text, timeoutMs, pollMs) == true;

    public bool WaitGone(string text, int timeoutMs = 8000, int pollMs = 250)
        => Svc?.WaitTextGone(text, timeoutMs, pollMs) == true;

    public bool Input(string text)
        => Svc?.TryInputText(text) == true;

    public bool ClearInput()
        => Svc?.TryClearInput() == true;

    public bool Enter()
        => Svc?.TryEnter() == true;

    public bool Back()
        => Svc?.TryGlobalBack() == true;

    public bool Home()
        => Svc?.TryGlobalHome() == true;

    public bool Recents()
        => Svc?.TryGlobalRecents() == true;

    public bool Notifications()
        => Svc?.TryGlobalNotifications() == true;

    public bool QuickSettings()
        => Svc?.TryGlobalQuickSettings() == true;

    public bool LockScreen()
        => Svc?.TryGlobalLockScreen() == true;

    public bool TakeScreenshot()
        => Svc?.TryGlobalTakeScreenshot() == true;

    public void OpenAccessibilitySettings()
        => FlyScreenInputAccessibilityService.OpenAccessibilitySettings();
}
#endif
