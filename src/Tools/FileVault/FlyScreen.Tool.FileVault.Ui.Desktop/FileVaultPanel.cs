using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using FlyScreen.Helpers.Localization;
using FlyScreen.Services.Tools;
using FlyScreen.Tool.FileVault.Store;

namespace FlyScreen.Tool.FileVault.Ui.Desktop;

public sealed class FileVaultPanel : UserControl
{
    private readonly IFileVaultService? _service;
    private readonly ListBox _list = new();
    private readonly TextBlock _state = new();
    private readonly TextBlock _status = new();
    private readonly TextBlock _path = new();
    private readonly CheckBox _secureDelete = new();
    private readonly CheckBox _autoMount = new();
    private readonly TextBox _idleLock = new() { Width = 48 };
    private bool _settingsSuppress;

    public Action<string>? StatusReporter { get; set; }

    public FileVaultPanel()
    {
        _service = ToolService.Instance.GetToolService<IFileVaultService>();
        var hint = new TextBlock
        {
            Text = Loc.T("Tool.FileVault.Hint"),
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            Margin = new Avalonia.Thickness(0, 0, 0, 4)
        };
        var notice = new TextBlock
        {
            Text = Loc.T("Tool.FileVault.SecurityNotice"),
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            Margin = new Avalonia.Thickness(0, 0, 0, 8),
            FontSize = 11
        };

        Button B(string key, EventHandler<Avalonia.Interactivity.RoutedEventArgs> click)
        {
            var b = new Button { Content = Loc.T(key), Margin = new Avalonia.Thickness(0, 0, 6, 6), Padding = new Avalonia.Thickness(10, 6) };
            b.Click += click;
            return b;
        }

        _secureDelete.Content = Loc.T("Tool.FileVault.SecureDelete");
        _autoMount.Content = Loc.T("Tool.FileVault.AutoMount");
        _secureDelete.IsCheckedChanged += (_, _) => PersistSettings();
        _autoMount.IsCheckedChanged += (_, _) => PersistSettings();
        _idleLock.LostFocus += (_, _) => PersistIdleLock();

        var toolbar1 = new WrapPanel
        {
            Children =
            {
                B("Tool.FileVault.Create", async (_, _) => await CreateAsync()),
                B("Tool.FileVault.OpenExisting", async (_, _) => await OpenAsync()),
                B("Tool.FileVault.Unlock", async (_, _) => await UnlockAsync()),
                B("Tool.FileVault.Lock", async (_, _) => await LockAsync()),
                _state
            }
        };
        var toolbar2 = new WrapPanel
        {
            Children =
            {
                B("Tool.FileVault.Up", (_, _) => { _service?.NavigateUp(); Refresh(); }),
                B("Tool.FileVault.Import", async (_, _) => await ImportAsync()),
                B("Tool.FileVault.ImportFolder", async (_, _) => await ImportFolderAsync()),
                B("Tool.FileVault.Export", async (_, _) => await ExportAsync()),
                B("Tool.FileVault.Delete", async (_, _) => await DeleteAsync()),
                B("Tool.FileVault.NewFolder", async (_, _) => await NewFolderAsync()),
                B("Tool.FileVault.Mount", async (_, _) => await MountAsync()),
                B("Tool.FileVault.OpenMount", async (_, _) => await OpenMountAsync()),
                B("Tool.FileVault.Migrate", async (_, _) => await MigrateAsync()),
                B("Tool.FileVault.Backup", async (_, _) => await BackupAsync()),
                B("Tool.FileVault.Restore", async (_, _) => await RestoreAsync()),
                _path
            }
        };
        var settings = new WrapPanel
        {
            Children =
            {
                _secureDelete,
                _autoMount,
                new TextBlock
                {
                    Text = Loc.T("Tool.FileVault.IdleLock"),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Avalonia.Thickness(8, 0, 6, 0)
                },
                _idleLock
            }
        };

        _list.DoubleTapped += async (_, _) =>
        {
            if (_list.SelectedItem is EntryRow row && _service != null)
            {
                await _service.OpenEntryAsync(row.Id);
                Refresh();
            }
        };

        var top = new StackPanel { Children = { hint, notice, toolbar1, toolbar2, settings, _status } };
        DockPanel.SetDock(top, Dock.Top);
        Content = new DockPanel
        {
            Margin = new Avalonia.Thickness(4),
            LastChildFill = true,
            Children = { top, _list }
        };

        if (_service != null)
        {
            _service.SessionChanged += () => Dispatcher.UIThread.Post(Refresh);
            _service.ProgressChanged += p => Dispatcher.UIThread.Post(() =>
            {
                _status.Text = $"{p.Phase} {p.CurrentName} {p.Done}/{p.Total}";
                StatusReporter?.Invoke(_status.Text);
            });
        }
        Refresh();
    }

