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
/// </summary>
internal static class MauiWindowChrome
{
    public static void ApplyTheme(bool dark)
    {
        try
        {
            var app = Microsoft.Maui.Controls.Application.Current;
            if (app == null) return;

            foreach (var window in app.Windows)
            {
                if (window.Handler?.PlatformView is not Microsoft.UI.Xaml.Window native)
                    continue;

                if (native.Content is FrameworkElement root)
                    root.RequestedTheme = dark ? ElementTheme.Dark : ElementTheme.Light;

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
        // 与 MauiThemeService WinBg / Text / CardBg 对齐
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

        var barRoot = FindByName(tree, "AppTitleBarRoot") as WinBorder
                      ?? FindByName(tree, "AppTitleBar") as WinBorder;
        if (barRoot != null)
            barRoot.Background = brushBg;

        var title = FindByName(tree, "AppTitle") as WinTextBlock;
        if (title != null)
            title.Foreground = brushFg;

        var projectBtn = FindByName(tree, "TitleProjectBtn") as WinButton;
        if (projectBtn != null)
            projectBtn.Foreground = brushFg;
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
