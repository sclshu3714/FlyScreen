#if WINDOWS
using System.Runtime.CompilerServices;
using FlyScreen.AppMaui.Services;
using WinRT.Interop;

namespace FlyScreen.AppMaui.Platforms.Windows;

/// <summary>
/// Maui-Win：标题栏 X / Alt+F4 等同「隐藏到托盘」；仅 <see cref="RequestExit"/> 才真正退出。
/// </summary>
internal static class MauiCloseToTray
{
    private static readonly HashSet<int> WindowHooked = [];
    private static readonly HashSet<nint> AppWindowHooked = [];
    private static bool _exitRequested;

    /// <summary>托盘「退出」、底部「退出」、热键退出等走此入口。</summary>
    public static void RequestExit()
    {
        _exitRequested = true;
        try { Application.Current?.Quit(); }
        catch { /* ignore */ }
    }

    public static void TryHook(Microsoft.Maui.Controls.Window window)
    {
        var id = RuntimeHelpers.GetHashCode(window);
        if (WindowHooked.Add(id))
        {
            window.HandlerChanged += (_, _) => HookOnce(window);
            window.Created += (_, _) => HookOnce(window);
        }

        HookOnce(window);
        window.Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(200), () => HookOnce(window));
    }

    private static void HookOnce(Microsoft.Maui.Controls.Window window)
    {
        try
        {
            if (window.Handler?.PlatformView is not Microsoft.UI.Xaml.Window native)
                return;
            var hwnd = WindowNative.GetWindowHandle(native);
            if (!AppWindowHooked.Add(hwnd)) return;
            var wid = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
            var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(wid);
            appWindow.Closing += OnAppWindowClosing;
        }
        catch { /* Activate 后再绑亦可 */ }
    }

    private static void OnAppWindowClosing(Microsoft.UI.Windowing.AppWindow sender,
        Microsoft.UI.Windowing.AppWindowClosingEventArgs args)
    {
        if (_exitRequested) return;
        args.Cancel = true;
        MainThread.BeginInvokeOnMainThread(HideLikeTrayButton);
    }

    private static void HideLikeTrayButton()
    {
        try
        {
            var host = MauiAppHost.EnsureCreated();
            if (!host.Platform.SupportsTray) return;
            host.Platform.HideToBackground(
                showMain: () => { },
                reconnect: () => _ = host.Connections.ReconnectNowAsync(),
                about: () =>
                {
                    MainThread.BeginInvokeOnMainThread(() =>
                    {
                        try
                        {
                            var root = Application.Current?.Windows.FirstOrDefault()?.Page;
                            var main = FindMainPage(root);
                            main?.ShowAboutFromTray();
                        }
                        catch { /* ignore */ }
                    });
                },
                exit: RequestExit);
        }
        catch { /* ignore */ }
    }

    private static MainPage? FindMainPage(Page? page)
    {
        switch (page)
        {
            case MainPage main:
                return main;
            case FlyoutPage flyout:
                return FindMainPage(flyout.Detail) ?? FindMainPage(flyout.Flyout);
            case NavigationPage nav:
                return FindMainPage(nav.CurrentPage);
            case TabbedPage tabs:
                return FindMainPage(tabs.CurrentPage);
            case Shell shell:
                return FindMainPage(shell.CurrentPage);
            default:
                return null;
        }
    }
}
#endif
