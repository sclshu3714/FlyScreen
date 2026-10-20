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
/// 全屏容器透传触摸，仅候选条本体可点；跨屏输入时收起本机软键盘。
/// </summary>
public static class RemoteCompositionOverlay
{
#if ANDROID
    private static PassThroughFrameLayout? _root;
    private static LinearLayout? _panel;
    private static TextView? _reading;
    private static Android.Views.View? _readingUnderline;
    private static LinearLayout? _candidatesRow;
    private static AButton? _prev;
    private static AButton? _next;
    private static string? _sourcePeer;
    private static List<string> _candidates = [];
    private static int _numberBase = 1;
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
            if (_root == null || _panel == null || _reading == null || _candidatesRow == null) return;
            _sourcePeer = peer;
            _numberBase = payload.NumberBase <= 0 ? 1 : payload.NumberBase;
            _candidates = payload.Candidates?.ToList() ?? [];
            var reading = payload.Reading ?? "";
            _reading.Text = reading;
            var readingVisible = !string.IsNullOrEmpty(reading);
            _reading.Visibility = readingVisible ? ViewStates.Visible : ViewStates.Gone;
            if (_readingUnderline != null)
                _readingUnderline.Visibility = readingVisible ? ViewStates.Visible : ViewStates.Gone;
            RebuildCandidates(payload.Highlight);
            if (_prev != null) _prev.Enabled = payload.CanPagePrev;
            if (_next != null) _next.Enabled = payload.CanPageNext;
            PlaceNearCursor();
            _root.Visibility = ViewStates.Visible;
            _root.BringToFront();
            SoftKeyboardGuard.Hide();
        });
#else
        _ = peer;
        _ = payload;
