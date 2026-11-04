#if WINDOWS
using System.Runtime.CompilerServices;
using FlyScreen.AppMaui.Helpers;
using FlyScreen.Helpers;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinRT.Interop;
using WinButton = Microsoft.UI.Xaml.Controls.Button;
using WinStackPanel = Microsoft.UI.Xaml.Controls.StackPanel;
using WinTextBlock = Microsoft.UI.Xaml.Controls.TextBlock;
using WinOrientation = Microsoft.UI.Xaml.Controls.Orientation;
using WinThickness = Microsoft.UI.Xaml.Thickness;
using WinVerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment;

namespace FlyScreen.AppMaui.Platforms.Windows;

/// <summary>
/// 标题栏投影按钮：与 Core <see cref="ProjectionUi"/> 状态机同步。
/// 悬停刷新必须 Post/Enqueue，禁止在 Pointer 同步回调里直接改 WinUI（否则 RPC_E_CANTCALLOUT_ININPUTSYNCCALL）。
/// </summary>
internal static class MauiProjectionTitleBar
{
    public static Action? OpenProjection { get; set; }
    public static Func<ProjectionUiPhase>? ResolvePhase { get; set; }

    private static readonly HashSet<int> Hooked = [];
    private static WinButton? _btn;
    private static WinTextBlock? _iconTb;
    private static WinTextBlock? _labelTb;
    private static bool _hovered;
    private static bool _pointerHooked;
    private static bool _refreshQueued;
    private static string? _lastTooltip;

    public static void TryHook(Microsoft.Maui.Controls.Window window)
    {
        var id = RuntimeHelpers.GetHashCode(window);
        if (!Hooked.Add(id))
        {
            HookOnce(window);
            return;
        }

        window.HandlerChanged += (_, _) => HookOnce(window);
        window.Created += (_, _) => HookOnce(window);
        HookOnce(window);
        window.Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(200), () => HookOnce(window));
        window.Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(800), () => HookOnce(window));
    }

    public static void Refresh() => ScheduleRefresh();

    /// <summary>兼容旧调用：仅 Hosting/Idle。</summary>
    public static void SetProjecting(bool on) => ScheduleRefresh();

    private static void ScheduleRefresh()
    {
        if (_refreshQueued) return;
        _refreshQueued = true;

        void Run()
        {
            _refreshQueued = false;
            try { ApplyChromeNow(); }
            catch { /* ignore */ }
        }

        try
        {
            var dq = _btn?.DispatcherQueue;
            if (dq != null && dq.TryEnqueue(DispatcherQueuePriority.Normal, Run))
                return;
        }
        catch { /* fall through */ }

        MainThread.BeginInvokeOnMainThread(Run);
    }

    private static void ApplyChromeNow()
    {
        if (_btn == null) return;
        var phase = ResolvePhase?.Invoke() ?? ProjectionUiPhase.Idle;
        var chrome = ProjectionUi.Title(phase, _hovered);
        var brush = ResolveForeground(chrome.Emphasized);

        EnsureContentHost();
        if (_iconTb != null)
        {
            _iconTb.Text = chrome.Mdl2Glyph;
            _iconTb.Foreground = brush;
        }

        if (_labelTb != null)
        {
            _labelTb.Text = chrome.Label;
            _labelTb.Foreground = brush;
        }

        _btn.Width = chrome.SuggestedWidth;
        _btn.MinWidth = chrome.SuggestedWidth;
        _btn.Padding = new WinThickness(8, 0, 8, 0);
        _btn.Foreground = brush;

        // ToolTip 也是 COM 出站；仅在文案变化时更新，避免悬停风暴
        if (!string.Equals(_lastTooltip, chrome.Tooltip, StringComparison.Ordinal))
        {
            _lastTooltip = chrome.Tooltip;
            ToolTipService.SetToolTip(_btn, chrome.Tooltip);
        }
    }

    /// <summary>与 <see cref="MauiWindowChrome"/> 标题栏文字色对齐；深色下勿写死 #111827（会看不见）。</summary>
    private static Microsoft.UI.Xaml.Media.SolidColorBrush ResolveForeground(bool emphasized)
    {
        if (emphasized)
            return new Microsoft.UI.Xaml.Media.SolidColorBrush(
                global::Windows.UI.Color.FromArgb(255, 0x25, 0x63, 0xEB));

        var dark = false;
        try { dark = MauiThemeService.IsDark(MauiThemeService.Current); }
        catch { /* ignore */ }

        return new Microsoft.UI.Xaml.Media.SolidColorBrush(dark
            ? global::Windows.UI.Color.FromArgb(255, 0xE2, 0xE8, 0xF0)
            : global::Windows.UI.Color.FromArgb(255, 0x11, 0x18, 0x27));
    }

    private static void EnsureContentHost()
    {
        if (_btn == null) return;
        if (_iconTb != null && _labelTb != null && _btn.Content is WinStackPanel)
            return;

        _iconTb = new WinTextBlock
        {
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 13,
            VerticalAlignment = WinVerticalAlignment.Center,
            Margin = new WinThickness(0, 0, 5, 0)
        };
        _labelTb = new WinTextBlock
        {
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 11,
            VerticalAlignment = WinVerticalAlignment.Center
        };
        var row = new WinStackPanel { Orientation = WinOrientation.Horizontal };
        row.Children.Add(_iconTb);
        row.Children.Add(_labelTb);
        _btn.Content = row;
    }

    private static void HookOnce(Microsoft.Maui.Controls.Window window)
    {
        try
        {
            if (window.Handler?.PlatformView is not Microsoft.UI.Xaml.Window native)
                return;
            if (native.Content is not DependencyObject tree)
                return;

            var btn = FindByName(tree, "TitleProjectBtn") as WinButton;
            if (btn == null) return;
            if (!ReferenceEquals(_btn, btn))
            {
                _btn = btn;
                _iconTb = null;
                _labelTb = null;
                _lastTooltip = null;
            }

            btn.Click -= OnClick;
            btn.Click += OnClick;

            if (!_pointerHooked)
            {
                _pointerHooked = true;
                // 只改标志位，UI 刷新延后到队列——避免 RPC_E_CANTCALLOUT_ININPUTSYNCCALL
                btn.PointerEntered += (_, _) =>
                {
                    _hovered = true;
                    ScheduleRefresh();
                };
                btn.PointerExited += (_, _) =>
                {
                    _hovered = false;
                    ScheduleRefresh();
                };
            }

            try
            {
                var hwnd = WindowNative.GetWindowHandle(native);
                var wid = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
                var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(wid);
                var inset = (int)appWindow.TitleBar.RightInset;
                if (inset > 0)
                    btn.Margin = new WinThickness(0, 0, Math.Max(inset, 140), 0);
            }
            catch { /* keep default margin */ }

            ScheduleRefresh();
        }
        catch { /* ignore */ }
    }

    private static void OnClick(object sender, RoutedEventArgs e)
    {
        // Click 回调同样可能处于输入同步上下文，业务入口走异步投递
        try
        {
            var open = OpenProjection;
            if (open == null) return;
            MainThread.BeginInvokeOnMainThread(() =>
            {
                try { open(); } catch { /* ignore */ }
            });
        }
        catch { /* ignore */ }
    }

    private static DependencyObject? FindByName(DependencyObject parent, string name)
    {
        if (parent is FrameworkElement fe && fe.Name == name)
            return parent;
        var count = VisualTreeHelper.GetChildrenCount(parent);
        for (var i = 0; i < count; i++)
        {
            var found = FindByName(VisualTreeHelper.GetChild(parent, i), name);
            if (found != null) return found;
        }
        return null;
    }
}
#endif
