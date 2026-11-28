#if WINDOWS
using FlyScreen.Ui.Maui;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using WinRT.Interop;

namespace FlyScreen.AppMaui.Platforms.Windows;

/// <summary>
/// 工具独立窗：居中、改标题、隐藏主窗「投影」按钮、套 ForDialog 色板。
/// 文件保险箱交给 <see cref="MauiFileVaultWindowChrome"/>。
/// </summary>
internal static class MauiToolWindowChrome
{
    private static readonly HashSet<int> CenteredWindows = new();

    public static void Apply(Microsoft.Maui.Controls.Window window)
    {
        try
        {
            if (window.Page is Microsoft.Maui.Controls.Page page)
                MauiDialogShell.ApplyForDialogColors(page);

            if (MauiFileVaultWindowChrome.IsFileVaultWindow(window))
            {
                MauiFileVaultWindowChrome.Apply(window);
                return;
            }

            ApplyNative(window);

            window.HandlerChanged -= OnHandlerChanged;
            window.HandlerChanged += OnHandlerChanged;
            window.Created -= OnCreated;
            window.Created += OnCreated;
            _ = RefreshDelayedAsync(window);
        }
        catch
        {
            // ignore
        }
    }

    private static void OnHandlerChanged(object? sender, EventArgs e)
    {
        if (sender is Microsoft.Maui.Controls.Window w)
            MainThread.BeginInvokeOnMainThread(() => ApplyNative(w));
    }

    private static void OnCreated(object? sender, EventArgs e)
    {
        if (sender is Microsoft.Maui.Controls.Window w)
            MainThread.BeginInvokeOnMainThread(() => ApplyNative(w));
    }

    private static async Task RefreshDelayedAsync(Microsoft.Maui.Controls.Window window)
    {
        foreach (var ms in new[] { 30, 80, 160, 320 })
        {
            try
            {
                await Task.Delay(ms).ConfigureAwait(true);
                MainThread.BeginInvokeOnMainThread(() => ApplyNative(window));
            }
            catch
            {
                // ignore
            }
        }
    }

    private static void ApplyNative(Microsoft.Maui.Controls.Window window)
    {
        try
        {
            if (MauiFileVaultWindowChrome.IsFileVaultWindow(window))
            {
                MauiFileVaultWindowChrome.Apply(window);
                return;
            }

            if (window.Handler?.PlatformView is not Microsoft.UI.Xaml.Window native)
                return;

            var title = string.IsNullOrWhiteSpace(window.Title) ? "玄鸟" : window.Title;
            try { native.Title = title; } catch { /* ignore */ }

            var hwnd = WindowNative.GetWindowHandle(native);
            var id = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
            var appWindow = AppWindow.GetFromWindowId(id);
            try { appWindow.Title = title; } catch { /* ignore */ }

            if (native.Content is not DependencyObject tree)
                return;

            if (FindByName(tree, "TitleProjectBtn") is Microsoft.UI.Xaml.Controls.Button projectBtn)
                projectBtn.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;

            if (FindByName(tree, "AppTitle") is Microsoft.UI.Xaml.Controls.TextBlock appTitle)
                appTitle.Text = title;

            if (CenteredWindows.Add(window.GetHashCode()))
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
        catch
        {
            // ignore
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
}
#endif
