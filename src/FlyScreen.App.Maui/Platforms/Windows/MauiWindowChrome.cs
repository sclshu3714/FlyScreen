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
/// 自定义（背景图）主题用半透明标题栏，让壁纸透出。
/// </summary>
internal static class MauiWindowChrome
{
    private static int _followUpSeq;
    private static bool _translucent;

    public static void ApplyTheme(bool dark, bool scheduleFollowUp = true, bool translucent = false)
    {
        _translucent = translucent;
        ApplyThemeCore(dark, translucent);
        if (scheduleFollowUp)
            ScheduleFollowUp(dark, translucent);
    }

    /// <summary>弹窗打开/关闭后强制跟刷，避免左右两截。</summary>
    public static void RefreshAfterModal()
        => ApplyTheme(MauiThemeServiceIsDark(), scheduleFollowUp: true, translucent: _translucent);

    /// <summary>
    /// 弹层页壳外全透明：清掉 ContentPage 及其 WinUI 祖先的实心底色，
    /// 否则 Maui-Win 模态页常残留灰/白罩，透不出主界面。
    /// </summary>
    public static void ClearOverlayPageBackground(Microsoft.Maui.Controls.Page page)
    {
        try
        {
            page.BackgroundColor = Microsoft.Maui.Graphics.Colors.Transparent;
            if (page.Handler?.PlatformView is not FrameworkElement fe)
                return;

            var clear = new WinSolidBrush(Microsoft.UI.Colors.Transparent);
            // 只清弹层页自身及紧邻壳，避免把主窗侧栏/主区背景一并掏空
            DependencyObject? node = fe;
            for (var i = 0; i < 3 && node != null; i++)
            {
                switch (node)
                {
                    case Microsoft.UI.Xaml.Controls.Panel panel:
                        panel.Background = clear;
                        break;
                    case WinBorder border:
                        border.Background = clear;
                        break;
                    case Microsoft.UI.Xaml.Controls.ContentPresenter presenter:
                        presenter.Background = clear;
                        break;
                    case Microsoft.UI.Xaml.Controls.Control control:
                        // 勿清 Button 等控件；仅清页根一类
                        if (i == 0)
                            control.Background = clear;
                        break;
                }
                node = VisualTreeHelper.GetParent(node);
            }
        }
        catch
        {
            // ignore
        }
    }

    private static bool MauiThemeServiceIsDark()
    {
        try
        {
            return FlyScreen.AppMaui.Helpers.MauiThemeService.IsDark(
                FlyScreen.AppMaui.Helpers.MauiThemeService.Current);
        }
        catch { return false; }
    }

    private static void ScheduleFollowUp(bool dark, bool translucent)
    {
        var seq = ++_followUpSeq;
        foreach (var ms in new[] { 16, 50, 120, 250, 500, 900 })
            _ = FollowUpAsync(seq, dark, translucent, ms);
    }

    private static async Task FollowUpAsync(int seq, bool dark, bool translucent, int delayMs)
    {
        try
        {
            await Task.Delay(delayMs).ConfigureAwait(true);
            if (seq != _followUpSeq) return;
            Microsoft.Maui.ApplicationModel.MainThread.BeginInvokeOnMainThread(() =>
            {
                if (seq != _followUpSeq) return;
                ApplyThemeCore(dark, translucent);
            });
        }
        catch
        {
            // ignore
        }
    }

    private static void ApplyThemeCore(bool dark, bool translucent)
    {
        try
        {
            var app = Microsoft.Maui.Controls.Application.Current;
            if (app == null) return;

            foreach (var window in app.Windows)
            {
                if (window.Handler?.PlatformView is not Microsoft.UI.Xaml.Window native)
                    continue;

                // 保险箱等自绘 Tab 标题栏：勿刷系统/模板标题栏色，否则会顶回白条与系统窗控
                if (MauiFileVaultWindowChrome.IsFileVaultWindow(window))
                    continue;

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
                        PaintTitleBar(appWindow.TitleBar, dark, translucent);
                }
                catch
                {
                    // ignore
                }

                PaintMauiTitleBarTemplate(native, dark, translucent);
            }
        }
        catch
        {
            // ignore
        }
    }

    private static void PaintTitleBar(AppWindowTitleBar bar, bool dark, bool translucent)
    {
        var bg = TitleBg(dark, translucent);
        var fg = dark ? Rgb(0xE2, 0xE8, 0xF0) : Rgb(0x11, 0x18, 0x27);
        var muted = dark ? Rgb(0x94, 0xA3, 0xB8) : Rgb(0x6B, 0x72, 0x80);
        var hover = translucent
            ? (dark ? Argb(0x99, 0x1E, 0x29, 0x3B) : Argb(0x99, 0xE5, 0xE7, 0xEB))
            : (dark ? Rgb(0x1E, 0x29, 0x3B) : Rgb(0xE5, 0xE7, 0xEB));

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

    private static void PaintMauiTitleBarTemplate(Microsoft.UI.Xaml.Window native, bool dark, bool translucent)
    {
        if (native.Content is not DependencyObject tree) return;

        var bg = TitleBg(dark, translucent);
        var fg = dark ? Rgb(0xE2, 0xE8, 0xF0) : Rgb(0x11, 0x18, 0x27);
        var brushBg = new WinSolidBrush(bg);
        var brushFg = new WinSolidBrush(fg);
        var clear = new WinSolidBrush(global::Windows.UI.Color.FromArgb(0, 0, 0, 0));

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

        MauiProjectionTitleBar.Rebind(native);
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

    /// <summary>自定义主题：约 35% 白/深色，标题栏透出壁纸。</summary>
    private static global::Windows.UI.Color TitleBg(bool dark, bool translucent)
    {
        if (!translucent)
            return dark ? Rgb(0x0F, 0x17, 0x2A) : Rgb(0xF7, 0xF8, 0xFA);
        return dark ? Argb(0x59, 0x0F, 0x17, 0x2A) : Argb(0x59, 0xFF, 0xFF, 0xFF);
    }

    private static global::Windows.UI.Color Rgb(byte r, byte g, byte b)
        => global::Windows.UI.Color.FromArgb(255, r, g, b);

    private static global::Windows.UI.Color Argb(byte a, byte r, byte g, byte b)
        => global::Windows.UI.Color.FromArgb(a, r, g, b);
}
#endif
