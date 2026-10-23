#if ANDROID
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Views;
using Android.Widget;
using AColor = global::Android.Graphics.Color;
using AButton = Android.Widget.Button;
using GravityFlags = global::Android.Views.GravityFlags;
using ViewStates = global::Android.Views.ViewStates;
using FlyScreen.AppMaui.Platforms.Android;
#endif
using FlyScreen.Services.Network;

namespace FlyScreen.AppMaui.Services;

/// <summary>
/// 电脑后台组字 → 手机光标旁浮动候选条（拼音虚线 + 编号词组，样式对齐桌面 IME）。
/// 全屏容器透传触摸，仅候选条本体可点；点空白处取消组字；跨屏输入时收起本机软键盘。
/// </summary>
public static class RemoteCompositionOverlay
{
#if ANDROID
    private static PassThroughFrameLayout? _root;
    private static LinearLayout? _panel;
    private static LinearLayout? _readingBlock;
    private static TextView? _reading;
    private static Android.Views.View? _readingUnderline;
    private static LinearLayout? _candFrame;
    private static LinearLayout? _candidatesHost;
    private static LinearLayout? _footerRow;
    private static AButton? _prev;
    private static AButton? _next;
    private static TextView? _closeMulti;
    private static string? _sourcePeer;
    private static List<string> _candidates = [];
    private static int _numberBase = 1;
    private static bool _multiLine;
    private static int _gridColumns = 5;
    private static bool _visible;
#endif

    public static void Attach(Microsoft.Maui.Controls.Window window)
    {
#if ANDROID
        window.Created += (_, _) => MainThread.BeginInvokeOnMainThread(Ensure);
        window.HandlerChanged += (_, _) => MainThread.BeginInvokeOnMainThread(Ensure);
        MainThread.BeginInvokeOnMainThread(Ensure);
#else
        _ = window;
#endif
    }

    public static void Show(string peer, CompositionUpdatePayload payload)
    {
#if ANDROID
        MainThread.BeginInvokeOnMainThread(() =>
        {
            Ensure();
            if (_root == null || _panel == null || _reading == null || _candidatesHost == null) return;

            var reading = payload.Reading ?? "";
            var candidates = payload.Candidates?.ToList() ?? [];
            if (string.IsNullOrWhiteSpace(reading) && candidates.Count == 0)
            {
                Hide();
                return;
            }

            _sourcePeer = peer;
            _numberBase = payload.NumberBase <= 0 ? 1 : payload.NumberBase;
            _candidates = candidates;
            _multiLine = payload.MultiLine;
            _gridColumns = payload.GridColumns > 0 ? payload.GridColumns : 5;

            // 拼音行始终白底深色字（深色编辑器上也能看清）
            _reading.Text = reading;
            _reading.Visibility = ViewStates.Visible;
            if (_readingBlock != null)
                _readingBlock.Visibility = ViewStates.Visible;
            if (_readingUnderline != null)
                _readingUnderline.Visibility = string.IsNullOrEmpty(reading)
                    ? ViewStates.Gone
                    : ViewStates.Visible;

            RebuildCandidates(payload.Highlight);
            if (_prev != null) _prev.Enabled = payload.CanPagePrev;
            if (_next != null) _next.Enabled = payload.CanPageNext;
            if (_closeMulti != null)
                _closeMulti.Visibility = _multiLine ? ViewStates.Visible : ViewStates.Gone;

            try { PlaceNearCursor(); }
            catch { /* 布局未就绪时勿崩进程 */ }
            _root.Visibility = ViewStates.Visible;
            // 必须可点：否则空白区域触摸不会进 DispatchTouchEvent，无法取消组字
            _root.Clickable = true;
            _visible = true;
            _root.BringToFront();
            SoftKeyboardGuard.Hide();
            try { CompositionCaretAnchor.RequestRefresh?.Invoke(); } catch { /* ignore */ }
            MainThread.BeginInvokeOnMainThread(() =>
            {
                try { PlaceNearCursor(); } catch { /* ignore */ }
            });
        });
#else
        _ = peer;
        _ = payload;
#endif
    }

