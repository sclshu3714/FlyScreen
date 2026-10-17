#if ANDROID
using Android.Content;
using Android.Graphics;
using Android.OS;
using Android.Views;
using AndroidX.Core.View;
using View = Android.Views.View;

namespace FlyScreen.AppMaui.Platforms.Android;

/// <summary>
/// 电脑鼠标穿入本机时的指针。使用「显示在其他应用上层」，不依赖无障碍服务。
/// </summary>
public static class FlyScreenCursorOverlay
{
    private static readonly object Gate = new();
    private static bool _scheduled;
    private static bool _wantVisible;
    private static double _nx = 0.5;
    private static double _ny = 0.5;

    private static IWindowManager? _wm;
    private static View? _cursor;
    private static WindowManagerLayoutParams? _lp;
    private static int _screenW = 1080;
    private static int _screenH = 1920;
    private static bool _added;

    public static bool CanDraw()
    {
        var ctx = global::Android.App.Application.Context;
        return ctx != null && global::Android.Provider.Settings.CanDrawOverlays(ctx);
    }

    public static bool IsShowing => _added;

    public static bool TryBegin(double nx, double ny)
    {
        if (!CanDraw()) return false;
        lock (Gate)
        {
            _wantVisible = true;
            _nx = Math.Clamp(nx, 0, 1);
            _ny = Math.Clamp(ny, 0, 1);
        }
        Schedule();
        return true;
    }

    public static void MoveAbsolute(double nx, double ny)
    {
        lock (Gate)
        {
            _wantVisible = true;
            _nx = Math.Clamp(nx, 0, 1);
            _ny = Math.Clamp(ny, 0, 1);
        }
        Schedule();
    }

    public static void MoveRelative(int dx, int dy)
    {
        lock (Gate)
        {
            if (_screenW <= 0 || _screenH <= 0)
            {
                _nx = Math.Clamp(_nx + dx / 1080d, 0, 1);
                _ny = Math.Clamp(_ny + dy / 1920d, 0, 1);
            }
            else
            {
                _nx = Math.Clamp(_nx + dx / (double)_screenW, 0, 1);
                _ny = Math.Clamp(_ny + dy / (double)_screenH, 0, 1);
            }
            _wantVisible = true;
        }
        Schedule();
    }

    public static void End()
    {
        lock (Gate)
            _wantVisible = false;
        Schedule();
    }

    private static void Schedule()
    {
        lock (Gate)
        {
            if (_scheduled) return;
            _scheduled = true;
        }
        MainThread.BeginInvokeOnMainThread(Flush);
    }

    private static void Flush()
    {
        bool want;
        double nx, ny;
        lock (Gate)
        {
            want = _wantVisible;
            nx = _nx;
            ny = _ny;
            _scheduled = false;
        }

        if (!want)
        {
            Hide();
            return;
        }

        if (!CanDraw())
        {
            Hide();
            return;
        }

        EnsureView();
        if (_wm == null || _cursor == null || _lp == null) return;
        RefreshScreen();
        var x = (int)(nx * Math.Max(_screenW - 1, 1));
        var y = (int)(ny * Math.Max(_screenH - 1, 1));
        _lp.X = x - _lp.Width / 2;
        _lp.Y = y - _lp.Height / 2;
        try
        {
            if (!_added)
            {
                _wm.AddView(_cursor, _lp);
                _added = true;
            }
            else
            {
                _wm.UpdateViewLayout(_cursor, _lp);
            }
        }
        catch
        {
            _added = false;
        }
    }

    private static void Hide()
    {
        if (!_added || _wm == null || _cursor == null) return;
        try { _wm.RemoveView(_cursor); } catch { /* ignore */ }
        _added = false;
    }

    private static void EnsureView()
    {
        if (_cursor != null && _wm != null && _lp != null) return;
        var ctx = global::Android.App.Application.Context;
        if (ctx == null) return;
        _wm = ctx.GetSystemService(Context.WindowService) as IWindowManager;
        if (_wm == null) return;

        var density = ctx.Resources?.DisplayMetrics?.Density ?? 2f;
        var size = (int)(18 * density);
        _cursor = new View(ctx);
        _cursor.SetBackgroundColor(global::Android.Graphics.Color.Argb(220, 37, 99, 235));
        ViewCompat.SetElevation(_cursor, 24f);

        var type = WindowManagerTypes.SystemAlert;
        if (OperatingSystem.IsAndroidVersionAtLeast(26))
            type = WindowManagerTypes.ApplicationOverlay;

        _lp = new WindowManagerLayoutParams(
            size, size, type,
            WindowManagerFlags.NotFocusable
            | WindowManagerFlags.NotTouchable
            | WindowManagerFlags.LayoutNoLimits
            | WindowManagerFlags.LayoutInScreen,
            Format.Translucent)
        {
            Gravity = GravityFlags.Top | GravityFlags.Left
        };
    }

    private static void RefreshScreen()
    {
        var dm = global::Android.App.Application.Context?.Resources?.DisplayMetrics;
        if (dm == null) return;
        _screenW = dm.WidthPixels;
        _screenH = dm.HeightPixels;
    }
}
#endif
