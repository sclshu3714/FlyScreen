#if ANDROID
using Android.Content;
using Android.Graphics;
using Android.Hardware.Display;
using Android.OS;
using Android.Runtime;
using Android.Util;
using Android.Views;
using Android.Widget;
using FlyScreen.Services;
using FlyScreen.Services.Programs;
using Color = Android.Graphics.Color;
using Point = Android.Graphics.Point;

namespace FlyScreen.AppMaui.Platforms.Android;

/// <summary>左中调试日志浮层：最多显示 <see cref="ProgramDebugHud.MaxLines"/> 行，不抢触摸。</summary>
public static class FlyScreenDebugLogHud
{
    private static Handler? _ui;
    private static IWindowManager? _wm;
    private static TextView? _view;
    private static WindowManagerLayoutParams? _lp;
    private static bool _added;

    public static void Register()
    {
        ProgramDebugHud.Register(SetActive, SetLines);
    }

    private static void SetActive(bool on, string? programId, string? programName)
    {
        EnsureHandler();
        _ui!.Post(() =>
        {
            if (on) Show();
            else Hide();
        });
    }

    private static void SetLines(IReadOnlyList<string> lines)
    {
        EnsureHandler();
        _ui!.Post(() =>
        {
            if (_view == null) return;
            _view.Text = lines.Count == 0 ? "…" : string.Join("\n", lines);
            // 日志刷新时顺带校正左中位置（旋转后 Show 不会重跑）
            Relayout();
        });
    }

    /// <summary>按当前屏幕尺寸把浮层重新放到左侧垂直居中（旋转后调用）。</summary>
    public static void Relayout()
    {
        if (!_added || _view == null || _lp == null || _wm == null)
            return;
        try
        {
            var app = global::Android.App.Application.Context ?? _view.Context;
            if (app == null) return;
            RefreshScreen(app, out var sw, out var sh);
            var density = _view.Resources?.DisplayMetrics?.Density ?? 2f;
            var pad = (int)(8 * density);
            var panelW = Math.Max((int)(sw * 0.42), (int)(160 * density));
            var panelH = (int)(Math.Max(5, ProgramDebugHud.MaxLines) * 16 * density + pad * 2);
            var x = (int)(8 * density);
            var y = Math.Max(0, (sh - panelH) / 2 - (int)(24 * density));
            _lp.X = x;
            _lp.Y = y;
            _lp.Width = panelW;
            _lp.Height = panelH;
            _wm.UpdateViewLayout(_view, _lp);
        }
        catch (Exception ex)
        {
            Log.Warn("FlyHud", "relayout: " + ex.Message);
        }
    }

    private static void EnsureHandler()
    {
        if (_ui != null) return;
        var looper = Looper.MainLooper;
        if (looper != null)
            _ui = new Handler(looper);
    }

