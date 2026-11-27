using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FlyScreen.Helpers.Localization;
using FlyScreen.Services.Tools;
using FlyScreen.Tool.FileVault.Store;
using Microsoft.Win32;

namespace FlyScreen.Tool.FileVault.Ui.Win;

public partial class FileVaultPanel : UserControl
{
    private readonly IFileVaultService? _service;
    private readonly ObservableCollection<EntryRow> _rows = new();
    private bool _settingsSuppress;

    public Action<string>? StatusReporter { get; set; }

    public FileVaultPanel()
    {
        InitializeComponent();
        _service = ToolService.Instance.GetToolService<IFileVaultService>();
        EntryList.ItemsSource = _rows;
        ApplyLabels();
        if (_service != null)
        {
            _service.SessionChanged += () => Dispatcher.Invoke(RefreshUi);
            _service.ProgressChanged += p => Dispatcher.Invoke(() =>
            {
                StatusText.Text = $"{p.Phase} {p.CurrentName} {p.Done}/{p.Total}";
                StatusReporter?.Invoke(StatusText.Text);
            });
        }
        RefreshUi();
    }

    private void ApplyLabels()
    {
        HintText.Text = Loc.T("Tool.FileVault.Hint");
        SecurityNoticeText.Text = Loc.T("Tool.FileVault.SecurityNotice");
        CreateBtn.Content = Loc.T("Tool.FileVault.Create");
        OpenBtn.Content = Loc.T("Tool.FileVault.OpenExisting");
        UnlockBtn.Content = Loc.T("Tool.FileVault.Unlock");
        LockBtn.Content = Loc.T("Tool.FileVault.Lock");
        UpBtn.Content = Loc.T("Tool.FileVault.Up");
        ImportBtn.Content = Loc.T("Tool.FileVault.Import");
        ImportFolderBtn.Content = Loc.T("Tool.FileVault.ImportFolder");
        ExportBtn.Content = Loc.T("Tool.FileVault.Export");
        DeleteBtn.Content = Loc.T("Tool.FileVault.Delete");
        NewFolderBtn.Content = Loc.T("Tool.FileVault.NewFolder");
        MountBtn.Content = Loc.T("Tool.FileVault.Mount");
        OpenMountBtn.Content = Loc.T("Tool.FileVault.OpenMount");
        MigrateBtn.Content = Loc.T("Tool.FileVault.Migrate");
        BackupBtn.Content = Loc.T("Tool.FileVault.Backup");
        RestoreBtn.Content = Loc.T("Tool.FileVault.Restore");
        SecureDeleteCheck.Content = Loc.T("Tool.FileVault.SecureDelete");
        AutoMountCheck.Content = Loc.T("Tool.FileVault.AutoMount");
        IdleLockLabel.Text = Loc.T("Tool.FileVault.IdleLock");
        ColName.Header = Loc.T("Tool.FileVault.ColName");
        ColSize.Header = Loc.T("Tool.FileVault.ColSize");
        ColModified.Header = Loc.T("Tool.FileVault.ColModified");
    }

    private void RefreshUi()
    {
        if (_service == null)
        {
            StatusText.Text = Loc.T("Tool.FileVault.NotReady");
            return;
        }

        _settingsSuppress = true;
        SecureDeleteCheck.IsChecked = _service.Settings.SecureDeleteSource;
        AutoMountCheck.IsChecked = _service.Settings.AutoMount;
        IdleLockBox.Text = _service.Settings.IdleLockMinutes.ToString();
        _settingsSuppress = false;

        StateText.Text = _service.IsUnlocked
            ? Loc.T("Tool.FileVault.Unlocked")
            : _service.HasVault ? Loc.T("Tool.FileVault.Locked") : Loc.T("Tool.FileVault.NoVault");
        PathText.Text = _service.CurrentPathLabel;
        StatusText.Text = string.Format(Loc.T("Tool.FileVault.StatusRoot"), _service.VaultRoot ?? "-");
        if (_service.IsMounted)
            StatusText.Text += "  " + string.Format(Loc.T("Tool.FileVault.StatusMount"), _service.MountPath);

        _rows.Clear();
        foreach (var e in _service.ListCurrent())
        {
            _rows.Add(new EntryRow
            {
                Id = e.Id,
                RawName = e.Name,
                Name = (e.IsDir ? "[D] " : "") + e.Name,
                IsDir = e.IsDir,
                SizeText = e.IsDir ? "" : FormatSize(e.Size),
                ModifiedText = e.ModifiedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
            });
        }
    }

