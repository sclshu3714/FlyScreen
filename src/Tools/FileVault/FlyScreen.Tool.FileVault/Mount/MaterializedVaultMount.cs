using FlyScreen.Tool.FileVault.Store;

namespace FlyScreen.Tool.FileVault.Mount;

/// <summary>
/// 解锁后将保险箱物化到 ACL 尽量收紧的挂载目录，供系统程序打开；
/// 锁定时安全擦除。不依赖第三方虚拟盘驱动。
/// </summary>
internal sealed class MaterializedVaultMount : IDisposable
{
    private readonly VaultRepository _repo;
    private string? _mountRoot;
    private CancellationTokenSource? _cts;
    private Task? _syncTask;

    public MaterializedVaultMount(VaultRepository repo) => _repo = repo;

    public bool IsMounted => !string.IsNullOrEmpty(_mountRoot) && Directory.Exists(_mountRoot);
    public string? MountPath => _mountRoot;

    public async Task MountAsync(IProgress<VaultProgress>? progress, CancellationToken ct)
    {
        await UnmountAsync().ConfigureAwait(false);
        if (!_repo.IsUnlocked || string.IsNullOrEmpty(_repo.Root))
            throw new InvalidOperationException("请先解锁");

        _mountRoot = Path.Combine(Path.GetTempPath(), "FlyScreenVaultMount", _repo.Manifest!.VaultId);
        if (Directory.Exists(_mountRoot))
            SecureDelete.DirectoryTree(_mountRoot);
        Directory.CreateDirectory(_mountRoot);
        TryRestrictAcl(_mountRoot);

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = _cts.Token;
        _syncTask = Task.Run(() =>
        {
            _repo.ExportTree(null, _mountRoot, progress, token);
        }, token);
        await _syncTask.ConfigureAwait(false);
    }

    public async Task UnmountAsync()
    {
        try { _cts?.Cancel(); } catch { /* ignore */ }
        if (_syncTask != null)
        {
            try { await _syncTask.ConfigureAwait(false); }
            catch { /* ignore */ }
        }
        _syncTask = null;
        _cts?.Dispose();
        _cts = null;

        if (!string.IsNullOrEmpty(_mountRoot))
        {
            SecureDelete.DirectoryTree(_mountRoot);
            _mountRoot = null;
        }
    }

    public string? ResolveMountedPath(string entryId)
    {
        if (!IsMounted) return null;
        try
        {
            var virt = _repo.GetVirtualPath(entryId);
            if (string.IsNullOrEmpty(virt) || virt == "/") return _mountRoot;
            var rel = virt.Trim('/').Replace('/', Path.DirectorySeparatorChar);
            var full = Path.Combine(_mountRoot!, rel);
            return File.Exists(full) || Directory.Exists(full) ? full : null;
        }
        catch
        {
            return null;
        }
    }

    public void Dispose() => UnmountAsync().GetAwaiter().GetResult();

    private static void TryRestrictAcl(string path)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            // 仅当前用户可访问（尽力）
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "icacls",
                Arguments = $"\"{path}\" /inheritance:r /grant:r \"%USERNAME%\":(OI)(CI)F",
                CreateNoWindow = true,
                UseShellExecute = false
            };
            using var p = System.Diagnostics.Process.Start(psi);
            p?.WaitForExit(5000);
        }
        catch
        {
            // ignore
        }
    }
}
