namespace FlyScreen.Services.Programs;

/// <summary>
/// 用户程序脚本 API（C# / JS 共用语义）。
/// 手机端需开启「玄鸟键鼠注入」无障碍后，<see cref="Ready"/> 为 true。
/// </summary>
public static class Fly
{
    public static bool Ready
        => ProgramDevice.Current?.IsReady == true;

    public static string? App
        => ProgramDevice.Current?.ForegroundPackage;

    public static int ScreenWidth
        => ProgramDevice.Current?.ScreenSize.Width ?? 0;

    public static int ScreenHeight
        => ProgramDevice.Current?.ScreenSize.Height ?? 0;

    public static bool HasText(string text)
        => Require()?.ContainsText(text) == true;

    public static string[] Texts(int max = 80)
        => Require()?.CollectTexts(max).ToArray() ?? [];

    public static bool TapText(string text)
        => Require()?.TapText(text) == true;

    public static bool Tap(double nx, double ny)
        => Require()?.Tap(nx, ny) == true;

    public static bool Swipe(double x1, double y1, double x2, double y2, int durationMs = 320)
        => Require()?.Swipe(x1, y1, x2, y2, durationMs) == true;

    public static bool SwipeUp(double distance = 0.45, int durationMs = 320)
        => Require()?.SwipeUp(distance, durationMs) == true;

    public static bool SwipeDown(double distance = 0.45, int durationMs = 320)
        => Require()?.SwipeDown(distance, durationMs) == true;

    public static bool Back()
        => Require()?.Back() == true;

    public static bool Home()
        => Require()?.Home() == true;

    public static void OpenSettings()
        => ProgramDevice.Current?.OpenAccessibilitySettings();

    private static IProgramDeviceControl? Require()
    {
        var d = ProgramDevice.Current;
        if (d == null)
        {
            AppLog.Warn("Fly：设备控制仅在手机 Android 可用；请在手机玄鸟上运行本程序（电脑端无界面注入）");
            return null;
        }
        if (!d.IsReady)
        {
            AppLog.Warn("Fly：请先开启「玄鸟键鼠注入」无障碍（高级 → 行为）");
            return null;
        }
        return d;
    }
}

/// <summary>供 Jint 绑定的实例桥（静态类无法直接 SetValue）。</summary>
public sealed class FlyScriptBridge
{
    public bool ready() => Fly.Ready;
    public string? app() => Fly.App;
    public int screenWidth() => Fly.ScreenWidth;
    public int screenHeight() => Fly.ScreenHeight;
    public bool hasText(string text) => Fly.HasText(text);
    public string[] texts(int max = 80) => Fly.Texts(max);
    public bool tapText(string text) => Fly.TapText(text);
    public bool tap(double nx, double ny) => Fly.Tap(nx, ny);
    public bool swipe(double x1, double y1, double x2, double y2, int durationMs = 320)
        => Fly.Swipe(x1, y1, x2, y2, durationMs);
    public bool swipeUp(double distance = 0.45, int durationMs = 320)
        => Fly.SwipeUp(distance, durationMs);
    public bool swipeDown(double distance = 0.45, int durationMs = 320)
        => Fly.SwipeDown(distance, durationMs);
    public bool back() => Fly.Back();
    public bool home() => Fly.Home();
    public void openSettings() => Fly.OpenSettings();
}