    private void PersistSettings()
    {
        if (_settingsSuppress || _service == null) return;
        _service.UpdateSettings(s =>
        {
            s.SecureDeleteSource = _secureDelete.IsChecked == true;
            s.AutoMount = _autoMount.IsChecked == true;
        });
    }

    private void PersistIdleLock()
    {
        if (_settingsSuppress || _service == null) return;
        if (!int.TryParse(_idleLock.Text?.Trim(), out var minutes))
            minutes = 10;
        minutes = Math.Clamp(minutes, 0, 24 * 60);
        _idleLock.Text = minutes.ToString();
        _service.UpdateSettings(s => s.IdleLockMinutes = minutes);
    }

    private void Refresh()
    {
        if (_service == null)
        {
            _status.Text = Loc.T("Tool.FileVault.NotReady");
            return;
        }

        _settingsSuppress = true;
        _secureDelete.IsChecked = _service.Settings.SecureDeleteSource;
        _autoMount.IsChecked = _service.Settings.AutoMount;
        _idleLock.Text = _service.Settings.IdleLockMinutes.ToString();
        _settingsSuppress = false;

        _state.Text = _service.IsUnlocked
            ? Loc.T("Tool.FileVault.Unlocked")
            : _service.HasVault ? Loc.T("Tool.FileVault.Locked") : Loc.T("Tool.FileVault.NoVault");
        _path.Text = _service.CurrentPathLabel;
        _status.Text = string.Format(Loc.T("Tool.FileVault.StatusRoot"), _service.VaultRoot ?? "-");
        _list.ItemsSource = _service.ListCurrent()
            .Select(e => new EntryRow(e.Id, (e.IsDir ? "[D] " : "") + e.Name, e.IsDir))
            .ToList();
    }

    private async Task CreateAsync()
    {
        if (_service == null || VisualRoot is not Window wnd) return;
        try
        {
            var folder = await PickFolder(wnd, Loc.T("Tool.FileVault.PickVaultDir"));
            if (folder == null) return;
            var pwd = await FileVaultDialogs.PromptPassword(wnd);
            if (string.IsNullOrEmpty(pwd)) return;
            if (!VaultPasswordPolicy.TryValidate(pwd, out var errKey))
                throw new InvalidOperationException(Loc.T(errKey));
            var pwd2 = await FileVaultDialogs.PromptPassword(wnd);
            if (pwd != pwd2) throw new InvalidOperationException(Loc.T("Tool.FileVault.PasswordMismatch"));
            await _service.CreateVaultAsync(folder, pwd);
            Refresh();
        }
        catch (Exception ex) { await Alert(ex.Message); }
    }

    private async Task OpenAsync()
    {
        if (_service == null || VisualRoot is not Window wnd) return;
        try
        {
            var folder = await PickFolder(wnd, Loc.T("Tool.FileVault.PickVaultDir"));
            if (folder == null) return;
            await _service.OpenVaultAsync(folder);
            Refresh();
        }
        catch (Exception ex) { await Alert(ex.Message); }
    }

    private async Task UnlockAsync()
    {
        if (_service == null || VisualRoot is not Window wnd) return;
        try
        {
            var pwd = await FileVaultDialogs.PromptPassword(wnd);
            if (string.IsNullOrEmpty(pwd)) return;
            await _service.UnlockAsync(pwd);
            Refresh();
        }
        catch (Exception ex) { await Alert(ex.Message); }
    }

    private async Task LockAsync()
    {
        if (_service == null) return;
        try { await _service.LockAsync(); Refresh(); }
        catch (Exception ex) { await Alert(ex.Message); }
    }

