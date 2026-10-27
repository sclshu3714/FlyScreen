using System.Reflection;
using FlyScreen.Services.Update;

namespace FlyScreen.AppMaui.Services;

/// <summary>MAUI 更新：按平台通道检查；安装由 <see cref="MauiUpdateInstaller"/> 完成。</summary>
public sealed class MauiUpdateChecker
{
    public static string ReleasesUrl => GitHubReleaseClient.ReleasesUrl;

    private readonly GitHubReleaseClient _client = new();

    public static UpdateChannel Channel =>
#if ANDROID
        UpdateChannel.MauiAndroid;
#elif MACCATALYST || MACOS || IOS
        UpdateChannel.MauiMac;
#else
        UpdateChannel.MauiWin;
#endif

    public static Version GetLocalVersion()
        => GitHubReleaseClient.GetLocalVersion(typeof(MauiUpdateChecker).Assembly);

    public static string FormatVersion(Version v) => GitHubReleaseClient.FormatVersion(v);

    public Task<UpdateCheckResult> CheckAsync(CancellationToken ct = default)
        => _client.CheckAsync(Channel, typeof(MauiUpdateChecker).Assembly, ct);
}
