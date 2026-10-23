using FlyScreen.Services.Update;

namespace FlyScreen.AppMaui.Services;

/// <summary>MAUI 跨平台安装入口：桌面自替换；Android 走平台 APK 安装。</summary>
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
        // Windows / MacCatalyst：单文件自替换
        return await new SelfReplaceInstaller().DownloadAndApplyAsync(check, progress, ct)
            .ConfigureAwait(false);
#endif
    }
}
