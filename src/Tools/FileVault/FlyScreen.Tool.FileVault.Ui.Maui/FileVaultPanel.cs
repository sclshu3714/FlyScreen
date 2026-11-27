using System.Collections.ObjectModel;
using FlyScreen.Helpers.Localization;
using FlyScreen.Services.Tools;
using FlyScreen.Tool.FileVault.Store;

namespace FlyScreen.Tool.FileVault.Ui.Maui;

public sealed class FileVaultPanel : ContentView
{
    private readonly IFileVaultService? _service;
    private readonly ObservableCollection<EntryRow> _rows = new();
    private readonly Label _state = new() { FontSize = 13 };
    private readonly Label _status = new() { FontSize = 12 };
    private readonly Label _path = new() { FontSize = 12 };
    private readonly Microsoft.Maui.Controls.CheckBox _secureDelete = new();
    private readonly Microsoft.Maui.Controls.CheckBox _autoMount = new();
    private readonly Entry _idleLock = new() { Keyboard = Keyboard.Numeric, WidthRequest = 64 };
    private readonly CollectionView _list;
    private bool _settingsSuppress;

    public Action<string>? StatusReporter { get; set; }

    public FileVaultPanel()
    {
        _service = ToolService.Instance.GetToolService<IFileVaultService>();

        Button B(string key, Func<Task> action)
        {
            var b = new Button
            {
                Text = Loc.T(key),
                FontSize = 12,
                Padding = new Thickness(10, 6),
                Margin = new Thickness(0, 0, 6, 6)
            };
            b.Clicked += async (_, _) =>
            {
                try { await action(); }
                catch (Exception ex) { await Alert(ex.Message); }
            };
            return b;
        }

        _secureDelete.CheckedChanged += (_, _) => PersistSettings();
        _autoMount.CheckedChanged += (_, _) => PersistSettings();
        _idleLock.Unfocused += (_, _) => PersistIdleLock();

        _list = new CollectionView
        {
            ItemsSource = _rows,
            SelectionMode = SelectionMode.Single,
            EmptyView = new Label
            {
                Text = Loc.T("Tool.FileVault.Empty"),
                Margin = new Thickness(12, 24),
                HorizontalOptions = LayoutOptions.Center
            },
            ItemTemplate = new DataTemplate(() =>
            {
                var label = new Label { FontSize = 13, Margin = new Thickness(8, 6) };
                label.SetBinding(Label.TextProperty, nameof(EntryRow.Name));
                return label;
            })
        };
        _list.SelectionChanged += async (_, e) =>
        {
            if (e.CurrentSelection.FirstOrDefault() is EntryRow row && _service != null)
            {
                try
                {
                    await _service.OpenEntryAsync(row.Id);
                    Refresh();
                }
                catch (Exception ex) { await Alert(ex.Message); }
            }
        };

        Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Padding = 8,
                Spacing = 6,
                Children =
                {
                    new Label { Text = Loc.T("Tool.FileVault.Hint"), FontSize = 12, LineBreakMode = LineBreakMode.WordWrap },
                    new Label { Text = Loc.T("Tool.FileVault.SecurityNotice"), FontSize = 11, LineBreakMode = LineBreakMode.WordWrap },
                    new FlexLayout
                    {
                        Wrap = Microsoft.Maui.Layouts.FlexWrap.Wrap,
                        Children =
                        {
                            B("Tool.FileVault.Create", CreateAsync),
                            B("Tool.FileVault.OpenExisting", OpenAsync),
                            B("Tool.FileVault.Unlock", UnlockAsync),
                            B("Tool.FileVault.Lock", LockAsync),
                            _state
                        }
                    },
                    new FlexLayout
                    {
                        Wrap = Microsoft.Maui.Layouts.FlexWrap.Wrap,
                        Children =
                        {
                            B("Tool.FileVault.Up", () => { _service?.NavigateUp(); Refresh(); return Task.CompletedTask; }),
                            B("Tool.FileVault.Import", ImportAsync),
                            B("Tool.FileVault.ImportFolder", ImportFolderAsync),
                            B("Tool.FileVault.Export", ExportAsync),
                            B("Tool.FileVault.Delete", DeleteAsync),
                            B("Tool.FileVault.NewFolder", NewFolderAsync),
                            B("Tool.FileVault.Mount", MountAsync),
                            B("Tool.FileVault.OpenMount", OpenMountAsync),
                            B("Tool.FileVault.Migrate", MigrateAsync),
                            B("Tool.FileVault.Backup", BackupAsync),
                            B("Tool.FileVault.Restore", RestoreAsync),
                            _path
                        }
                    },
                    new FlexLayout
                    {
                        Wrap = Microsoft.Maui.Layouts.FlexWrap.Wrap,
                        Children =
                        {
                            new HorizontalStackLayout
                            {
                                Spacing = 4,
                                Children =
                                {
                                    _secureDelete,
                                    new Label
                                    {
                                        Text = Loc.T("Tool.FileVault.SecureDelete"),
                                        FontSize = 12,
                                        VerticalOptions = LayoutOptions.Center
                                    }
                                }
                            },
                            new HorizontalStackLayout
                            {
                                Spacing = 4,
                                Margin = new Thickness(12, 0, 0, 0),
                                Children =
                                {
                                    _autoMount,
                                    new Label
                                    {
                                        Text = Loc.T("Tool.FileVault.AutoMount"),
                                        FontSize = 12,
                                        VerticalOptions = LayoutOptions.Center
                                    }
                                }
                            },
                            new Label
                            {
                                Text = Loc.T("Tool.FileVault.IdleLock"),
                                FontSize = 12,
                                VerticalOptions = LayoutOptions.Center,
                                Margin = new Thickness(12, 0, 6, 0)
                            },
                            _idleLock
                        }
                    },
                    _list,
                    _status
                }
            }
        };

        if (_service != null)
        {
            _service.SessionChanged += () => MainThread.BeginInvokeOnMainThread(Refresh);
            _service.ProgressChanged += p => MainThread.BeginInvokeOnMainThread(() =>
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
            s.SecureDeleteSource = _secureDelete.IsChecked;
            s.AutoMount = _autoMount.IsChecked;
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
        _rows.Clear();
        foreach (var e in _service.ListCurrent())
            _rows.Add(new EntryRow(e.Id, (e.IsDir ? "[D] " : "") + e.Name, e.IsDir));
    }

    private async Task CreateAsync()
    {
        if (_service == null) return;
        var folder = await FileVaultMauiDialogs.PickFolderAsync(Loc.T("Tool.FileVault.PickVaultDir"));
        if (string.IsNullOrEmpty(folder)) return;
        var page = GetPage();
        var pwd = await page!.DisplayPromptAsync(Loc.T("Tool.FileVault.Password"), Loc.T("Tool.FileVault.Password"));
        if (string.IsNullOrEmpty(pwd)) return;
        if (!VaultPasswordPolicy.TryValidate(pwd, out var errKey))
            throw new InvalidOperationException(Loc.T(errKey));
        var pwd2 = await page.DisplayPromptAsync(Loc.T("Tool.FileVault.PasswordConfirm"), Loc.T("Tool.FileVault.PasswordConfirm"));
        if (pwd != pwd2) throw new InvalidOperationException(Loc.T("Tool.FileVault.PasswordMismatch"));
        await _service.CreateVaultAsync(folder, pwd);
        Refresh();
    }

    private async Task OpenAsync()
    {
        if (_service == null) return;
        var folder = await FileVaultMauiDialogs.PickFolderAsync(Loc.T("Tool.FileVault.PickVaultDir"));
        if (string.IsNullOrEmpty(folder)) return;
        await _service.OpenVaultAsync(folder);
        Refresh();
    }

    private async Task UnlockAsync()
    {
        if (_service == null) return;
        var pwd = await GetPage()!.DisplayPromptAsync(Loc.T("Tool.FileVault.Password"), Loc.T("Tool.FileVault.Password"));
        if (string.IsNullOrEmpty(pwd)) return;
        await _service.UnlockAsync(pwd);
        Refresh();
    }

    private async Task LockAsync()
    {
        if (_service == null) return;
        await _service.LockAsync();
        Refresh();
    }

    private async Task ImportAsync()
    {
        if (_service == null) return;
        var results = await FilePicker.Default.PickMultipleAsync();
        if (results == null || !results.Any()) return;
        await _service.ImportPathsAsync(results.Select(r => r.FullPath));
        Refresh();
    }

    private async Task ImportFolderAsync()
    {
        if (_service == null) return;
        var folder = await FileVaultMauiDialogs.PickFolderAsync(Loc.T("Tool.FileVault.ImportFolder"));
        if (string.IsNullOrEmpty(folder)) return;
        await _service.ImportPathsAsync([folder]);
        Refresh();
    }

    private async Task ExportAsync()
    {
        if (_service == null || _list.SelectedItem is not EntryRow row) return;
        var folder = await FileVaultMauiDialogs.PickFolderAsync(Loc.T("Tool.FileVault.Export"));
        if (string.IsNullOrEmpty(folder)) return;
        var name = row.Name.Replace("[D] ", "", StringComparison.Ordinal);
        await _service.ExportSelectedAsync(row.Id, Path.Combine(folder, name));
    }

    private async Task DeleteAsync()
    {
        if (_service == null || _list.SelectedItem is not EntryRow row) return;
        await _service.DeleteAsync(row.Id);
        Refresh();
    }

    private async Task NewFolderAsync()
    {
        if (_service == null) return;
        var name = await GetPage()!.DisplayPromptAsync(Loc.T("Tool.FileVault.NewFolder"), "");
        if (string.IsNullOrWhiteSpace(name)) return;
        await _service.NewFolderAsync(name);
        Refresh();
    }

    private async Task MountAsync()
    {
        if (_service == null) return;
        if (_service.IsMounted) await _service.UnmountAsync();
        else await _service.MountAsync();
        Refresh();
    }

    private async Task OpenMountAsync()
    {
        if (_service == null) return;
        await _service.OpenMountInExplorerAsync();
    }

    private async Task MigrateAsync()
    {
        if (_service == null) return;
        var folder = await FileVaultMauiDialogs.PickFolderAsync(Loc.T("Tool.FileVault.PickMigrateDir"));
        if (string.IsNullOrEmpty(folder)) return;
        await _service.MigrateAsync(folder);
        Refresh();
    }

    private async Task BackupAsync()
    {
        if (_service == null) return;
        var folder = await FileVaultMauiDialogs.PickFolderAsync(Loc.T("Tool.FileVault.Backup"));
        if (string.IsNullOrEmpty(folder)) return;
        await _service.ExportBackupAsync(Path.Combine(folder, "vault.flyvault"));
    }

    private async Task RestoreAsync()
    {
        if (_service == null) return;
        var file = await FilePicker.Default.PickAsync();
        if (file == null) return;
        var folder = await FileVaultMauiDialogs.PickFolderAsync(Loc.T("Tool.FileVault.PickVaultDir"));
        if (string.IsNullOrEmpty(folder)) return;
        await _service.ImportBackupAsync(file.FullPath, folder);
        Refresh();
    }

    private Page? GetPage() => Application.Current?.Windows.FirstOrDefault()?.Page;

    private async Task Alert(string message)
    {
        var page = GetPage();
        if (page != null)
            await page.DisplayAlert(Loc.T("Tool.FileVault.Name"), message, Loc.T("Tool.FileVault.Ok"));
    }

    private sealed record EntryRow(string Id, string Name, bool IsDir);
}
