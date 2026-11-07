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
using View = Android.Views.View;
using Button = Android.Widget.Button;
using ImageButton = Android.Widget.ImageButton;
using Paint = Android.Graphics.Paint;
using Color = Android.Graphics.Color;
using Point = Android.Graphics.Point;

namespace FlyScreen.AppMaui.Platforms.Android;

/// <summary>标定结束状态（同一时间只允许一个会话）。</summary>
public enum RegionCalibrateStatus
{
    Ok,
    Busy,
    Failed
}

/// <summary>单区域触屏标定（识别区或操作区）；结果写入程序 settings.json 对应键。</summary>
public static class FlyScreenRegionCalibrator
{
    private static int _sessionBusy; // 0=空闲 1=标定中（同一时间只允许一个）

    /// <summary>是否已有标定会话在进行（含方向提示等待期间）。</summary>
    public static bool IsBusy => Volatile.Read(ref _sessionBusy) != 0;

    /// <summary>占用标定会话（提示弹窗前调用，避免同时开多个）。失败表示已有标定。</summary>
    public static bool TryBeginSession()
        => Interlocked.CompareExchange(ref _sessionBusy, 1, 0) == 0;

    /// <summary>放弃已占用但尚未进入浮层的会话（例如提示后取消）。</summary>
    public static void CancelSession()
        => Interlocked.Exchange(ref _sessionBusy, 0);

    /// <param name="settingKey">如 ocrRegionPortrait / actionRegionLandscape</param>
    /// <param name="sessionAlreadyHeld">为 true 时表示调用方已 <see cref="TryBeginSession"/>，不再二次抢锁。</param>
    public static Task<RegionCalibrateStatus> RunAsync(
        ProgramCatalog catalog,
        UserProgram prog,
        string settingKey,
        bool sessionAlreadyHeld = false)
    {
        if (!sessionAlreadyHeld)
        {
            if (Interlocked.CompareExchange(ref _sessionBusy, 1, 0) != 0)
            {
                AppLog.Warn("已有区域标定进行中，请先完成或取消当前标定");
                ShowBusyToast();
                return Task.FromResult(RegionCalibrateStatus.Busy);
            }
        }
        else if (!IsBusy)
        {
            // 调用方声称已占用但标志已丢，重新抢锁
            if (Interlocked.CompareExchange(ref _sessionBusy, 1, 0) != 0)
            {
                ShowBusyToast();
                return Task.FromResult(RegionCalibrateStatus.Busy);
            }
        }

        var tcs = new TaskCompletionSource<RegionCalibrateStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        var main = Looper.MainLooper;
        if (main == null)
        {
            Interlocked.Exchange(ref _sessionBusy, 0);
            tcs.TrySetResult(RegionCalibrateStatus.Failed);
            return tcs.Task;
        }

        var key = string.IsNullOrWhiteSpace(settingKey)
            ? ProgramRegionKeys.OcrPortrait
            : settingKey.Trim();
        var handler = new Handler(main);
        handler.Post(() =>
        {
            try
            {
                var host = new CalibratorHost(catalog, prog, key, ok =>
                {
                    Interlocked.Exchange(ref _sessionBusy, 0);
                    tcs.TrySetResult(ok ? RegionCalibrateStatus.Ok : RegionCalibrateStatus.Failed);
                });
                if (!host.Show())
                {
                    Interlocked.Exchange(ref _sessionBusy, 0);
                    tcs.TrySetResult(RegionCalibrateStatus.Failed);
                }
            }
            catch (Exception ex)
            {
                Interlocked.Exchange(ref _sessionBusy, 0);
                Log.Warn("FlyCalib", ex.Message);
                AppLog.Warn("区域标定失败: " + ex.Message);
                tcs.TrySetResult(RegionCalibrateStatus.Failed);
            }
        });
        return tcs.Task;
    }

    private static void ShowBusyToast()
    {
        try
        {
            var toast = Toast.MakeText(
                global::Android.App.Application.Context,
                FlyScreen.Helpers.Localization.Loc.T("Prog.CalibrateBusy"),
                ToastLength.Short);
            toast?.Show();
        }
        catch { /* ignore */ }
    }

    private sealed class CalibratorHost
    {
        private readonly ProgramCatalog _catalog;
        private readonly UserProgram _prog;
        private readonly string _settingKey;
        private readonly string _caption;
        private readonly Action<bool> _onDone;
        private readonly Context _appCtx;
        private IWindowManager? _wm;
        private Context? _windowCtx;
        private WindowManagerTypes _overlayType;

        // 全屏半透明遮罩（NotTouchable：触摸穿透到底下 App）
        private FrameLayout? _dimRoot;
        private WindowManagerLayoutParams? _dimLp;
        private TextView? _title;
        private TextView? _status;

        // 仅覆盖选区+手柄热区的可点窗口
        private FrameLayout? _boxRoot;
        private WindowManagerLayoutParams? _boxLp;
        private CalibBoxView? _box;

        // 工具条独立窗口
        private LinearLayout? _toolbar;
        private WindowManagerLayoutParams? _toolbarLp;
        private Button? _confirm;
        private Button? _cancel;
        private ImageButton? _sample;
        private ImageButton? _move;
        private bool _sampling;

        private float _density = 2f;
        private int _screenW = 1080;
        private int _screenH = 1920;
        private float _sl, _st, _sr, _sb; // 选区屏幕像素
        private bool _boxAdded;
        private bool _toolbarAdded;
        private bool _dimAdded;

