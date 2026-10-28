#if ANDROID
using Android.Content;
using Android.OS;
using Android.Util;

namespace FlyScreen.AppMaui.Platforms.Android;

/// <summary>
/// 无通知栏常驻：程序运行时持有 Partial WakeLock；进程保活主要依赖无障碍服务绑定。
/// （Android 前台服务强制要通知，故不采用 FGS 保活。）
/// </summary>
public static class AndroidProgramWakeLock
{
    private static readonly object Gate = new();
    private static PowerManager.WakeLock? _wakeLock;

    public static void SetProgramsRunning(int count)
    {
        if (count > 0)
            Acquire();
        else
            Release();
    }

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
