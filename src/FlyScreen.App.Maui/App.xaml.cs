namespace FlyScreen.AppMaui;

public partial class App : Application
{
    public App()
    {
        InitializeComponent();
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        Window window;
        if (DeviceInfo.Idiom == DeviceIdiom.Phone)
            window = new Window(new NavigationPage(new PhoneTouchpadPage()));
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
#endif
        return window;
    }

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
