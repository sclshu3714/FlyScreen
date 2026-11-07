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
using View = Android.Views.View;
using ScrollView = Android.Widget.ScrollView;
using TextView = Android.Widget.TextView;
using FrameLayout = Android.Widget.FrameLayout;
using LinearLayout = Android.Widget.LinearLayout;

namespace FlyScreen.AppMaui.Platforms.Android;

/// <summary>
/// 调试日志浮层：可拖动、右下角缩放、内部滚动；仅 debugLog=true 时由 <see cref="ProgramDebugHud"/> 拉起。
/// </summary>
public static class FlyScreenDebugLogHud
{
    private static Handler? _ui;
    private static IWindowManager? _wm;
    private static FrameLayout? _root;
    private static TextView? _title;
    private static TextView? _body;
    private static ScrollView? _scroll;
    private static WindowManagerLayoutParams? _lp;
    private static bool _added;
    private static float _density = 2f;
    private static int _screenW = 1080;
    private static int _screenH = 1920;
    // 记住用户拖/缩后的几何，避免刷日志时跳回默认位
    private static int _x = int.MinValue;
    private static int _y = int.MinValue;
    private static int _w;
    private static int _h;
    private static bool _stickToBottom = true;

    public static void Register()
    {
        ProgramDebugHud.Register(SetActive, SetLines);
    }

    private static void SetActive(bool on, string? programId, string? programName)
    {
        EnsureHandler();
        _ui!.Post(() =>
        {
            if (on) Show(programName);
            else Hide();
        });
    }

    private static void SetLines(IReadOnlyList<string> lines)
    {
        EnsureHandler();
        _ui!.Post(() =>
        {
            if (_body == null || _scroll == null) return;
            var text = lines.Count == 0 ? "…" : string.Join("\n", lines);
            _body.Text = text;
            if (_stickToBottom)
            {
                _scroll.Post(() =>
                {
                    try { _scroll.FullScroll(FocusSearchDirection.Down); } catch { /* ignore */ }
                });
            }
            // 不在此 Relayout：避免每次日志把窗口拽回默认位置（「一闪而过」）
        });
    }

