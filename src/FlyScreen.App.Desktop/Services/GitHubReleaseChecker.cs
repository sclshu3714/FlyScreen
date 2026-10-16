using System.Reflection;
using FlyScreen.Services.Update;

namespace FlyScreen.DesktopUi.Services;

/// <summary>Avalonia Desktop：检查 + 自替换（通道按 OS 选择）。</summary>
public sealed class GitHubReleaseChecker
{
    public static string ReleasesUrl => GitHubReleaseClient.ReleasesUrl;

    private readonly GitHubReleaseClient _client = new();
    private readonly SelfReplaceInstaller _installer = new();

    public static UpdateChannel Channel => UpdateChannelRules.DetectDesktopChannel();

    public static Version GetLocalVersion()
        => GitHubReleaseClient.GetLocalVersion(Assembly.GetEntryAssembly());

    public static string FormatVersion(Version v) => GitHubReleaseClient.FormatVersion(v);

    public Task<UpdateCheckResult> CheckAsync(CancellationToken ct = default)
        => _client.CheckAsync(Channel, Assembly.GetEntryAssembly(), ct);

    public Task<UpdateApplyResult> DownloadAndApplyAsync(
        UpdateCheckResult check,
        IProgress<UpdateDownloadProgress>? progress = null,
        CancellationToken ct = default)
        => _installer.DownloadAndApplyAsync(check, progress, ct);
}
