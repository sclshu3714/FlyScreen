#if ANDROID
using FlyScreen.Services.Programs;

namespace FlyScreen.AppMaui.Platforms.Android;

/// <summary>Android：把无障碍能力挂到用户程序 <see cref="Fly"/> API。</summary>
public sealed class AndroidProgramDeviceControl : IProgramDeviceControl
{
    public static AndroidProgramDeviceControl Instance { get; } = new();

    public bool IsReady => FlyScreenInputAccessibilityService.IsEnabled;

    public string? ForegroundPackage
        => FlyScreenInputAccessibilityService.Instance?.TryGetForegroundPackage();

    public (int Width, int Height) ScreenSize
    {
        get
        {
            var svc = FlyScreenInputAccessibilityService.Instance;
            if (svc == null) return (0, 0);
            return svc.GetScreenPixels();
        }
    }

    public bool ContainsText(string text)
        => FlyScreenInputAccessibilityService.Instance?.ContainsVisibleText(text) == true;

    public IReadOnlyList<string> CollectTexts(int max = 80)
        => FlyScreenInputAccessibilityService.Instance?.CollectVisibleTexts(max)
           ?? (IReadOnlyList<string>)Array.Empty<string>();

    public string OcrRegion(int left, int top, int right, int bottom)
        => FlyScreenInputAccessibilityService.Instance?.TryOcrRegion(left, top, right, bottom) ?? "";

    public bool TapText(string text)
        => FlyScreenInputAccessibilityService.Instance?.TryTapText(text) == true;

    public bool Tap(double nx, double ny)
        => FlyScreenInputAccessibilityService.Instance?.TryTapNormalized(nx, ny) == true;

    public bool Swipe(double x1, double y1, double x2, double y2, int durationMs = 320)
        => FlyScreenInputAccessibilityService.Instance?.TrySwipeNormalized(x1, y1, x2, y2, durationMs) == true;

    public bool SwipeUp(double distance = 0.45, int durationMs = 320)
    {
        var d = Math.Clamp(distance, 0.1, 0.9);
        var (w, h) = ScreenSize;
        // 横屏竖直行程短，加大相对距离并略偏左，避开右侧系统手势区
        var landscape = w > h && h > 0;
        var mid = landscape ? 0.42 : 0.5;
        if (landscape)
            d = Math.Clamp(Math.Max(d, 0.58), 0.1, 0.85);
        var y1 = Math.Clamp(0.5 + d / 2, 0.08, 0.92);
        var y2 = Math.Clamp(0.5 - d / 2, 0.08, 0.92);
        return Swipe(mid, y1, mid, y2, durationMs);
    }

    public bool SwipeDown(double distance = 0.45, int durationMs = 320)
    {
        var d = Math.Clamp(distance, 0.1, 0.9);
        var mid = 0.5;
        var y1 = Math.Clamp(0.5 - d / 2, 0.05, 0.95);
        var y2 = Math.Clamp(0.5 + d / 2, 0.05, 0.95);
        return Swipe(mid, y1, mid, y2, durationMs);
    }

    public bool Back()
        => FlyScreenInputAccessibilityService.Instance?.TryGlobalBack() == true;

    public bool Home()
        => FlyScreenInputAccessibilityService.Instance?.TryGlobalHome() == true;

    public void OpenAccessibilitySettings()
        => FlyScreenInputAccessibilityService.OpenAccessibilitySettings();
}
#endif