    /// <summary>旋转后把浮层夹回可见区域（保留相对位置比例尽量不动）。</summary>
    public static void Relayout()
    {
        if (!_added || _root == null || _lp == null || _wm == null)
            return;
        try
        {
            var app = global::Android.App.Application.Context ?? _root.Context;
            if (app == null) return;
            RefreshScreen(app);
            ClampGeometry();
            ApplyLayout();
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

    private static void Show(string? programName)
    {
        try
        {
            if (_added && _root != null)
            {
                if (_title != null)
                    _title.Text = string.IsNullOrEmpty(programName)
                        ? "调试日志 · 拖标题移动"
                        : $"调试 · {programName} · 拖标题移动";
                return;
            }

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

            RefreshScreen(app);
            _density = viewCtx.Resources?.DisplayMetrics?.Density ?? 2f;
            if (_w <= 0 || _h <= 0 || _x == int.MinValue)
            {
                _w = Math.Max((int)(_screenW * 0.48), (int)(200 * _density));
                _h = Math.Max((int)(_screenH * 0.36), (int)(180 * _density));
                _x = (int)(8 * _density);
                _y = Math.Max(0, (_screenH - _h) / 2 - (int)(24 * _density));
            }
            ClampGeometry();

            var pad = (int)(8 * _density);
            var titleH = (int)(36 * _density);
            var handle = (int)(22 * _density);

            _root = new FrameLayout(viewCtx);
            _root.SetBackgroundColor(Color.Argb(0xE6, 0x10, 0x14, 0x1A));
            try
            {
                var bg = new global::Android.Graphics.Drawables.GradientDrawable();
                bg.SetColor(Color.Argb(0xE8, 0x12, 0x16, 0x1C));
                bg.SetCornerRadius(10f * _density);
                bg.SetStroke(Math.Max(1, (int)_density), Color.Argb(0x66, 0x80, 0xCB, 0xC4));
                _root.Background = bg;
            }
            catch { /* ignore */ }

            var col = new LinearLayout(viewCtx) { Orientation = Orientation.Vertical };
            _root.AddView(col, new FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));

            _title = new TextView(viewCtx);
            _title.Text = string.IsNullOrEmpty(programName)
                ? "调试日志 · 拖标题移动"
                : $"调试 · {programName} · 拖标题移动";
            _title.SetTextColor(Color.Argb(0xFF, 0xB2, 0xDF, 0xDB));
            _title.TextSize = 12f;
            _title.SetPadding(pad, pad / 2, pad, pad / 2);
            _title.SetBackgroundColor(Color.Argb(0x66, 0x00, 0x96, 0x88));
            _title.Gravity = GravityFlags.CenterVertical;
            col.AddView(_title, new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent, titleH));

            _scroll = new ScrollView(viewCtx)
            {
                FillViewport = false,
                VerticalScrollBarEnabled = true,
                ScrollBarStyle = ScrollbarStyles.OutsideOverlay
            };
            _scroll.ScrollChange += (_, e) =>
            {
                // 用户往上翻时别强行吸底
                try
                {
                    var child = _scroll.GetChildAt(0);
                    if (child == null) return;
                    var atBottom = e.ScrollY + _scroll.Height >= child.Height - (int)(8 * _density);
                    _stickToBottom = atBottom;
                }
                catch { _stickToBottom = true; }
            };

            _body = new TextView(viewCtx);
            _body.SetTextColor(Color.Argb(0xF0, 0xE8, 0xF5, 0xE9));
            _body.TextSize = 11f;
            _body.SetPadding(pad, pad / 2, pad, pad);
            _body.SetLineSpacing(2 * _density, 1f);
            _body.SetSingleLine(false);
            _body.SetHorizontallyScrolling(false);
            _body.Text = "…";
            _body.SetTextIsSelectable(false);
            _scroll.AddView(_body, new ViewGroup.LayoutParams(
                ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
            col.AddView(_scroll, new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent, 0, 1f));

            var resize = new TextView(viewCtx)
            {
                Text = "◢",
                Gravity = GravityFlags.Center,
                TextSize = 14f
            };
            resize.SetTextColor(Color.Argb(0xCC, 0x80, 0xCB, 0xC4));
            resize.SetBackgroundColor(Color.Argb(0x55, 0x00, 0x00, 0x00));
            _root.AddView(resize, new FrameLayout.LayoutParams(handle, handle)
            {
                Gravity = GravityFlags.Bottom | GravityFlags.Right
            });

            _title.Touch += OnTitleTouch;
            resize.Touch += OnResizeTouch;

            // 可触摸以便拖/缩/滚；NotTouchModal 让窗外触摸穿透
            _lp = new WindowManagerLayoutParams(
                _w,
                _h,
                overlayType,
                WindowManagerFlags.NotFocusable
                | WindowManagerFlags.NotTouchModal
                | WindowManagerFlags.LayoutInScreen,
                Format.Translucent)
            {
                Gravity = GravityFlags.Top | GravityFlags.Left,
                X = _x,
                Y = _y,
                Width = _w,
                Height = _h,
                Title = "FlyScreen.DebugLogHud"
            };
            if (OperatingSystem.IsAndroidVersionAtLeast(28))
                _lp.LayoutInDisplayCutoutMode = LayoutInDisplayCutoutMode.ShortEdges;

            _wm = wm;
            _wm.AddView(_root, _lp);
            _added = true;
            _stickToBottom = true;
            Log.Info("FlyHud", "debug log hud on (draggable)");
        }
        catch (Exception ex)
        {
            Log.Warn("FlyHud", "show: " + ex.Message);
            AppLog.Warn("调试日志浮层失败（需「显示在其他应用上层」）: " + ex.Message);
            Hide();
        }
    }

    private static float _dragStartRawX;
    private static float _dragStartRawY;
    private static int _dragStartX;
    private static int _dragStartY;
    private static int _resizeStartW;
    private static int _resizeStartH;

    private static void OnTitleTouch(object? sender, View.TouchEventArgs e)
    {
        var ev = e.Event;
        if (ev == null || _lp == null || _wm == null || _root == null)
        {
            e.Handled = false;
            return;
        }

        switch (ev.ActionMasked)
        {
            case MotionEventActions.Down:
                _dragStartRawX = ev.RawX;
                _dragStartRawY = ev.RawY;
                _dragStartX = _lp.X;
                _dragStartY = _lp.Y;
                e.Handled = true;
                break;
            case MotionEventActions.Move:
                _x = _dragStartX + (int)(ev.RawX - _dragStartRawX);
                _y = _dragStartY + (int)(ev.RawY - _dragStartRawY);
                ClampGeometry();
                ApplyLayout();
                e.Handled = true;
                break;
            case MotionEventActions.Up:
            case MotionEventActions.Cancel:
                e.Handled = true;
                break;
            default:
                e.Handled = false;
                break;
        }
    }

    private static void OnResizeTouch(object? sender, View.TouchEventArgs e)
    {
        var ev = e.Event;
        if (ev == null || _lp == null || _wm == null)
        {
            e.Handled = false;
            return;
        }

        switch (ev.ActionMasked)
        {
            case MotionEventActions.Down:
                _dragStartRawX = ev.RawX;
                _dragStartRawY = ev.RawY;
                _resizeStartW = _lp.Width;
                _resizeStartH = _lp.Height;
                e.Handled = true;
                break;
            case MotionEventActions.Move:
                _w = _resizeStartW + (int)(ev.RawX - _dragStartRawX);
                _h = _resizeStartH + (int)(ev.RawY - _dragStartRawY);
                ClampGeometry();
                ApplyLayout();
                e.Handled = true;
                break;
            case MotionEventActions.Up:
            case MotionEventActions.Cancel:
                e.Handled = true;
                break;
            default:
                e.Handled = false;
                break;
        }
    }

    private static void ClampGeometry()
    {
        var minW = (int)(160 * _density);
        var minH = (int)(120 * _density);
        _w = Math.Clamp(_w, minW, Math.Max(minW, _screenW - (int)(8 * _density)));
        _h = Math.Clamp(_h, minH, Math.Max(minH, _screenH - (int)(8 * _density)));
        _x = Math.Clamp(_x, 0, Math.Max(0, _screenW - _w));
        _y = Math.Clamp(_y, 0, Math.Max(0, _screenH - _h));
    }

    private static void ApplyLayout()
    {
        if (_lp == null || _wm == null || _root == null || !_added) return;
        _lp.X = _x;
        _lp.Y = _y;
        _lp.Width = _w;
        _lp.Height = _h;
        try { _wm.UpdateViewLayout(_root, _lp); } catch { /* ignore */ }
    }

    private static void Hide()
    {
        try
        {
            if (_added && _root != null && _wm != null)
                _wm.RemoveView(_root);
        }
        catch { /* ignore */ }
        _added = false;
        _root = null;
        _title = null;
        _body = null;
        _scroll = null;
        _lp = null;
        _wm = null;
    }

    private static void RefreshScreen(Context ctx)
    {
        _screenW = 1080;
        _screenH = 1920;
        try
        {
            var a11y = FlyScreenInputAccessibilityService.Instance;
            if (a11y != null)
            {
                var (w, h) = a11y.GetScreenPixels();
                if (w > 0 && h > 0)
                {
                    _screenW = w;
                    _screenH = h;
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
                _screenW = size.X;
                _screenH = size.Y;
                return;
            }
        }
        catch { /* ignore */ }
        var dm = ctx.Resources?.DisplayMetrics;
        if (dm != null)
        {
            _screenW = dm.WidthPixels;
            _screenH = dm.HeightPixels;
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
