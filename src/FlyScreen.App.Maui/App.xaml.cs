using FlyScreen.AppMaui.Helpers;
using FlyScreen.AppMaui.Services;

namespace FlyScreen.AppMaui;

public partial class App : Application
{
    public App()
    {
        InitializeComponent();
        // 尽早落地主题，避免 Android RadioButton 首次绘制吃到系统夜间深色底
        try
        {
            var theme = MauiAppHost.EnsureCreated().Settings.UiTheme;
            UserAppTheme = MauiThemeService.IsDark(theme) ? AppTheme.Dark : AppTheme.Light;
            MauiThemeService.Apply(theme);
        }
        catch
        {
            UserAppTheme = AppTheme.Light;
        }
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        // 手机/平板/桌面统一进玄鸟主界面；触控板由布局工具条按需打开
        Window window;
        if (DeviceInfo.Idiom == DeviceIdiom.Phone)
            window = new Window(new AppShell()) { Title = "玄鸟" };
        else
        {
            window = new Window(new AppShell())
            {
                Title = "玄鸟",
                Width = 960,
                Height = 640,
                MinimumWidth = 900,
                MinimumHeight = 600
            };
            CenterWindow(window);
        }

#if WINDOWS
        Services.MauiWindowIcon.Apply(window);
        Platforms.Windows.MauiCloseToTray.TryHook(window);
        Platforms.Windows.MauiProjectionTitleBar.OpenProjection = () =>
            MainThread.BeginInvokeOnMainThread(async () =>
            {
                try
                {
                    var host = Services.MauiAppHost.EnsureCreated();
                    if (host.ScreenExtend.IsActive
                        && host.ScreenExtend.Role == FlyScreen.Services.ScreenExtend.ScreenExtendRole.Host)
                    {
                        await host.StopProjectionAsync();
                        Platforms.Windows.MauiProjectionTitleBar.SetProjecting(false);
                        return;
                    }

                    if (Current?.Windows.FirstOrDefault()?.Page is not Page root) return;
                    var main = FindMainPage(root);
                    if (main != null)
                        await main.OpenProjectionDialogAsync();
                    Platforms.Windows.MauiProjectionTitleBar.SetProjecting(
                        host.ScreenExtend.IsActive
                        && host.ScreenExtend.Role == FlyScreen.Services.ScreenExtend.ScreenExtendRole.Host);
                }
                catch { /* ignore */ }
            });
        Platforms.Windows.MauiProjectionTitleBar.TryHook(window);
        window.HandlerChanged += (_, _) =>
            MainThread.BeginInvokeOnMainThread(() =>
            {
                Platforms.Windows.MauiWindowChrome.ApplyTheme(MauiThemeService.IsDark(MauiThemeService.Current));
                Platforms.Windows.MauiCloseToTray.TryHook(window);
                Platforms.Windows.MauiProjectionTitleBar.TryHook(window);
            });
        window.Created += (_, _) =>
            MainThread.BeginInvokeOnMainThread(() =>
            {
                Platforms.Windows.MauiWindowChrome.ApplyTheme(MauiThemeService.IsDark(MauiThemeService.Current));
                Platforms.Windows.MauiCloseToTray.TryHook(window);
                Platforms.Windows.MauiProjectionTitleBar.TryHook(window);
                window.Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(120), () =>
                {
                    Platforms.Windows.MauiWindowChrome.ApplyTheme(MauiThemeService.IsDark(MauiThemeService.Current));
                    Platforms.Windows.MauiCloseToTray.TryHook(window);
                    Platforms.Windows.MauiProjectionTitleBar.TryHook(window);
                });
            });
#endif
#if ANDROID
        Services.VirtualCursorOverlay.Attach(window);
        Services.RemoteCompositionOverlay.Attach(window);
#endif
        return window;
    }

#if WINDOWS
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
#endif

    /// <summary>启动时居中（主显示器工作区）。</summary>
    private static void CenterWindow(Window window)
    {
        void Place()
        {
            try
            {
#if WINDOWS
                if (window.Handler?.PlatformView is Microsoft.UI.Xaml.Window native)
                {
                    var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(native);
                    var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
                    var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);
                    var area = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(
                        windowId, Microsoft.UI.Windowing.DisplayAreaFallback.Primary);
                    var work = area.WorkArea;
                    var w = appWindow.Size.Width;
                    var h = appWindow.Size.Height;
                    if (w <= 0) w = (int)window.Width;
                    if (h <= 0) h = (int)window.Height;
                    appWindow.Move(new Windows.Graphics.PointInt32(
                        work.X + (work.Width - w) / 2,
                        work.Y + (work.Height - h) / 2));
                    return;
                }
#endif
                var display = DeviceDisplay.MainDisplayInfo;
                var density = display.Density <= 0 ? 1 : display.Density;
                var screenW = display.Width / density;
                var screenH = display.Height / density;
                window.X = Math.Max(0, (screenW - window.Width) / 2);
                window.Y = Math.Max(0, (screenH - window.Height) / 2);
            }
            catch
            {
                /* ignore */
            }
        }

        window.HandlerChanged += (_, _) => MainThread.BeginInvokeOnMainThread(Place);
        window.Created += (_, _) => MainThread.BeginInvokeOnMainThread(() =>
        {
            Place();
            window.Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(100), Place);
        });
    }
}
