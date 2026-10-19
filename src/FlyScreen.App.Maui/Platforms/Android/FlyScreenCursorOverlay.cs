#if ANDROID
using Android.Content;
using Android.Graphics;
using Android.OS;
using Android.Util;
using Android.Views;
using FlyScreen.Services;
using Path = Android.Graphics.Path;
using View = Android.Views.View;

namespace FlyScreen.AppMaui.Platforms.Android;

/// <summary>
/// 电脑鼠标穿入本机时的指针：一块小悬浮窗跟着坐标移动。
/// 全屏透明层在部分国产 ROM 上会加窗成功却不绘制；小窗更稳。
/// </summary>
public static class FlyScreenCursorOverlay
{
    private const float CursorDp = 36f;

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
    private static int _cursorPx = 72;
    private static bool _added;
    private static string? _lastError;

    public static bool CanDraw()
    {
        var ctx = global::Android.App.Application.Context;
        return ctx != null && global::Android.Provider.Settings.CanDrawOverlays(ctx);
    }

    /// <summary>窗体已真正加到 WindowManager。</summary>
    public static bool IsShowing
    {
        get { lock (Gate) return _added && _wantVisible; }
    }

    public static string? LastError => _lastError;

    /// <summary>请求显示。返回值表示本帧已成功加窗（或已在显示）。</summary>
    public static bool TryBegin(double nx, double ny)
    {
        if (!CanDraw())
        {
            _lastError = "无悬浮窗权限";
            return false;
        }

        lock (Gate)
        {
            _wantVisible = true;
            _nx = Math.Clamp(nx, 0, 1);
            _ny = Math.Clamp(ny, 0, 1);
        }

        return FlushNow();
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
            var w = Math.Max(_screenW, 1);
            var h = Math.Max(_screenH, 1);
            _nx = Math.Clamp(_nx + dx / (double)w, 0, 1);
            _ny = Math.Clamp(_ny + dy / (double)h, 0, 1);
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

        if (MainThread.IsMainThread)
        {
            Flush();
            return;
        }

        MainThread.BeginInvokeOnMainThread(() => Flush());
    }

    /// <summary>在主线程同步刷新；非主线程则切过去并等待结果。</summary>
    private static bool FlushNow()
    {
        if (MainThread.IsMainThread)
            return Flush();

        var tcs = new TaskCompletionSource<bool>();
        MainThread.BeginInvokeOnMainThread(() =>
        {
            try { tcs.TrySetResult(Flush()); }
            catch (Exception ex) { tcs.TrySetException(ex); }
        });
        try
        {
            return tcs.Task.GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
            AppLog.Warn($"悬浮指针 FlushNow: {ex.Message}");
            return false;
        }
    }

    private static bool Flush()
    {
        lock (Gate)
            _scheduled = false;

        bool want;
        double nx, ny;
        lock (Gate)
        {
            want = _wantVisible;
            nx = _nx;
            ny = _ny;
        }

        if (!want)
        {
            Hide();
            return false;
        }

        if (!CanDraw())
        {
            Hide();
            _lastError = "无悬浮窗权限";
            return false;
        }

        if (!EnsureView())
            return false;

        RefreshScreen();
        PlaceWindow(nx, ny);

        try
        {
            if (!_added)
            {
                _wm!.AddView(_cursor, _lp);
                _added = true;
                _lastError = null;
                Log.Info("FlyScreen", $"cursor overlay added {_cursorPx}px @ ({_lp!.X},{_lp.Y})");
                AppLog.Info("悬浮指针已显示（其他应用上层）");
            }
            else
            {
                _wm!.UpdateViewLayout(_cursor, _lp);
            }

            _cursor!.Invalidate();
            return true;
        }
        catch (Exception ex)
        {
            _added = false;
            _lastError = ex.Message;
            Log.Warn("FlyScreen", "cursor overlay add/update failed: " + ex.Message);
            AppLog.Warn($"悬浮指针加窗失败: {ex.Message}");
            return false;
        }
    }

    private static void PlaceWindow(double nx, double ny)
    {
        var size = _cursorPx;
        var x = (int)Math.Round(nx * Math.Max(_screenW - 1, 1));
        var y = (int)Math.Round(ny * Math.Max(_screenH - 1, 1));
        // 箭头尖在左上角
        _lp!.X = Math.Clamp(x, 0, Math.Max(_screenW - size, 0));
        _lp.Y = Math.Clamp(y, 0, Math.Max(_screenH - size, 0));
        _lp.Width = size;
        _lp.Height = size;
    }