    public static void Hide()
    {
#if ANDROID
        if (MainThread.IsMainThread) HideCore();
        else MainThread.BeginInvokeOnMainThread(HideCore);
#endif
    }

#if ANDROID
    private static void HideCore()
    {
        if (_root == null) return;
        _root.Visibility = ViewStates.Gone;
        _root.Clickable = false;
        _visible = false;
        _sourcePeer = null;
        _candidates = [];
        _multiLine = false;
        _candidatesHost?.RemoveAllViews();
        if (_reading != null) _reading.Text = "";
        if (_readingUnderline != null) _readingUnderline.Visibility = ViewStates.Gone;
        if (_closeMulti != null) _closeMulti.Visibility = ViewStates.Gone;
    }
#endif

    /// <summary>切应用 / 点空白：本地隐藏并通知电脑取消组字。</summary>
    public static void DismissAndCancel()
    {
#if ANDROID
        void Core()
        {
            if (!_visible && string.IsNullOrEmpty(_sourcePeer)) return;
            var peer = _sourcePeer;
            // 先发取消再藏，避免 peer 被清空；电脑侧 HardReset Imm
            if (!string.IsNullOrEmpty(peer))
                _ = SendCancelAsync(peer);
            HideCore();
        }

        if (MainThread.IsMainThread) Core();
        else MainThread.BeginInvokeOnMainThread(Core);
#endif
    }

    /// <summary>远程鼠标按下时：若热点不在候选条内则取消组字。</summary>
    public static void DismissIfPointerOutside()
    {
#if ANDROID
        void Core()
        {
            if (!_visible || _panel == null) return;
            if (!FlyScreenCursorOverlay.TryGetHotspotPixels(out var sx, out var sy)
                && !(FlyScreenInputAccessibilityService.Instance?.TryGetCursorScreenPixels(out sx, out sy) ?? false))
                return;

            var loc = new int[2];
            _panel.GetLocationOnScreen(loc);
            var left = loc[0];
            var top = loc[1];
            var right = left + Math.Max(_panel.Width, 1);
            var bottom = top + Math.Max(_panel.Height, 1);
            if (sx >= left && sx <= right && sy >= top && sy <= bottom)
                return;

            DismissAndCancel();
        }

        if (MainThread.IsMainThread) Core();
        else MainThread.BeginInvokeOnMainThread(Core);
#endif
    }

#if ANDROID
    private static void Ensure()
    {
        if (_root?.Parent != null) return;
        var activity = Platform.CurrentActivity;
        if (activity?.Window?.DecorView is not ViewGroup decor) return;

        var density = activity.Resources?.DisplayMetrics?.Density ?? 2f;
        var padH = (int)(10 * density);
        var padV = (int)(8 * density);

        _root = new PassThroughFrameLayout(activity)
        {
            // Show 时再设为 true；Gone 时 false，避免挡住下层
            Clickable = false,
            Focusable = false,
            Elevation = 56f * density
        };
        _root.SetBackgroundColor(AColor.Transparent);
        _root.Visibility = ViewStates.Gone;
        _root.ShouldHandleTouch = IsInsidePanel;
        _root.OnOutsideTouch = OnOutsideTouch;

        // 整体白底卡片（拼音 + 候选），深色背景下拼音可读
        _panel = new LinearLayout(activity)
        {
            Orientation = Orientation.Vertical,
            Clickable = true,
            Focusable = false,
            Elevation = 10f * density
        };
        var panelBg = new GradientDrawable();
        panelBg.SetColor(AColor.White);
        panelBg.SetCornerRadius(4f * density);
        panelBg.SetStroke(Math.Max(2, (int)(1.5f * density)), AColor.Rgb(0, 120, 215));
        _panel.Background = panelBg;
        _panel.SetPadding(padH / 2, padV / 2, padH / 2, padV / 2);

        // —— 第一行：拼音 + 虚线下划线 ——
        _readingBlock = new LinearLayout(activity)
        {
            Orientation = Orientation.Vertical,
            Clickable = false
        };
        _readingBlock.SetPadding(padH / 4, 0, padH / 4, 0);
        _reading = new TextView(activity)
        {
            TextSize = 15f,
            Ellipsize = Android.Text.TextUtils.TruncateAt.End
        };
        _reading.SetMaxLines(1);
        _reading.SetTextColor(AColor.Rgb(32, 32, 32));
        _readingBlock.AddView(_reading, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent));

