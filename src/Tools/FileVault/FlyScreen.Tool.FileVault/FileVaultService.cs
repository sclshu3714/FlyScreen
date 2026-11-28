using System.Diagnostics;
using FlyScreen.Helpers.Localization;
using FlyScreen.Models;
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
    private readonly List<string> _clipboard = new();
    private bool _clipboardCut;

    public FileVaultService()
    {
        _mount = new MaterializedVaultMount(_repo);
    }

    public bool IsRunning => _running;
    public bool HasVault => !string.IsNullOrEmpty(_settings.ActiveVaultRoot)
                            && VaultRepository.LooksLikeVault(_settings.ActiveVaultRoot!);
    public bool IsUnlocked => _repo.IsUnlocked;
    public bool LikelyHasEntries
    {
        get
        {
            if (IsUnlocked)
                return _repo.Entries.Values.Any(e => !e.IsDir);
            var n = _repo.Manifest?.EntryCount ?? 0;
            if (n > 0) return true;
            // Manifest 未加载时尝试只读扫一眼
            var root = VaultRoot;
            if (string.IsNullOrWhiteSpace(root) || !VaultRepository.LooksLikeVault(root))
                return false;
            try
            {
                if (string.IsNullOrEmpty(_repo.Root))
                    _repo.OpenLocked(root);
                return (_repo.Manifest?.EntryCount ?? 0) > 0;
            }
            catch
            {
                return false;
            }
        }
    }
    public string? VaultRoot => string.IsNullOrEmpty(_repo.Root) ? _settings.ActiveVaultRoot : _repo.Root;
    public string? MountPath => _mount.MountPath;
    public bool IsMounted => _mount.IsMounted;
    public bool HasClipboard => _clipboard.Count > 0;
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
        // 每次进入工具都必须重新输入主密码
        _ = LockAsync();
    }

    public void Stop()
    {
        _ = LockAsync();
        _running = false;
        Raise();
    }

    public string EnsureDefaultRoot()
    {
        if (string.IsNullOrWhiteSpace(_settings.ActiveVaultRoot))
        {
            _settings.ActiveVaultRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "FlyScreen", "Vaults", "default");
            PersistSettings();
        }
        return _settings.ActiveVaultRoot!;
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

    public Task CreateDefaultVaultAsync(string password, CancellationToken ct = default)
        => CreateVaultAsync(EnsureDefaultRoot(), password, ct);

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

    public IReadOnlyList<PathBreadcrumbItem> GetPathSegments()
    {
        // 根段用可读名（对齐资源管理器「此电脑」），路径条显示：🔐 › 保险箱 › …
        var root = new PathBreadcrumbItem
        {
            Id = null,
            Title = Loc.T("Tool.FileVault.BreadcrumbRoot"),
            ParentId = null
        };
        if (!IsUnlocked || string.IsNullOrEmpty(_currentFolderId))
            return [root];

        var chain = new List<PathBreadcrumbItem>();
        var cur = _currentFolderId;
        var guard = 0;
        while (!string.IsNullOrEmpty(cur) && guard++ < 256)
        {
            var e = _repo.Get(cur);
            if (e == null) break;
            chain.Add(new PathBreadcrumbItem
            {
                Id = e.Id,
                Title = e.Name,
                ParentId = e.ParentId
            });
            cur = e.ParentId;
        }
        chain.Reverse();
        chain.Insert(0, root);
        return chain;
    }

    public IReadOnlyList<VaultEntryView> ListFolders(string? parentId)
    {
        if (!IsUnlocked) return [];
        TouchIdle();
        return _repo.ListChildren(parentId).Where(e => e.IsDir).ToList();
    }

    public bool TryNavigatePath(string virtualPath)
    {
        if (!IsUnlocked) return false;
        if (!_repo.TryResolveVirtualPath(virtualPath, out var folderId))
            return false;
        NavigateTo(folderId);
        return true;
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

    public Task ImportPathsAsync(IEnumerable<string> paths, CancellationToken ct = default)
        => ImportPathsAsync(paths, _settings.SecureDeleteSource, ct);

    public async Task ImportPathsAsync(IEnumerable<string> paths, bool removeSource, CancellationToken ct = default)
    {
        EnsureUnlocked();
        var progress = new Progress<VaultProgress>(p => ProgressChanged?.Invoke(p));
        foreach (var path in paths)
        {
            ct.ThrowIfCancellationRequested();
            if (Directory.Exists(path))
                await ImportDirectoryAsync(path, _currentFolderId, removeSource, progress, ct).ConfigureAwait(false);
            else if (File.Exists(path))
                _repo.ImportFile(_currentFolderId, path, removeSource, progress, ct);
        }
        TouchIdle();
        if (_settings.AutoMount && IsMounted)
            await MountAsync(ct).ConfigureAwait(false);
        Raise();
    }

    public Task CreateNewAsync(VaultNewEntry entry, string? shortcutTarget = null, CancellationToken ct = default)
    {
        EnsureUnlocked();
        ct.ThrowIfCancellationRequested();
        if (entry.Kind == VaultNewKind.Folder)
        {
            var name = string.IsNullOrWhiteSpace(entry.SuggestedName) ? "新建文件夹" : entry.SuggestedName;
            _repo.CreateUniqueDirectory(_currentFolderId, name);
            TouchIdle();
            Raise();
            return Task.CompletedTask;
        }

        string? tempDir = null;
        try
        {
            tempDir = Path.Combine(Path.GetTempPath(), "FlyScreen", "vault-new", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            var fileName = entry.Kind == VaultNewKind.Shortcut
                ? SanitizeFileName(Path.GetFileNameWithoutExtension(shortcutTarget ?? "") + ".lnk")
                : SanitizeFileName(entry.SuggestedName);
            if (string.IsNullOrWhiteSpace(fileName) || fileName == ".lnk")
                throw new InvalidOperationException("没有快捷方式目标");
            var path = Path.Combine(tempDir, fileName);
            if (entry.Kind == VaultNewKind.Shortcut)
                WriteShortcut(path, shortcutTarget ?? throw new InvalidOperationException("没有快捷方式目标"));
            else if (!string.IsNullOrEmpty(entry.TemplatePath) && File.Exists(entry.TemplatePath))
                File.Copy(entry.TemplatePath, path, overwrite: false);
            else
                File.WriteAllBytes(path, entry.TemplateBytes ?? []);
            var progress = new Progress<VaultProgress>(p => ProgressChanged?.Invoke(p));
            _repo.ImportFile(_currentFolderId, path, secureDeleteSource: false, progress, ct);
        }
        finally
        {
            if (tempDir != null)
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { /* ignore */ }
            }
        }
        TouchIdle();
        Raise();
        return Task.CompletedTask;
    }

    private static void WriteShortcut(string path, string target)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new InvalidOperationException("无法创建快捷方式");
        var shell = Activator.CreateInstance(shellType)!;
        var shortcut = shellType.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, shell, [path]);
        shortcut?.GetType().InvokeMember("TargetPath", System.Reflection.BindingFlags.SetProperty, null, shortcut, [target]);
        shortcut?.GetType().InvokeMember("Save", System.Reflection.BindingFlags.InvokeMethod, null, shortcut, null);
    }

    private static string SanitizeFileName(string name)
    {
        name = Path.GetFileName(name.Trim());
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name;
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

    public Task RenameAsync(string entryId, string newName, CancellationToken ct = default)
    {
        EnsureUnlocked();
        ct.ThrowIfCancellationRequested();
        _repo.RenameEntry(entryId, newName);
        TouchIdle();
        Raise();
        return Task.CompletedTask;
    }

    public void StageClipboard(IReadOnlyList<string> entryIds, bool cut)
    {
        _clipboard.Clear();
        _clipboardCut = cut;
        if (entryIds == null) return;
        foreach (var id in entryIds)
        {
            if (!string.IsNullOrWhiteSpace(id))
                _clipboard.Add(id);
        }
    }

    public Task PasteAsync(CancellationToken ct = default)
    {
        EnsureUnlocked();
        if (_clipboard.Count == 0)
            return Task.CompletedTask;
        var ids = _clipboard.ToList();
        foreach (var id in ids)
        {
            ct.ThrowIfCancellationRequested();
            if (_clipboardCut)
                _repo.MoveEntry(id, _currentFolderId);
            else
                _repo.CopyEntry(id, _currentFolderId);
        }
        if (_clipboardCut)
            _clipboard.Clear();
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
            {
                // 确保已打开库目录（未启动过工具窗口时 Root 可能为空）
                if (string.IsNullOrEmpty(_repo.Root))
                {
                    var root = _settings.ActiveVaultRoot;
                    if (string.IsNullOrWhiteSpace(root) || !VaultRepository.LooksLikeVault(root))
                        throw new InvalidOperationException("未找到有效的保险箱数据");
                    _repo.OpenLocked(root);
                }

                // 卸载解密勿走 AutoMount（避免先物化到 Temp 失败而中断）
                if (!IsUnlocked)
                {
                    if (string.IsNullOrEmpty(password))
                        throw new ArgumentException("需要密码以解密保留");
                    _repo.Unlock(password);
                    _currentFolderId = null;
                }

                var hasFiles = _repo.Entries.Values.Any(e => !e.IsDir);
                if (hasFiles)
                {
                    if (string.IsNullOrWhiteSpace(decryptDestDir))
                        throw new ArgumentException("保险箱内有文件，请指定解密输出目录（可粘贴路径）");
                    decryptDestDir = Path.GetFullPath(decryptDestDir);

                    // 禁止解到保险箱目录内部（随后 Destroy 会删掉刚写出的明文）
                    var vaultRoot = Path.GetFullPath(_repo.Root);
                    if (decryptDestDir.Equals(vaultRoot, StringComparison.OrdinalIgnoreCase)
                        || decryptDestDir.StartsWith(
                            vaultRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                            + Path.DirectorySeparatorChar,
                            StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("请选择保险箱目录以外的输出文件夹");

                    Directory.CreateDirectory(decryptDestDir);
                    var progress = new Progress<VaultProgress>(p => ProgressChanged?.Invoke(p));
                    _repo.ExportTree(null, decryptDestDir, progress, ct);
                }
                // 空库：无明文可导，直接销毁加密壳

                await LockAsync(ct).ConfigureAwait(false);
                var destroyRoot = _settings.ActiveVaultRoot;
                if (!string.IsNullOrWhiteSpace(destroyRoot) && VaultRepository.LooksLikeVault(destroyRoot))
                {
                    try
                    {
                        _repo.OpenLocked(destroyRoot);
                        _repo.DestroyVault();
                    }
                    catch
                    {
                        // 明文已导出（或本为空库）；销毁失败不阻断卸载
                    }
                }

                _settings.ActiveVaultRoot = null;
                PersistSettings();
                return;
            }
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
        bool removeSource,
        IProgress<VaultProgress> progress,
        CancellationToken ct)
    {
        var name = Path.GetFileName(dirPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var folder = _repo.EnsureDirectory(parentId, name);
        foreach (var file in Directory.EnumerateFiles(dirPath))
        {
            ct.ThrowIfCancellationRequested();
            _repo.ImportFile(folder.Id, file, removeSource, progress, ct);
        }
        foreach (var sub in Directory.EnumerateDirectories(dirPath))
        {
            ct.ThrowIfCancellationRequested();
            await ImportDirectoryAsync(sub, folder.Id, removeSource, progress, ct).ConfigureAwait(false);
        }
        if (removeSource)
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
