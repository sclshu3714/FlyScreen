#if ANDROID
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Util;
using FlyScreen.Services;
using FlyScreen.Services.Programs;

namespace FlyScreen.AppMaui.Platforms.Android;

/// <summary>
/// 子程序后台保活：仅当「通知注入」开启且 RunningCount&gt;0 时以前台通知驻留；
/// 无子程序或关闭选项时立即撤通知/停服务。
/// </summary>
[Service(
    Exported = false,
    Name = "flyscreen.program.FlyScreenProgramNotifyService",
    ForegroundServiceType = ForegroundService.TypeSpecialUse)]
[MetaData("android.app.PROPERTY_SPECIAL_USE_FGS_SUBTYPE",
    Value = "Keep user scripts running while the app is in background")]
public sealed class FlyScreenProgramNotifyService : Service
{
    public const string ActionSync = "flyscreen.program.SYNC";
    public const string ActionStop = "flyscreen.program.STOP";
    private const int NotifyId = 48;
    private const string ChannelId = "flyscreen.program";

    public static FlyScreenProgramNotifyService? Instance { get; private set; }

    private static readonly object Gate = new();
    private static int s_runningCount;
    private static bool s_enabled;
    private bool _foregrounded;

    /// <summary>按当前运行数与设置同步前台通知（可从任意线程调用）。</summary>
    public static void Sync(int runningCount, bool notificationInjectEnabled)
    {
        lock (Gate)
        {
            s_runningCount = Math.Max(0, runningCount);
            s_enabled = notificationInjectEnabled;
        }

        var want = notificationInjectEnabled && runningCount > 0;
        var ctx = global::Android.App.Application.Context;
        if (ctx == null) return;

        try
        {
            if (want)
            {
                var intent = new Intent(ctx, typeof(FlyScreenProgramNotifyService));
                intent.SetAction(ActionSync);
                if (OperatingSystem.IsAndroidVersionAtLeast(26))
                    ctx.StartForegroundService(intent);
                else
                    ctx.StartService(intent);
            }
            else if (Instance != null)
            {
                var intent = new Intent(ctx, typeof(FlyScreenProgramNotifyService));
                intent.SetAction(ActionStop);
                ctx.StartService(intent);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("FlyProgramNotify", "sync: " + ex.Message);
            AppLog.Warn("子程序通知保活同步失败: " + ex.Message);
        }
    }

    /// <summary>从宿主设置与 Runner 当前状态刷新一次。</summary>
    public static void SyncFromHost()
    {
        try
        {
            var host = Services.MauiAppHost.EnsureCreated();
            Sync(ProgramService.Instance.Runner.RunningCount, host.Settings.NotificationInject);
        }
        catch (Exception ex)
        {
            Log.Warn("FlyProgramNotify", "sync-host: " + ex.Message);
        }
    }

    public override void OnCreate()
    {
        base.OnCreate();
        Instance = this;
    }

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        var action = intent?.Action;
        bool enabled;
        int count;
        lock (Gate)
        {
            enabled = s_enabled;
            count = s_runningCount;
        }

        if (string.Equals(action, ActionStop, StringComparison.Ordinal) || !enabled || count <= 0)
        {
            TearDownForeground();
            StopSelf();
            return StartCommandResult.NotSticky;
        }

        EnterForeground(count);
        return StartCommandResult.Sticky;
    }

    public override void OnDestroy()
    {
        TearDownForeground();
        if (ReferenceEquals(Instance, this))
            Instance = null;
        base.OnDestroy();
    }

    public override IBinder? OnBind(Intent? intent) => null;

    private void EnterForeground(int runningCount)
    {
        try
        {
            if (OperatingSystem.IsAndroidVersionAtLeast(26))
            {
                var manager = GetSystemService(NotificationService) as NotificationManager;
                manager?.CreateNotificationChannel(new NotificationChannel(
                    ChannelId, "子程序运行", NotificationImportance.Low));
            }

            var text = runningCount <= 1
                ? "用户子程序运行中"
                : $"用户子程序运行中（{runningCount}）";
            var notification = new Notification.Builder(this, ChannelId)
                .SetContentTitle("玄鸟")
                .SetContentText(text)
                .SetSmallIcon(global::Android.Resource.Drawable.IcMenuInfoDetails)
                .SetOngoing(true)
                .SetOnlyAlertOnce(true)
                .Build();

            if (OperatingSystem.IsAndroidVersionAtLeast(34))
                StartForeground(NotifyId, notification, ForegroundService.TypeSpecialUse);
            else
                StartForeground(NotifyId, notification);

            _foregrounded = true;
            Log.Info("FlyProgramNotify", $"FGS on count={runningCount}");
        }
        catch (Exception ex)
        {
            Log.Warn("FlyProgramNotify", "foreground: " + ex.Message);
            AppLog.Warn("子程序前台通知失败: " + ex.Message);
        }
    }

    private void TearDownForeground()
    {
        try
        {
            if (_foregrounded && OperatingSystem.IsAndroidVersionAtLeast(24))
                StopForeground(StopForegroundFlags.Remove);
        }
        catch { /* ignore */ }
        _foregrounded = false;
    }
}
#endif
