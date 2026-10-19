#if ANDROID
using Android.Util;
using FlyScreen.Services;

namespace FlyScreen.AppMaui.Platforms.Android;

/// <summary>
/// 系统级远程指针 API。实际加窗在 <see cref="FlyScreenCursorOverlayService"/> 前台服务中，
/// 避免绑 Activity / DecorView 导致退后台看不见。
/// </summary>
public static class FlyScreenCursorOverlay
{
    public static bool CanDraw()
    {
        var ctx = global::Android.App.Application.Context;
        return ctx != null && global::Android.Provider.Settings.CanDrawOverlays(ctx);
    }

    public static bool IsShowing => FlyScreenCursorOverlayService.IsShowing;

    public static string? LastError => FlyScreenCursorOverlayService.LastError;

    public static void ShowAt(double nx, double ny)
    {
        if (!CanDraw())
        {
            Log.Warn("FlyScreen", "cursor overlay: no SYSTEM_ALERT_WINDOW");
            AppLog.Warn("无悬浮窗权限，无法显示远程指针");
            return;
        }

        FlyScreenCursorOverlayService.RequestShow(nx, ny);
    }

    public static void MoveAbsolute(double nx, double ny)
        => FlyScreenCursorOverlayService.RequestMoveAbsolute(nx, ny);

    public static void MoveRelative(int dx, int dy)
        => FlyScreenCursorOverlayService.RequestMoveRelative(dx, dy);

    public static void End()
        => FlyScreenCursorOverlayService.RequestEnd();
}
#endif
