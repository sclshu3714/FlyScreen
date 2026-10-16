using System.Reflection;

namespace FlyScreen.Services.Update;

/// <summary>
/// WPF stable 更新入口：检查 + 自替换（委托 Core，通道 <see cref="UpdateChannel.WinWpf"/>）。
/// </summary>
public sealed class GitHubUpdateService
{
    public static string ReleasesUrl => GitHubReleaseClient.ReleasesUrl;
    public static string LatestApiUrl => GitHubReleaseClient.LatestApiUrl;
    public static string UpdateLogPath => SelfReplaceInstaller.UpdateLogPath;

    private readonly GitHubReleaseClient _client = new();
    private readonly SelfReplaceInstaller _installer = new();

    public static Version GetLocalVersion() => GitHubReleaseClient.GetLocalVersion(Assembly.GetExecutingAssembly());

    public static string FormatVersion(Version v) => GitHubReleaseClient.FormatVersion(v);

    public Task<UpdateCheckResult> CheckAsync(CancellationToken ct = default)
        => _client.CheckAsync(UpdateChannel.WinWpf, Assembly.GetExecutingAssembly(), ct);

    public Task<UpdateApplyResult> DownloadAndApplyAsync(
        UpdateCheckResult check,
        IProgress<UpdateDownloadProgress>? progress = null,
        CancellationToken ct = default)
        => _installer.DownloadAndApplyAsync(check, progress, ct);
}
