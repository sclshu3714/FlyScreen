using System.Security.Cryptography;
using System.Text;

namespace FlyScreen.Services.Tools;

/// <summary>
/// 工具安装器。本期对内置 <c>shared-devices</c> 走 Bundled 标记安装；
/// 完整 <c>.flytool</c> 解压管线后续补齐，接口先统一。耗时步骤通过进度回调刷新卡片底条。
/// </summary>
public sealed class ToolInstaller
{
    private readonly ToolCatalog _catalog;
    private readonly ToolInstallProgress _progress;

    public ToolInstaller(ToolCatalog catalog, ToolInstallProgress progress)
    {
        _catalog = catalog;
        _progress = progress;
    }

    public ToolInstallResult InstallBundled(string toolId)
        => InstallBundledAsync(toolId).GetAwaiter().GetResult();

    public async Task<ToolInstallResult> InstallBundledAsync(string toolId, CancellationToken ct = default)
    {
        var manifest = _catalog.FindManifest(toolId);
        if (manifest == null)
            return ToolInstallResult.Fail("未知工具：" + toolId);

        if (_catalog.IsInstalled(toolId))
            return ToolInstallResult.Ok("已安装");

        if (_progress.IsInstalling(toolId))
            return ToolInstallResult.Fail("正在安装中");

        _progress.Begin(toolId);
        try
        {
            await StepAsync(toolId, 8, ct).ConfigureAwait(false);

            var version = manifest.PackageVersion;
            var installDir = Path.Combine(FlyScreenDataPaths.ToolsDirectory, toolId, version);
            Directory.CreateDirectory(installDir);
            await StepAsync(toolId, 28, ct).ConfigureAwait(false);

            // 预留：解压 .flytool / 校验哈希 / 装虚拟音频组件等耗时步骤
            await StepAsync(toolId, 55, ct).ConfigureAwait(false);

            var marker = Path.Combine(installDir, "manifest.json");
            var markerBody =
                $"{{\"id\":\"{toolId}\",\"version\":\"{version}\",\"bundled\":true}}";
            await File.WriteAllTextAsync(marker, markerBody, Encoding.UTF8, ct).ConfigureAwait(false);
            await StepAsync(toolId, 78, ct).ConfigureAwait(false);

            var hash = Sha256Hex(Encoding.UTF8.GetBytes(markerBody));
            _catalog.UpsertInstall(new ToolInstallRecord
            {
                Id = toolId,
                Version = version,
                Path = installDir,
                ContentHash = hash,
                TrustLevel = nameof(ToolTrustLevel.Bundled),
                InstalledAt = DateTimeOffset.UtcNow
            });
            await StepAsync(toolId, 100, ct).ConfigureAwait(false);

            AppLog.Info($"工具已安装：{toolId} {version}");
            return ToolInstallResult.Ok("安装成功（对端需重新连接后可见工具能力）");
        }
        catch (OperationCanceledException)
        {
            return ToolInstallResult.Fail("已取消");
        }
        catch (Exception ex)
        {
            AppLog.Warn($"安装工具失败 {toolId}: {ex.Message}");
            return ToolInstallResult.Fail(ex.Message);
        }
        finally
        {
            _progress.End(toolId);
        }
    }

    public ToolInstallResult Uninstall(string toolId)
    {
        var rec = _catalog.FindRecord(toolId);
        if (rec == null)
            return ToolInstallResult.Ok("未安装");

        try
        {
            if (!string.IsNullOrWhiteSpace(rec.Path) && Directory.Exists(rec.Path))
            {
                try { Directory.Delete(rec.Path, recursive: true); }
                catch (Exception ex)
                {
                    AppLog.Warn($"删除工具目录失败 {rec.Path}: {ex.Message}");
                }
            }

            try
            {
                var idDir = Path.Combine(FlyScreenDataPaths.ToolsDirectory, toolId);
                if (Directory.Exists(idDir) && !Directory.EnumerateFileSystemEntries(idDir).Any())
                    Directory.Delete(idDir);
            }
            catch { /* ignore */ }

            _catalog.RemoveInstall(toolId);
            AppLog.Info("工具已卸载：" + toolId);
            return ToolInstallResult.Ok("已卸载");
        }
        catch (Exception ex)
        {
            AppLog.Warn($"卸载工具失败 {toolId}: {ex.Message}");
            return ToolInstallResult.Fail(ex.Message);
        }
    }

    private async Task StepAsync(string toolId, int percent, CancellationToken ct)
    {
        _progress.Report(toolId, percent);
        // 给 UI 一帧刷新；真实耗时安装时以业务步骤为准，不必刻意延迟
        await Task.Delay(40, ct).ConfigureAwait(false);
    }

    private static string Sha256Hex(byte[] data)
    {
        var hash = SHA256.HashData(data);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

public readonly struct ToolInstallResult
{
    public bool Success { get; init; }
    public string Message { get; init; }

    public static ToolInstallResult Ok(string message) => new() { Success = true, Message = message };
    public static ToolInstallResult Fail(string message) => new() { Success = false, Message = message };
}
