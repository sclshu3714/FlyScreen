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

    Task CreateVaultAsync(string root, string password, CancellationToken ct = default);
    Task OpenVaultAsync(string root, CancellationToken ct = default);
    Task UnlockAsync(string password, CancellationToken ct = default);
    Task LockAsync(CancellationToken ct = default);

    IReadOnlyList<VaultEntryView> ListCurrent();
    void NavigateTo(string? folderId);
    void NavigateUp();

    Task ImportPathsAsync(IEnumerable<string> paths, CancellationToken ct = default);
    Task ExportSelectedAsync(string entryId, string destPath, CancellationToken ct = default);
    Task DeleteAsync(string entryId, CancellationToken ct = default);
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
