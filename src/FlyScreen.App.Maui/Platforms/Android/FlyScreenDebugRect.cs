#if ANDROID
using Android.Content;
using Android.Hardware.Display;
using Android.Graphics;
using Android.OS;
using Android.Runtime;
using Android.Util;
using Android.Views;
using FlyScreen.Services;
using FlyScreen.Services.Programs;
using Color = Android.Graphics.Color;
using Paint = Android.Graphics.Paint;
using View = Android.Views.View;

namespace FlyScreen.AppMaui.Platforms.Android;

/// <summary>无障碍 / 系统悬浮窗上画调试矩形（供 <c>Fly.DrawRect</c> / <c>Fly.SetDebugRect</c>）。</summary>
public sealed partial class FlyScreenInputAccessibilityService
{
    private readonly object _debugRectGate = new();
    private int _debugRectSeq;
    private readonly Dictionary<string, DebugRectSlot> _debugSlots = new(StringComparer.Ordinal);
    /// <summary>常驻框的归一化规格；旋转后用新屏尺寸重放，避免仍画旧像素。</summary>
    private readonly Dictionary<string, DebugRectSpec> _debugSpecs = new(StringComparer.Ordinal);
    private Context? _debugRectWindowCtx;
    private string? _debugRectLastError;
    private int _debugRectCtxScreenW;
    private int _debugRectCtxScreenH;

    private sealed class DebugRectSpec
    {
        public double X;
        public double Y;
        public double W;
        public double H;
        public uint Argb;
    }

    private sealed class DebugRectSlot
    {
        public required View View;
        public required IWindowManager Wm;
        public int ExpireSeq;
    }

    /// <summary>旋转后丢弃 createWindowContext，避免仍按旧朝向加窗。</summary>
    private void InvalidateDebugOverlayContext()
    {
        try { _debugRectWindowCtx?.Dispose(); } catch { /* ignore */ }
        _debugRectWindowCtx = null;
        _debugRectCtxScreenW = 0;
        _debugRectCtxScreenH = 0;
    }

    /// <summary>
    /// 在屏幕上描边矩形，持续 <paramref name="durationMs"/> 后自动移除（槽位 <c>_draw</c>）。
    /// </summary>
    public bool TryDrawRect(int left, int top, int right, int bottom, uint argb, int durationMs)
        => TryUpsertDebugRectPixels("_draw", left, top, right, bottom, argb, durationMs, storeSpec: false);

    /// <summary>持久调试框（像素）；内部转成归一化后存储，便于旋转重放。</summary>
    public bool TrySetDebugRect(string id, int left, int top, int right, int bottom, uint argb)
    {
        id = string.IsNullOrWhiteSpace(id) ? "default" : id.Trim();
        RefreshScreenSize();
        var sw = Math.Max(_screenW, 1);
        var sh = Math.Max(_screenH, 1);
        var nr = NormRect.FromPixels(left, top, right, bottom, sw, sh);
        return TrySetDebugRectNorm(id, nr.X, nr.Y, nr.W, nr.H, argb);
    }

    /// <summary>持久调试框（归一化 0..1）；旋转后由 <see cref="RelayoutDebugRects"/> 按新屏重画。</summary>
    public bool TrySetDebugRectNorm(string id, double x, double y, double w, double h, uint argb)
    {
        id = string.IsNullOrWhiteSpace(id) ? "default" : id.Trim();
        var nr = new NormRect(x, y, w, h);
        if (nr.IsEmpty)
        {
            LogDrawRectFail("无效归一化矩形");
            return false;
        }

        lock (_debugRectGate)
        {
            _debugSpecs[id] = new DebugRectSpec
            {
                X = nr.X,
                Y = nr.Y,
                W = nr.W,
                H = nr.H,
                Argb = argb
            };
        }

        RefreshScreenSize();
        var (l, t, r, b) = nr.ToPixels(_screenW, _screenH);
        return TryUpsertDebugRectPixels(id, l, t, r, b, argb, durationMs: 0, storeSpec: false);
    }

