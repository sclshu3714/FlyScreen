using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Principal;

namespace FlyScreen.Services.Update;

/// <summary>
/// 安装包模式更新：下载 *-Setup.exe，提权启动安装程序覆盖安装，然后退出当前进程。
/// </summary>
public sealed class SetupPackageInstaller
{
    private readonly GitHubReleaseClient _client = new();

    public async Task<UpdateApplyResult> DownloadAndApplyAsync(
        UpdateCheckResult check,
        IProgress<UpdateDownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (!check.Success || !check.UpdateAvailable)
            return UpdateApplyResult.Fail("没有可用更新。");
        if (string.IsNullOrWhiteSpace(check.AssetDownloadUrl) || string.IsNullOrWhiteSpace(check.AssetName))
            return UpdateApplyResult.Fail("Release 中没有本端安装包（*-Setup.exe）。请打开：\n" + check.ReleaseUrl);

        if (!OperatingSystem.IsWindows())
            return UpdateApplyResult.Fail("安装包模式更新仅支持 Windows。");

        var name = check.AssetName;
        if (!name.Contains("Setup", StringComparison.OrdinalIgnoreCase)
            || !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return UpdateApplyResult.Fail(
                "当前为安装包安装，但匹配到的资源不是 Setup.exe。请发版时上传 *-Setup.exe，或手动打开：\n"
                + check.ReleaseUrl);
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "XuanNiaoSetupUpdate_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);
        var setupPath = Path.Combine(tempDir, GitHubReleaseClient.SanitizeFileName(name));

        try
        {
            await _client.DownloadAssetAsync(check.AssetDownloadUrl!, setupPath, progress, ct)
                .ConfigureAwait(false);

            progress?.Report(UpdateDownloadProgress.Complete("正在启动安装程序…"));

            var psi = new ProcessStartInfo
            {
                FileName = setupPath,
                UseShellExecute = true,
                WorkingDirectory = tempDir
            };
            if (IsElevated())
                psi.Verb = "runas";

            try
            {
                if (Process.Start(psi) == null)
                    return UpdateApplyResult.Fail("无法启动安装程序。");
            }
            catch (Exception) when (psi.Verb == "runas")
            {
                psi.Verb = null;
                if (Process.Start(psi) == null)
                    return UpdateApplyResult.Fail("无法启动安装程序。");
            }

            return UpdateApplyResult.Ok(
                "已启动安装程序。请在安装向导中完成升级（可覆盖安装，个人配置默认保留）。\n完成后本程序将退出。");
        }
        catch (Exception ex)
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* ignore */ }
            return UpdateApplyResult.Fail("下载或启动安装包失败：" + ex.Message);
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool IsElevated()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }
}