#endif
    }

    public static void Hide()
    {
#if ANDROID
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (_root == null) return;
            _root.Visibility = ViewStates.Gone;
            _sourcePeer = null;
            _candidates = [];
            _candidatesRow?.RemoveAllViews();
            if (_reading != null) _reading.Text = "";
            if (_readingUnderline != null) _readingUnderline.Visibility = ViewStates.Gone;
        });
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
            Clickable = false,
            Focusable = false,
            Elevation = 56f * density
        };
        _root.SetBackgroundColor(AColor.Transparent);
        _root.Visibility = ViewStates.Gone;
        _root.ShouldHandleTouch = IsInsidePanel;

        // 白底圆角浮层（对齐桌面候选窗）
        _panel = new LinearLayout(activity)
        {
            Orientation = Orientation.Vertical,
            Clickable = true,
            Focusable = false,
            Elevation = 10f * density
        };
        var border = new GradientDrawable();
        border.SetColor(AColor.White);
        border.SetCornerRadius(10f * density);
        border.SetStroke(Math.Max(1, (int)(0.5f * density)), AColor.Argb(40, 0, 0, 0));
        _panel.Background = border;
        _panel.SetPadding(padH, padV, padH / 2, padV);

        // 拼音行 + 虚线下划线
        var readingBlock = new LinearLayout(activity)
        {
            Orientation = Orientation.Vertical,
            Clickable = false
        };
        _reading = new TextView(activity)
        {
            TextSize = 15f,
            Ellipsize = Android.Text.TextUtils.TruncateAt.End,
            Typeface = Typeface.Monospace
        };
        _reading.SetMaxLines(1);
        _reading.SetTextColor(AColor.Rgb(40, 40, 40));
        readingBlock.AddView(_reading, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent));

        _readingUnderline = new Android.Views.View(activity);
        var dash = new GradientDrawable();
        dash.SetShape(ShapeType.Rectangle);
        dash.SetColor(AColor.Transparent);
        dash.SetStroke(Math.Max(1, (int)(1.2f * density)), AColor.Rgb(120, 120, 128), 3f * density, 3f * density);
        _readingUnderline.Background = dash;
        _readingUnderline.Visibility = ViewStates.Gone;
        readingBlock.AddView(_readingUnderline, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, Math.Max(2, (int)(1.5f * density)))
        {
            TopMargin = (int)(2 * density),
            BottomMargin = (int)(6 * density)
        });
        _panel.AddView(readingBlock, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent));

        // 候选行：词组… | 分隔 | ◀ ▶
        var candLine = new LinearLayout(activity)
        {
            Orientation = Orientation.Horizontal,
            Clickable = true
        };
        candLine.SetGravity(GravityFlags.CenterVertical);

        _candidatesRow = new LinearLayout(activity)
        {
            Orientation = Orientation.Horizontal,
            Clickable = true
        };
        _candidatesRow.SetGravity(GravityFlags.CenterVertical);
        candLine.AddView(_candidatesRow, new LinearLayout.LayoutParams(
            0, ViewGroup.LayoutParams.WrapContent, 1f));

        var divider = new Android.Views.View(activity);
        divider.SetBackgroundColor(AColor.Rgb(220, 220, 224));
        candLine.AddView(divider, new LinearLayout.LayoutParams(
            Math.Max(1, (int)(1 * density)), (int)(22 * density))
        {
            LeftMargin = (int)(4 * density),
            RightMargin = (int)(2 * density)
        });

        _prev = MakePageButton(activity, "◂", -10, density);
        candLine.AddView(_prev, new LinearLayout.LayoutParams(
            (int)(30 * density), (int)(30 * density)));

        _next = MakePageButton(activity, "▸", -11, density);
        candLine.AddView(_next, new LinearLayout.LayoutParams(
            (int)(30 * density), (int)(30 * density)));

        _panel.AddView(candLine, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent));

        _root.AddView(_panel, new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent));

        decor.AddView(_root, new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));
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
        b.SetTextColor(AColor.Rgb(90, 90, 98));
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
        if (_candidatesRow == null) return;
        _candidatesRow.RemoveAllViews();
        var activity = Platform.CurrentActivity;
        if (activity == null) return;
        var density = activity.Resources?.DisplayMetrics?.Density ?? 2f;
        if (highlight < 0 && _candidates.Count > 0) highlight = 0;

        for (var i = 0; i < _candidates.Count; i++)
        {
            var idx = i;
            var text = _candidates[i];
            var num = _numberBase + i;
            var selected = idx == highlight;

            var cell = new LinearLayout(activity)
            {
                Orientation = Orientation.Horizontal,
                Clickable = true,
                Focusable = true
            };
            cell.SetGravity(GravityFlags.CenterVertical);

            var cellBg = new GradientDrawable();
            cellBg.SetCornerRadius(4f * density);
            cellBg.SetColor(selected ? AColor.Rgb(236, 236, 240) : AColor.Transparent);
            cell.Background = cellBg;
            cell.SetPadding(0, (int)(2 * density), (int)(8 * density), (int)(2 * density));

            if (selected)
            {
                var accent = new Android.Views.View(activity);
                accent.SetBackgroundColor(AColor.Rgb(0, 120, 215));
                cell.AddView(accent, new LinearLayout.LayoutParams(
                    Math.Max(2, (int)(3 * density)), ViewGroup.LayoutParams.MatchParent)
                {
                    RightMargin = (int)(6 * density)
                });
            }
            else
            {
                cell.SetPadding((int)(8 * density), (int)(2 * density), (int)(8 * density), (int)(2 * density));
            }

            var label = new TextView(activity)
            {
                Text = $"{num} {text}",
                TextSize = 14f
            };
            label.SetTextColor(AColor.Rgb(28, 28, 30));
            label.SetMaxLines(1);
            cell.AddView(label, new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent));

            cell.Click += (_, _) =>
            {
                if (string.IsNullOrEmpty(_sourcePeer)) return;
                SoftKeyboardGuard.Hide();
                _ = SendSelectAsync(_sourcePeer, idx, text);
            };

            _candidatesRow.AddView(cell, new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent)
            {
                RightMargin = (int)(2 * density)
            });
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

        var maxX = Math.Max(0, _root.Width - pw - 8);
        var maxY = Math.Max(0, _root.Height - ph - 8);
        if (_root.Width <= 0)
        {
            var dm = activity.Resources?.DisplayMetrics;
            maxX = Math.Max(0, (dm?.WidthPixels ?? 1080) - pw - 8);
            maxY = Math.Max(0, (dm?.HeightPixels ?? 1920) - ph - 8);
        }

        localX = Math.Clamp(localX, 8, maxX);
        localY = Math.Clamp(localY, 8, maxY);

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

    private static void TryGetAnchorPixels(out int x, out int y)
    {
        if (FlyScreenCursorOverlay.TryGetHotspotPixels(out x, out y))
            return;

        var a11y = FlyScreenInputAccessibilityService.Instance;
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

    /// <summary>全屏层：点在候选条外则不消费事件，触屏仍落到下层 UI。</summary>
    private sealed class PassThroughFrameLayout : FrameLayout
    {
        public Func<MotionEvent, bool>? ShouldHandleTouch { get; set; }

        public PassThroughFrameLayout(global::Android.Content.Context context) : base(context) { }

        public override bool DispatchTouchEvent(MotionEvent? e)
        {
            if (e == null) return false;
            if (ShouldHandleTouch != null && !ShouldHandleTouch(e))
                return false;
            return base.DispatchTouchEvent(e);
        }
    }
#endif
}
