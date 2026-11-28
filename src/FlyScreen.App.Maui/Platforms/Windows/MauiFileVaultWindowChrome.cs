#if WINDOWS
using System.Reflection;
using Microsoft.UI.Windowing;
using WinRT.Interop;

namespace FlyScreen.AppMaui.Platforms.Windows;

/// <summary>
/// 文件保险箱：Tab 放进系统标题栏。保留标题栏区域，把底色刷成窗口背景，系统按钮留在右侧。
/// </summary>
internal static class MauiFileVaultWindowChrome
{
    private static readonly HashSet<int> Centered = new();
    private static readonly HashSet<int> DelayArmed = new();
    private static readonly HashSet<int> DropHooked = new();

    public static bool IsFileVaultWindow(Microsoft.Maui.Controls.Window window)
    {
        var content = (window.Page as Microsoft.Maui.Controls.ContentPage)?.Content;
        return content is not null
               && string.Equals(content.GetType().Name, "FileVaultPanel", StringComparison.Ordinal);
    }

    public static void Apply(Microsoft.Maui.Controls.Window window)
    {
        if (!IsFileVaultWindow(window))
            return;

        try
        {
            MountTab(window);
            ApplyCore(window);
            var id = window.GetHashCode();
            if (!DelayArmed.Add(id))
                return;
            foreach (var ms in new[] { 100, 300, 800, 1600, 3000 })
            {
                window.Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(ms), () =>
                {
                    MountTab(window);
                    ApplyCore(window);
                });
            }
            window.Destroying += (_, _) =>
            {
                DelayArmed.Remove(id);
                Centered.Remove(id);
                DropHooked.Remove(id);
            };
        }
        catch
        {
            // ignore
        }
    }

    private static void MountTab(Microsoft.Maui.Controls.Window window)
    {
        var panel = (window.Page as Microsoft.Maui.Controls.ContentPage)?.Content;
        if (panel is null) return;
        panel.GetType()
            .GetMethod("MountCaptionTabs", BindingFlags.Instance | BindingFlags.Public)
            ?.Invoke(panel, new object[] { window });
    }

    private static void ApplyCore(Microsoft.Maui.Controls.Window window)
    {
        try
        {
            if (window.Handler?.PlatformView is not Microsoft.UI.Xaml.Window native)
                return;

            var title = string.IsNullOrWhiteSpace(window.Title) ? "文件保险箱" : window.Title;
            try { native.Title = title; } catch { /* ignore */ }

            var hwnd = WindowNative.GetWindowHandle(native);
            var id = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
            var appWindow = AppWindow.GetFromWindowId(id);
            try { appWindow.Title = title; } catch { /* ignore */ }

            // 标题栏留着，用来放 Tab。关掉 Extends 或 hasTitleBar 会把这条区域收掉。
            try { native.ExtendsContentIntoTitleBar = true; } catch { /* ignore */ }
            try
            {
                if (AppWindowTitleBar.IsCustomizationSupported())
                {
                    var bar = appWindow.TitleBar;
                    bar.ExtendsContentIntoTitleBar = true;
                    PaintCaptionButtons(bar, window);
                }
            }
            catch { /* ignore */ }

            if (Centered.Add(window.GetHashCode()))
                TryCenter(appWindow, window, id);
            HookFileDrop(native, window);
        }
        catch
        {
            // ignore
        }
    }

    private static void PaintCaptionButtons(AppWindowTitleBar bar, Microsoft.Maui.Controls.Window window)
    {
        var bgColor = (window.Page as Microsoft.Maui.Controls.ContentPage)?.Content?.BackgroundColor;
        var dark = bgColor is null || bgColor.Red + bgColor.Green + bgColor.Blue < 1.5f;
        var bg = dark
            ? global::Windows.UI.Color.FromArgb(255, 0x0F, 0x17, 0x2A)
            : global::Windows.UI.Color.FromArgb(255, 0xF7, 0xF8, 0xFA);
        var fg = dark
            ? global::Windows.UI.Color.FromArgb(255, 0xE2, 0xE8, 0xF0)
            : global::Windows.UI.Color.FromArgb(255, 0x11, 0x18, 0x27);
        var hover = dark
            ? global::Windows.UI.Color.FromArgb(255, 0x1E, 0x29, 0x3B)
            : global::Windows.UI.Color.FromArgb(255, 0xE5, 0xE7, 0xEB);

        bar.BackgroundColor = bg;
        bar.ForegroundColor = fg;
        bar.InactiveBackgroundColor = bg;
        bar.InactiveForegroundColor = fg;
        bar.ButtonBackgroundColor = bg;
        bar.ButtonForegroundColor = fg;
        bar.ButtonInactiveBackgroundColor = bg;
        bar.ButtonInactiveForegroundColor = fg;
        bar.ButtonHoverBackgroundColor = hover;
        bar.ButtonHoverForegroundColor = fg;
        bar.ButtonPressedBackgroundColor = hover;
        bar.ButtonPressedForegroundColor = fg;

        if (window.TitleBar is Microsoft.Maui.Controls.TitleBar titleBar && bgColor is not null)
            titleBar.BackgroundColor = bgColor;
    }

    private static void HookFileDrop(Microsoft.UI.Xaml.Window native, Microsoft.Maui.Controls.Window window)
    {
        if (native.Content is not Microsoft.UI.Xaml.UIElement root)
            return;
        if (!DropHooked.Add(window.GetHashCode()))
            return;

        // 提权进程上的 WinUI AllowDrop 会注册 OLE 目标，资源管理器拖进来就是禁止光标。
        root.AllowDrop = false;
    }

    private static void TryCenter(AppWindow appWindow, Microsoft.Maui.Controls.Window window, Microsoft.UI.WindowId id)
    {
        try
        {
            var area = DisplayArea.GetFromWindowId(id, DisplayAreaFallback.Primary);
            var work = area.WorkArea;
            var w = appWindow.Size.Width;
            var h = appWindow.Size.Height;
            if (w <= 0) w = (int)Math.Max(1, window.Width);
            if (h <= 0) h = (int)Math.Max(1, window.Height);
            appWindow.Move(new global::Windows.Graphics.PointInt32(
                work.X + (work.Width - w) / 2,
                work.Y + (work.Height - h) / 2));
        }
        catch { /* ignore */ }
    }
}
#endif
