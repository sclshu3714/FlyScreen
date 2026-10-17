#if ANDROID
using Android.Content;
using Android.Graphics;
using Android.OS;
using Android.Views;
using Path = Android.Graphics.Path;
using View = Android.Views.View;

namespace FlyScreen.AppMaui.Platforms.Android;

/// <summary>
/// 电脑鼠标穿入本机时的指针。使用「显示在其他应用上层」，不依赖无障碍服务。
/// 箭头是一块小窗，跟着坐标移动，玄鸟退到后台后仍盖在其他应用上。
/// </summary>
public static class FlyScreenCursorOverlay
{
    private static readonly object Gate = new();
    private static bool _scheduled;
    private static bool _wantVisible;
    private static double _nx = 0.5;
    private static double _ny = 0.5;

    private static IWindowManager? _wm;
    private static PointerView? _cursor;
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
        _lp.X = 0;
        _lp.Y = 0;
        _lp.Width = Math.Max(_screenW, 1);
        _lp.Height = Math.Max(_screenH, 1);
        _cursor.SetPoint((float)nx, (float)ny, _screenW, _screenH);
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
            _cursor.Invalidate();
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
        _cursor = new PointerView(ctx, density);

        var type = WindowManagerTypes.SystemAlert;
        if (OperatingSystem.IsAndroidVersionAtLeast(26))
            type = WindowManagerTypes.ApplicationOverlay;

        _lp = new WindowManagerLayoutParams(
            ViewGroup.LayoutParams.MatchParent,
            ViewGroup.LayoutParams.MatchParent,
            type,
            WindowManagerFlags.NotFocusable
            | WindowManagerFlags.NotTouchable
            | WindowManagerFlags.NotTouchModal
            | WindowManagerFlags.LayoutInScreen,
            Format.Translucent)
        {
            Gravity = GravityFlags.Top | GravityFlags.Left,
            Alpha = 1f
        };
        if (OperatingSystem.IsAndroidVersionAtLeast(28))
            _lp.LayoutInDisplayCutoutMode = LayoutInDisplayCutoutMode.ShortEdges;
    }

    private sealed class PointerView : View
    {
        private readonly float _density;
        private readonly global::Android.Graphics.Paint _fill;
        private readonly global::Android.Graphics.Paint _stroke;
        private readonly Path _arrow;

        private float _nx = 0.5f;
        private float _ny = 0.5f;
        private int _screenW = 1080;
        private int _screenH = 1920;

        public PointerView(Context context, float density) : base(context)
        {
            _density = density <= 0 ? 2f : density;
            SetWillNotDraw(false);
            SetLayerType(LayerType.Software, null);
            SetBackgroundColor(global::Android.Graphics.Color.Transparent);
            _fill = new global::Android.Graphics.Paint(PaintFlags.AntiAlias);
            _fill.Color = global::Android.Graphics.Color.White;
            _fill.SetStyle(global::Android.Graphics.Paint.Style.Fill);
            _stroke = new global::Android.Graphics.Paint(PaintFlags.AntiAlias);
            _stroke.Color = global::Android.Graphics.Color.Black;
            _stroke.StrokeWidth = 0.55f;
            _stroke.StrokeJoin = global::Android.Graphics.Paint.Join.Round;
            _stroke.SetStyle(global::Android.Graphics.Paint.Style.Stroke);
            _arrow = new Path();
            _arrow.MoveTo(1, 1);
            _arrow.LineTo(1, 26);
            _arrow.LineTo(7, 20);
            _arrow.LineTo(12, 30);
            _arrow.LineTo(16, 28);
            _arrow.LineTo(11, 18);
            _arrow.LineTo(18, 18);
            _arrow.Close();
        }

        public void SetPoint(float nx, float ny, int screenW, int screenH)
        {
            _nx = nx;
            _ny = ny;
            _screenW = Math.Max(screenW, 1);
            _screenH = Math.Max(screenH, 1);
        }

        protected override void OnDraw(Canvas canvas)
        {
            var w = Width > 1 ? Width : _screenW;
            var h = Height > 1 ? Height : _screenH;
            canvas.Save();
            canvas.Translate(_nx * (w - 1), _ny * (h - 1));
            canvas.Scale(_density, _density);
            canvas.DrawPath(_arrow, _fill);
            canvas.DrawPath(_arrow, _stroke);
            canvas.Restore();
        }
    }

    private static void RefreshScreen()
    {
        try
        {
            var size = new global::Android.Graphics.Point();
            _wm?.DefaultDisplay?.GetRealSize(size);
            if (size.X > 0 && size.Y > 0)
            {
                _screenW = size.X;
                _screenH = size.Y;
                return;
            }
        }
        catch
        {
            /* 旧接口失败时用 DisplayMetrics */
        }

        var dm = global::Android.App.Application.Context?.Resources?.DisplayMetrics;
        if (dm == null) return;
        _screenW = dm.WidthPixels;
        _screenH = dm.HeightPixels;
    }
}
#endif
