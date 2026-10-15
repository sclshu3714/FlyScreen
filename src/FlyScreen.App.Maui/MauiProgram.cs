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

        builder.Services.AddSingleton(MauiAppHost.EnsureCreated());
        builder.Services.AddSingleton<MainPage>();

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
