using Foundation;
using FlyScreen.Helpers;
using UIKit;

namespace FlyScreen.AppMaui;

[Register("AppDelegate")]
public class AppDelegate : MauiUIApplicationDelegate
{
    private SingleInstanceHelper? _singleInstance;

    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();

    public override bool FinishedLaunching(UIApplication application, NSDictionary? launchOptions)
    {
        // Mac Catalyst：同机只允许一个进程；二次启动发信号后退出
        if (!SingleInstanceHelper.TryStartPrimary(out _singleInstance))
        {
            Environment.Exit(0);
            return false;
        }

        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { _singleInstance?.Dispose(); }
            catch { /* ignore */ }
            _singleInstance = null;
        };

        var ok = base.FinishedLaunching(application, launchOptions);
        _singleInstance?.StartListening(() =>
        {
            try { MainThread.BeginInvokeOnMainThread(ActivateMainWindow); }
            catch { /* ignore */ }
        });
        return ok;
    }

    private static void ActivateMainWindow()
    {
        try
        {
            foreach (var scene in UIApplication.SharedApplication.ConnectedScenes)
            {
                if (scene is not UIWindowScene windowScene) continue;
                foreach (var w in windowScene.Windows)
                {
                    if (w == null) continue;
                    w.MakeKeyAndVisible();
                    return;
                }
            }
        }
        catch { /* ignore */ }
    }
}
