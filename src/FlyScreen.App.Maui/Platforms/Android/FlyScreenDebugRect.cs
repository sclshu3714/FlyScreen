#if ANDROID
using Android.Content;
using Android.Graphics;
using Android.OS;
using Android.Util;
using Android.Views;
using Color = Android.Graphics.Color;
using Paint = Android.Graphics.Paint;
using View = Android.Views.View;

namespace FlyScreen.AppMaui.Platforms.Android;

/// <summary>无障碍 Overlay 上画调试矩形（供 <c>Fly.DrawRect</c>）。</summary>
public sealed partial class FlyScreenInputAccessibilityService
{
    private readonly object _debugRectGate = new();
    private int _debugRectSeq;

    /// <summary>
    /// 在屏幕上描边矩形，持续 <paramref name="durationMs"/> 后自动移除。
    /// 坐标为屏幕像素；ARGB 为 0xAARRGGBB（如红色 0xFFFF0000）。
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

        void Show()
        {
            try
            {
                EnsureForeground();
                EnsureCursorOverlay(); // 确保 _wm 就绪
                _wm ??= GetSystemService(WindowService) as IWindowManager;
                if (_wm == null) return;

                RefreshScreenSize();
                var density = Resources?.DisplayMetrics?.Density ?? 2f;
                var stroke = Math.Max(2f, density * 2f);

                var view = new DebugRectView(this, l, t, r, b, color, stroke);
                var lp = new WindowManagerLayoutParams(
                    ViewGroup.LayoutParams.MatchParent,
                    ViewGroup.LayoutParams.MatchParent,
                    _overlayType,
                    WindowManagerFlags.NotFocusable
                    | WindowManagerFlags.NotTouchable
                    | WindowManagerFlags.NotTouchModal
                    | WindowManagerFlags.LayoutInScreen,
                    Format.Translucent)
                {
                    Gravity = GravityFlags.Top | GravityFlags.Left,
                    X = 0,
                    Y = 0,
                    Width = Math.Max(_screenW, 1),
                    Height = Math.Max(_screenH, 1),
                    Alpha = 1f
                };
                if (OperatingSystem.IsAndroidVersionAtLeast(28))
                    lp.LayoutInDisplayCutoutMode = LayoutInDisplayCutoutMode.ShortEdges;

                if (!TryAddDebugRect(view, lp))
                {
                    Log.Warn("FlyScreen", "DrawRect: add overlay failed");
                    return;
                }

                _ui.PostDelayed(() =>
                {
                    try { _wm?.RemoveView(view); }
                    catch { /* ignore */ }
                    try { view.Dispose(); }
                    catch { /* ignore */ }
                }, ms);

                _ = id;
            }
            catch (Exception ex)
            {
                Log.Warn("FlyScreen", "DrawRect failed: " + ex.Message);
            }
        }

        if (Looper.MainLooper != null && Looper.MyLooper() == Looper.MainLooper)
            Show();
        else
            _ui.Post(Show);

        return true;
    }

    private bool TryAddDebugRect(View view, WindowManagerLayoutParams lp)
    {
        if (_wm == null) return false;
        var types = new List<WindowManagerTypes> { WindowManagerTypes.AccessibilityOverlay, WindowManagerTypes.SystemAlert };
        if (OperatingSystem.IsAndroidVersionAtLeast(26)
            && global::Android.Provider.Settings.CanDrawOverlays(this))
            types.Insert(0, WindowManagerTypes.ApplicationOverlay);

        foreach (var type in types)
        {
            if (type == WindowManagerTypes.ApplicationOverlay
                && !global::Android.Provider.Settings.CanDrawOverlays(this))
                continue;
            lp.Type = type;
            try
            {
                _wm.AddView(view, lp);
                _overlayType = type;
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
        private readonly global::Android.Graphics.RectF _rect;

        public DebugRectView(Context context, int left, int top, int right, int bottom, Color color, float strokeWidth)
            : base(context)
        {
            _rect = new global::Android.Graphics.RectF(left, top, right, bottom);
            _stroke = new Paint(PaintFlags.AntiAlias)
            {
                Color = color,
                StrokeWidth = strokeWidth
            };
            _stroke.SetStyle(Paint.Style.Stroke);
            SetWillNotDraw(false);
            ImportantForAccessibility = ImportantForAccessibility.No;
        }

        protected override void OnDraw(Canvas canvas)
        {
            base.OnDraw(canvas);
            canvas.DrawRect(_rect, _stroke);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _stroke.Dispose();
            base.Dispose(disposing);
        }
    }
}
#endif
