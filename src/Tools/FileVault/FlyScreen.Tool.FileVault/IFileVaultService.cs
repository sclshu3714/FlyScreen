using FlyScreen.Models;
using FlyScreen.Tool.FileVault.Store;

namespace FlyScreen.Tool.FileVault;

public interface IFileVaultService
{
    bool IsRunning { get; }
    bool HasVault { get; }
    bool IsUnlocked { get; }
    string? VaultRoot { get; }
    string? MountPath { get; }
    bool IsMounted { get; }
    string? CurrentFolderId { get; }
    string CurrentPathLabel { get; }
    VaultSettingsDto Settings { get; }

    event Action? SessionChanged;
    event Action<VaultProgress>? ProgressChanged;

    void Start();
    void Stop();

    /// <summary>活动保险箱目录；若未设置则落到默认 LocalAppData\FlyScreen\Vaults\default。</summary>
    string EnsureDefaultRoot();

    Task CreateVaultAsync(string root, string password, CancellationToken ct = default);
    /// <summary>在默认目录创建并解锁（单保险箱，无需再选路径）。</summary>
    Task CreateDefaultVaultAsync(string password, CancellationToken ct = default);
    Task OpenVaultAsync(string root, CancellationToken ct = default);
    Task UnlockAsync(string password, CancellationToken ct = default);
    Task LockAsync(CancellationToken ct = default);

    IReadOnlyList<VaultEntryView> ListCurrent();
    /// <summary>根→当前路径段（根 Title 为可读名，如「保险箱」）。</summary>
    IReadOnlyList<PathBreadcrumbItem> GetPathSegments();
    /// <summary>指定父下的文件夹（› 同级菜单）。</summary>
    IReadOnlyList<VaultEntryView> ListFolders(string? parentId);
    /// <summary>按虚拟路径导航，如 /a/b；失败返回 false。</summary>
    bool TryNavigatePath(string virtualPath);
    void NavigateTo(string? folderId);
    void NavigateUp();

    Task ImportPathsAsync(IEnumerable<string> paths, CancellationToken ct = default);
    /// <summary>导入路径。<paramref name="removeSource"/> 为 false 时只复制，不删除电脑上的原文件。</summary>
    Task ImportPathsAsync(IEnumerable<string> paths, bool removeSource, CancellationToken ct = default);
    Task CreateNewAsync(VaultNewEntry entry, string? shortcutTarget = null, CancellationToken ct = default);
    Task ExportSelectedAsync(string entryId, string destPath, CancellationToken ct = default);
    Task DeleteAsync(string entryId, CancellationToken ct = default);
    Task RenameAsync(string entryId, string newName, CancellationToken ct = default);
    bool HasClipboard { get; }
    void StageClipboard(IReadOnlyList<string> entryIds, bool cut);
    Task PasteAsync(CancellationToken ct = default);
    Task NewFolderAsync(string name, CancellationToken ct = default);
    Task OpenEntryAsync(string entryId, CancellationToken ct = default);

    Task MountAsync(CancellationToken ct = default);
    Task UnmountAsync(CancellationToken ct = default);
    Task OpenMountInExplorerAsync(CancellationToken ct = default);

    Task MigrateAsync(string destRoot, CancellationToken ct = default);
    Task ExportBackupAsync(string flyVaultPath, CancellationToken ct = default);
    Task ImportBackupAsync(string flyVaultPath, string destRoot, CancellationToken ct = default);

    Task ApplyUninstallActionAsync(VaultUninstallAction action, string? decryptDestDir, string? password, CancellationToken ct = default);
    void UpdateSettings(Action<VaultSettingsDto> mutate);
}