    private async Task ImportAsync()
    {
        if (_service == null || VisualRoot is not Window wnd) return;
        try
        {
            var files = await wnd.StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
            {
                AllowMultiple = true,
                Title = Loc.T("Tool.FileVault.Import")
            });
            if (files.Count == 0) return;
            await _service.ImportPathsAsync(files.Select(f => f.Path.LocalPath));
            Refresh();
        }
        catch (Exception ex) { await Alert(ex.Message); }
    }

    private async Task ImportFolderAsync()
    {
        if (_service == null || VisualRoot is not Window wnd) return;
        try
        {
            var folder = await PickFolder(wnd, Loc.T("Tool.FileVault.ImportFolder"));
            if (folder == null) return;
            await _service.ImportPathsAsync([folder]);
            Refresh();
        }
        catch (Exception ex) { await Alert(ex.Message); }
    }

    private async Task ExportAsync()
    {
        if (_service == null || VisualRoot is not Window wnd || _list.SelectedItem is not EntryRow row) return;
        try
        {
            var folder = await PickFolder(wnd, Loc.T("Tool.FileVault.Export"));
            if (folder == null) return;
            var name = row.Name.Replace("[D] ", "", StringComparison.Ordinal);
            await _service.ExportSelectedAsync(row.Id, Path.Combine(folder, name));
        }
        catch (Exception ex) { await Alert(ex.Message); }
    }

    private async Task DeleteAsync()
    {
        if (_service == null || _list.SelectedItem is not EntryRow row) return;
        try { await _service.DeleteAsync(row.Id); Refresh(); }
        catch (Exception ex) { await Alert(ex.Message); }
    }

    private async Task NewFolderAsync()
    {
        if (_service == null || VisualRoot is not Window wnd) return;
        try
        {
            var name = await FileVaultDialogs.PromptText(wnd, Loc.T("Tool.FileVault.NewFolder"));
            if (string.IsNullOrWhiteSpace(name)) return;
            await _service.NewFolderAsync(name);
            Refresh();
        }
        catch (Exception ex) { await Alert(ex.Message); }
    }

    private async Task MountAsync()
    {
        if (_service == null) return;
        try
        {
            if (_service.IsMounted) await _service.UnmountAsync();
            else await _service.MountAsync();
            Refresh();
        }
        catch (Exception ex) { await Alert(ex.Message); }
    }

    private async Task OpenMountAsync()
    {
        if (_service == null) return;
        try { await _service.OpenMountInExplorerAsync(); }
        catch (Exception ex) { await Alert(ex.Message); }
    }

    private async Task MigrateAsync()
    {
        if (_service == null || VisualRoot is not Window wnd) return;
        try
        {
            var folder = await PickFolder(wnd, Loc.T("Tool.FileVault.PickMigrateDir"));
            if (folder == null) return;
            await _service.MigrateAsync(folder);
            Refresh();
        }
        catch (Exception ex) { await Alert(ex.Message); }
    }

    private async Task BackupAsync()
    {
        if (_service == null || VisualRoot is not Window wnd) return;
        try
        {
            var file = await wnd.StorageProvider.SaveFilePickerAsync(new Avalonia.Platform.Storage.FilePickerSaveOptions
            {
                SuggestedFileName = "vault.flyvault",
                DefaultExtension = "flyvault"
            });
            if (file == null) return;
            await _service.ExportBackupAsync(file.Path.LocalPath);
        }
        catch (Exception ex) { await Alert(ex.Message); }
    }

    private async Task RestoreAsync()
    {
        if (_service == null || VisualRoot is not Window wnd) return;
        try
        {
            var files = await wnd.StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
            {
                AllowMultiple = false,
                Title = Loc.T("Tool.FileVault.Restore")
            });
            if (files.Count == 0) return;
            var folder = await PickFolder(wnd, Loc.T("Tool.FileVault.PickVaultDir"));
            if (folder == null) return;
            await _service.ImportBackupAsync(files[0].Path.LocalPath, folder);
            Refresh();
        }
        catch (Exception ex) { await Alert(ex.Message); }
    }

    private static async Task<string?> PickFolder(Window wnd, string title)
    {
        var folders = await wnd.StorageProvider.OpenFolderPickerAsync(new Avalonia.Platform.Storage.FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false
        });
        return folders.Count > 0 ? folders[0].Path.LocalPath : null;
    }

    private async Task Alert(string message)
    {
        if (VisualRoot is not Window wnd) return;
        var win = new Window
        {
            Title = Loc.T("Tool.FileVault.Name"),
            Width = 360,
            Height = 140,
            Content = new TextBlock { Text = message, Margin = new Avalonia.Thickness(16), TextWrapping = Avalonia.Media.TextWrapping.Wrap }
        };
        await win.ShowDialog(wnd);
    }

    private sealed record EntryRow(string Id, string Name, bool IsDir)
    {
        public override string ToString() => Name;
    }
}
