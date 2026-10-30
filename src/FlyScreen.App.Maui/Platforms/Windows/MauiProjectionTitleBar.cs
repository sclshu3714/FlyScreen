#if WINDOWS
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinRT.Interop;
using WinButton = Microsoft.UI.Xaml.Controls.Button;

namespace FlyScreen.AppMaui.Platforms.Windows;

/// <summary>标题栏投影按钮：打开弹窗 / 投影中可停止。</summary>
internal static class MauiProjectionTitleBar
{
    public static Action? OpenProjection { get; set; }
    private static readonly HashSet<int> Hooked = [];
    private static WinButton? _btn;

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

    public static void SetProjecting(bool on)
    {
        try
        {
            if (_btn == null) return;
            _btn.Content = on ? "投影中" : "\uE8B9";
            ToolTipService.SetToolTip(_btn, on ? "正在投影（点击停止）" : "投影到电脑");
            _btn.Width = on ? 72 : 46;
            _btn.FontFamily = new FontFamily(on ? "Segoe UI" : "Segoe MDL2 Assets");
            _btn.FontSize = on ? 11 : 13;
        }
        catch { /* ignore */ }
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
            _btn = btn;

            btn.Click -= OnClick;
            btn.Click += OnClick;

            try
            {
                var hwnd = WindowNative.GetWindowHandle(native);
                var wid = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
                var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(wid);
                var inset = (int)appWindow.TitleBar.RightInset;
                if (inset > 0)
                    btn.Margin = new Microsoft.UI.Xaml.Thickness(0, 0, Math.Max(inset, 140), 0);
            }
            catch { /* keep default margin */ }
        }
        catch { /* ignore */ }
    }

    private static void OnClick(object sender, RoutedEventArgs e)
    {
        try { OpenProjection?.Invoke(); }
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
