namespace FlyScreen.Services.Update;

/// <summary>
/// 统一更新入口：安装包模式走 Setup.exe；单文件/绿色版走自替换。
/// </summary>
public sealed class UpdateApplier
{
    private readonly SelfReplaceInstaller _selfReplace = new();
    private readonly SetupPackageInstaller _setupPackage = new();

    public Task<UpdateApplyResult> DownloadAndApplyAsync(
        UpdateCheckResult check,
        IProgress<UpdateDownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        var preferSetup = AppInstallLayout.PreferInstallerPackageAsset()
                          || LooksLikeSetupAsset(check.AssetName);

        if (preferSetup && OperatingSystem.IsWindows())
            return _setupPackage.DownloadAndApplyAsync(check, progress, ct);

        return _selfReplace.DownloadAndApplyAsync(check, progress, ct);
    }

    private static bool LooksLikeSetupAsset(string? name)
        => !string.IsNullOrWhiteSpace(name)
           && name.Contains("Setup", StringComparison.OrdinalIgnoreCase)
           && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
}
