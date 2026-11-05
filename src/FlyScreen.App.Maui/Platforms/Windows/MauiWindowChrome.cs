#if WINDOWS
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using WinRT.Interop;
using WinBorder = Microsoft.UI.Xaml.Controls.Border;
using WinButton = Microsoft.UI.Xaml.Controls.Button;
using WinSolidBrush = Microsoft.UI.Xaml.Media.SolidColorBrush;
using WinTextBlock = Microsoft.UI.Xaml.Controls.TextBlock;

namespace FlyScreen.AppMaui.Platforms.Windows;

/// <summary>
/// Maui-Win：跟随淡色/深色设置标题栏与 WinUI RequestedTheme。
/// Modal（投影弹窗等）会重置标题栏模板为 App.xaml 浅色默认，须跟刷。
/// </summary>
internal static class MauiWindowChrome
{
    private static int _followUpSeq;

    public static void ApplyTheme(bool dark, bool scheduleFollowUp = true)
    {
        ApplyThemeCore(dark);
        if (scheduleFollowUp)
            ScheduleFollowUp(dark);
    }

    /// <summary>弹窗打开/关闭后强制跟刷，避免左右两截。</summary>
    public static void RefreshAfterModal()
        => ApplyTheme(MauiThemeServiceIsDark(), scheduleFollowUp: true);

    private static bool MauiThemeServiceIsDark()
    {
        try
        {
            return FlyScreen.AppMaui.Helpers.MauiThemeService.IsDark(
                FlyScreen.AppMaui.Helpers.MauiThemeService.Current);
        }
        catch { return false; }
    }

    private static void ScheduleFollowUp(bool dark)
    {
        var seq = ++_followUpSeq;
        // RequestedTheme / PushModal 会异步重刷模板，分帧再涂色
        // Modal 动画与模板重绑可持续数百毫秒，多档跟刷
        foreach (var ms in new[] { 16, 50, 120, 250, 500, 900 })
            _ = FollowUpAsync(seq, dark, ms);
    }

    private static async Task FollowUpAsync(int seq, bool dark, int delayMs)
    {
        try
        {
            await Task.Delay(delayMs).ConfigureAwait(true);
            if (seq != _followUpSeq) return;
            Microsoft.Maui.ApplicationModel.MainThread.BeginInvokeOnMainThread(() =>
            {
                if (seq != _followUpSeq) return;
                ApplyThemeCore(dark);
            });
        }
        catch
        {
            // ignore
        }
    }

    private static void ApplyThemeCore(bool dark)
    {
        try
        {
            var app = Microsoft.Maui.Controls.Application.Current;
            if (app == null) return;

            foreach (var window in app.Windows)
            {
                if (window.Handler?.PlatformView is not Microsoft.UI.Xaml.Window native)
                    continue;

                // 已是目标主题则勿反复赋值，否则 WinUI 会重刷标题栏模板（浅色默认闪一下）
                if (native.Content is FrameworkElement root)
                {
                    var want = dark ? ElementTheme.Dark : ElementTheme.Light;
                    if (root.RequestedTheme != want)
                        root.RequestedTheme = want;
                }

                try
                {
                    var hwnd = WindowNative.GetWindowHandle(native);
                    var id = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
                    var appWindow = AppWindow.GetFromWindowId(id);
                    if (AppWindowTitleBar.IsCustomizationSupported())
                        PaintTitleBar(appWindow.TitleBar, dark);
                }
                catch
                {
                    // ignore
                }

                PaintMauiTitleBarTemplate(native, dark);
            }
        }
        catch
        {
            // ignore
        }
    }

