using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using FlyScreen.DesktopUi;
using FlyScreen.Helpers;
using FlyScreen.Mac;

namespace FlyScreen.MacApp;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        UiMarshal.Configure(
            post: a => Dispatcher.UIThread.Post(a, DispatcherPriority.Background),
            send: a =>
            {
                if (Dispatcher.UIThread.CheckAccess()) a();
                else Dispatcher.UIThread.Invoke(a);
            });

        var host = MacAppHost.EnsureCreated();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow(new MacDesktopShellHost(host));
            desktop.ShutdownRequested += async (_, e) =>
            {
                e.Cancel = true;
                try { await MacAppHost.Instance.DisposeAsync().ConfigureAwait(true); }
                catch { /* ignore */ }
                desktop.Shutdown();
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
