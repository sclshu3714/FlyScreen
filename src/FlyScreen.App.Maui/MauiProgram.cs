using FlyScreen.Helpers;
using FlyScreen.AppMaui.Services;
using Microsoft.Extensions.Logging;

namespace FlyScreen.AppMaui;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>();

#if WINDOWS
        // 确保每个 Window 都套上图标（含后续再开的窗口）
        Microsoft.Maui.Handlers.WindowHandler.Mapper.AppendToMapping("FlyScreenIcon", (handler, _) =>
        {
            if (handler.VirtualView is Window window)
                Services.MauiWindowIcon.Apply(window);
        });

        // WinUI CheckBox 默认 MinWidth 很大；只清 MinWidth，勿强设 Width（会裁掉勾选图标）
        Microsoft.Maui.Handlers.CheckBoxHandler.Mapper.AppendToMapping("FlyScreenCompact", (handler, _) =>
        {
            if (handler.PlatformView is not Microsoft.UI.Xaml.Controls.CheckBox native) return;
            native.MinWidth = 0;
            native.MinHeight = 0;
            native.Padding = new Microsoft.UI.Xaml.Thickness(0);
            native.Margin = new Microsoft.UI.Xaml.Thickness(0);
            native.HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Left;
            native.VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Center;
        });
#endif

        var host = MauiAppHost.EnsureCreated();
#if WINDOWS
        host.AttachPlatform(new Platforms.Windows.WindowsMauiPlatformServices(
            host.Settings, host.Connections, host.Transfers));
#elif ANDROID
        host.AttachPlatform(new Platforms.Android.AndroidMauiPlatformServices(
            host.Settings, host.Transfers));
#endif

        builder.Services.AddSingleton(host);
        builder.Services.AddSingleton<MainPage>();
        builder.Services.AddSingleton<PhoneTouchpadPage>();

#if DEBUG
        builder.Logging.AddDebug();
#endif

        var app = builder.Build();

        UiMarshal.Configure(
            post: a => MainThread.BeginInvokeOnMainThread(a),
            send: a =>
            {
                if (MainThread.IsMainThread) a();
                else MainThread.InvokeOnMainThreadAsync(a).GetAwaiter().GetResult();
            });

        return app;
    }
}
