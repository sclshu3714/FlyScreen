#if ANDROID
using Android.Graphics;
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
/// 电脑侧组字 → 手机在远程光标旁浮动显示：拼音行 + 编号候选（仿系统 IME，不挡其它触控）。
/// </summary>
public static class RemoteCompositionOverlay
{
#if ANDROID
    private static FrameLayout? _root;
    private static LinearLayout? _panel;
    private static TextView? _reading;
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
            _reading.Text = string.IsNullOrEmpty(payload.Reading) ? "" : payload.Reading;
            _reading.Visibility = string.IsNullOrEmpty(_reading.Text) ? ViewStates.Gone : ViewStates.Visible;
            RebuildCandidates(payload.Highlight);
            if (_prev != null) _prev.Enabled = payload.CanPagePrev;
            if (_next != null) _next.Enabled = payload.CanPageNext;
            PlaceNearCursor();
            _root.Visibility = ViewStates.Visible;
            _root.BringToFront();
            HideSoftKeyboard();
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
        var pad = (int)(8 * density);

        // 全屏透明容器：不拦截触摸；仅内部 panel 可点
        _root = new FrameLayout(activity)
        {
            Clickable = false,
            Focusable = false,
            Elevation = 48f * density
        };
        _root.SetBackgroundColor(AColor.Transparent);
        _root.Visibility = ViewStates.Gone;

        _panel = new LinearLayout(activity)
        {
            Orientation = Orientation.Vertical,
            Clickable = true,
            Focusable = false
        };
        _panel.SetBackgroundColor(AColor.White);
        _panel.SetPadding(pad, pad / 2, pad, pad / 2);
        // 蓝边框（仿系统候选窗）
        var border = new Android.Graphics.Drawables.GradientDrawable();
        border.SetColor(AColor.White);
        border.SetStroke(Math.Max(1, (int)(1.5f * density)), AColor.Rgb(0, 120, 215));
        border.SetCornerRadius(4f * density);
        _panel.Background = border;
        _panel.Elevation = 8f * density;

        _reading = new TextView(activity)
        {
            TextSize = 15f,
            Ellipsize = Android.Text.TextUtils.TruncateAt.End
        };
        _reading.SetMaxLines(1);
        _reading.SetTextColor(AColor.Rgb(32, 32, 32));
        // 虚线式组字提示：下划线
        _reading.PaintFlags |= PaintFlags.UnderlineText;
        _panel.AddView(_reading, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent)
        {
            BottomMargin = (int)(4 * density)
        });

        var candLine = new LinearLayout(activity)
        {
            Orientation = Orientation.Horizontal,
            Clickable = true
        };
        candLine.SetGravity(GravityFlags.CenterVertical);

        _prev = MakePageButton(activity, "◂", -10);
        candLine.AddView(_prev, new LinearLayout.LayoutParams(
            (int)(28 * density), (int)(32 * density)));

        _candidatesRow = new LinearLayout(activity)
        {
            Orientation = Orientation.Horizontal,
            Clickable = true
        };
        _candidatesRow.SetGravity(GravityFlags.CenterVertical);
        candLine.AddView(_candidatesRow, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent)
        {
            LeftMargin = (int)(2 * density),
            RightMargin = (int)(2 * density)
        });

        _next = MakePageButton(activity, "▸", -11);
        candLine.AddView(_next, new LinearLayout.LayoutParams(
            (int)(28 * density), (int)(32 * density)));

        _panel.AddView(candLine, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent));

        _root.AddView(_panel, new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent));

        decor.AddView(_root, new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));

        // 容器不吃触摸：子 panel 自己处理
        _root.Touch += (_, e) =>
        {
            // 点在 panel 外：不消费，让下层收到
            if (e.Event?.Action == MotionEventActions.Down && _panel != null)
            {
                var x = e.Event.GetX();
                var y = e.Event.GetY();
                if (x < _panel.Left || x > _panel.Right || y < _panel.Top || y > _panel.Bottom)
                    e.Handled = false;
            }
        };
    }

    private static AButton MakePageButton(global::Android.Content.Context ctx, string label, int selectIndex)
    {
        var b = new AButton(ctx)
        {
            Text = label,
            TextSize = 14f
        };
        b.SetBackgroundColor(AColor.Transparent);
        b.SetTextColor(AColor.Rgb(80, 80, 90));
        b.SetPadding(0, 0, 0, 0);
        b.SetMinWidth(0);
        b.SetMinimumWidth(0);
        b.Click += (_, _) =>
        {
            if (string.IsNullOrEmpty(_sourcePeer)) return;
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

        for (var i = 0; i < _candidates.Count; i++)
        {
            var idx = i;
            var text = _candidates[i];
            var num = _numberBase + i;
            var btn = new AButton(activity)
            {
                Text = $"{num}.{text}",
                TextSize = 14f,
                Tag = idx
            };
            btn.SetAllCaps(false);
            btn.SetMinWidth(0);
            btn.SetMinimumWidth(0);
            btn.SetBackgroundColor(idx == highlight
                ? AColor.Rgb(0, 120, 215)
                : AColor.Transparent);
            btn.SetTextColor(idx == highlight ? AColor.White : AColor.Rgb(20, 20, 20));
            btn.SetPadding((int)(6 * density), (int)(2 * density), (int)(6 * density), (int)(2 * density));
            btn.Click += (_, _) =>
            {
                if (string.IsNullOrEmpty(_sourcePeer)) return;
                _ = SendSelectAsync(_sourcePeer, idx, text);
            };
            _candidatesRow.AddView(btn, new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent));
        }
    }

    private static void PlaceNearCursor()
    {
        if (_root == null || _panel == null) return;
        var activity = Platform.CurrentActivity;
        if (activity == null) return;

        var density = activity.Resources?.DisplayMetrics?.Density ?? 2f;
        TryGetAnchorPixels(out var ax, out var ay);

        // Decor 坐标：减去状态栏等，用 GetLocationOnScreen 对齐
        var decorLoc = new int[2];
        if (_root.Parent is ViewGroup parent)
            parent.GetLocationOnScreen(decorLoc);
        else
            _root.GetLocationOnScreen(decorLoc);

        var localX = ax - decorLoc[0];
        var localY = ay - decorLoc[1] + (int)(22 * density); // 光标下方

        _panel.Measure(
            View.MeasureSpec.MakeMeasureSpec(0, MeasureSpecMode.Unspecified),
            View.MeasureSpec.MakeMeasureSpec(0, MeasureSpecMode.Unspecified));
        var pw = Math.Max(_panel.MeasuredWidth, (int)(160 * density));
        var ph = Math.Max(_panel.MeasuredHeight, (int)(56 * density));

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

    private static void HideSoftKeyboard()
    {
        try
        {
            var activity = Platform.CurrentActivity;
            var token = activity?.CurrentFocus?.WindowToken ?? activity?.Window?.DecorView?.WindowToken;
            if (activity == null || token == null) return;
            var imm = activity.GetSystemService(global::Android.Content.Context.InputMethodService)
                as global::Android.Views.InputMethods.InputMethodManager;
            imm?.HideSoftInputFromWindow(token, global::Android.Views.InputMethods.HideSoftInputFlags.None);
        }
        catch
        {
            /* ignore */
        }
    }
#endif
}
