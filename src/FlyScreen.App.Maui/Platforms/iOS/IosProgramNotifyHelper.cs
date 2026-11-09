#if IOS
using FlyScreen.Services;
using UserNotifications;

namespace FlyScreen.AppMaui.Platforms.iOS;

/// <summary>
/// iOS：对齐 Android「通知注入」。有子程序且开启选项时发本地通知；
/// iOS 无前台服务，不能像 Android FGS 一样强制保活，仅作运行提示。
/// </summary>
public static class IosProgramNotifyHelper
{
    private const string NotifyId = "flyscreen.program.running";
    private static int _lastCount;

    public static void SetProgramsRunning(int count)
    {
        _lastCount = Math.Max(0, count);
        Refresh();
    }

    /// <summary>设置页切换「通知注入」后立刻按当前运行数刷新。</summary>
    public static void RefreshNotificationInject(bool enabled)
        => Refresh(enabled);

    private static void Refresh(bool? enabledOverride = null)
    {
        var enabled = enabledOverride ?? false;
        if (enabledOverride == null)
        {
            try { enabled = Services.MauiAppHost.EnsureCreated().Settings.NotificationInject; }
            catch { /* ignore */ }
        }

        var want = enabled && _lastCount > 0;
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (want)
                _ = ShowAsync(_lastCount);
            else
                Remove();
        });
    }

    public static async Task<bool> RequestPermissionAsync()
    {
        try
        {
            var center = UNUserNotificationCenter.Current;
            var (granted, _) = await center.RequestAuthorizationAsync(
                UNAuthorizationOptions.Alert | UNAuthorizationOptions.Badge | UNAuthorizationOptions.Sound);
            return granted;
        }
        catch
        {
            return false;
        }
    }

    private static async Task ShowAsync(int runningCount)
    {
        try
        {
            var center = UNUserNotificationCenter.Current;
            var settings = await center.GetNotificationSettingsAsync();
            if (settings.AuthorizationStatus is not (UNAuthorizationStatus.Authorized
                or UNAuthorizationStatus.Provisional or UNAuthorizationStatus.Ephemeral))
                return;

            var text = runningCount <= 1
                ? "用户子程序运行中"
                : $"用户子程序运行中（{runningCount}）";
            var content = new UNMutableNotificationContent
            {
                Title = "玄鸟",
                Body = text,
                Sound = null
            };
            // 立即投递；同 id 覆盖，避免通知栏堆多条
            var request = UNNotificationRequest.FromIdentifier(
                NotifyId, content, trigger: null);
            await center.AddNotificationRequestAsync(request);
        }
        catch (Exception ex)
        {
            AppLog.Warn("IosProgramNotify show: " + ex.Message);
        }
    }

    private static void Remove()
    {
        try
        {
            var center = UNUserNotificationCenter.Current;
            center.RemoveDeliveredNotifications([NotifyId]);
            center.RemovePendingNotificationRequests([NotifyId]);
        }
        catch (Exception ex)
        {
            AppLog.Warn("IosProgramNotify remove: " + ex.Message);
        }
    }
}
#endif