    private void Settings_Changed(object sender, RoutedEventArgs e)
    {
        if (_settingsSuppress || _service == null) return;
        _service.UpdateSettings(s =>
        {
            s.SecureDeleteSource = SecureDeleteCheck.IsChecked == true;
            s.AutoMount = AutoMountCheck.IsChecked == true;
        });
    }

    private void IdleLockBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_settingsSuppress || _service == null) return;
        if (!int.TryParse(IdleLockBox.Text.Trim(), out var minutes))
            minutes = 10;
        minutes = Math.Clamp(minutes, 0, 24 * 60);
        IdleLockBox.Text = minutes.ToString();
        _service.UpdateSettings(s => s.IdleLockMinutes = minutes);
    }

    private async void CreateBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_service == null) return;
        try
        {
            var folder = PickFolder(Loc.T("Tool.FileVault.PickVaultDir"));
            if (folder == null) return;
            var pwd = FileVaultPasswordDialog.Show(Window.GetWindow(this), Loc.T("Tool.FileVault.Password"));
            if (string.IsNullOrEmpty(pwd)) return;
            if (!VaultPasswordPolicy.TryValidate(pwd, out var errKey))
            {
                MessageBox.Show(Loc.T(errKey));
                return;
            }
            var pwd2 = FileVaultPasswordDialog.Show(Window.GetWindow(this), Loc.T("Tool.FileVault.PasswordConfirm"));
            if (pwd != pwd2)
            {
                MessageBox.Show(Loc.T("Tool.FileVault.PasswordMismatch"));
                return;
            }
            await _service.CreateVaultAsync(folder, pwd);
            RefreshUi();
        }
        catch (Exception ex) { ShowErr(ex); }
    }

    private async void OpenBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_service == null) return;
        try
        {
            var folder = PickFolder(Loc.T("Tool.FileVault.PickVaultDir"));
            if (folder == null) return;
            await _service.OpenVaultAsync(folder);
            RefreshUi();
        }
        catch (Exception ex) { ShowErr(ex); }
    }

    private async void UnlockBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_service == null) return;
        try
        {
            var pwd = FileVaultPasswordDialog.Show(Window.GetWindow(this), Loc.T("Tool.FileVault.Password"));
            if (string.IsNullOrEmpty(pwd)) return;
            await _service.UnlockAsync(pwd);
            RefreshUi();
        }
        catch (Exception ex) { ShowErr(ex); }
    }

    private async void LockBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_service == null) return;
        try { await _service.LockAsync(); RefreshUi(); }
        catch (Exception ex) { ShowErr(ex); }
    }

    private void UpBtn_Click(object sender, RoutedEventArgs e)
    {
        _service?.NavigateUp();
        RefreshUi();
    }

    private async void ImportBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_service == null) return;
        try
        {
            var dlg = new OpenFileDialog { Multiselect = true, Title = Loc.T("Tool.FileVault.Import") };
            if (dlg.ShowDialog() != true) return;
            await _service.ImportPathsAsync(dlg.FileNames);
            RefreshUi();
        }
        catch (Exception ex) { ShowErr(ex); }
    }

    private async void ImportFolderBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_service == null) return;
        try
        {
            var folder = PickFolder(Loc.T("Tool.FileVault.ImportFolder"));
            if (folder == null) return;
            await _service.ImportPathsAsync([folder]);
            RefreshUi();
        }
        catch (Exception ex) { ShowErr(ex); }
    }

    private async void ExportBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_service == null || EntryList.SelectedItem is not EntryRow row) return;
        try
        {
            var folder = PickFolder(Loc.T("Tool.FileVault.Export"));
            if (folder == null) return;
            var dest = System.IO.Path.Combine(folder, row.RawName);
            await _service.ExportSelectedAsync(row.Id, dest);
            RefreshUi();
        }
        catch (Exception ex) { ShowErr(ex); }
    }

    private async void DeleteBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_service == null || EntryList.SelectedItem is not EntryRow row) return;
        try
        {
            if (MessageBox.Show(Loc.T("Tool.FileVault.Delete") + "?", Loc.T("Tool.FileVault.Name"),
                    MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;
            await _service.DeleteAsync(row.Id);
            RefreshUi();
        }
        catch (Exception ex) { ShowErr(ex); }
    }

    private async void NewFolderBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_service == null) return;
        try
        {
            var name = Prompt(Loc.T("Tool.FileVault.NewFolder"));
            if (string.IsNullOrWhiteSpace(name)) return;
            await _service.NewFolderAsync(name);
            RefreshUi();
        }
        catch (Exception ex) { ShowErr(ex); }
    }

    private async void MountBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_service == null) return;
        try
        {
            if (_service.IsMounted) await _service.UnmountAsync();
            else await _service.MountAsync();
            RefreshUi();
        }
        catch (Exception ex) { ShowErr(ex); }
    }

    private async void OpenMountBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_service == null) return;
        try { await _service.OpenMountInExplorerAsync(); }
        catch (Exception ex) { ShowErr(ex); }
    }

    private async void MigrateBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_service == null) return;
        try
        {
            var folder = PickFolder(Loc.T("Tool.FileVault.PickMigrateDir"));
            if (folder == null) return;
            await _service.MigrateAsync(folder);
            RefreshUi();
        }
        catch (Exception ex) { ShowErr(ex); }
    }

    private async void BackupBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_service == null) return;
        try
        {
            var dlg = new SaveFileDialog
            {
                Filter = "FlyVault|*.flyvault",
                FileName = "vault.flyvault"
            };
            if (dlg.ShowDialog() != true) return;
            await _service.ExportBackupAsync(dlg.FileName);
        }
        catch (Exception ex) { ShowErr(ex); }
    }

    private async void RestoreBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_service == null) return;
        try
        {
            var open = new OpenFileDialog { Filter = "FlyVault|*.flyvault" };
            if (open.ShowDialog() != true) return;
            var folder = PickFolder(Loc.T("Tool.FileVault.PickVaultDir"));
            if (folder == null) return;
            await _service.ImportBackupAsync(open.FileName, folder);
            RefreshUi();
        }
        catch (Exception ex) { ShowErr(ex); }
    }

    private async void EntryList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_service == null || EntryList.SelectedItem is not EntryRow row) return;
        try
        {
            await _service.OpenEntryAsync(row.Id);
            RefreshUi();
        }
        catch (Exception ex) { ShowErr(ex); }
    }

    private static string? PickFolder(string title)
    {
        var dlg = new OpenFolderDialog { Title = title };
        return dlg.ShowDialog() == true ? dlg.FolderName : null;
    }

    private string? Prompt(string title)
    {
        var win = new Window
        {
            Title = title,
            Width = 360,
            Height = 140,
            Owner = Window.GetWindow(this),
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize
        };
        var box = new TextBox { Margin = new Thickness(16) };
        var ok = new Button { Content = Loc.T("Tool.FileVault.Ok"), Width = 80, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        string? result = null;
        ok.Click += (_, _) => { result = box.Text; win.DialogResult = true; };
        win.Content = new StackPanel
        {
            Children =
            {
                box,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Margin = new Thickness(16),
                    Children = { ok, new Button { Content = Loc.T("Tool.FileVault.Cancel"), Width = 80, IsCancel = true } }
                }
            }
        };
        return win.ShowDialog() == true ? result : null;
    }

    private void ShowErr(Exception ex)
        => MessageBox.Show(Window.GetWindow(this), ex.Message, Loc.T("Tool.FileVault.Name"),
            MessageBoxButton.OK, MessageBoxImage.Warning);

    private static string FormatSize(long size)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double v = size;
        var i = 0;
        while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
        return $"{v:0.##} {units[i]}";
    }

    private sealed class EntryRow
    {
        public string Id { get; set; } = "";
        public string RawName { get; set; } = "";
        public string Name { get; set; } = "";
        public bool IsDir { get; set; }
        public string SizeText { get; set; } = "";
        public string ModifiedText { get; set; } = "";
    }
}
