using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FlyScreen.Services.Tools;

/// <summary>
/// 工具安装器：本地 .flytool 解压到 Tools/Installed；支持 Bundled 标记安装兼容路径。
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
        var local = ToolPackageResolver.ResolveLocalPack(toolId);
        if (local != null)
            return await InstallFromFlyToolFileAsync(local.Path, trustHint: local.Source == "bundled"
                ? ToolTrustLevel.Bundled
                : ToolTrustLevel.KnownRelease, ct).ConfigureAwait(false);

        // 无包时：兼容旧「仅写标记」——若内置清单存在则写空安装目录 + manifest 桩（供尚未打包的过渡期）
        return await InstallMarkerOnlyAsync(toolId, ct).ConfigureAwait(false);
    }

    public async Task<ToolInstallResult> InstallFromFlyToolFileAsync(
        string flytoolPath,
        ToolTrustLevel trustHint = ToolTrustLevel.Unknown,
        CancellationToken ct = default,
        bool forceOverwrite = false)
    {
        if (string.IsNullOrWhiteSpace(flytoolPath) || !File.Exists(flytoolPath))
            return ToolInstallResult.Fail("找不到工具包文件");

        FlyScreenDataPaths.EnsureToolsLayout();

        byte[] bytes;
        try
        {
            bytes = await File.ReadAllBytesAsync(flytoolPath, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return ToolInstallResult.Fail(ex.Message);
        }

        var hash = Sha256Hex(bytes);
        var pack = await ToolPackageResolver.ReadManifestFromPackAsync(flytoolPath, ct).ConfigureAwait(false);
        if (pack == null || string.IsNullOrWhiteSpace(pack.Id))
            return ToolInstallResult.Fail("无效的 .flytool（缺少 manifest）");

        var toolId = pack.Id.Trim();
        if (_progress.IsInstalling(toolId))
            return ToolInstallResult.Fail("正在安装中");

        var existing = _catalog.FindRecord(toolId);
        if (!forceOverwrite
            && existing != null
            && IsInstallComplete(existing)
            && string.Equals(existing.Version, pack.PackageVersion, StringComparison.OrdinalIgnoreCase))
            return ToolInstallResult.Ok("已安装");

        // 残缺安装、版本升级或用户确认覆盖：先卸再装（调用方应已 Host.Unload；此处只清目录/记录）
        if (existing != null)
        {
            AppLog.Info(forceOverwrite
                ? $"工具覆盖重装：{toolId} {existing.Version} → {pack.PackageVersion}"
                : $"工具需重装（残缺或版本变更）：{toolId} {existing.Version} → {pack.PackageVersion}");
            var removed = Uninstall(toolId);
            if (!removed.Success)
                return removed;
        }

        // Unknown 需 UI 同意——此处由调用方先弹窗；安装器仅记录 trust
        _progress.Begin(toolId);
        try
        {
            await StepAsync(toolId, 10, ct).ConfigureAwait(false);
            var version = pack.PackageVersion;
            var installDir = Path.Combine(FlyScreenDataPaths.ToolsInstalledDirectory, toolId, version);
            if (Directory.Exists(installDir) && !TryDeleteDirectory(installDir))
                return ToolInstallResult.Fail("无法覆盖安装目录（文件被占用），请关闭相关窗口后重试");

            Directory.CreateDirectory(installDir);
            await StepAsync(toolId, 35, ct).ConfigureAwait(false);

            try
            {
                ZipFile.ExtractToDirectory(flytoolPath, installDir, overwriteFiles: true);
            }
            catch (IOException ex) when (IsSharingViolation(ex))
            {
                // 仍可能碰上旧版 LoadFromAssemblyPath 锁；短暂等待后再试一次
                await Task.Delay(200, ct).ConfigureAwait(false);
                GC.Collect();
                GC.WaitForPendingFinalizers();
                if (!TryDeleteDirectory(installDir))
                    return ToolInstallResult.Fail(ex.Message);
                Directory.CreateDirectory(installDir);
                ZipFile.ExtractToDirectory(flytoolPath, installDir, overwriteFiles: true);
            }
            await StepAsync(toolId, 70, ct).ConfigureAwait(false);

            // 确保根目录有 manifest.json
            var mf = Path.Combine(installDir, "manifest.json");
            if (!File.Exists(mf))
            {
                await File.WriteAllTextAsync(mf,
                    JsonSerializer.Serialize(pack, ToolPackageJson.Options), Encoding.UTF8, ct)
                    .ConfigureAwait(false);
            }

            // 外部导入：落一份到 Bundled（内置包已在此目录，不重复拷）
            try { MaybePersistExternalPack(flytoolPath, toolId, version); }
            catch { /* ignore */ }

            await StepAsync(toolId, 90, ct).ConfigureAwait(false);

            _catalog.UpsertInstall(new ToolInstallRecord
            {
                Id = toolId,
                Version = version,
                Path = installDir,
                ContentHash = hash,
                TrustLevel = trustHint.ToString(),
                InstalledAt = DateTimeOffset.UtcNow
            });

            await StepAsync(toolId, 100, ct).ConfigureAwait(false);
            AppLog.Info($"工具已安装：{toolId} {version} ← {flytoolPath}");
            return ToolInstallResult.Ok("安装成功");
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

    private async Task<ToolInstallResult> InstallMarkerOnlyAsync(string toolId, CancellationToken ct)
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
            await StepAsync(toolId, 20, ct).ConfigureAwait(false);
            var version = manifest.PackageVersion;
            var installDir = Path.Combine(FlyScreenDataPaths.ToolsInstalledDirectory, toolId, version);
            Directory.CreateDirectory(installDir);
            var markerBody =
                $"{{\"id\":\"{toolId}\",\"package\":{{\"version\":\"{version}\",\"format\":\"flytool\",\"formatVersion\":1}},\"entryAssembly\":\"\",\"entryType\":\"\",\"bundled\":true}}";
            await File.WriteAllTextAsync(Path.Combine(installDir, "manifest.json"), markerBody, Encoding.UTF8, ct)
                .ConfigureAwait(false);
            await StepAsync(toolId, 80, ct).ConfigureAwait(false);
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
            return ToolInstallResult.Ok("安装成功（对端需重新连接后可见工具能力）");
        }
        catch (Exception ex)
        {
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
                if (!TryDeleteDirectory(rec.Path))
                {
                    AppLog.Warn($"删除工具目录失败（文件占用）: {rec.Path}");
                    return ToolInstallResult.Fail("无法删除工具文件（仍被占用），请关闭工具窗口后重试");
                }
            }

            try
            {
                var idDir = Path.Combine(FlyScreenDataPaths.ToolsInstalledDirectory, toolId);
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

    /// <summary>源包不在 Bundled 时，以 <c>id-version.flytool</c> 写入 Bundled 供重装。</summary>
    private static void MaybePersistExternalPack(string flytoolPath, string toolId, string version)
    {
        var fullSrc = Path.GetFullPath(flytoolPath);
        var bundledRoot = WithTrailingSep(Path.GetFullPath(FlyScreenDataPaths.ToolsBundledDirectory));
        if (fullSrc.StartsWith(bundledRoot, StringComparison.OrdinalIgnoreCase))
            return;

        Directory.CreateDirectory(FlyScreenDataPaths.ToolsBundledDirectory);
        var dest = Path.Combine(FlyScreenDataPaths.ToolsBundledDirectory, $"{toolId}-{version}.flytool");
        File.Copy(flytoolPath, dest, overwrite: true);
    }

    private static string WithTrailingSep(string path)
        => path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
           + Path.DirectorySeparatorChar;

    /// <summary>带 GC/退避的目录删除，缓解 ALC 卸载后短暂文件锁。</summary>
    internal static bool TryDeleteDirectory(string path, int attempts = 10)
    {
        if (string.IsNullOrWhiteSpace(path)) return true;
        for (var i = 0; i < attempts; i++)
        {
            try
            {
                if (!Directory.Exists(path))
                    return true;
                Directory.Delete(path, recursive: true);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                Thread.Sleep(40 * (i + 1));
            }
        }

        return !Directory.Exists(path);
    }

    private static bool IsSharingViolation(Exception ex)
        => ex.Message.Contains("being used by another process", StringComparison.OrdinalIgnoreCase)
           || ex.HResult == unchecked((int)0x80070020);

    /// <summary>安装目录是否含可 ALC 加载的入口程序集（排除旧 marker-only）。</summary>
    public static bool IsInstallComplete(ToolInstallRecord? rec)
    {
        if (rec == null || string.IsNullOrWhiteSpace(rec.Path) || !Directory.Exists(rec.Path))
            return false;

        try
        {
            var mfPath = Path.Combine(rec.Path, "manifest.json");
            if (!File.Exists(mfPath))
                return false;

            var pack = JsonSerializer.Deserialize<ToolPackageManifest>(
                File.ReadAllText(mfPath), ToolPackageJson.Options);
            if (pack == null || string.IsNullOrWhiteSpace(pack.EntryAssembly))
                return false;

            var dllName = pack.EntryAssembly.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                ? pack.EntryAssembly
                : pack.EntryAssembly + ".dll";
            if (File.Exists(Path.Combine(rec.Path, dllName)))
                return true;

            foreach (var _ in Directory.EnumerateFiles(rec.Path, dllName, SearchOption.AllDirectories))
                return true;
            return false;
        }
        catch
        {
            return false;
        }
    }

    private async Task StepAsync(string toolId, int percent, CancellationToken ct)
    {
        _progress.Report(toolId, percent);
        await Task.Delay(15, ct).ConfigureAwait(false);
    }

    public static string Sha256Hex(byte[] data)
    {
        var hash = SHA256.HashData(data);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

public sealed class ToolInstallResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = "";

    public static ToolInstallResult Ok(string message) => new() { Success = true, Message = message };
    public static ToolInstallResult Fail(string message) => new() { Success = false, Message = message };
}
