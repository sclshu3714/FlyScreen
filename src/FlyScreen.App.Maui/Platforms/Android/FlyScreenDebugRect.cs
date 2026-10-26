#if ANDROID
using Android.Content;
using Android.Graphics;
using Android.OS;
using Android.Util;
using Android.Views;
using FlyScreen.Services;
using Color = Android.Graphics.Color;
using Paint = Android.Graphics.Paint;
using View = Android.Views.View;

namespace FlyScreen.AppMaui.Platforms.Android;

/// <summary>无障碍 Overlay 上画调试矩形（供 <c>Fly.DrawRect</c>）。</summary>
public sealed partial class FlyScreenInputAccessibilityService
{
    private readonly object _debugRectGate = new();
    private int _debugRectSeq;
    private View? _debugRectView;

    /// <summary>
    /// 在屏幕上描边矩形，持续 <paramref name="durationMs"/> 后自动移除。
    /// 坐标为屏幕像素；ARGB 为 0xAARRGGBB（如红色 0xFFFF0000）。
    /// 返回值表示浮层是否成功加上（同步等待主线程加窗结果）。
    /// </summary>
    public bool TryDrawRect(int left, int top, int right, int bottom, uint argb, int durationMs)
    {
        var l = Math.Min(left, right);
        var t = Math.Min(top, bottom);
        var r = Math.Max(left, right);
        var b = Math.Max(top, bottom);
        if (r <= l || b <= t)
            return false;

        var ms = Math.Clamp(durationMs <= 0 ? 1200 : durationMs, 50, 60_000);
        var a = (byte)((argb >> 24) & 0xFF);
        var rr = (byte)((argb >> 16) & 0xFF);
        var g = (byte)((argb >> 8) & 0xFF);
        var bl = (byte)(argb & 0xFF);
        if (a == 0) a = 0xFF;
        var color = Color.Argb(a, rr, g, bl);

        int id;
        lock (_debugRectGate)
            id = ++_debugRectSeq;

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        void Show()
        {
            try
            {
                _wm ??= GetSystemService(WindowService) as IWindowManager;
                if (_wm == null)
                {
                    AppLog.Warn("DrawRect 失败：无 WindowManager");
                    tcs.TrySetResult(false);
                    return;
                }

                RefreshScreenSize();
                var density = Resources?.DisplayMetrics?.Density ?? 2f;
                // 很粗的描边 + 半透明填充，深色视频上也能看见
                var stroke = Math.Max(8f, density * 5f);

                RemoveDebugRectView_NoPost();

                var view = new DebugRectView(this, l, t, r, b, color, rr, g, bl, stroke);
                var lp = new WindowManagerLayoutParams(
                    Math.Max(_screenW, 1),
                    Math.Max(_screenH, 1),
                    WindowManagerTypes.AccessibilityOverlay,
                    WindowManagerFlags.NotFocusable
                    | WindowManagerFlags.NotTouchable
                    | WindowManagerFlags.NotTouchModal
                    | WindowManagerFlags.LayoutInScreen
                    | WindowManagerFlags.LayoutNoLimits
                    | WindowManagerFlags.LayoutInsetDecor,
                    Format.Translucent)
                {
                    Gravity = GravityFlags.Top | GravityFlags.Left,
                    X = 0,
                    Y = 0,
                    Width = Math.Max(_screenW, 1),
                    Height = Math.Max(_screenH, 1),
                    Alpha = 1f,
                    Title = "FlyScreen.DebugRect"
                };
                if (OperatingSystem.IsAndroidVersionAtLeast(28))
                    lp.LayoutInDisplayCutoutMode = LayoutInDisplayCutoutMode.ShortEdges;

                if (!TryAddDebugRect(view, lp))
                {
                    Log.Warn("FlyScreen", "DrawRect: add overlay failed");
                    try
                    {
                        AppLog.Warn(
                            "DrawRect 加窗失败。请开启：设置→应用→玄鸟→「显示在其他应用上层」，并确认无障碍已开");
                    }
                    catch { /* ignore */ }
                    try { view.Dispose(); } catch { /* ignore */ }
                    tcs.TrySetResult(false);
                    return;
                }

                lock (_debugRectGate)
                    _debugRectView = view;

                var seq = id;
                _ui.PostDelayed(() =>
                {
                    lock (_debugRectGate)
                    {
                        if (seq != _debugRectSeq)
                            return;
                        if (!ReferenceEquals(_debugRectView, view))
                            return;
                        _debugRectView = null;
                    }
                    try { _wm?.RemoveView(view); } catch { /* ignore */ }
                    try { view.Dispose(); } catch { /* ignore */ }
                }, ms);

                AppLog.Info($"DrawRect ok {_screenW}x{_screenH} ({l},{t})-({r},{b}) {ms}ms type={_overlayType}");
                tcs.TrySetResult(true);
            }
            catch (Exception ex)
            {
                Log.Warn("FlyScreen", "DrawRect failed: " + ex.Message);
                try { AppLog.Warn("DrawRect 异常: " + ex.Message); } catch { /* ignore */ }
                tcs.TrySetResult(false);
            }
        }

        if (Looper.MainLooper != null && Looper.MyLooper() == Looper.MainLooper)
            Show();
        else
        {
            _ui.Post(Show);
            if (!tcs.Task.Wait(TimeSpan.FromSeconds(2)))
                return false;
        }

        return tcs.Task.IsCompletedSuccessfully && tcs.Task.Result;
    }