    /// <summary>清除指定 id；id 为空则清除全部调试框与规格。</summary>
    public bool TryClearDebugRect(string? id = null)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Work()
        {
            try
            {
                if (string.IsNullOrWhiteSpace(id))
                {
                    ClearAllDebugSlots_NoPost();
                    lock (_debugRectGate)
                        _debugSpecs.Clear();
                }
                else
                {
                    var key = id.Trim();
                    RemoveDebugSlot_NoPost(key);
                    lock (_debugRectGate)
                        _debugSpecs.Remove(key);
                }
                tcs.TrySetResult(true);
            }
            catch
            {
                tcs.TrySetResult(false);
            }
        }

        if (Looper.MainLooper != null && Looper.MyLooper() == Looper.MainLooper)
            Work();
        else
        {
            _ui.Post(Work);
            if (!tcs.Task.Wait(TimeSpan.FromSeconds(2)))
                return false;
        }

        return tcs.Task.IsCompletedSuccessfully && tcs.Task.Result;
    }

    /// <summary>
    /// 旋转后：按已存归一化规格用最新屏尺寸重画常驻框。
    /// 横竖切换时规格由脚本换成另一套键后再 Set；此处只负责「同套键、新像素」。
    /// </summary>
    public void RelayoutDebugRects()
    {
        void Work()
        {
            try
            {
                RefreshScreenSize();
                InvalidateDebugOverlayContext();
                ClearAllDebugSlots_NoPost();

                List<KeyValuePair<string, DebugRectSpec>> specs;
                lock (_debugRectGate)
                    specs = _debugSpecs.ToList();

                foreach (var (id, spec) in specs)
                {
                    var nr = new NormRect(spec.X, spec.Y, spec.W, spec.H);
                    if (nr.IsEmpty) continue;
                    var (l, t, r, b) = nr.ToPixels(_screenW, _screenH);
                    TryUpsertDebugRectPixels(id, l, t, r, b, spec.Argb, durationMs: 0, storeSpec: false);
                }

                Log.Info("FlyScreen", $"DebugRect relayout {_screenW}x{_screenH} count={specs.Count}");
            }
            catch (Exception ex)
            {
                Log.Warn("FlyScreen", "DebugRect relayout: " + ex.Message);
            }
        }

        if (Looper.MainLooper != null && Looper.MyLooper() == Looper.MainLooper)
            Work();
        else
            _ui.Post(Work);
    }

    /// <summary>横竖切换时丢掉旧规格（Portrait/Landscape 键不同），避免用竖屏比例画在横屏上。</summary>
    public void ClearDebugRectSpecsOnly()
    {
        lock (_debugRectGate)
            _debugSpecs.Clear();
    }

    private bool TryUpsertDebugRectPixels(
        string id, int left, int top, int right, int bottom, uint argb, int durationMs, bool storeSpec)
    {
        var l = Math.Min(left, right);
        var t = Math.Min(top, bottom);
        var r = Math.Max(left, right);
        var b = Math.Max(top, bottom);
        if (r <= l || b <= t)
        {
            LogDrawRectFail("无效矩形");
            return false;
        }

        var persist = durationMs <= 0;
        var ms = persist ? 0 : Math.Clamp(durationMs, 50, 60_000);
        var a = (byte)((argb >> 24) & 0xFF);
        var rr = (byte)((argb >> 16) & 0xFF);
        var g = (byte)((argb >> 8) & 0xFF);
        var bl = (byte)(argb & 0xFF);
        if (a == 0) a = 0xFF;
        var color = Color.Argb(a, rr, g, bl);

        if (storeSpec && persist)
        {
            RefreshScreenSize();
            var nr = NormRect.FromPixels(l, t, r, b, Math.Max(_screenW, 1), Math.Max(_screenH, 1));
            lock (_debugRectGate)
            {
                _debugSpecs[id] = new DebugRectSpec
                {
                    X = nr.X, Y = nr.Y, W = nr.W, H = nr.H, Argb = argb
                };
            }
        }

        int expireSeq;
        lock (_debugRectGate)
            expireSeq = ++_debugRectSeq;

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        void Show()
        {
            try
            {
                RefreshScreenSize();
                if (_debugRectCtxScreenW > 0 && _debugRectCtxScreenH > 0
                    && (_debugRectCtxScreenW != _screenW || _debugRectCtxScreenH != _screenH))
                {
                    InvalidateDebugOverlayContext();
                    ClearAllDebugSlots_NoPost();
                }

                var density = Resources?.DisplayMetrics?.Density ?? 2f;
                var stroke = Math.Max(8f, density * 5f);

                RemoveDebugSlot_NoPost(id);

                if (!TryAddDebugRectLayers(id, l, t, r, b, color, stroke, out var view, out var wm))
                {
                    var canDraw = SafeCanDrawOverlays();
                    var detail = string.IsNullOrEmpty(_debugRectLastError)
                        ? "加窗失败"
                        : _debugRectLastError;
                    LogDrawRectFail($"{detail}; canDrawOverlays={canDraw}; screen={_screenW}x{_screenH}");
                    try { view?.Dispose(); } catch { /* ignore */ }
                    tcs.TrySetResult(false);
                    return;
                }

                lock (_debugRectGate)
                {
                    _debugSlots[id] = new DebugRectSlot
                    {
                        View = view,
                        Wm = wm,
                        ExpireSeq = expireSeq
                    };
                }

                if (!persist)
                {
                    var seq = expireSeq;
                    var slotId = id;
                    _ui.PostDelayed(() =>
                    {
                        lock (_debugRectGate)
                        {
                            if (!_debugSlots.TryGetValue(slotId, out var slot) || slot.ExpireSeq != seq)
                                return;
                            _debugSlots.Remove(slotId);
                            try { slot.Wm.RemoveView(slot.View); } catch { /* ignore */ }
                            try { slot.View.Dispose(); } catch { /* ignore */ }
                        }
                    }, ms);
                }

                var okMsg = $"DrawRect[{id}] ok {_screenW}x{_screenH} ({l},{t})-({r},{b}) {(persist ? "persist" : ms + "ms")} type={_overlayType}";
                AppLog.Info(okMsg);
                try { Console.WriteLine(okMsg); } catch { /* ignore */ }
                tcs.TrySetResult(true);
            }
            catch (Exception ex)
            {
                _debugRectLastError = ex.Message;
                LogDrawRectFail("异常: " + ex.Message);
                tcs.TrySetResult(false);
            }
        }

        if (Looper.MainLooper != null && Looper.MyLooper() == Looper.MainLooper)
            Show();
        else
        {
            _ui.Post(Show);
            if (!tcs.Task.Wait(TimeSpan.FromSeconds(3)))
            {
                LogDrawRectFail("主线程加窗超时（3s）");
                return false;
            }
        }

        return tcs.Task.IsCompletedSuccessfully && tcs.Task.Result;
    }

    private static void LogDrawRectFail(string detail)
    {
        Log.Warn("FlyScreen", "DrawRect: " + detail);
        try { AppLog.Warn("DrawRect 失败：" + detail); } catch { /* ignore */ }
        try { Console.WriteLine("DrawRect 失败：" + detail); } catch { /* ignore */ }
    }

    private bool SafeCanDrawOverlays()
    {
        try
        {
            return OperatingSystem.IsAndroidVersionAtLeast(23)
                && global::Android.Provider.Settings.CanDrawOverlays(this);
        }
        catch
        {
            return false;
        }
    }

    private void RemoveDebugSlot_NoPost(string id)
    {
        DebugRectSlot? old;
        lock (_debugRectGate)
        {
            if (!_debugSlots.Remove(id, out old))
                return;
        }
        try { old.Wm.RemoveView(old.View); } catch { /* ignore */ }
        try { old.View.Dispose(); } catch { /* ignore */ }
    }

    private void ClearAllDebugSlots_NoPost()
    {
        List<DebugRectSlot> all;
        lock (_debugRectGate)
        {
            all = _debugSlots.Values.ToList();
            _debugSlots.Clear();
            _debugRectSeq++;
        }
        foreach (var slot in all)
        {
            try { slot.Wm.RemoveView(slot.View); } catch { /* ignore */ }
            try { slot.View.Dispose(); } catch { /* ignore */ }
        }
    }

    /// <summary>OCR 前只撤视图，保留规格以便随后重画。</summary>
    private void RemoveDebugRectView_NoPost() => ClearAllDebugSlots_NoPost();

    private static string CaptionForDebugId(string id) => id switch
    {
        "ocr" => "识别区",
        "action" => "操作区",
        _ => id
    };

    private bool TryAddDebugRectLayers(
        string id, int l, int t, int r, int b,
        Color color, float stroke,
        out View view, out IWindowManager wm)
    {
        view = null!;
        wm = null!;
        _debugRectLastError = null;
        var errors = new List<string>(4);
        var caption = CaptionForDebugId(id);
        // 描边外扩，避免线宽被窗裁切；窗只覆盖矩形一带，避免全屏层吞触摸
        var pad = Math.Max(16, (int)Math.Ceiling(stroke + 4));

        _wm ??= GetSystemService(WindowService) as IWindowManager;
        if (_wm != null)
        {
            var a11yView = new DebugRectView(this, r - l, b - t, pad, color, stroke, caption);
            var a11yLp = BuildDebugLp(
                WindowManagerTypes.AccessibilityOverlay,
                layoutNoLimits: false,
                l, t, r, b, pad);
            try
            {
                a11yView.Clickable = false;
                a11yView.Focusable = false;
                a11yView.ImportantForAccessibility = ImportantForAccessibility.No;
                _wm.AddView(a11yView, a11yLp);
                _overlayType = WindowManagerTypes.AccessibilityOverlay;
                view = a11yView;
                wm = _wm;
                Log.Info("FlyScreen", "DrawRect ok type=AccessibilityOverlay");
                return true;
            }
            catch (Exception ex)
            {
                errors.Add($"AccessibilityOverlay: {ex.Message}");
                Log.Warn("FlyScreen", $"DrawRect add AccessibilityOverlay: {ex.Message}");
                try { _wm.RemoveView(a11yView); } catch { /* 未加上 */ }
                try { a11yView.Dispose(); } catch { /* ignore */ }
            }
        }
        else
        {
            errors.Add("AccessibilityOverlay: 无 WindowManager");
        }

        if (!SafeCanDrawOverlays())
        {
            errors.Add("ApplicationOverlay: 无「显示在其他应用上层」权限");
            _debugRectLastError = string.Join(" | ", errors);
            return false;
        }

        var overlayType = OperatingSystem.IsAndroidVersionAtLeast(26)
            ? WindowManagerTypes.ApplicationOverlay
            : WindowManagerTypes.SystemAlert;
        var ctx = EnsureDebugOverlayContext((int)overlayType);
        var overlayWm = ctx != null ? ResolveDebugWindowManager(ctx) : null;
        if (ctx == null || overlayWm == null)
        {
            errors.Add("ApplicationOverlay: WindowContext/WM 为空");
            _debugRectLastError = string.Join(" | ", errors);
            return false;
        }

        var overlayView = new DebugRectView(ctx, r - l, b - t, pad, color, stroke, caption);
        var overlayLp = BuildDebugLp(overlayType, layoutNoLimits: true, l, t, r, b, pad);
        try
        {
            overlayView.Clickable = false;
            overlayView.Focusable = false;
            overlayView.ImportantForAccessibility = ImportantForAccessibility.No;
            overlayWm.AddView(overlayView, overlayLp);
            _overlayType = overlayType;
            view = overlayView;
            wm = overlayWm;
            Log.Info("FlyScreen", $"DrawRect ok type={overlayType}");
            return true;
        }
        catch (Exception ex)
        {
            errors.Add($"{overlayType}: {ex.Message}");
            Log.Warn("FlyScreen", $"DrawRect add {overlayType}: {ex.Message}");
            try { overlayWm.RemoveView(overlayView); } catch { /* 未加上 */ }
            try { overlayView.Dispose(); } catch { /* ignore */ }
        }

        _debugRectLastError = string.Join(" | ", errors);
        return false;
    }

    /// <summary>仅覆盖目标矩形（含描边 padding），禁止全屏 MATCH_PARENT（会挡触摸）。</summary>
    private WindowManagerLayoutParams BuildDebugLp(
        WindowManagerTypes type, bool layoutNoLimits,
        int l, int t, int r, int b, int pad)
    {
        var flags = WindowManagerFlags.NotFocusable
                    | WindowManagerFlags.NotTouchable
                    | WindowManagerFlags.NotTouchModal
                    | WindowManagerFlags.LayoutInScreen;
        if (layoutNoLimits)
            flags |= WindowManagerFlags.LayoutNoLimits;

        var x = Math.Max(0, l - pad);
        var y = Math.Max(0, t - pad);
        var w = Math.Max(1, (r - l) + pad * 2);
        var h = Math.Max(1, (b - t) + pad * 2);

        var lp = new WindowManagerLayoutParams(
            w,
            h,
            type,
            flags,
            Format.Translucent)
        {
            Gravity = GravityFlags.Top | GravityFlags.Left,
            X = x,
            Y = y,
            Width = w,
            Height = h,
            Alpha = 1f,
            Title = "FlyScreen.DebugRect"
        };
        if (OperatingSystem.IsAndroidVersionAtLeast(28))
            lp.LayoutInDisplayCutoutMode = LayoutInDisplayCutoutMode.ShortEdges;
        return lp;
    }

    private Context? EnsureDebugOverlayContext(int overlayType)
    {
        if (_debugRectWindowCtx != null)
            return _debugRectWindowCtx;
        if (!OperatingSystem.IsAndroidVersionAtLeast(30))
            return this;

        try
        {
            var dmRaw = GetSystemService(DisplayService);
            var dm = dmRaw as DisplayManager ?? dmRaw?.JavaCast<DisplayManager>();
            var display = dm?.GetDisplay(global::Android.Views.Display.DefaultDisplay);
            if (display == null)
            {
                var displays = dm?.GetDisplays();
                if (displays is { Length: > 0 })
                    display = displays[0];
            }
            if (display == null)
                return null;

            var displayCtx = CreateDisplayContext(display);
            if (displayCtx == null)
                return null;

            _debugRectWindowCtx = displayCtx.CreateWindowContext(overlayType, null);
            _debugRectCtxScreenW = _screenW;
            _debugRectCtxScreenH = _screenH;
            return _debugRectWindowCtx;
        }
        catch (Exception ex)
        {
            Log.Warn("FlyScreen", "DrawRect window context: " + ex.Message);
            return null;
        }
    }

    private static IWindowManager? ResolveDebugWindowManager(Context ctx)
    {
        try
        {
            var raw = ctx.GetSystemService(WindowService);
            if (raw == null) return null;
            return raw as IWindowManager ?? raw.JavaCast<IWindowManager>();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>局部坐标描边：窗已定位到屏幕 (l,t)，内部从 pad 起画 contentW×contentH。</summary>
    private sealed class DebugRectView : View
    {
        private readonly Paint _stroke;
        private readonly Paint _corner;
        private readonly Paint _labelBg;
        private readonly Paint _labelFg;
        private readonly global::Android.Graphics.RectF _rect;
        private readonly float _strokeWidth;
        private readonly string _caption;

        public DebugRectView(
            Context context, int contentW, int contentH, int pad,
            Color color, float strokeWidth, string caption)
            : base(context)
        {
            _caption = caption ?? "";
            _strokeWidth = strokeWidth;
            var inset = strokeWidth * 0.5f;
            _rect = new global::Android.Graphics.RectF(
                pad + inset,
                pad + inset,
                pad + contentW - inset,
                pad + contentH - inset);
            _stroke = new Paint(PaintFlags.AntiAlias)
            {
                Color = color,
                StrokeWidth = strokeWidth
            };
            _stroke.SetStyle(Paint.Style.Stroke);
            _corner = new Paint(PaintFlags.AntiAlias)
            {
                Color = color,
                StrokeWidth = strokeWidth + 2f
            };
            _corner.SetStyle(Paint.Style.Stroke);
            _labelBg = new Paint(PaintFlags.AntiAlias)
            {
                Color = Color.Argb(0xCC, color.R, color.G, color.B)
            };
            _labelBg.SetStyle(Paint.Style.Fill);
            _labelFg = new Paint(PaintFlags.AntiAlias)
            {
                Color = Color.White,
                TextSize = Math.Max(28f, strokeWidth * 3.5f),
                FakeBoldText = true
            };
            SetWillNotDraw(false);
            ImportantForAccessibility = ImportantForAccessibility.No;
            SetLayerType(LayerType.Software, null);
            try { Elevation = 64f; } catch { /* ignore */ }
        }

        protected override void OnDraw(Canvas canvas)
        {
            base.OnDraw(canvas);
            canvas.DrawRect(_rect, _stroke);

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

            if (!string.IsNullOrEmpty(_caption))
            {
                var tw = _labelFg.MeasureText(_caption);
                var th = _labelFg.TextSize;
                var bx = l + 4;
                var by = t + 4;
                canvas.DrawRect(bx, by, bx + tw + 12, by + th + 8, _labelBg);
                canvas.DrawText(_caption, bx + 6, by + th, _labelFg);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _stroke.Dispose();
                _corner.Dispose();
                _labelBg.Dispose();
                _labelFg.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
#endif
