#if ANDROID
using Android.Content;
using Android.OS;
using Android.Util;

namespace FlyScreen.AppMaui.Platforms.Android;

/// <summary>
/// 程序运行时持 Partial WakeLock；若开启「通知注入」且 count&gt;0，再叠前台通知保活。
/// 无子程序时绝不占通知栏。
/// </summary>
public static class AndroidProgramWakeLock
{
    private static readonly object Gate = new();
    private static PowerManager.WakeLock? _wakeLock;
    private static int _lastCount;

    public static void SetProgramsRunning(int count)
    {
        _lastCount = Math.Max(0, count);
        if (_lastCount > 0)
            Acquire();
        else
            Release();

        // 通知注入：仅有子程序且用户开启时才 FGS
        var enabled = false;
        try { enabled = Services.MauiAppHost.EnsureCreated().Settings.NotificationInject; }
        catch { /* ignore */ }
        FlyScreenProgramNotifyService.Sync(_lastCount, enabled);
    }

    /// <summary>设置页切换「通知注入」后立刻按当前运行数刷新。</summary>
    public static void RefreshNotificationInject(bool enabled)
        => FlyScreenProgramNotifyService.Sync(_lastCount, enabled);

    private static void Acquire()
    {
        lock (Gate)
        {
            try
            {
                if (_wakeLock?.IsHeld == true) return;
                var ctx = global::Android.App.Application.Context;
                var pm = ctx?.GetSystemService(Context.PowerService) as PowerManager;
                _wakeLock = pm?.NewWakeLock(WakeLockFlags.Partial, "FlyScreen:ProgramRun");
                _wakeLock?.SetReferenceCounted(false);
                _wakeLock?.Acquire();
                if (_wakeLock?.IsHeld == true)
                    Log.Info("FlyKeepAlive", "program wakelock on");
            }
            catch (Exception ex)
            {
                Log.Warn("FlyKeepAlive", "wakelock: " + ex.Message);
            }
        }
    }

    private static void Release()
    {
        lock (Gate)
        {
            try
            {
                if (_wakeLock?.IsHeld == true)
                    _wakeLock.Release();
            }
            catch { /* ignore */ }
            _wakeLock = null;
        }
    }
}
#endif