    private void RemoveDebugRectView_NoPost()
    {
        View? old;
        lock (_debugRectGate)
        {
            old = _debugRectView;
            _debugRectView = null;
        }
        if (old == null) return;
        try { _wm?.RemoveView(old); } catch { /* ignore */ }
        try { old.Dispose(); } catch { /* ignore */ }
    }

    private bool TryAddDebugRect(View view, WindowManagerLayoutParams lp)
    {
        if (_wm == null) return false;

        // AccessibilityOverlay 通常能盖过全屏播放器；再试 ApplicationOverlay / SystemAlert
        var types = new List<WindowManagerTypes>
        {
            WindowManagerTypes.AccessibilityOverlay
        };
        if (OperatingSystem.IsAndroidVersionAtLeast(26)
            && global::Android.Provider.Settings.CanDrawOverlays(this))
            types.Add(WindowManagerTypes.ApplicationOverlay);
        types.Add(WindowManagerTypes.SystemAlert);

        foreach (var type in types)
        {
            lp.Type = type;
            try
            {
                _wm.AddView(view, lp);
                _overlayType = type;
                try { view.BringToFront(); } catch { /* ignore */ }
                Log.Info("FlyScreen", $"DrawRect ok type={type} {lp.Width}x{lp.Height}");
                return true;
            }
            catch (Exception ex)
            {
                Log.Warn("FlyScreen", $"DrawRect add {type}: {ex.Message}");
                try { _wm.RemoveView(view); } catch { /* 未加上 */ }
            }
        }

        return false;
    }

    private sealed class DebugRectView : View
    {
        private readonly Paint _stroke;
        private readonly Paint _fill;
        private readonly Paint _corner;
        private readonly global::Android.Graphics.RectF _rect;
        private readonly float _strokeWidth;

        public DebugRectView(Context context, int left, int top, int right, int bottom, Color color, byte red, byte green, byte blue, float strokeWidth)
            : base(context)
        {
            _rect = new global::Android.Graphics.RectF(left, top, right, bottom);
            _strokeWidth = strokeWidth;
            _stroke = new Paint(PaintFlags.AntiAlias)
            {
                Color = color,
                StrokeWidth = strokeWidth
            };
            _stroke.SetStyle(Paint.Style.Stroke);
            // 更醒目的半透明填充
            _fill = new Paint(PaintFlags.AntiAlias)
            {
                Color = Color.Argb(0x66, red, green, blue)
            };
            _fill.SetStyle(Paint.Style.Fill);
            // 四角亮黄，深色视频上对比更强
            _corner = new Paint(PaintFlags.AntiAlias)
            {
                Color = Color.Argb(0xFF, 0xFF, 0xE0, 0x00),
                StrokeWidth = strokeWidth + 2f
            };
            _corner.SetStyle(Paint.Style.Stroke);
            SetWillNotDraw(false);
            ImportantForAccessibility = ImportantForAccessibility.No;
            SetLayerType(LayerType.Software, null);
            try { Elevation = 64f; } catch { /* ignore */ }
        }

        protected override void OnDraw(Canvas canvas)
        {
            base.OnDraw(canvas);
            canvas.DrawRect(_rect, _fill);
            canvas.DrawRect(_rect, _stroke);

            // 四角 L 形标记，即使中部被盖住也能看见区域角点
            var len = Math.Max(28f, _strokeWidth * 6f);
            float l = _rect.Left, t = _rect.Top, r = _rect.Right, b = _rect.Bottom;
            canvas.DrawLine(l, t, l + len, t, _corner);
            canvas.DrawLine(l, t, l, t + len, _corner);
            canvas.DrawLine(r, t, r - len, t, _corner);
            canvas.DrawLine(r, t, r, t + len, _corner);
            canvas.DrawLine(l, b, l + len, b, _corner);
            canvas.DrawLine(l, b, l, b - len, _corner);
            canvas.DrawLine(r, b, r - len, b, _corner);
            canvas.DrawLine(r, b, r, b - len, _corner);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _stroke.Dispose();
                _fill.Dispose();
                _corner.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
#endif
