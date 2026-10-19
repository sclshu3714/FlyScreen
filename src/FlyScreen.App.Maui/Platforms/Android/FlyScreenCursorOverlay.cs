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

    /// <summary>
    /// 悬浮指针箭头尖的真实屏幕像素（GetLocationOnScreen）。
    /// 点击/拖拽必须用此坐标，避免 WindowManager 布局原点与无障碍手势坐标系不一致导致上下偏移。
    /// </summary>
    public static bool TryGetHotspotPixels(out int x, out int y)
        => FlyScreenCursorOverlayService.TryGetHotspotPixels(out x, out y);

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
