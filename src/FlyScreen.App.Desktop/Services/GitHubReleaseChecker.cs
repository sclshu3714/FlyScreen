using System.Reflection;
using FlyScreen.Services.Update;

namespace FlyScreen.DesktopUi.Services;

/// <summary>Avalonia Desktop：检查 + 更新（Mac/Linux 自替换；若将来有 Win Avalonia 安装包亦走 UpdateApplier）。</summary>
public sealed class GitHubReleaseChecker
{
    public static string ReleasesUrl => GitHubReleaseClient.ReleasesUrl;

    private readonly GitHubReleaseClient _client = new();
    private readonly UpdateApplier _applier = new();

    public static UpdateChannel Channel => UpdateChannelRules.DetectDesktopChannel();

    public static Version GetLocalVersion()
        => GitHubReleaseClient.GetLocalVersion(Assembly.GetEntryAssembly());

    public static string FormatVersion(Version v) => GitHubReleaseClient.FormatVersion(v);

    public Task<UpdateCheckResult> CheckAsync(CancellationToken ct = default)
        => _client.CheckAsync(
            Channel,
            Assembly.GetEntryAssembly(),
            ct,
            preferInstallerPackage: AppInstallLayout.PreferInstallerPackageAsset());

    public Task<UpdateApplyResult> DownloadAndApplyAsync(
        UpdateCheckResult check,
        IProgress<UpdateDownloadProgress>? progress = null,
        CancellationToken ct = default)
        => _applier.DownloadAndApplyAsync(check, progress, ct);
}