        _readingUnderline = new Android.Views.View(activity);
        var dash = new GradientDrawable();
        dash.SetShape(ShapeType.Rectangle);
        dash.SetColor(AColor.Transparent);
        dash.SetStroke(Math.Max(1, (int)(1.2f * density)), AColor.Rgb(100, 100, 108), 3f * density, 3f * density);
        _readingUnderline.Background = dash;
        _readingUnderline.Visibility = ViewStates.Gone;
        _readingBlock.AddView(_readingUnderline, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, Math.Max(2, (int)(1.5f * density)))
        {
            TopMargin = (int)(2 * density),
            BottomMargin = (int)(6 * density)
        });
        _panel.AddView(_readingBlock, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));

        // —— 候选区（单行或网格）——
        _candFrame = new LinearLayout(activity)
        {
            Orientation = Orientation.Vertical,
            Clickable = true
        };
        _candidatesHost = new LinearLayout(activity)
        {
            Orientation = Orientation.Vertical,
            Clickable = true
        };
        _candFrame.AddView(_candidatesHost, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));

        // 底栏：翻页 + 关闭多行
        _footerRow = new LinearLayout(activity)
        {
            Orientation = Orientation.Horizontal,
            Clickable = true
        };
        _footerRow.SetGravity(GravityFlags.CenterVertical);
        _prev = MakePageButton(activity, "◂", -10, density);
        _next = MakePageButton(activity, "▸", -11, density);
        _footerRow.AddView(_prev, new LinearLayout.LayoutParams(
            (int)(28 * density), (int)(28 * density)));
        _footerRow.AddView(_next, new LinearLayout.LayoutParams(
            (int)(28 * density), (int)(28 * density)));

        var spacer = new Android.Views.View(activity);
        _footerRow.AddView(spacer, new LinearLayout.LayoutParams(0, 1, 1f));

        _closeMulti = new TextView(activity)
        {
            Text = "关闭多行候选",
            TextSize = 12f,
            Clickable = true
        };
        _closeMulti.SetTextColor(AColor.Rgb(0, 120, 215));
        _closeMulti.SetPadding((int)(8 * density), (int)(4 * density), (int)(4 * density), (int)(4 * density));
        _closeMulti.Visibility = ViewStates.Gone;
        _closeMulti.Click += (_, _) =>
        {
            if (string.IsNullOrEmpty(_sourcePeer)) return;
            SoftKeyboardGuard.Hide();
            _ = SendSelectAsync(_sourcePeer, -12, null);
        };
        _footerRow.AddView(_closeMulti, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent));

        _candFrame.AddView(_footerRow, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent)
        {
            TopMargin = (int)(4 * density)
        });

        _panel.AddView(_candFrame, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent));

        _root.AddView(_panel, new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent));

        decor.AddView(_root, new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));
    }

    private static void OnOutsideTouch()
    {
        DismissAndCancel();
    }

    private static bool IsInsidePanel(MotionEvent e)
    {
        if (_panel == null || _root == null) return false;
        var x = e.GetX();
        var y = e.GetY();
        return x >= _panel.Left && x <= _panel.Right && y >= _panel.Top && y <= _panel.Bottom;
    }

    private static AButton MakePageButton(global::Android.Content.Context ctx, string label, int selectIndex, float density)
    {
        var b = new AButton(ctx)
        {
            Text = label,
            TextSize = 13f
        };
        b.SetBackgroundColor(AColor.Transparent);
        b.SetTextColor(AColor.Rgb(0, 120, 215));
        b.SetPadding(0, 0, 0, 0);
        b.SetMinWidth(0);
        b.SetMinimumWidth(0);
        b.SetMinHeight(0);
        b.SetMinimumHeight(0);
        b.Click += (_, _) =>
        {
            if (string.IsNullOrEmpty(_sourcePeer)) return;
            SoftKeyboardGuard.Hide();
            _ = SendSelectAsync(_sourcePeer, selectIndex, null);
        };
        return b;
    }

    private static void RebuildCandidates(int highlight)
    {
        if (_candidatesHost == null) return;
        _candidatesHost.RemoveAllViews();
        var activity = Platform.CurrentActivity;
        if (activity == null) return;
        var density = activity.Resources?.DisplayMetrics?.Density ?? 2f;
        if (highlight < 0 && _candidates.Count > 0) highlight = 0;

        // 逻辑列数：多行时按此分行；数字 1–N 只标在高亮所在行（对齐桌面 IME）
        var cols = Math.Max(1, _gridColumns);
        var activeRow = _multiLine && highlight >= 0 ? highlight / cols : 0;

        // 纵向展示，但按逻辑行分组 + 分隔线
        for (var i = 0; i < _candidates.Count; i++)
        {
            var idx = i;
            var text = _candidates[i];
            var row = _multiLine ? idx / cols : 0;
            var colInRow = _multiLine ? idx % cols : idx;
            var selected = idx == highlight;
            var onActiveRow = !_multiLine || row == activeRow;

            // 行首分隔（多行）：视觉上区分逻辑行
            if (_multiLine && colInRow == 0 && idx > 0)
            {
                var sep = new Android.Views.View(activity);
                sep.SetBackgroundColor(AColor.Rgb(220, 220, 224));
                _candidatesHost.AddView(sep, new LinearLayout.LayoutParams(
                    ViewGroup.LayoutParams.MatchParent, Math.Max(1, (int)(1 * density)))
                {
                    TopMargin = (int)(2 * density),
                    BottomMargin = (int)(2 * density)
                });
            }

            var cell = new LinearLayout(activity)
            {
                Orientation = Orientation.Horizontal,
                Clickable = true,
                Focusable = true
            };
            cell.SetGravity(GravityFlags.CenterVertical);
            cell.SetPadding((int)(10 * density), (int)(5 * density), (int)(10 * density), (int)(5 * density));

            // 单行：全程编号；多行：仅当前行 1..cols
            string labelText;
            if (!_multiLine)
                labelText = $"{_numberBase + i}.{text}";
            else if (onActiveRow)
                labelText = $"{colInRow + 1}.{text}";
            else
                labelText = text;

            var label = new TextView(activity)
            {
                Text = labelText,
                TextSize = 14f
            };
            label.SetTextColor(selected
                ? AColor.White
                : onActiveRow
                    ? AColor.Rgb(28, 28, 30)
                    : AColor.Rgb(110, 110, 118));
            label.SetMaxLines(1);
            label.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
            cell.AddView(label, new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent));

            if (selected)
            {
                var bg = new GradientDrawable();
                bg.SetColor(AColor.Rgb(0, 120, 215));
                bg.SetCornerRadius(2f * density);
                cell.Background = bg;
            }
            else if (_multiLine && onActiveRow)
            {
                // 当前行浅底，提示数字键作用域（附件2）
                cell.SetBackgroundColor(AColor.Argb(40, 0, 120, 215));
            }

            cell.Click += (_, _) =>
            {
                if (string.IsNullOrEmpty(_sourcePeer)) return;
                SoftKeyboardGuard.Hide();
                _ = SendSelectAsync(_sourcePeer, idx, text);
            };

            _candidatesHost.AddView(cell, new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
        }
    }

    private static void PlaceNearCursor()
    {
        if (_root == null || _panel == null) return;
        var activity = Platform.CurrentActivity;
        if (activity == null) return;

        var density = activity.Resources?.DisplayMetrics?.Density ?? 2f;
        TryGetAnchorPixels(out var ax, out var ay);

        var decorLoc = new int[2];
        if (_root.Parent is ViewGroup parent)
            parent.GetLocationOnScreen(decorLoc);
        else
            _root.GetLocationOnScreen(decorLoc);

        var localX = ax - decorLoc[0];
        var localY = ay - decorLoc[1] + (int)(24 * density);

        _panel.Measure(
            Android.Views.View.MeasureSpec.MakeMeasureSpec(0, MeasureSpecMode.Unspecified),
            Android.Views.View.MeasureSpec.MakeMeasureSpec(0, MeasureSpecMode.Unspecified));
        var pw = Math.Max(_panel.MeasuredWidth, (int)(200 * density));
        var ph = Math.Max(_panel.MeasuredHeight, (int)(64 * density));

        var areaW = _root.Width > 0 ? _root.Width : (activity.Resources?.DisplayMetrics?.WidthPixels ?? 1080);
        var areaH = _root.Height > 0 ? _root.Height : (activity.Resources?.DisplayMetrics?.HeightPixels ?? 1920);
        var maxX = Math.Max(0, areaW - pw - 8);
        var maxY = Math.Max(0, areaH - ph - 8);

        localX = SafeClamp(localX, 0, maxX);
        localY = SafeClamp(localY, 0, maxY);

        if (_panel.LayoutParameters is FrameLayout.LayoutParams lp)
        {
            lp.Gravity = GravityFlags.Top | GravityFlags.Start;
            lp.LeftMargin = localX;
            lp.TopMargin = localY;
            lp.Width = ViewGroup.LayoutParams.WrapContent;
            lp.Height = ViewGroup.LayoutParams.WrapContent;
            _panel.LayoutParameters = lp;
        }
        else
        {
            _panel.SetX(localX);
            _panel.SetY(localY);
        }
    }

    private static int SafeClamp(int value, int min, int max)
    {
        if (max < min) return min;
        if (value < min) return min;
        if (value > max) return max;
        return value;
    }

    private static void TryGetAnchorPixels(out int x, out int y)
    {
        if (CompositionCaretAnchor.TryGetScreenPixels(out x, out y))
            return;

        var a11y = FlyScreenInputAccessibilityService.Instance;
        if (a11y != null && a11y.TryGetFocusedEditableCaretScreenPixels(out x, out y))
            return;

        if (FlyScreenCursorOverlay.TryGetHotspotPixels(out x, out y))
            return;

        if (a11y != null && a11y.TryGetCursorScreenPixels(out x, out y))
            return;

        var dm = Platform.CurrentActivity?.Resources?.DisplayMetrics;
        x = (dm?.WidthPixels ?? 1080) / 2;
        y = (dm?.HeightPixels ?? 1920) / 3;
    }

    private static async Task SendSelectAsync(string peer, int index, string? text)
    {
        try
        {
            var host = MauiAppHost.EnsureCreated();
            await host.Connections.SendToAsync(peer, MessageType.CompositionSelect,
                MessageCodec.JsonBytes(new CompositionSelectPayload { Index = index, Text = text }))
                .ConfigureAwait(false);
        }
        catch
        {
            /* ignore */
        }
    }

    private static async Task SendCancelAsync(string peer)
    {
        try
        {
            var host = MauiAppHost.EnsureCreated();
            await host.Connections.SendToAsync(peer, MessageType.CompositionCancel, Array.Empty<byte>())
                .ConfigureAwait(false);
        }
        catch
        {
            /* ignore */
        }
    }

    /// <summary>全屏层：点在候选条外则取消组字并透传事件。</summary>
    private sealed class PassThroughFrameLayout : FrameLayout
    {
        public Func<MotionEvent, bool>? ShouldHandleTouch { get; set; }
        public Action? OnOutsideTouch { get; set; }

        public PassThroughFrameLayout(global::Android.Content.Context context) : base(context) { }

        public override bool DispatchTouchEvent(MotionEvent? e)
        {
            if (e == null) return false;
            if (ShouldHandleTouch != null && !ShouldHandleTouch(e))
            {
                // 空白处：取消组字并消费 Down，避免只藏 UI、电脑 Imm 未清
                if (e.ActionMasked == MotionEventActions.Down)
                    OnOutsideTouch?.Invoke();
                return true;
            }
            return base.DispatchTouchEvent(e);
        }
    }
#endif
}
