#if ANDROID
using Android.Content;
using Android.OS;
using FlyScreen.Services.Update;
using Application = Android.App.Application;
using AxFileProvider = AndroidX.Core.Content.FileProvider;

namespace FlyScreen.AppMaui.Platforms.Android;

/// <summary>下载 maui-android APK 并拉起系统安装界面。</summary>
public static class AndroidApkUpdater
{
    private static readonly GitHubReleaseClient Client = new();

    public static async Task<UpdateApplyResult> DownloadAndInstallAsync(
        UpdateCheckResult check,
        IProgress<UpdateDownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (!check.Success || !check.UpdateAvailable)
            return UpdateApplyResult.Fail("没有可用更新。");
        if (string.IsNullOrWhiteSpace(check.AssetDownloadUrl))
            return UpdateApplyResult.Fail("Release 中没有本端 APK。请打开：\n" + check.ReleaseUrl);

        var ctx = Application.Context
                  ?? throw new InvalidOperationException("Android Context 不可用。");

        // Android 8+：需允许「安装未知应用」
        if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
        {
            var pm = ctx.PackageManager;
            if (pm != null && !pm.CanRequestPackageInstalls())
            {
                try
                {
                    var settings = new Intent(global::Android.Provider.Settings.ActionManageUnknownAppSources);
                    settings.SetData(global::Android.Net.Uri.Parse("package:" + ctx.PackageName));
                    settings.AddFlags(ActivityFlags.NewTask);
                    ctx.StartActivity(settings);
                }
                catch
                {
                    // ignore
                }

                return UpdateApplyResult.Fail(
                    "请先允许「安装未知应用」，然后再次点击应用更新。\n" +
                    "已尝试打开系统设置。");
            }
        }

        var dir = Path.Combine(ctx.CacheDir?.AbsolutePath ?? Path.GetTempPath(), "updates");
        Directory.CreateDirectory(dir);
        var apkName = string.IsNullOrWhiteSpace(check.AssetName)
            ? "FlyScreen-update.apk"
            : GitHubReleaseClient.SanitizeFileName(check.AssetName);
        if (!apkName.EndsWith(".apk", StringComparison.OrdinalIgnoreCase))
            apkName += ".apk";
        var apkPath = Path.Combine(dir, apkName);

        try
        {
            if (File.Exists(apkPath))
                File.Delete(apkPath);

            await Client.DownloadAssetAsync(check.AssetDownloadUrl!, apkPath, progress, ct)
                .ConfigureAwait(false);

            progress?.Report(UpdateDownloadProgress.Complete("正在打开安装界面…"));

            var file = new Java.IO.File(apkPath);
            var uri = AxFileProvider.GetUriForFile(ctx, ctx.PackageName + ".fileprovider", file);
            var intent = new Intent(Intent.ActionView);
            intent.SetDataAndType(uri, "application/vnd.android.package-archive");
            intent.AddFlags(ActivityFlags.GrantReadUriPermission | ActivityFlags.NewTask);
            ctx.StartActivity(intent);

            return UpdateApplyResult.Ok(
                "已打开系统安装界面，请确认安装。安装完成后可手动打开新版本。",
                shouldExit: false);
        }
        catch (Exception ex)
        {
            return UpdateApplyResult.Fail("下载或安装 APK 失败：" + ex.Message + "\n也可打开：\n" + check.ReleaseUrl);
        }
    }
}
#endif
