using Avalonia;
using FlyScreen.Helpers;

namespace FlyScreen.MacApp;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Core 模型通知回投 Avalonia UI 线程（启动后由 App 再绑一次 Dispatcher）
        UiMarshal.Configure(a => a());

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
