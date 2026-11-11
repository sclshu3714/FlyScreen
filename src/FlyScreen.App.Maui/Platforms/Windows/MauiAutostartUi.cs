#if WINDOWS
using System.Runtime.InteropServices;
using FlyScreen.Helpers;
using Microsoft.UI.Windowing;
using WinRT.Interop;

namespace FlyScreen.AppMaui.Platforms.Windows;

/// <summary>
/// 开机自启：有托盘的桌面端不直接亮主界面（避免未渲染完的黑色空窗）。
/// </summary>
internal static class MauiAutostartUi
{
    private static readonly bool StartHidden =
        AutoStartHelper.IsAutostartLaunch(Environment.GetCommandLineArgs());

    private static bool _keepHidden = StartHidden;

    public static bool ShouldStartHidden => StartHidden;

    public static bool IsKeepingHidden => _keepHidden;

    public static void AllowShow() => _keepHidden = false;

    public static void Hook(Microsoft.Maui.Controls.Window window)
    {
        if (!StartHidden) return;

        void HideNow()
        {
            if (!_keepHidden) return;
            TryHideNative(window);
        }

        window.HandlerChanged += (_, _) => MainThread.BeginInvokeOnMainThread(HideNow);
        window.Created += (_, _) => MainThread.BeginInvokeOnMainThread(() =>
        {
            HideNow();
            // WinUI 首帧/主题/居中回调可能把窗重新露出来，多挡几次
            window.Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(50), HideNow);
            window.Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(200), HideNow);
            window.Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(500), HideNow);
            window.Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(1200), HideNow);
        });
        HideNow();
    }

    public static void TryHideNative(Microsoft.Maui.Controls.Window? window)
    {
        if (window?.Handler?.PlatformView is not Microsoft.UI.Xaml.Window native)
            return;

        try
        {
            var hwnd = WindowNative.GetWindowHandle(native);
            var id = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
            var appWindow = AppWindow.GetFromWindowId(id);
            appWindow.IsShownInSwitchers = false;
            appWindow.Hide();
            ShowWindow(hwnd, SwHide);
        }
        catch
        {
            /* Activate 前可能尚无 AppWindow */
        }
    }

    public static void TryShowNative(Microsoft.Maui.Controls.Window? window)
    {
        AllowShow();
        if (window?.Handler?.PlatformView is not Microsoft.UI.Xaml.Window native)
            return;

        try
        {
            var hwnd = WindowNative.GetWindowHandle(native);
            var id = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
            var appWindow = AppWindow.GetFromWindowId(id);
            appWindow.IsShownInSwitchers = true;
            appWindow.Show();
            if (IsIconic(hwnd))
                ShowWindow(hwnd, SwRestore);
            else
                ShowWindow(hwnd, SwShow);
            native.Activate();
            SetForegroundWindow(hwnd);
        }
        catch
        {
            /* ignore */
        }
    }

    /// <summary>二次启动 / 托盘：显示并置前主窗（含最小化、藏托盘）。</summary>
    public static void ActivateMainWindow()
    {
        var window = Microsoft.Maui.Controls.Application.Current?.Windows.FirstOrDefault();
        TryShowNative(window);
    }

    private const int SwHide = 0;
    private const int SwShow = 5;
    private const int SwRestore = 9;

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(nint hWnd);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hWnd);
}
#endif