    private static void Hide()
    {
        if (!_added || _wm == null || _cursor == null) return;
        try { _wm.RemoveView(_cursor); } catch { /* ignore */ }
        _added = false;
    }

    private static bool EnsureView()
    {
        if (_cursor != null && _wm != null && _lp != null) return true;
        var ctx = global::Android.App.Application.Context;
        if (ctx == null)
        {
            _lastError = "Application.Context 为空";
            return false;
        }

        _wm = ctx.GetSystemService(Context.WindowService) as IWindowManager;
        if (_wm == null)
        {
            _lastError = "WindowManager 为空";
            return false;
        }

        var density = ctx.Resources?.DisplayMetrics?.Density ?? 2f;
        _cursorPx = Math.Max(48, (int)(CursorDp * density));
        _cursor = new PointerView(ctx, density);

        var type = OperatingSystem.IsAndroidVersionAtLeast(26)
            ? WindowManagerTypes.ApplicationOverlay
            : WindowManagerTypes.SystemAlert;

        _lp = new WindowManagerLayoutParams(
            _cursorPx,
            _cursorPx,
            type,
            // 不加 LayoutNoLimits：小米等 ROM 会裁掉带该标志的小窗
            WindowManagerFlags.NotFocusable
            | WindowManagerFlags.NotTouchable
            | WindowManagerFlags.NotTouchModal
            | WindowManagerFlags.LayoutInScreen,
            Format.Translucent)
        {
            Gravity = GravityFlags.Top | GravityFlags.Left,
            X = 0,
            Y = 0,
            Alpha = 1f,
            Title = "FlyScreenCursor"
        };
        if (OperatingSystem.IsAndroidVersionAtLeast(28))
            _lp.LayoutInDisplayCutoutMode = LayoutInDisplayCutoutMode.ShortEdges;

        return true;
    }

    private static void RefreshScreen()
    {
        try
        {
            var size = new global::Android.Graphics.Point();
            if (OperatingSystem.IsAndroidVersionAtLeast(30))
            {
                var bounds = _wm?.CurrentWindowMetrics?.Bounds;
                if (bounds != null && bounds.Width() > 0 && bounds.Height() > 0)
                {
                    _screenW = bounds.Width();
                    _screenH = bounds.Height();
                    return;
                }
            }

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
            /* fall through */
        }

        var dm = global::Android.App.Application.Context?.Resources?.DisplayMetrics;
        if (dm == null) return;
        _screenW = dm.WidthPixels;
        _screenH = dm.HeightPixels;
    }

    private sealed class PointerView : View
    {
        private readonly float _density;
        private readonly global::Android.Graphics.Paint _fill;
        private readonly global::Android.Graphics.Paint _stroke;
        private readonly global::Android.Graphics.Paint _glow;
        private readonly Path _arrow;

        public PointerView(Context context, float density) : base(context)
        {
            _density = density <= 0 ? 2f : density;
            SetWillNotDraw(false);
            // 硬件层在部分机型上对 TYPE_APPLICATION_OVERLAY 会整层不画
            SetLayerType(LayerType.Software, null);
            SetBackgroundColor(global::Android.Graphics.Color.Transparent);

            // 高对比：亮黄填充 + 黑描边，深浅背景下都看得见
            _fill = new global::Android.Graphics.Paint(PaintFlags.AntiAlias)
            {
                Color = global::Android.Graphics.Color.Rgb(255, 214, 10)
            };
            _fill.SetStyle(global::Android.Graphics.Paint.Style.Fill);

            _stroke = new global::Android.Graphics.Paint(PaintFlags.AntiAlias)
            {
                Color = global::Android.Graphics.Color.Black,
                StrokeWidth = 1.8f
            };
            _stroke.StrokeJoin = global::Android.Graphics.Paint.Join.Round;
            _stroke.SetStyle(global::Android.Graphics.Paint.Style.Stroke);

            _glow = new global::Android.Graphics.Paint(PaintFlags.AntiAlias)
            {
                Color = global::Android.Graphics.Color.Argb(90, 0, 0, 0)
            };
            _glow.SetStyle(global::Android.Graphics.Paint.Style.Fill);

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

        protected override void OnDraw(Canvas canvas)
        {
            var scale = Math.Max(Width, Height) / 32f;
            if (scale < 0.5f) scale = _density;
            canvas.Save();
            canvas.Scale(scale, scale);
            canvas.DrawPath(_arrow, _glow);
            canvas.DrawPath(_arrow, _fill);
            canvas.DrawPath(_arrow, _stroke);
            canvas.Restore();
        }
    }
}
#endif
