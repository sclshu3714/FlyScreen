using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using AndroidX.Activity.Result;
using AndroidX.Activity.Result.Contract;
using FlyScreen.AppMaui.Platforms.Android;
using FlyScreen.AppMaui.Services;
using Object = Java.Lang.Object;

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
    private static MainActivity? s_current;
    private static TaskCompletionSource<Android.Net.Uri?>? s_treePick;
    private ActivityResultLauncher? _treeLauncher;

    internal static MainActivity? Current => s_current;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        s_current = this;
        _treeLauncher = RegisterForActivityResult(
            new ActivityResultContracts.OpenDocumentTree(),
            new TreeUriCallback());
    }

    protected override void OnDestroy()
    {
        if (ReferenceEquals(s_current, this))
            s_current = null;
        base.OnDestroy();
    }

    /// <summary>弹出系统「选择文件夹」；取消返回 null。</summary>
    internal static Task<Android.Net.Uri?> PickDocumentTreeAsync()
    {
        var activity = s_current ?? Platform.CurrentActivity as MainActivity;
        if (activity?._treeLauncher is null)
            return Task.FromResult<Android.Net.Uri?>(null);

        var tcs = new TaskCompletionSource<Android.Net.Uri?>();
        s_treePick = tcs;
        try
        {
            activity.RunOnUiThread(() => activity._treeLauncher.Launch(null));
        }
        catch (Exception ex)
        {
            s_treePick = null;
            tcs.TrySetException(ex);
        }

        return tcs.Task;
    }

    private sealed class TreeUriCallback : Object, IActivityResultCallback
    {
        public void OnActivityResult(Object? result)
        {
            var uri = result as Android.Net.Uri;
            var tcs = Interlocked.Exchange(ref s_treePick, null);
            tcs?.TrySetResult(uri);
        }
    }

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

        // 从无障碍设置返回：刷新「高级 → 行为」勾选状态
        try
        {
            Microsoft.Maui.Controls.Application.Current?.Dispatcher.Dispatch(() =>
            {
                foreach (var w in Microsoft.Maui.Controls.Application.Current?.Windows ?? [])
                {
                    if (w.Page is NavigationPage nav && nav.CurrentPage is MainPage mp)
                        mp.RefreshA11yInjectFromResume();
                    else if (w.Page is MainPage main)
                        main.RefreshA11yInjectFromResume();
                }
            });
        }
        catch
        {
            /* ignore */
        }
    }

}