        public CalibratorHost(ProgramCatalog catalog, UserProgram prog, string settingKey, Action<bool> onDone)
        {
            _catalog = catalog;
            _prog = prog;
            _settingKey = settingKey;
            _caption = ProgramRegionKeys.Caption(settingKey);
            _onDone = onDone;
            _appCtx = global::Android.App.Application.Context
                      ?? throw new InvalidOperationException("无 Application Context");
        }

        private float HandlePad => Math.Max(28f, 28f * _density);

        public bool Show()
        {
            RefreshScreen();
            _overlayType = OperatingSystem.IsAndroidVersionAtLeast(26)
                ? WindowManagerTypes.ApplicationOverlay
                : WindowManagerTypes.SystemAlert;

            var canDraw = OperatingSystem.IsAndroidVersionAtLeast(23)
                && global::Android.Provider.Settings.CanDrawOverlays(_appCtx);
            if (!canDraw)
            {
                try
                {
                    var uri = global::Android.Net.Uri.Parse("package:" + _appCtx.PackageName);
                    var intent = new Intent(global::Android.Provider.Settings.ActionManageOverlayPermission, uri);
                    intent.AddFlags(ActivityFlags.NewTask);
                    _appCtx.StartActivity(intent);
                }
                catch { /* ignore */ }
                AppLog.Warn("标定：请开启「显示在其他应用上层」，以便切换应用后仍可拖动识别框");
                if (FlyScreenInputAccessibilityService.Instance == null)
                    return false;
            }

            _windowCtx = TryOverlayContext((int)_overlayType) ?? _appCtx;
            _wm = ResolveWm(_windowCtx);
            if (_wm == null) return false;

            _density = _windowCtx.Resources?.DisplayMetrics?.Density ?? 2f;
            // 有 WindowContext 后再取一次，MaximumWindowMetrics 更准
            RefreshScreen();

            var existing = ProgramSettingsStore.LoadResolved(_catalog, _prog);
            NormRect start =
                NormRect.TryParse(existing.GetValueOrDefault(_settingKey))
                ?? ProgramRegionKeys.DefaultRect(_settingKey);
            var (l, t, r, b) = start.ToPixels(_screenW, _screenH);
            _sl = l; _st = t; _sr = r; _sb = b;

            if (!AddDimLayer() || !AddBoxLayer() || !AddToolbarLayer())
            {
                Finish(false);
                return false;
            }

            return true;
        }

        private WindowManagerLayoutParams BaseLp(int w, int h, WindowManagerFlags flags)
        {
            var lp = new WindowManagerLayoutParams(
                w, h, _overlayType, flags, Format.Translucent)
            {
                Gravity = GravityFlags.Top | GravityFlags.Left,
                Width = w,
                Height = h,
                Title = "FlyScreen.RegionCalib"
            };
            if (OperatingSystem.IsAndroidVersionAtLeast(28))
                lp.LayoutInDisplayCutoutMode = LayoutInDisplayCutoutMode.ShortEdges;
            // 不避开系统栏，否则遮罩/选区会被裁掉导航栏高度
            try
            {
                if (OperatingSystem.IsAndroidVersionAtLeast(30))
                    lp.FitInsetsTypes = 0;
            }
            catch { /* ignore */ }
            return lp;
        }

        private bool AddDimLayer()
        {
            if (_wm == null || _windowCtx == null) return false;
            _dimRoot = new FrameLayout(_windowCtx);
            _dimRoot.SetBackgroundColor(Color.Argb(0x40, 0, 0, 0));

            _title = new TextView(_windowCtx);
            _title.SetTextColor(Color.White);
            _title.TextSize = 16f;
            _title.SetPadding(24, 24, 24, 8);
            var orientHint = ProgramRegionKeys.IsLandscapeKey(_settingKey)
                ? "请先转到横屏；"
                : "请先转到竖屏；";
            _title.Text = $"{orientHint}可切到目标应用；框内拖动，角/边点缩放；框外可正常点手机";
            _dimRoot.AddView(_title, new FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent)
            {
                Gravity = GravityFlags.Top
            });

            _status = new TextView(_windowCtx);
            _status.SetTextColor(Color.Argb(0xFF, 0xFE, 0xF0, 0x8C));
            _status.TextSize = 14f;
            _status.SetPadding(24, 8, 24, 8);
            _status.Text = "";
            _dimRoot.AddView(_status, new FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent)
            {
                Gravity = GravityFlags.Top,
                TopMargin = (int)(52 * _density)
            });

            // MatchParent + 不吃 insets：盖住含导航栏的整屏；NotTouchable 点穿
            _dimLp = BaseLp(
                ViewGroup.LayoutParams.MatchParent,
                ViewGroup.LayoutParams.MatchParent,
                WindowManagerFlags.NotFocusable
                | WindowManagerFlags.NotTouchable
                | WindowManagerFlags.LayoutInScreen
                | WindowManagerFlags.LayoutNoLimits);
            _dimLp.X = 0;
            _dimLp.Y = 0;
            _dimLp.Width = ViewGroup.LayoutParams.MatchParent;
            _dimLp.Height = ViewGroup.LayoutParams.MatchParent;

            if (!TryAddView(_dimRoot, _dimLp, ref _dimAdded))
                return false;

