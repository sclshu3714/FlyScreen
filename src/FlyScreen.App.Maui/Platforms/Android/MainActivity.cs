using Android.App;
using Android.Content.PM;
using Android.OS;
using FlyScreen.AppMaui.Platforms.Android;
using FlyScreen.AppMaui.Services;

namespace FlyScreen.AppMaui;

[Activity(
    Theme = "@style/Maui.SplashTheme",
    MainLauncher = true,
    Icon = "@mipmap/appicon",
    RoundIcon = "@mipmap/appicon_round",
    ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode |
                           ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
    protected override void OnPause()
    {
        base.OnPause();
        // 退到后台：解除触控板屏蔽，电脑键鼠可控制本机其他应用并显示指针；清组字浮层
        try
        {
            RemoteCompositionOverlay.DismissAndCancel();
            if (MauiAppHost.EnsureCreated().Platform is AndroidMauiPlatformServices android)
                android.NotifyAppBackgrounded();
        }
        catch
        {
            /* ignore */
        }
    }

    protected override void OnResume()
    {
        base.OnResume();
        try
        {
            if (MauiAppHost.EnsureCreated().Platform is AndroidMauiPlatformServices android)
                android.NotifyAppForegrounded();
        }
        catch
        {
            /* ignore */
        }
    }
}
