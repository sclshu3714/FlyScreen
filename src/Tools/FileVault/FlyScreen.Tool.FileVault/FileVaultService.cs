using System.Diagnostics;
using FlyScreen.Tool.FileVault.Mount;
using FlyScreen.Tool.FileVault.Open;
using FlyScreen.Tool.FileVault.Store;
using FlyScreen.ToolSdk;

namespace FlyScreen.Tool.FileVault;

public sealed class FileVaultService : IFileVaultService
{
    private readonly VaultRepository _repo = new();
    private readonly MaterializedVaultMount _mount;
    private readonly TempOpenGuard _tempOpen = new();
    private VaultSettingsStore? _settingsStore;
    private VaultSettingsDto _settings = new();
    private IToolHostServices? _host;
    private string? _currentFolderId;
    private bool _running;
    private CancellationTokenSource? _idleCts;

    public FileVaultService()
    {
        _mount = new MaterializedVaultMount(_repo);
    }

    public bool IsRunning => _running;
    public bool HasVault => !string.IsNullOrEmpty(_settings.ActiveVaultRoot)
                            && VaultRepository.LooksLikeVault(_settings.ActiveVaultRoot!);
    public bool IsUnlocked => _repo.IsUnlocked;
    public string? VaultRoot => string.IsNullOrEmpty(_repo.Root) ? _settings.ActiveVaultRoot : _repo.Root;
    public string? MountPath => _mount.MountPath;
    public bool IsMounted => _mount.IsMounted;
    public string? CurrentFolderId => _currentFolderId;
    public string CurrentPathLabel
    {
        get
        {
            if (!IsUnlocked) return "/";
            if (string.IsNullOrEmpty(_currentFolderId)) return "/";
            try { return _repo.GetVirtualPath(_currentFolderId); }
            catch { return "/"; }
        }
    }
    public VaultSettingsDto Settings => _settings;

    public event Action? SessionChanged;
    public event Action<VaultProgress>? ProgressChanged;

