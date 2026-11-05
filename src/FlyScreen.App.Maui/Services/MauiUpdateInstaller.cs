using FlyScreen.Services.Update;

namespace FlyScreen.AppMaui.Services;

/// <summary>MAUI 跨平台安装入口：Android APK；Win 按安装形态 Setup/自替换；其它桌面自替换。</summary>
public static class MauiUpdateInstaller
{
    public static async Task<UpdateApplyResult> DownloadAndApplyAsync(
        UpdateCheckResult check,
        IProgress<UpdateDownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
#if ANDROID
        return await Platforms.Android.AndroidApkUpdater.DownloadAndInstallAsync(check, progress, ct)
            .ConfigureAwait(false);
#else
        return await new UpdateApplier().DownloadAndApplyAsync(check, progress, ct)
            .ConfigureAwait(false);
#endif
    }
}