    private static void PaintTitleBar(AppWindowTitleBar bar, bool dark)
    {
        // 与 MauiThemeService WinBg / Text 对齐；按钮区与拖拽区必须同色，否则左右两截
        var bg = dark ? Rgb(0x0F, 0x17, 0x2A) : Rgb(0xF7, 0xF8, 0xFA);
        var fg = dark ? Rgb(0xE2, 0xE8, 0xF0) : Rgb(0x11, 0x18, 0x27);
        var muted = dark ? Rgb(0x94, 0xA3, 0xB8) : Rgb(0x6B, 0x72, 0x80);
        var hover = dark ? Rgb(0x1E, 0x29, 0x3B) : Rgb(0xE5, 0xE7, 0xEB);

        bar.BackgroundColor = bg;
        bar.ForegroundColor = fg;
        bar.InactiveBackgroundColor = bg;
        bar.InactiveForegroundColor = muted;
        bar.ButtonBackgroundColor = bg;
        bar.ButtonForegroundColor = fg;
        bar.ButtonInactiveBackgroundColor = bg;
        bar.ButtonInactiveForegroundColor = muted;
        bar.ButtonHoverBackgroundColor = hover;
        bar.ButtonHoverForegroundColor = fg;
        bar.ButtonPressedBackgroundColor = hover;
        bar.ButtonPressedForegroundColor = fg;
    }

    private static void PaintMauiTitleBarTemplate(Microsoft.UI.Xaml.Window native, bool dark)
    {
        if (native.Content is not DependencyObject tree) return;

        var bg = dark ? Rgb(0x0F, 0x17, 0x2A) : Rgb(0xF7, 0xF8, 0xFA);
        var fg = dark ? Rgb(0xE2, 0xE8, 0xF0) : Rgb(0x11, 0x18, 0x27);
        var brushBg = new WinSolidBrush(bg);
        var brushFg = new WinSolidBrush(fg);
        var clear = new WinSolidBrush(global::Windows.UI.Color.FromArgb(0, 0, 0, 0));

        // 内容区与按钮区必须同色实心；模板默认 #F7F8FA，Modal 后常被重置
        var barRoot = FindByName(tree, "AppTitleBarRoot") as WinBorder
                      ?? FindByName(tree, "AppTitleBar") as WinBorder;
        if (barRoot != null)
        {
            barRoot.Background = brushBg;
            for (var p = VisualTreeHelper.GetParent(barRoot) as FrameworkElement;
                 p != null;
                 p = VisualTreeHelper.GetParent(p) as FrameworkElement)
            {
                if (p is WinBorder pb)
                    pb.Background = brushBg;
                if (string.Equals(p.Name, "AppTitleBar", StringComparison.Ordinal)
                    || string.Equals(p.Name, "TitleBar", StringComparison.Ordinal))
                    break;
            }
        }

        // 模板重绑后可能换了实例，把含图标的横向条也刷一遍
        PaintNamedBackgrounds(tree, brushBg, "AppTitleBarRoot", "AppTitleBar", "AppTitleBarInner");

        if (FindByName(tree, "AppTitleBarInner") is Microsoft.UI.Xaml.Controls.Grid inner)
            inner.Background = brushBg;

        var title = FindByName(tree, "AppTitle") as WinTextBlock;
        if (title != null)
            title.Foreground = brushFg;

        var projectBtn = FindByName(tree, "TitleProjectBtn") as WinButton;
        if (projectBtn != null)
        {
            projectBtn.Foreground = brushFg;
            projectBtn.Background = clear;
        }

        MauiProjectionTitleBar.Refresh();
    }

    private static void PaintNamedBackgrounds(DependencyObject tree, WinSolidBrush brush, params string[] names)
    {
        foreach (var name in names)
        {
            var el = FindByName(tree, name);
            if (el is WinBorder b)
                b.Background = brush;
            else if (el is Microsoft.UI.Xaml.Controls.Grid g)
                g.Background = brush;
            else if (el is Microsoft.UI.Xaml.Controls.Panel panel)
                panel.Background = brush;
        }
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

    private static global::Windows.UI.Color Rgb(byte r, byte g, byte b)
        => global::Windows.UI.Color.FromArgb(255, r, g, b);
}
#endif
