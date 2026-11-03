using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Media.Projection;
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
    private static TaskCompletionSource<MediaProjectionGrant>? s_projGrant;
    private ActivityResultLauncher? _treeLauncher;
    private ActivityResultLauncher? _projLauncher;

    internal static MainActivity? Current => s_current;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        s_current = this;
        _treeLauncher = RegisterForActivityResult(
            new ActivityResultContracts.OpenDocumentTree(),
            new TreeUriCallback());
        _projLauncher = RegisterForActivityResult(
            new ActivityResultContracts.StartActivityForResult(),
            new MediaProjectionCallback());
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

    /// <summary>系统屏幕录制授权；取消或失败时 Ok=false。</summary>
    internal static Task<MediaProjectionGrant> RequestMediaProjectionAsync()
    {
        var activity = s_current ?? Platform.CurrentActivity as MainActivity;
        if (activity?._projLauncher is null)
            return Task.FromResult(MediaProjectionGrant.Fail("Activity 未就绪，无法申请录屏"));

        var tcs = new TaskCompletionSource<MediaProjectionGrant>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        s_projGrant = tcs;
        try
        {
            activity.RunOnUiThread(() =>
            {
                try
                {
                    var mpm = activity.GetSystemService(MediaProjectionService) as MediaProjectionManager;
                    if (mpm == null)
                    {
                        Interlocked.Exchange(ref s_projGrant, null)
                            ?.TrySetResult(MediaProjectionGrant.Fail("系统不支持屏幕录制"));
                        return;
                    }

                    activity._projLauncher.Launch(mpm.CreateScreenCaptureIntent());
                }
                catch (Exception ex)
                {
                    Interlocked.Exchange(ref s_projGrant, null)
                        ?.TrySetResult(MediaProjectionGrant.Fail(ex.Message));
                }
            });
        }
        catch (Exception ex)
        {
            s_projGrant = null;
            tcs.TrySetResult(MediaProjectionGrant.Fail(ex.Message));
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

    private sealed class MediaProjectionCallback : Object, IActivityResultCallback
    {
        public void OnActivityResult(Object? result)
        {
            var tcs = Interlocked.Exchange(ref s_projGrant, null);
            if (tcs == null) return;
            try
            {
                if (result is not ActivityResult ar)
                {
                    tcs.TrySetResult(MediaProjectionGrant.Fail("授权结果无效"));
                    return;
                }

                if (ar.ResultCode != (int)Result.Ok || ar.Data == null)
                {
                    tcs.TrySetResult(MediaProjectionGrant.Fail("用户取消了屏幕录制授权"));
                    return;
                }

                tcs.TrySetResult(MediaProjectionGrant.Success(ar.ResultCode, ar.Data));
            }
            catch (Exception ex)
            {
                tcs.TrySetResult(MediaProjectionGrant.Fail(ex.Message));
            }
        }
    }

    internal readonly record struct MediaProjectionGrant(
        bool Ok, int ResultCode, Intent? Data, string? Error)
    {
        public static MediaProjectionGrant Success(int code, Intent data)
            => new(true, code, data, null);

        public static MediaProjectionGrant Fail(string error)
            => new(false, 0, null, error);
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

    private static MainPage? FindMainPage(Page? page)
    {
        switch (page)
        {
            case MainPage main:
                return main;
            case FlyoutPage flyout:
                return FindMainPage(flyout.Detail) ?? FindMainPage(flyout.Flyout);
            case NavigationPage nav:
                return FindMainPage(nav.CurrentPage) ?? FindMainPage(nav.RootPage);
            case TabbedPage tabs:
                return FindMainPage(tabs.CurrentPage);
            case Shell shell:
                return FindMainPage(shell.CurrentPage);
            default:
                return null;
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

        // 从无障碍设置返回：刷新「高级 → 行为」勾选状态（主界面在 Shell 里）
        try
        {
            Microsoft.Maui.Controls.Application.Current?.Dispatcher.Dispatch(() =>
            {
                foreach (var w in Microsoft.Maui.Controls.Application.Current?.Windows ?? [])
                    FindMainPage(w.Page)?.RefreshA11yInjectFromResume();
            });
        }
        catch
        {
            /* ignore */
        }
    }

}