            // 加窗后按实际布局校正屏幕像素（部分 OEM 初次 GetRealSize 偏小）
            _dimRoot.Post(() =>
            {
                try
                {
                    var w = _dimRoot.Width;
                    var h = _dimRoot.Height;
                    if (w > _screenW) _screenW = w;
                    if (h > _screenH) _screenH = h;
                    PushRectToBoxView();
                    RelayoutBoxWindow();
                    RelayoutToolbar();
                }
                catch { /* ignore */ }
            });
            return true;
        }

        private bool AddBoxLayer()
        {
            if (_wm == null || _windowCtx == null) return false;
            _boxRoot = new FrameLayout(_windowCtx);
            _box = new CalibBoxView(_windowCtx, _density);
            _box.SetCaption(_caption);
            _box.ScreenRectChanged += OnBoxScreenRectChanged;
            _boxRoot.AddView(_box, new FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));

            var pad = (int)Math.Ceiling(HandlePad);
            var bw = Math.Max(1, (int)Math.Ceiling(_sr - _sl) + pad * 2);
            var bh = Math.Max(1, (int)Math.Ceiling(_sb - _st) + pad * 2);
            _boxLp = BaseLp(
                bw, bh,
                WindowManagerFlags.NotFocusable
                | WindowManagerFlags.NotTouchModal
                | WindowManagerFlags.LayoutInScreen
                | WindowManagerFlags.LayoutNoLimits);
            _boxLp.X = (int)Math.Floor(_sl) - pad;
            _boxLp.Y = (int)Math.Floor(_st) - pad;

            if (!TryAddView(_boxRoot, _boxLp, ref _boxAdded))
                return false;

            PushRectToBoxView();
            return true;
        }

        private bool AddToolbarLayer()
        {
            if (_wm == null || _windowCtx == null) return false;
            _toolbar = BuildToolbar(_windowCtx, _density);
            _toolbarLp = BaseLp(
                ViewGroup.LayoutParams.WrapContent,
                ViewGroup.LayoutParams.WrapContent,
                WindowManagerFlags.NotFocusable
                | WindowManagerFlags.NotTouchModal
                | WindowManagerFlags.LayoutInScreen
                | WindowManagerFlags.LayoutNoLimits);
            _toolbarLp.Width = ViewGroup.LayoutParams.WrapContent;
            _toolbarLp.Height = ViewGroup.LayoutParams.WrapContent;
            if (!TryAddView(_toolbar, _toolbarLp, ref _toolbarAdded))
                return false;
            _toolbar.Post(RelayoutToolbar);
            return true;
        }

        private bool TryAddView(View view, WindowManagerLayoutParams lp, ref bool addedFlag)
        {
            if (_wm == null) return false;
            try
            {
                _wm.AddView(view, lp);
                addedFlag = true;
                return true;
            }
            catch (Exception ex)
            {
                Log.Warn("FlyCalib", $"add {_overlayType}: {ex.Message}");
                // 尝试 AccessibilityOverlay 兜底（仅首次）
                var a11y = FlyScreenInputAccessibilityService.Instance;
                if (a11y != null && _overlayType != WindowManagerTypes.AccessibilityOverlay)
                {
                    try
                    {
                        _windowCtx = a11y;
                        _wm = a11y.GetSystemService(Context.WindowService) as IWindowManager
                              ?? ResolveWm(a11y);
                        _overlayType = WindowManagerTypes.AccessibilityOverlay;
                        lp.Type = _overlayType;
                        if (_wm != null)
                        {
                            _wm.AddView(view, lp);
                            addedFlag = true;
                            return true;
                        }
                    }
                    catch (Exception ex2)
                    {
                        Log.Warn("FlyCalib", "a11y add: " + ex2.Message);
                    }
                }
                return false;
            }
        }

        private void OnBoxScreenRectChanged(float l, float t, float r, float b)
        {
            _sl = l; _st = t; _sr = r; _sb = b;
            RelayoutBoxWindow();
            RelayoutToolbar();
        }

        private void PushRectToBoxView()
        {
            if (_box == null || _boxLp == null) return;
            _box.SetScreenRect(_boxLp.X, _boxLp.Y, _sl, _st, _sr, _sb, _screenW, _screenH);
        }

        private void RelayoutBoxWindow()
        {
            if (_box == null || _boxLp == null || _wm == null || _boxRoot == null || !_boxAdded)
                return;
            ClampScreenRect();
            var pad = (int)Math.Ceiling(HandlePad);
            var bw = Math.Max(1, (int)Math.Ceiling(_sr - _sl) + pad * 2);
            var bh = Math.Max(1, (int)Math.Ceiling(_sb - _st) + pad * 2);
            _boxLp.X = (int)Math.Floor(_sl) - pad;
            _boxLp.Y = (int)Math.Floor(_st) - pad;
            _boxLp.Width = bw;
            _boxLp.Height = bh;
            try { _wm.UpdateViewLayout(_boxRoot, _boxLp); } catch { /* ignore */ }
            PushRectToBoxView();
        }

        private void ClampScreenRect()
        {
            var min = 48f * _density;
            if (_sr - _sl < min) _sr = _sl + min;
            if (_sb - _st < min) _sb = _st + min;
            var bw = _sr - _sl;
            var bh = _sb - _st;
            if (_sl < 0) { _sl = 0; _sr = bw; }
            if (_st < 0) { _st = 0; _sb = bh; }
            if (_sr > _screenW) { _sr = _screenW; _sl = _screenW - bw; }
            if (_sb > _screenH) { _sb = _screenH; _st = _screenH - bh; }
            _sl = Math.Clamp(_sl, 0, _screenW);
            _st = Math.Clamp(_st, 0, _screenH);
            _sr = Math.Clamp(_sr, _sl + min, _screenW);
            _sb = Math.Clamp(_sb, _st + min, _screenH);
        }

        private LinearLayout BuildToolbar(Context ctx, float density)
        {
            var bar = new LinearLayout(ctx)
            {
                Orientation = Orientation.Horizontal,
                Elevation = 12f * density
            };
            var padH = (int)(6 * density);
            var padV = (int)(4 * density);
            bar.SetPadding(padH, padV, padH, padV);
            try
            {
                var bg = new global::Android.Graphics.Drawables.GradientDrawable();
                bg.SetColor(Color.Argb(0xF5, 0xFF, 0xFF, 0xFF));
                bg.SetCornerRadius(18f * density);
                bg.SetStroke(Math.Max(1, (int)density), Color.Argb(0x40, 0, 0, 0));
                bar.Background = bg;
            }
            catch { /* ignore */ }

            var gap = (int)(4 * density);

            if (ProgramRegionKeys.IsOcrKey(_settingKey))
            {
                _sample = MakeIconToolButton(
                    ctx, density, Color.Argb(0xFF, 0x25, 0x63, 0xEB), "抽样", DrawSampleIcon);
                _sample.Click += (_, _) => OnSample();
                bar.AddView(_sample, new LinearLayout.LayoutParams(
                    ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent)
                { RightMargin = gap });
            }

            _move = MakeIconToolButton(
                ctx, density, Color.Argb(0xFF, 0x0E, 0xA5, 0xE9), "移动选区", DrawMoveIcon);
            AttachMoveButton(_move);
            bar.AddView(_move, new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent)
            { RightMargin = gap });

            _cancel = MakeToolButton(ctx, density, "✕", Color.Argb(0xFF, 0xE5, 0x3E, 0x3E));
            _confirm = MakeToolButton(ctx, density, "✓", Color.Argb(0xFF, 0x22, 0xC5, 0x5E));
            _cancel.Click += (_, _) => Finish(false);
            _confirm.Click += (_, _) => OnConfirm();

            bar.AddView(_cancel, new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent)
            { RightMargin = gap });
            bar.AddView(_confirm, new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent));
            return bar;
        }

        private void AttachMoveButton(View btn)
        {
            float startRawX = 0, startRawY = 0;
            float origL = 0, origT = 0, origR = 0, origB = 0;
            btn.Touch += (_, e) =>
            {
                var ev = e.Event;
                if (ev == null) { e.Handled = false; return; }
                switch (ev.ActionMasked)
                {
                    case MotionEventActions.Down:
                        startRawX = ev.RawX;
                        startRawY = ev.RawY;
                        origL = _sl; origT = _st; origR = _sr; origB = _sb;
                        e.Handled = true;
                        break;
                    case MotionEventActions.Move:
                        var dx = ev.RawX - startRawX;
                        var dy = ev.RawY - startRawY;
                        _sl = origL + dx; _sr = origR + dx;
                        _st = origT + dy; _sb = origB + dy;
                        RelayoutBoxWindow();
                        RelayoutToolbar();
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
            };
        }

        private void OnSample()
        {
            if (_sampling || _box == null) return;
            if (!ProgramRegionKeys.IsOcrKey(_settingKey)) return;

            RefreshScreen();
            ClampScreenRect();
            var il = (int)Math.Round(Math.Min(_sl, _sr));
            var it = (int)Math.Round(Math.Min(_st, _sb));
            var ir = (int)Math.Round(Math.Max(_sl, _sr));
            var ib = (int)Math.Round(Math.Max(_st, _sb));
            if (ir - il < 8 || ib - it < 8)
            {
                ShowSampleFeedback("识别区太小，请先放大选区", ok: false);
                return;
            }

            _sampling = true;
            SetToolbarEnabled(false);
            ShowSampleFeedback("抽样中：截屏 + OCR…", ok: null);

            var stem = $"{_prog.Id}_{_settingKey}";
            var main = Looper.MainLooper;
            var ui = main != null ? new Handler(main) : null;

            // 截屏前藏可点层与遮罩
            SetOverlayVisible(false);

            Task.Run(() =>
            {
                string? path = null;
                var text = "";
                string? err = null;
                try
                {
                    try { System.Threading.Thread.Sleep(120); } catch { /* ignore */ }

                    var svc = FlyScreenInputAccessibilityService.Instance;
                    if (svc == null)
                        err = "无障碍未连接，无法抽样截屏";
                    else
                    {
                        path = svc.TrySampleOcrRegion(il, it, ir, ib, stem, out text);
                        if (path == null)
                        {
                            err = string.IsNullOrWhiteSpace(text)
                                ? "抽样失败：截屏或保存未成功（见日志）"
                                : text;
                        }
                    }
                }
                catch (Exception ex)
                {
                    err = ex.Message;
                }

                void Done()
                {
                    try
                    {
                        SetOverlayVisible(true);
                        SetToolbarEnabled(true);
                        _sampling = false;

                        if (!string.IsNullOrEmpty(err))
                        {
                            AppLog.Warn("识别区抽样: " + err);
                            ShowSampleFeedback(err, ok: false);
                            return;
                        }

                        var preview = string.IsNullOrWhiteSpace(text)
                            ? "(无文字)"
                            : (text.Length > 60 ? text[..60] + "…" : text);
                        var file = System.IO.Path.GetFileName(path) ?? "";
                        var msg = $"已保存 下载/XuanNiao/ocr_samples/{file}\n识别: {preview}";
                        AppLog.Info($"识别区抽样 path={path} OCR={text}");
                        ShowSampleFeedback(msg, ok: true);
                    }
                    catch { /* ignore */ }
                }

                if (ui != null) ui.Post(Done);
                else Done();
            });
        }

        private void SetOverlayVisible(bool visible)
        {
            void Apply()
            {
                try
                {
                    var v = visible ? ViewStates.Visible : ViewStates.Invisible;
                    if (_dimRoot != null) _dimRoot.Visibility = v;
                    if (_boxRoot != null) _boxRoot.Visibility = v;
                    if (_toolbar != null) _toolbar.Visibility = v;
                }
                catch { /* ignore */ }
            }

            var main = Looper.MainLooper;
            if (main != null && Looper.MyLooper() != main)
                new Handler(main).Post(Apply);
            else
                Apply();
        }

        private void ShowSampleFeedback(string message, bool? ok)
        {
            try
            {
                if (_status != null)
                {
                    _status.Text = message;
                    _status.SetTextColor(ok == false
                        ? Color.Argb(0xFF, 0xFE, 0xCA, 0xCA)
                        : ok == true
                            ? Color.Argb(0xFF, 0xBB, 0xF7, 0xD0)
                            : Color.Argb(0xFF, 0xFE, 0xF0, 0x8C));
                }

                var toast = Toast.MakeText(_appCtx, message, ToastLength.Long);
                toast?.Show();
            }
            catch { /* ignore */ }
        }

        private void SetToolbarEnabled(bool enabled)
        {
            if (_toolbar != null) _toolbar.Enabled = enabled;
            if (_sample != null) _sample.Enabled = enabled;
            if (_move != null) _move.Enabled = enabled;
            if (_cancel != null) _cancel.Enabled = enabled;
            if (_confirm != null) _confirm.Enabled = enabled;
        }

        private static Button MakeToolButton(Context ctx, float density, string text, Color fg)
        {
            var btn = new Button(ctx)
            {
                Text = text,
                TextSize = 14f
            };
            btn.SetTextColor(fg);
            btn.SetPadding((int)(10 * density), (int)(4 * density), (int)(10 * density), (int)(4 * density));
            btn.SetMinWidth((int)(36 * density));
            btn.SetMinHeight((int)(32 * density));
            btn.SetMinimumWidth((int)(36 * density));
            btn.SetMinimumHeight((int)(32 * density));
            ApplyToolButtonChrome(btn, density, fg);
            btn.SetAllCaps(false);
            return btn;
        }

        private static ImageButton MakeIconToolButton(
            Context ctx, float density, Color fg, string contentDescription, Action<Canvas, int, Paint> draw)
        {
            var iconPx = Math.Max(18, (int)(18 * density));
            var bmp = Bitmap.CreateBitmap(iconPx, iconPx, Bitmap.Config.Argb8888!)
                       ?? throw new InvalidOperationException("icon bitmap");
            using (var canvas = new Canvas(bmp))
            {
                var paint = new Paint(PaintFlags.AntiAlias)
                {
                    Color = fg,
                    StrokeWidth = Math.Max(1.5f, 1.6f * density),
                    StrokeCap = Paint.Cap.Round,
                    StrokeJoin = Paint.Join.Round
                };
                paint.SetStyle(Paint.Style.Stroke);
                draw(canvas, iconPx, paint);
            }

            var btn = new ImageButton(ctx);
            btn.SetImageBitmap(bmp);
            btn.SetScaleType(global::Android.Widget.ImageView.ScaleType.CenterInside);
            btn.ContentDescription = contentDescription;
            var pad = (int)(8 * density);
            btn.SetPadding(pad, pad, pad, pad);
            btn.SetMinimumWidth((int)(36 * density));
            btn.SetMinimumHeight((int)(32 * density));
            ApplyToolButtonChrome(btn, density, fg);
            return btn;
        }

        private static void ApplyToolButtonChrome(View btn, float density, Color fg)
        {
            try
            {
                var bg = new global::Android.Graphics.Drawables.GradientDrawable();
                bg.SetColor(Color.Argb(0x12, fg.R, fg.G, fg.B));
                bg.SetCornerRadius(12f * density);
                btn.Background = bg;
            }
            catch
            {
                btn.SetBackgroundColor(Color.Transparent);
            }
        }

        /// <summary>取景框 + 快门点：抽样截屏。</summary>
        private static void DrawSampleIcon(Canvas canvas, int size, Paint paint)
        {
            var m = size * 0.18f;
            var r = size * 0.12f;
            canvas.DrawRoundRect(m, m, size - m, size - m, r, r, paint);
            // 左上取景缺角暗示「框选」
            var len = size * 0.22f;
            canvas.DrawLine(m, m + len, m, m, paint);
            canvas.DrawLine(m, m, m + len, m, paint);
            canvas.DrawLine(size - m - len, m, size - m, m, paint);
            canvas.DrawLine(size - m, m, size - m, m + len, paint);
            paint.SetStyle(Paint.Style.Fill);
            canvas.DrawCircle(size * 0.5f, size * 0.55f, size * 0.1f, paint);
            paint.SetStyle(Paint.Style.Stroke);
        }

        /// <summary>四向箭头：按住拖动选区。</summary>
        private static void DrawMoveIcon(Canvas canvas, int size, Paint paint)
        {
            var c = size * 0.5f;
            var arm = size * 0.32f;
            var head = size * 0.12f;
            // 十字
            canvas.DrawLine(c, c - arm, c, c + arm, paint);
            canvas.DrawLine(c - arm, c, c + arm, c, paint);
            // 箭头
            canvas.DrawLine(c, c - arm, c - head, c - arm + head, paint);
            canvas.DrawLine(c, c - arm, c + head, c - arm + head, paint);
            canvas.DrawLine(c, c + arm, c - head, c + arm - head, paint);
            canvas.DrawLine(c, c + arm, c + head, c + arm - head, paint);
            canvas.DrawLine(c - arm, c, c - arm + head, c - head, paint);
            canvas.DrawLine(c - arm, c, c - arm + head, c + head, paint);
            canvas.DrawLine(c + arm, c, c + arm - head, c - head, paint);
            canvas.DrawLine(c + arm, c, c + arm - head, c + head, paint);
        }

        private void RelayoutToolbar()
        {
            if (_toolbar == null || _toolbarLp == null || _wm == null || !_toolbarAdded) return;

            var gap = 12f * _density;
            var edge = 10f * _density;
            _toolbar.Measure(
                View.MeasureSpec.MakeMeasureSpec(_screenW, MeasureSpecMode.AtMost),
                View.MeasureSpec.MakeMeasureSpec(_screenH, MeasureSpecMode.AtMost));
            var tw = _toolbar.MeasuredWidth > 0 ? _toolbar.MeasuredWidth : (int)(160 * _density);
            var th = _toolbar.MeasuredHeight > 0 ? _toolbar.MeasuredHeight : (int)(40 * _density);

            var l = _sl; var t = _st; var r = _sr; var b = _sb;
            var boxCx = (l + r) / 2f;
            float x, y;
            var belowY = b + gap;
            var aboveY = t - gap - th;
            if (belowY + th <= _screenH - edge)
            {
                y = belowY;
                x = boxCx - tw / 2f;
            }
            else if (aboveY >= edge)
            {
                y = aboveY;
                x = boxCx - tw / 2f;
            }
            else if (r + gap + tw <= _screenW - edge)
            {
                x = r + gap;
                y = (t + b) / 2f - th / 2f;
            }
            else if (l - gap - tw >= edge)
            {
                x = l - gap - tw;
                y = (t + b) / 2f - th / 2f;
            }
            else
            {
                y = Math.Min(b - th - gap, _screenH - th - edge);
                y = Math.Max(edge, y);
                x = boxCx - tw / 2f;
            }

            x = Math.Clamp(x, edge, Math.Max(edge, _screenW - tw - edge));
            y = Math.Clamp(y, edge, Math.Max(edge, _screenH - th - edge));

            _toolbarLp.X = (int)Math.Round(x);
            _toolbarLp.Y = (int)Math.Round(y);
            _toolbarLp.Width = ViewGroup.LayoutParams.WrapContent;
            _toolbarLp.Height = ViewGroup.LayoutParams.WrapContent;
            try { _wm.UpdateViewLayout(_toolbar, _toolbarLp); } catch { /* ignore */ }
        }

        private void OnConfirm()
        {
            ClampScreenRect();
            var nr = NormRect.FromPixels(
                (int)Math.Round(_sl), (int)Math.Round(_st),
                (int)Math.Round(_sr), (int)Math.Round(_sb),
                _screenW, _screenH);
            var map = ProgramSettingsStore.LoadResolved(_catalog, _prog);
            map[_settingKey] = nr.ToString();
            if (ProgramRegionKeys.IsOcrKey(_settingKey))
                map["debugOcr"] = "false";
            ProgramSettingsStore.SaveValues(_catalog, _prog, map);
            Finish(true);
        }

        private void Finish(bool ok)
        {
            void Remove(ref bool added, View? v)
            {
                if (!added || v == null || _wm == null) return;
                try { _wm.RemoveView(v); } catch { /* ignore */ }
                added = false;
            }

            Remove(ref _toolbarAdded, _toolbar);
            Remove(ref _boxAdded, _boxRoot);
            Remove(ref _dimAdded, _dimRoot);
            _toolbar = null;
            _boxRoot = null;
            _box = null;
            _dimRoot = null;
            _wm = null;
            _onDone(ok);
        }

        private void RefreshScreen()
        {
            // 1) 无障碍服务已校准的物理像素（含导航栏）
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
                        AlignWithConfiguration();
                        return;
                    }
                }
            }
            catch { /* ignore */ }

            // 2) API30+ MaximumWindowMetrics（覆盖系统栏的最大可达区域）
            try
            {
                if (OperatingSystem.IsAndroidVersionAtLeast(30))
                {
                    var wm = _windowCtx?.GetSystemService(Context.WindowService) as IWindowManager
                             ?? _appCtx.GetSystemService(Context.WindowService) as IWindowManager;
                    var bounds = wm?.MaximumWindowMetrics?.Bounds;
                    if (bounds != null && bounds.Width() > 0 && bounds.Height() > 0)
                    {
                        _screenW = bounds.Width();
                        _screenH = bounds.Height();
                        AlignWithConfiguration();
                        return;
                    }
                }
            }
            catch { /* ignore */ }

            // 3) Display.GetRealSize
            try
            {
                var wm = _appCtx.GetSystemService(Context.WindowService) as IWindowManager;
                var size = new Point();
                wm?.DefaultDisplay?.GetRealSize(size);
                if (size.X > 0 && size.Y > 0)
                {
                    _screenW = size.X;
                    _screenH = size.Y;
                    AlignWithConfiguration();
                    return;
                }
            }
            catch { /* ignore */ }

            var dm = _appCtx.Resources?.DisplayMetrics;
            if (dm != null)
            {
                _screenW = dm.WidthPixels;
                _screenH = dm.HeightPixels;
            }
            AlignWithConfiguration();
        }

        private void AlignWithConfiguration()
        {
            try
            {
                var orient = _appCtx.Resources?.Configuration?.Orientation
                             ?? global::Android.Content.Res.Orientation.Undefined;
                if (orient == global::Android.Content.Res.Orientation.Portrait && _screenW > _screenH)
                    (_screenW, _screenH) = (_screenH, _screenW);
                else if (orient == global::Android.Content.Res.Orientation.Landscape && _screenW < _screenH)
                    (_screenW, _screenH) = (_screenH, _screenW);
            }
            catch { /* ignore */ }
        }

        private Context? TryOverlayContext(int overlayType)
        {
            if (!OperatingSystem.IsAndroidVersionAtLeast(30))
                return _appCtx;
            try
            {
                var dmRaw = _appCtx.GetSystemService(Context.DisplayService);
                var dm = dmRaw as DisplayManager ?? dmRaw?.JavaCast<DisplayManager>();
                var display = dm?.GetDisplay(global::Android.Views.Display.DefaultDisplay);
                if (display == null)
                {
                    var displays = dm?.GetDisplays();
                    if (displays is { Length: > 0 }) display = displays[0];
                }
                if (display == null) return _appCtx;
                var displayCtx = _appCtx.CreateDisplayContext(display);
                return displayCtx?.CreateWindowContext(overlayType, null) ?? _appCtx;
            }
            catch
            {
                return _appCtx;
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

    /// <summary>
    /// 屏幕坐标标定框（相对窗口偏移绘制）：框内移动；8 手柄缩放。
    /// 手势用 RawX/RawY，避免宿主重排窗口后本地坐标错乱。
    /// </summary>
    private sealed class CalibBoxView : View
    {
        private readonly float _density;
        private readonly Paint _stroke;
        private readonly Paint _corner;
        private readonly Paint _textPaint;
        private readonly Paint _sizePaint;
        private string _caption = "识别区";
        private float _winX, _winY;
        private float _sl, _st, _sr, _sb;
        private int _screenW = 1080, _screenH = 1920;
        private float _downRawX, _downRawY;
        private float _origL, _origT, _origR, _origB;
        private enum Mode
        {
            None, Move,
            ResizeTl, ResizeTr, ResizeBl, ResizeBr,
            ResizeT, ResizeB, ResizeL, ResizeR
        }
        private Mode _mode;

        public event Action<float, float, float, float>? ScreenRectChanged;

        public CalibBoxView(Context context, float density) : base(context)
        {
            _density = density <= 0 ? 2f : density;
            _stroke = new Paint(PaintFlags.AntiAlias)
            {
                Color = Color.Argb(0xFF, 0x22, 0xC5, 0x5E),
                StrokeWidth = Math.Max(6f, _density * 3f)
            };
            _stroke.SetStyle(Paint.Style.Stroke);
            _corner = new Paint(PaintFlags.AntiAlias)
            {
                Color = Color.Argb(0xFF, 0x22, 0xC5, 0x5E),
                StrokeWidth = Math.Max(8f, _density * 4f)
            };
            _corner.SetStyle(Paint.Style.Stroke);
            _textPaint = new Paint(PaintFlags.AntiAlias)
            {
                Color = Color.White,
                TextSize = 16f * _density,
                TextAlign = Paint.Align.Center
            };
            _sizePaint = new Paint(PaintFlags.AntiAlias)
            {
                Color = Color.Argb(0xEE, 0xFF, 0xFF, 0xFF),
                TextSize = 12f * _density,
                TextAlign = Paint.Align.Left
            };
            Clickable = true;
            Focusable = true;
        }

        public void SetCaption(string caption) => _caption = caption;

        public void SetScreenRect(
            float winX, float winY,
            float sl, float st, float sr, float sb,
            int screenW, int screenH)
        {
            _winX = winX; _winY = winY;
            _sl = sl; _st = st; _sr = sr; _sb = sb;
            _screenW = Math.Max(1, screenW);
            _screenH = Math.Max(1, screenH);
            Invalidate();
        }

        public override bool OnTouchEvent(MotionEvent? e)
        {
            if (e == null) return false;
            var lx = e.GetX();
            var ly = e.GetY();
            var handle = 28f * _density;
            var ll = _sl - _winX;
            var lt = _st - _winY;
            var lr = _sr - _winX;
            var lb = _sb - _winY;

            switch (e.ActionMasked)
            {
                case MotionEventActions.Down:
                    _downRawX = e.RawX;
                    _downRawY = e.RawY;
                    _origL = _sl; _origT = _st; _origR = _sr; _origB = _sb;
                    _mode = HitMode(lx, ly, ll, lt, lr, lb, handle);
                    return _mode != Mode.None;
                case MotionEventActions.Move:
                    if (_mode == Mode.None) return false;
                    ApplyDrag(e.RawX - _downRawX, e.RawY - _downRawY);
                    ScreenRectChanged?.Invoke(_sl, _st, _sr, _sb);
                    return true;
                case MotionEventActions.Up:
                case MotionEventActions.Cancel:
                    var had = _mode != Mode.None;
                    _mode = Mode.None;
                    if (had) ScreenRectChanged?.Invoke(_sl, _st, _sr, _sb);
                    return had;
            }
            return base.OnTouchEvent(e);
        }

        private Mode HitMode(float x, float y, float l, float t, float r, float b, float handle)
        {
            var mx = (l + r) / 2f;
            var my = (t + b) / 2f;
            if (Near(x, l, handle) && Near(y, t, handle)) return Mode.ResizeTl;
            if (Near(x, r, handle) && Near(y, t, handle)) return Mode.ResizeTr;
            if (Near(x, l, handle) && Near(y, b, handle)) return Mode.ResizeBl;
            if (Near(x, r, handle) && Near(y, b, handle)) return Mode.ResizeBr;
            if (Near(x, mx, handle) && Near(y, t, handle)) return Mode.ResizeT;
            if (Near(x, mx, handle) && Near(y, b, handle)) return Mode.ResizeB;
            if (Near(x, l, handle) && Near(y, my, handle)) return Mode.ResizeL;
            if (Near(x, r, handle) && Near(y, my, handle)) return Mode.ResizeR;
            if (x >= l && x <= r && y >= t && y <= b) return Mode.Move;
            return Mode.None;
        }

        private static bool Near(float a, float b, float d) => Math.Abs(a - b) <= d;

        private void ApplyDrag(float dx, float dy)
        {
            var min = 48f * _density;
            switch (_mode)
            {
                case Mode.Move:
                    _sl = _origL + dx; _sr = _origR + dx;
                    _st = _origT + dy; _sb = _origB + dy;
                    break;
                case Mode.ResizeTl:
                    _sl = Math.Min(_origL + dx, _origR - min);
                    _st = Math.Min(_origT + dy, _origB - min);
                    break;
                case Mode.ResizeTr:
                    _sr = Math.Max(_origR + dx, _origL + min);
                    _st = Math.Min(_origT + dy, _origB - min);
                    break;
                case Mode.ResizeBl:
                    _sl = Math.Min(_origL + dx, _origR - min);
                    _sb = Math.Max(_origB + dy, _origT + min);
                    break;
                case Mode.ResizeBr:
                    _sr = Math.Max(_origR + dx, _origL + min);
                    _sb = Math.Max(_origB + dy, _origT + min);
                    break;
                case Mode.ResizeT:
                    _st = Math.Min(_origT + dy, _origB - min);
                    break;
                case Mode.ResizeB:
                    _sb = Math.Max(_origB + dy, _origT + min);
                    break;
                case Mode.ResizeL:
                    _sl = Math.Min(_origL + dx, _origR - min);
                    break;
                case Mode.ResizeR:
                    _sr = Math.Max(_origR + dx, _origL + min);
                    break;
            }

            var bw = _sr - _sl;
            var bh = _sb - _st;
            if (_sl < 0) { _sl = 0; _sr = bw; }
            if (_st < 0) { _st = 0; _sb = bh; }
            if (_sr > _screenW) { _sr = _screenW; _sl = _screenW - bw; }
            if (_sb > _screenH) { _sb = _screenH; _st = _screenH - bh; }
        }

        protected override void OnDraw(Canvas canvas)
        {
            base.OnDraw(canvas);
            var l = _sl - _winX;
            var t = _st - _winY;
            var r = _sr - _winX;
            var b = _sb - _winY;
            canvas.DrawRect(l, t, r, b, _stroke);
            var len = 28f * _density;
            var hs = 5f * _density;
            void Handle(float x, float y)
                => canvas.DrawRect(x - hs, y - hs, x + hs, y + hs, _corner);

            canvas.DrawLine(l, t, l + len, t, _corner);
            canvas.DrawLine(l, t, l, t + len, _corner);
            canvas.DrawLine(r, t, r - len, t, _corner);
            canvas.DrawLine(r, t, r, t + len, _corner);
            canvas.DrawLine(l, b, l + len, b, _corner);
            canvas.DrawLine(l, b, l, b - len, _corner);
            canvas.DrawLine(r, b, r - len, b, _corner);
            canvas.DrawLine(r, b, r, b - len, _corner);

            Handle(l, t); Handle(r, t); Handle(l, b); Handle(r, b);
            Handle((l + r) / 2f, t); Handle((l + r) / 2f, b);
            Handle(l, (t + b) / 2f); Handle(r, (t + b) / 2f);

            canvas.DrawText(_caption, (l + r) / 2f, (t + b) / 2f, _textPaint);
            var sizeLabel = $"{(int)(r - l)} × {(int)(b - t)}";
            canvas.DrawText(sizeLabel, l, Math.Max(t - 6f * _density, _sizePaint.TextSize + 4), _sizePaint);
        }
    }
}
#endif