    public void AttachHost(IToolHostServices host)
    {
        _host = host;
        var data = host.GetToolDataDirectory(Services.Tools.ToolIds.FileVault);
        _settingsStore = new VaultSettingsStore(data);
        _settings = _settingsStore.Load();
        if (!string.IsNullOrWhiteSpace(_settings.ActiveVaultRoot)
            && VaultRepository.LooksLikeVault(_settings.ActiveVaultRoot))
        {
            try { _repo.OpenLocked(_settings.ActiveVaultRoot); }
            catch { /* ignore */ }
        }
        else if (string.IsNullOrWhiteSpace(_settings.ActiveVaultRoot))
        {
            // 旧版默认在 ToolData/.../vault；若已存在则继续用，否则迁到 LocalAppData
            var legacy = Path.Combine(data, "vault");
            _settings.ActiveVaultRoot = VaultRepository.LooksLikeVault(legacy)
                ? legacy
                : Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "FlyScreen", "Vaults", "default");
            _settingsStore.Save(_settings);
        }
        Raise();
    }

    public void Start()
    {
        _running = true;
        Raise();
    }

    public void Stop()
    {
        _ = LockAsync();
        _running = false;
        Raise();
    }

    public async Task CreateVaultAsync(string root, string password, CancellationToken ct = default)
    {
        if (!VaultPasswordPolicy.TryValidate(password, out var errorKey))
            throw new ArgumentException(Helpers.Localization.Loc.T(errorKey));

        await LockAsync(ct).ConfigureAwait(false);
        Directory.CreateDirectory(root);
        _repo.Create(root, password);
        _settings.ActiveVaultRoot = Path.GetFullPath(root);
        PersistSettings();
        _currentFolderId = null;
        TouchIdle();
        if (_settings.AutoMount)
            await MountAsync(ct).ConfigureAwait(false);
        Raise();
        await Task.CompletedTask.ConfigureAwait(false);
    }

    public Task OpenVaultAsync(string root, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _repo.OpenLocked(root);
        _settings.ActiveVaultRoot = Path.GetFullPath(root);
        PersistSettings();
        _currentFolderId = null;
        Raise();
        return Task.CompletedTask;
    }

    public async Task UnlockAsync(string password, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_repo.Root) && !string.IsNullOrWhiteSpace(_settings.ActiveVaultRoot))
            _repo.OpenLocked(_settings.ActiveVaultRoot!);
        _repo.Unlock(password);
        _currentFolderId = null;
        TouchIdle();
        if (_settings.AutoMount)
            await MountAsync(ct).ConfigureAwait(false);
        Raise();
    }

    public async Task LockAsync(CancellationToken ct = default)
    {
        CancelIdle();
        await _mount.UnmountAsync().ConfigureAwait(false);
        _tempOpen.CleanupAll();
        _repo.Lock();
        _currentFolderId = null;
        Raise();
        await Task.CompletedTask.ConfigureAwait(false);
    }

    public IReadOnlyList<VaultEntryView> ListCurrent()
    {
        if (!IsUnlocked) return [];
        TouchIdle();
        return _repo.ListChildren(_currentFolderId);
    }

    public void NavigateTo(string? folderId)
    {
        if (!IsUnlocked) return;
        if (folderId != null && _repo.Get(folderId) is not { IsDir: true })
            return;
        _currentFolderId = folderId;
        TouchIdle();
        Raise();
    }

    public void NavigateUp()
    {
        if (!IsUnlocked || string.IsNullOrEmpty(_currentFolderId)) return;
        var cur = _repo.Get(_currentFolderId);
        _currentFolderId = cur?.ParentId;
        TouchIdle();
        Raise();
    }

    public async Task ImportPathsAsync(IEnumerable<string> paths, CancellationToken ct = default)
    {
        EnsureUnlocked();
        var progress = new Progress<VaultProgress>(p => ProgressChanged?.Invoke(p));
        foreach (var path in paths)
        {
            ct.ThrowIfCancellationRequested();
            if (Directory.Exists(path))
                await ImportDirectoryAsync(path, _currentFolderId, progress, ct).ConfigureAwait(false);
            else if (File.Exists(path))
                _repo.ImportFile(_currentFolderId, path, _settings.SecureDeleteSource, progress, ct);
        }
        TouchIdle();
        if (_settings.AutoMount && IsMounted)
            await MountAsync(ct).ConfigureAwait(false);
        Raise();
    }

    public Task ExportSelectedAsync(string entryId, string destPath, CancellationToken ct = default)
    {
        EnsureUnlocked();
        var progress = new Progress<VaultProgress>(p => ProgressChanged?.Invoke(p));
        var entry = _repo.Get(entryId) ?? throw new InvalidOperationException("条目不存在");
        if (entry.IsDir)
            _repo.ExportTree(entryId, destPath, progress, ct);
        else
            _repo.ExportFile(entryId, destPath, progress, ct);
        TouchIdle();
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string entryId, CancellationToken ct = default)
    {
        EnsureUnlocked();
        ct.ThrowIfCancellationRequested();
        _repo.DeleteEntry(entryId, recursive: true);
        TouchIdle();
        Raise();
        return Task.CompletedTask;
    }

    public Task NewFolderAsync(string name, CancellationToken ct = default)
    {
        EnsureUnlocked();
        ct.ThrowIfCancellationRequested();
        _repo.EnsureDirectory(_currentFolderId, name);
        TouchIdle();
        Raise();
        return Task.CompletedTask;
    }

    public async Task OpenEntryAsync(string entryId, CancellationToken ct = default)
    {
        EnsureUnlocked();
        var entry = _repo.Get(entryId) ?? throw new InvalidOperationException("条目不存在");
        if (entry.IsDir)
        {
            NavigateTo(entryId);
            return;
        }

        var mounted = _mount.ResolveMountedPath(entryId);
        if (!string.IsNullOrEmpty(mounted) && File.Exists(mounted))
        {
            Process.Start(new ProcessStartInfo { FileName = mounted, UseShellExecute = true });
        }
        else
        {
            _tempOpen.OpenFile(_repo, entryId);
        }
        TouchIdle();
        await Task.CompletedTask.ConfigureAwait(false);
    }

    public async Task MountAsync(CancellationToken ct = default)
    {
        EnsureUnlocked();
        var progress = new Progress<VaultProgress>(p => ProgressChanged?.Invoke(p));
        await _mount.MountAsync(progress, ct).ConfigureAwait(false);
        TouchIdle();
        Raise();
    }

    public async Task UnmountAsync(CancellationToken ct = default)
    {
        await _mount.UnmountAsync().ConfigureAwait(false);
        Raise();
        await Task.CompletedTask.ConfigureAwait(false);
    }

    public Task OpenMountInExplorerAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!IsMounted || string.IsNullOrEmpty(MountPath))
            throw new InvalidOperationException("尚未挂载");
        Process.Start(new ProcessStartInfo
        {
            FileName = MountPath,
            UseShellExecute = true
        });
        return Task.CompletedTask;
    }

    public async Task MigrateAsync(string destRoot, CancellationToken ct = default)
    {
        if (IsUnlocked)
            await LockAsync(ct).ConfigureAwait(false);
        var src = _settings.ActiveVaultRoot
                  ?? throw new InvalidOperationException("无活动保险箱");
        var progress = new Progress<VaultProgress>(p => ProgressChanged?.Invoke(p));
        await VaultMigrator.MigrateAsync(src, destRoot, progress, ct).ConfigureAwait(false);
        _settings.ActiveVaultRoot = Path.GetFullPath(destRoot);
        PersistSettings();
        _repo.OpenLocked(destRoot);
        Raise();
    }

    public async Task ExportBackupAsync(string flyVaultPath, CancellationToken ct = default)
    {
        if (IsUnlocked)
            await LockAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(_repo.Root))
        {
            if (string.IsNullOrWhiteSpace(_settings.ActiveVaultRoot))
                throw new InvalidOperationException("无活动保险箱");
            _repo.OpenLocked(_settings.ActiveVaultRoot!);
        }
        _repo.ExportArchive(flyVaultPath, ct);
        Raise();
        await Task.CompletedTask.ConfigureAwait(false);
    }

    public Task ImportBackupAsync(string flyVaultPath, string destRoot, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        VaultRepository.ImportArchive(flyVaultPath, destRoot);
        _settings.ActiveVaultRoot = Path.GetFullPath(destRoot);
        PersistSettings();
        _repo.OpenLocked(destRoot);
        Raise();
        return Task.CompletedTask;
    }

    public async Task ApplyUninstallActionAsync(
        VaultUninstallAction action,
        string? decryptDestDir,
        string? password,
        CancellationToken ct = default)
    {
        switch (action)
        {
            case VaultUninstallAction.Cancel:
                return;
            case VaultUninstallAction.KeepEncryptedUninstallTool:
                await LockAsync(ct).ConfigureAwait(false);
                return;
            case VaultUninstallAction.Destroy:
                await LockAsync(ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(_settings.ActiveVaultRoot)
                    && VaultRepository.LooksLikeVault(_settings.ActiveVaultRoot))
                {
                    _repo.OpenLocked(_settings.ActiveVaultRoot!);
                    _repo.DestroyVault();
                }
                _settings.ActiveVaultRoot = null;
                PersistSettings();
                return;
            case VaultUninstallAction.DecryptAndKeep:
                if (string.IsNullOrWhiteSpace(decryptDestDir))
                    throw new ArgumentException("请指定解密目标文件夹");
                if (!IsUnlocked)
                {
                    if (string.IsNullOrEmpty(password))
                        throw new ArgumentException("需要密码以解密保留");
                    await UnlockAsync(password, ct).ConfigureAwait(false);
                }
                var progress = new Progress<VaultProgress>(p => ProgressChanged?.Invoke(p));
                Directory.CreateDirectory(decryptDestDir);
                _repo.ExportTree(null, decryptDestDir, progress, ct);
                await LockAsync(ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(_settings.ActiveVaultRoot))
                {
                    _repo.OpenLocked(_settings.ActiveVaultRoot!);
                    _repo.DestroyVault();
                }
                _settings.ActiveVaultRoot = null;
                PersistSettings();
                return;
            default:
                throw new ArgumentOutOfRangeException(nameof(action));
        }
    }

    public void UpdateSettings(Action<VaultSettingsDto> mutate)
    {
        mutate(_settings);
        PersistSettings();
        Raise();
    }

    private async Task ImportDirectoryAsync(
        string dirPath,
        string? parentId,
        IProgress<VaultProgress> progress,
        CancellationToken ct)
    {
        var name = Path.GetFileName(dirPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var folder = _repo.EnsureDirectory(parentId, name);
        foreach (var file in Directory.EnumerateFiles(dirPath))
        {
            ct.ThrowIfCancellationRequested();
            _repo.ImportFile(folder.Id, file, _settings.SecureDeleteSource, progress, ct);
        }
        foreach (var sub in Directory.EnumerateDirectories(dirPath))
        {
            ct.ThrowIfCancellationRequested();
            await ImportDirectoryAsync(sub, folder.Id, progress, ct).ConfigureAwait(false);
        }
        if (_settings.SecureDeleteSource)
            SecureDelete.DirectoryTree(dirPath);
    }

    private void EnsureUnlocked()
    {
        if (!_repo.IsUnlocked)
            throw new InvalidOperationException("请先解锁保险箱");
    }

    private void PersistSettings() => _settingsStore?.Save(_settings);

    private void Raise() => SessionChanged?.Invoke();

    private void TouchIdle()
    {
        CancelIdle();
        var minutes = Math.Clamp(_settings.IdleLockMinutes, 0, 24 * 60);
        if (minutes <= 0 || !IsUnlocked) return;
        _idleCts = new CancellationTokenSource();
        var token = _idleCts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(minutes), token).ConfigureAwait(false);
                await LockAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { /* ignore */ }
        }, token);
    }

    private void CancelIdle()
    {
        try { _idleCts?.Cancel(); } catch { /* ignore */ }
        _idleCts?.Dispose();
        _idleCts = null;
    }
}
