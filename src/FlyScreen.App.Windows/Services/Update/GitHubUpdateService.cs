using System.Reflection;

namespace FlyScreen.Services.Update;

/// <summary>
/// WPF stable 更新入口：按安装形态选择单文件自替换或 Setup 安装包升级。
/// </summary>
public sealed class GitHubUpdateService
{
    public static string ReleasesUrl => GitHubReleaseClient.ReleasesUrl;
    public static string GiteeReleasesUrl => GitHubReleaseClient.GiteeReleasesUrl;
    public static string LatestApiUrl => GitHubReleaseClient.LatestApiUrl;
    public static string UpdateLogPath => SelfReplaceInstaller.UpdateLogPath;

    private readonly GitHubReleaseClient _client = new();
    private readonly UpdateApplier _applier = new();

    public static Version GetLocalVersion() => GitHubReleaseClient.GetLocalVersion(Assembly.GetExecutingAssembly());

    public static string FormatVersion(Version v) => GitHubReleaseClient.FormatVersion(v);

    public static AppInstallKind InstallKind => AppInstallLayout.Detect();

    public Task<UpdateCheckResult> CheckAsync(CancellationToken ct = default)
        => _client.CheckAsync(
            UpdateChannel.WinWpf,
            Assembly.GetExecutingAssembly(),
            ct,
            preferInstallerPackage: AppInstallLayout.PreferInstallerPackageAsset());

    public Task<UpdateApplyResult> DownloadAndApplyAsync(
        UpdateCheckResult check,
        IProgress<UpdateDownloadProgress>? progress = null,
        CancellationToken ct = default)
        => _applier.DownloadAndApplyAsync(check, progress, ct);
}
