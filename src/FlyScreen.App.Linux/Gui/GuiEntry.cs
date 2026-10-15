using Avalonia;
using Avalonia.Threading;
using FlyScreen.Helpers;

namespace FlyScreen.LinuxApp.Gui;

internal static class GuiEntry
{
    public static int Run(string[] args)
    {
        UiMarshal.Configure(a => a());

        return BuildAvaloniaApp()
            .StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithCjkFontSupport()
            .LogToTrace()
            .AfterSetup(_ =>
            {
                UiMarshal.Configure(
                    post: a => Dispatcher.UIThread.Post(a, DispatcherPriority.Background),
                    send: a =>
                    {
                        if (Dispatcher.UIThread.CheckAccess()) a();
                        else Dispatcher.UIThread.Invoke(a);
                    });
            });
}