    private static void Show()
    {
        try
        {
            if (_added && _view != null) return;
            var app = global::Android.App.Application.Context;
            if (app == null) return;

            var canDraw = OperatingSystem.IsAndroidVersionAtLeast(23)
                && global::Android.Provider.Settings.CanDrawOverlays(app);
            var overlayType = OperatingSystem.IsAndroidVersionAtLeast(26)
                ? WindowManagerTypes.ApplicationOverlay
                : WindowManagerTypes.SystemAlert;

            Context viewCtx = app;
            IWindowManager? wm = null;
            if (canDraw)
            {
                viewCtx = TryOverlayContext(app, (int)overlayType) ?? app;
                wm = ResolveWm(viewCtx);
            }
            if (wm == null)
            {
                var a11y = FlyScreenInputAccessibilityService.Instance;
                if (a11y != null)
                {
                    viewCtx = a11y;
                    wm = a11y.GetSystemService(Context.WindowService) as IWindowManager
                         ?? ResolveWm(a11y);
                    overlayType = WindowManagerTypes.AccessibilityOverlay;
                }
            }
            if (wm == null) return;

            RefreshScreen(app, out var sw, out var sh);
            var density = viewCtx.Resources?.DisplayMetrics?.Density ?? 2f;
            var pad = (int)(8 * density);
            var panelW = Math.Max((int)(sw * 0.42), (int)(160 * density));
            var panelH = (int)(Math.Max(5, ProgramDebugHud.MaxLines) * 16 * density + pad * 2);

            _view = new TextView(viewCtx);
            _view.SetTextColor(Color.Argb(0xEE, 0xE8, 0xF5, 0xE9));
            _view.SetBackgroundColor(Color.Argb(0xB3, 0x10, 0x14, 0x1A));
            _view.TextSize = 11f;
            _view.SetPadding(pad, pad, pad, pad);
            _view.SetLineSpacing(0, 1.05f);
            _view.SetSingleLine(false);
            _view.Ellipsize = global::Android.Text.TextUtils.TruncateAt.End;
            _view.Text = "…";
            _view.Clickable = false;
            _view.Focusable = false;
            _view.ImportantForAccessibility = ImportantForAccessibility.No;

            var x = (int)(8 * density);
            var y = Math.Max(0, (sh - panelH) / 2 - (int)(24 * density));

            // 必须 NotTouchable：否则日志层挡住左侧大片触控
            _lp = new WindowManagerLayoutParams(
                panelW,
                panelH,
                overlayType,
                WindowManagerFlags.NotFocusable
                | WindowManagerFlags.NotTouchable
                | WindowManagerFlags.NotTouchModal
                | WindowManagerFlags.LayoutInScreen,
                Format.Translucent)
            {
                Gravity = GravityFlags.Top | GravityFlags.Left,
                X = x,
                Y = y,
                Width = panelW,
                Height = panelH,
                Title = "FlyScreen.DebugLogHud"
            };
            if (OperatingSystem.IsAndroidVersionAtLeast(28))
                _lp.LayoutInDisplayCutoutMode = LayoutInDisplayCutoutMode.ShortEdges;

            _wm = wm;
            _wm.AddView(_view, _lp);
            _added = true;
            Log.Info("FlyHud", "debug log hud on");
        }
        catch (Exception ex)
        {
            Log.Warn("FlyHud", "show: " + ex.Message);
            AppLog.Warn("调试日志浮层失败（需「显示在其他应用上层」）: " + ex.Message);
            Hide();
        }
    }

    private static void Hide()
    {
        try
        {
            if (_added && _view != null && _wm != null)
                _wm.RemoveView(_view);
        }
        catch { /* ignore */ }
        _added = false;
        _view = null;
        _lp = null;
        _wm = null;
    }

    private static void RefreshScreen(Context ctx, out int sw, out int sh)
    {
        sw = 1080;
        sh = 1920;
        // 优先无障碍服务的实时物理像素（随旋转），Application 的 Metrics 有时仍是旧朝向
        try
        {
            var a11y = FlyScreenInputAccessibilityService.Instance;
            if (a11y != null)
            {
                var (w, h) = a11y.GetScreenPixels();
                if (w > 0 && h > 0)
                {
                    sw = w;
                    sh = h;
                    return;
                }
            }
        }
        catch { /* ignore */ }
        try
        {
            var wm = ctx.GetSystemService(Context.WindowService) as IWindowManager;
            var size = new Point();
            wm?.DefaultDisplay?.GetRealSize(size);
            if (size.X > 0 && size.Y > 0)
            {
                sw = size.X;
                sh = size.Y;
                return;
            }
        }
        catch { /* ignore */ }
        var dm = ctx.Resources?.DisplayMetrics;
        if (dm != null)
        {
            sw = dm.WidthPixels;
            sh = dm.HeightPixels;
        }
    }

    private static Context? TryOverlayContext(Context app, int overlayType)
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(30))
            return app;
        try
        {
            var dmRaw = app.GetSystemService(Context.DisplayService);
            var dm = dmRaw as DisplayManager ?? dmRaw?.JavaCast<DisplayManager>();
            var display = dm?.GetDisplay(global::Android.Views.Display.DefaultDisplay);
            if (display == null)
            {
                var displays = dm?.GetDisplays();
                if (displays is { Length: > 0 }) display = displays[0];
            }
            if (display == null) return app;
            return app.CreateDisplayContext(display)?.CreateWindowContext(overlayType, null) ?? app;
        }
        catch
        {
            return app;
        }
    }

    private static IWindowManager? ResolveWm(Context ctx)
    {
        try
        {
            var raw = ctx.GetSystemService(Context.WindowService);
            return raw as IWindowManager ?? raw?.JavaCast<IWindowManager>();
        }
        catch { return null; }
    }
}
#endif
