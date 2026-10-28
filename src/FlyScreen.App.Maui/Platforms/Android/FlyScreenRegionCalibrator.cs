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
using Paint = Android.Graphics.Paint;
using Color = Android.Graphics.Color;
using Point = Android.Graphics.Point;

namespace FlyScreen.AppMaui.Platforms.Android;

/// <summary>单区域触屏标定（识别区或操作区）；结果写入程序 settings.json 对应键。</summary>
public static class FlyScreenRegionCalibrator
{
    /// <param name="settingKey">如 ocrRegionPortrait / actionRegionLandscape</param>
    public static Task<bool> RunAsync(ProgramCatalog catalog, UserProgram prog, string settingKey)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var main = Looper.MainLooper;
        if (main == null)
        {
            tcs.TrySetResult(false);
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
                var host = new CalibratorHost(catalog, prog, key, ok => tcs.TrySetResult(ok));
                if (!host.Show())
                    tcs.TrySetResult(false);
            }
            catch (Exception ex)
            {
                Log.Warn("FlyCalib", ex.Message);
                AppLog.Warn("区域标定失败: " + ex.Message);
                tcs.TrySetResult(false);
            }
        });
        return tcs.Task;
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
        private FrameLayout? _root;
        private CalibBoxView? _box;
        private TextView? _title;
        private TextView? _status;
        private LinearLayout? _toolbar;
        private Button? _confirm;
        private Button? _cancel;
        private Button? _sample;
        private bool _sampling;
        private float _density = 2f;
        private int _screenW = 1080;
        private int _screenH = 1920;

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

        public bool Show()
        {
            RefreshScreen();
            var overlayType = OperatingSystem.IsAndroidVersionAtLeast(26)
                ? WindowManagerTypes.ApplicationOverlay
                : WindowManagerTypes.SystemAlert;

            // 切到其它 App 后浮层仍须可操作 → 优先系统悬浮窗；未授权则引导开启
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
                // 仍尝试无障碍 Overlay 兜底
                if (FlyScreenInputAccessibilityService.Instance == null)
                    return false;
            }

            _windowCtx = TryOverlayContext((int)overlayType) ?? _appCtx;
            _wm = ResolveWm(_windowCtx);
            if (_wm == null) return false;

            _density = _windowCtx.Resources?.DisplayMetrics?.Density ?? 2f;
            _root = new FrameLayout(_windowCtx);
            // 轻遮罩：能看见底下目标 App，便于对照调框
            _root.SetBackgroundColor(Color.Argb(0x40, 0, 0, 0));

            var existing = ProgramSettingsStore.LoadResolved(_catalog, _prog);
            NormRect start =
                NormRect.TryParse(existing.GetValueOrDefault(_settingKey))
                ?? ProgramRegionKeys.DefaultRect(_settingKey);

            _box = new CalibBoxView(_windowCtx, _density);
            _box.SetNormRect(start, _screenW, _screenH);
            _box.SetCaption(_caption);
            _box.RectChanged += RelayoutToolbar;
            _root.AddView(_box, new FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));

            _title = new TextView(_windowCtx);
            _title.SetTextColor(Color.White);
            _title.TextSize = 16f;
            _title.SetPadding(24, 24, 24, 8);
            var orientHint = ProgramRegionKeys.IsLandscapeKey(_settingKey)
                ? "请先转到横屏；"
                : "请先转到竖屏；";
            _title.Text = $"{orientHint}可切到目标应用；拖动/缩放「{_caption}」";
            _root.AddView(_title, new FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent)
            {
                Gravity = GravityFlags.Top
            });

            _status = new TextView(_windowCtx);
            _status.SetTextColor(Color.Argb(0xFF, 0xFE, 0xF0, 0x8C));
            _status.TextSize = 14f;
            _status.SetPadding(24, 8, 24, 8);
            _status.Text = "";
            _root.AddView(_status, new FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent)
            {
                Gravity = GravityFlags.Top,
                TopMargin = (int)(52 * _density)
            });

            // 跟随选区的浮动工具条（类似截图工具栏）：取消 / 确定
            _toolbar = BuildToolbar(_windowCtx, _density);
            _root.AddView(_toolbar, new FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent)
            {
                Gravity = GravityFlags.Top | GravityFlags.Left
            });
            _toolbar.Post(RelayoutToolbar);

            // NotFocusable：不抢焦点，用户可用手势/多任务切到目标 App；浮层仍可触摸拖框
            var lp = new WindowManagerLayoutParams(
                ViewGroup.LayoutParams.MatchParent,
                ViewGroup.LayoutParams.MatchParent,
                overlayType,
                WindowManagerFlags.NotFocusable
                | WindowManagerFlags.LayoutInScreen
                | WindowManagerFlags.LayoutNoLimits,
                Format.Translucent)
            {
                Gravity = GravityFlags.Top | GravityFlags.Left,
                Width = Math.Max(_screenW, 1),
                Height = Math.Max(_screenH, 1),
                Title = "FlyScreen.RegionCalib"
            };
            if (OperatingSystem.IsAndroidVersionAtLeast(28))
                lp.LayoutInDisplayCutoutMode = LayoutInDisplayCutoutMode.ShortEdges;

            // 优先 ApplicationOverlay；失败再试 AccessibilityOverlay（需无障碍服务 context）
            if (!TryAdd(lp, overlayType))
            {
                var a11y = FlyScreenInputAccessibilityService.Instance;
                if (a11y != null)
                {
                    _windowCtx = a11y;
                    _wm = a11y.GetSystemService(Context.WindowService) as IWindowManager
                          ?? ResolveWm(a11y);
                    lp.Type = WindowManagerTypes.AccessibilityOverlay;
                    if (_wm == null || !TryAdd(lp, WindowManagerTypes.AccessibilityOverlay))
                        return false;
                }
                else return false;
            }

            return true;
        }

        private bool TryAdd(WindowManagerLayoutParams lp, WindowManagerTypes type)
        {
            if (_wm == null || _root == null) return false;
            lp.Type = type;
            try
            {
                _wm.AddView(_root, lp);
                return true;
            }
            catch (Exception ex)
            {
                Log.Warn("FlyCalib", $"add {type}: {ex.Message}");
                try { _wm.RemoveView(_root); } catch { /* ignore */ }
                return false;
            }
        }

        private LinearLayout BuildToolbar(Context ctx, float density)
        {
            var bar = new LinearLayout(ctx)
            {
                Orientation = Orientation.Horizontal,
                Elevation = 12f * density
            };
            var padH = (int)(10 * density);
            var padV = (int)(6 * density);
            bar.SetPadding(padH, padV, padH, padV);
            bar.SetBackgroundColor(Color.Argb(0xF2, 0xFF, 0xFF, 0xFF));
            // 圆角白底：用 gradient drawable
            try
            {
                var bg = new global::Android.Graphics.Drawables.GradientDrawable();
                bg.SetColor(Color.Argb(0xF5, 0xFF, 0xFF, 0xFF));
                bg.SetCornerRadius(22f * density);
                bg.SetStroke(Math.Max(1, (int)density), Color.Argb(0x40, 0, 0, 0));
                bar.Background = bg;
            }
            catch { /* ignore */ }

            var gap = (int)(8 * density);

            // 识别区：抽样（截图+OCR 存 PNG）→ 取消 → 确定
            if (ProgramRegionKeys.IsOcrKey(_settingKey))
            {
                _sample = MakeToolButton(ctx, density, "抽", Color.Argb(0xFF, 0x25, 0x63, 0xEB));
                _sample.Click += (_, _) => OnSample();
                bar.AddView(_sample, new LinearLayout.LayoutParams(
                    ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent)
                { RightMargin = gap });
            }

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

        /// <summary>
        /// 直接裁切当前识别区截图 → OCR → 存 PNG+TXT（绿框描边可留在图内，一般不影响识别）。
        /// </summary>
        private void OnSample()
        {
            if (_sampling || _box == null || _root == null) return;
            if (!ProgramRegionKeys.IsOcrKey(_settingKey)) return;

            RefreshScreen();
            var (l, t, r, b) = _box.GetPixelRect();
            var il = (int)Math.Round(Math.Min(l, r));
            var it = (int)Math.Round(Math.Min(t, b));
            var ir = (int)Math.Round(Math.Max(l, r));
            var ib = (int)Math.Round(Math.Max(t, b));
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

            // 截屏前先藏浮层（半透明罩/绿框会进图，导致无图或 OCR 空）
            SetOverlayVisible(false);

            Task.Run(() =>
            {
                string? path = null;
                var text = "";
                string? err = null;
                try
                {
                    // 等合成层撤掉再截
                    try { System.Threading.Thread.Sleep(120); } catch { /* ignore */ }

                    var svc = FlyScreenInputAccessibilityService.Instance;
                    if (svc == null)
                    {
                        err = "无障碍未连接，无法抽样截屏";
                    }
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
                    if (_root != null)
                        _root.Visibility = visible ? ViewStates.Visible : ViewStates.Invisible;
                }
                catch { /* ignore */ }
            }

            var main = Looper.MainLooper;
            if (main != null && Looper.MyLooper() != main)
                new Handler(main).Post(Apply);
            else
                Apply();
        }

        /// <summary>浮层内状态条 + Application Toast（悬浮窗 Context 上 Toast 常不显示）。</summary>
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

                // 必须用 Application Context，否则 Overlay Window 上 Toast 经常不弹出
                var toast = Toast.MakeText(_appCtx, message, ToastLength.Long);
                toast?.Show();
            }
            catch { /* ignore */ }
        }

        private void SetToolbarEnabled(bool enabled)
        {
            if (_toolbar != null) _toolbar.Enabled = enabled;
            if (_sample != null) _sample.Enabled = enabled;
            if (_cancel != null) _cancel.Enabled = enabled;
            if (_confirm != null) _confirm.Enabled = enabled;
        }

        private static Button MakeToolButton(Context ctx, float density, string text, Color fg)
        {
            var btn = new Button(ctx)
            {
                Text = text,
                TextSize = 18f
            };
            btn.SetTextColor(fg);
            btn.SetPadding((int)(18 * density), (int)(8 * density), (int)(18 * density), (int)(8 * density));
            btn.SetMinWidth((int)(48 * density));
            btn.SetMinHeight((int)(40 * density));
            try
            {
                var bg = new global::Android.Graphics.Drawables.GradientDrawable();
                bg.SetColor(Color.Argb(0x12, fg.R, fg.G, fg.B));
                bg.SetCornerRadius(16f * density);
                btn.Background = bg;
            }
            catch
            {
                btn.SetBackgroundColor(Color.Transparent);
            }
            btn.SetAllCaps(false);
            return btn;
        }

        /// <summary>
        /// 工具条贴着选区：优先框下方居中（类截图工具栏），不够则上方/左右，并钳制在屏内防裁切。
        /// </summary>
        private void RelayoutToolbar()
        {
            if (_toolbar == null || _box == null || _root == null) return;

            var (l, t, r, b) = _box.GetPixelRect();
            var gap = 12f * _density;
            var edge = 10f * _density;

            // 尚未 layout 时先量一次
            _toolbar.Measure(
                View.MeasureSpec.MakeMeasureSpec(_screenW, MeasureSpecMode.AtMost),
                View.MeasureSpec.MakeMeasureSpec(_screenH, MeasureSpecMode.AtMost));
            var tw = _toolbar.MeasuredWidth > 0 ? _toolbar.MeasuredWidth : (int)(140 * _density);
            var th = _toolbar.MeasuredHeight > 0 ? _toolbar.MeasuredHeight : (int)(48 * _density);

            var boxCx = (l + r) / 2f;
            // 候选：下 → 上 → 右 → 左（相对选区）
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
                // 右侧垂直居中于框
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
                // 屏内兜底：贴选区内侧底部
                y = Math.Min(b - th - gap, _screenH - th - edge);
                y = Math.Max(edge, y);
                x = boxCx - tw / 2f;
            }

            x = Math.Clamp(x, edge, Math.Max(edge, _screenW - tw - edge));
            y = Math.Clamp(y, edge, Math.Max(edge, _screenH - th - edge));

            if (_toolbar.LayoutParameters is FrameLayout.LayoutParams lp)
            {
                lp.Gravity = GravityFlags.Top | GravityFlags.Left;
                lp.LeftMargin = (int)Math.Round(x);
                lp.TopMargin = (int)Math.Round(y);
                lp.Width = ViewGroup.LayoutParams.WrapContent;
                lp.Height = ViewGroup.LayoutParams.WrapContent;
                _toolbar.LayoutParameters = lp;
            }
            else
            {
                _toolbar.LayoutParameters = new FrameLayout.LayoutParams(
                    ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent)
                {
                    Gravity = GravityFlags.Top | GravityFlags.Left,
                    LeftMargin = (int)Math.Round(x),
                    TopMargin = (int)Math.Round(y)
                };
            }
        }

        private void OnConfirm()
        {
            if (_box == null) return;
            var nr = _box.GetNormRect(_screenW, _screenH);
            var map = ProgramSettingsStore.LoadResolved(_catalog, _prog);
            map[_settingKey] = nr.ToString();
            if (ProgramRegionKeys.IsOcrKey(_settingKey))
                map["debugOcr"] = "false";
            ProgramSettingsStore.SaveValues(_catalog, _prog, map);
            Finish(true);
        }

        private void Finish(bool ok)
        {
            try
            {
                if (_root != null && _wm != null)
                    _wm.RemoveView(_root);
            }
            catch { /* ignore */ }
            _root = null;
            _onDone(ok);
        }

        private void RefreshScreen()
        {
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

    /// <summary>可拖动、四角缩放的标定框（描边+角点，无填充）。</summary>
    private sealed class CalibBoxView : View
    {
        private readonly float _density;
        private readonly Paint _stroke;
        private readonly Paint _corner;
        private readonly Paint _textPaint;
        private readonly Paint _sizePaint;
        private string _caption = "识别区";
        private float _l, _t, _r, _b;
        private float _downX, _downY;
        private float _origL, _origT, _origR, _origB;
        private enum Mode { None, Move, ResizeTl, ResizeTr, ResizeBl, ResizeBr }
        private Mode _mode;

        /// <summary>选区变化时通知宿主重排工具条。</summary>
        public event Action? RectChanged;

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

        public (float L, float T, float R, float B) GetPixelRect() => (_l, _t, _r, _b);

        public void SetNormRect(NormRect nr, int sw, int sh)
        {
            var (l, t, r, b) = nr.ToPixels(sw, sh);
            _l = l; _t = t; _r = r; _b = b;
            Invalidate();
            RectChanged?.Invoke();
        }

        public NormRect GetNormRect(int sw, int sh)
            => NormRect.FromPixels((int)_l, (int)_t, (int)_r, (int)_b, sw, sh);

        public override bool OnTouchEvent(MotionEvent? e)
        {
            if (e == null) return false;
            var x = e.GetX();
            var y = e.GetY();
            var handle = 36f * _density;

            switch (e.ActionMasked)
            {
                case MotionEventActions.Down:
                    _downX = x; _downY = y;
                    _origL = _l; _origT = _t; _origR = _r; _origB = _b;
                    _mode = HitMode(x, y, handle);
                    return _mode != Mode.None;
                case MotionEventActions.Move:
                    if (_mode == Mode.None) return false;
                    var dx = x - _downX;
                    var dy = y - _downY;
                    ApplyDrag(dx, dy);
                    Invalidate();
                    RectChanged?.Invoke();
                    return true;
                case MotionEventActions.Up:
                case MotionEventActions.Cancel:
                    _mode = Mode.None;
                    RectChanged?.Invoke();
                    return true;
            }
            return base.OnTouchEvent(e);
        }

        private Mode HitMode(float x, float y, float handle)
        {
            if (Near(x, _l, handle) && Near(y, _t, handle)) return Mode.ResizeTl;
            if (Near(x, _r, handle) && Near(y, _t, handle)) return Mode.ResizeTr;
            if (Near(x, _l, handle) && Near(y, _b, handle)) return Mode.ResizeBl;
            if (Near(x, _r, handle) && Near(y, _b, handle)) return Mode.ResizeBr;
            if (x >= _l && x <= _r && y >= _t && y <= _b) return Mode.Move;
            return Mode.None;
        }

        private static bool Near(float a, float b, float d) => Math.Abs(a - b) <= d;

        private void ApplyDrag(float dx, float dy)
        {
            var min = 48f * _density;
            var w = Width > 1 ? Width : 1080f;
            var h = Height > 1 ? Height : 1920f;
            switch (_mode)
            {
                case Mode.Move:
                    _l = _origL + dx; _r = _origR + dx;
                    _t = _origT + dy; _b = _origB + dy;
                    break;
                case Mode.ResizeTl:
                    _l = Math.Min(_origL + dx, _origR - min);
                    _t = Math.Min(_origT + dy, _origB - min);
                    break;
                case Mode.ResizeTr:
                    _r = Math.Max(_origR + dx, _origL + min);
                    _t = Math.Min(_origT + dy, _origB - min);
                    break;
                case Mode.ResizeBl:
                    _l = Math.Min(_origL + dx, _origR - min);
                    _b = Math.Max(_origB + dy, _origT + min);
                    break;
                case Mode.ResizeBr:
                    _r = Math.Max(_origR + dx, _origL + min);
                    _b = Math.Max(_origB + dy, _origT + min);
                    break;
            }
            // 钳制在屏内
            var bw = _r - _l;
            var bh = _b - _t;
            if (_l < 0) { _l = 0; _r = bw; }
            if (_t < 0) { _t = 0; _b = bh; }
            if (_r > w) { _r = w; _l = w - bw; }
            if (_b > h) { _b = h; _t = h - bh; }
        }

        protected override void OnDraw(Canvas canvas)
        {
            base.OnDraw(canvas);
            canvas.DrawRect(_l, _t, _r, _b, _stroke);
            var len = 28f * _density;
            // 角点小方块（类截图选区手柄）
            var hs = 5f * _density;
            void Handle(float x, float y)
            {
                canvas.DrawRect(x - hs, y - hs, x + hs, y + hs, _corner);
            }
            canvas.DrawLine(_l, _t, _l + len, _t, _corner);
            canvas.DrawLine(_l, _t, _l, _t + len, _corner);
            canvas.DrawLine(_r, _t, _r - len, _t, _corner);
            canvas.DrawLine(_r, _t, _r, _t + len, _corner);
            canvas.DrawLine(_l, _b, _l + len, _b, _corner);
            canvas.DrawLine(_l, _b, _l, _b - len, _corner);
            canvas.DrawLine(_r, _b, _r - len, _b, _corner);
            canvas.DrawLine(_r, _b, _r, _b - len, _corner);
            Handle(_l, _t); Handle(_r, _t); Handle(_l, _b); Handle(_r, _b);
            Handle((_l + _r) / 2f, _t); Handle((_l + _r) / 2f, _b);
            Handle(_l, (_t + _b) / 2f); Handle(_r, (_t + _b) / 2f);

            canvas.DrawText(_caption, (_l + _r) / 2f, (_t + _b) / 2f, _textPaint);
            var sizeLabel = $"{(int)(_r - _l)} × {(int)(_b - _t)}";
            canvas.DrawText(sizeLabel, _l, Math.Max(_t - 6f * _density, _sizePaint.TextSize + 4), _sizePaint);
        }
    }
}
#endif
