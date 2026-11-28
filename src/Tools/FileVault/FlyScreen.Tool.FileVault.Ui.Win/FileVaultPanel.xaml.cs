using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using FlyScreen.Win32Clipboard;
using FlyScreen.Helpers.Localization;
using FlyScreen.Models;
using FlyScreen.Services.Tools;
using FlyScreen.Tool.FileVault.Store;
using FlyScreen.Ui.Theme.Wpf;
using FlyScreen.Ui.Wpf;
using Microsoft.Win32;

namespace FlyScreen.Tool.FileVault.Ui.Win;

public partial class FileVaultPanel : UserControl
{
    private readonly IFileVaultService? _service;
    private readonly ObservableCollection<EntryRow> _rows = new();
    private readonly List<EntryRow> _allRows = new();
    private readonly FlyPathBreadcrumbBar _pathBar = new();
    private readonly Stack<string?> _backStack = new();
    private readonly Stack<string?> _forwardStack = new();
    private bool _navHistorySuppress;
    private bool _gateNeedsConfirm;
    private bool _forcingLock;
    private bool _entered;
    private ExplorerFileDropTarget? _explorerDrop;
    private HwndSource? _dropSource;
    private HwndSourceHook? _dropHook;

    public Action<string>? StatusReporter { get; set; }

    public FileVaultPanel()
    {
        InitializeComponent();
        _service = ToolService.Instance.GetToolService<IFileVaultService>();
        EntryList.ItemsSource = _rows;
        PathBarHost.Content = _pathBar;
        // 锁形根图标（对齐「此电脑」位）
        _pathBar.RootIconGeometry =
            "M12,1 C8.7,1 6,3.7 6,7 L6,9 L4,9 L4,21 L20,21 L20,9 L18,9 L18,7 C18,3.7 15.3,1 12,1 Z M12,3 C14.2,3 16,4.8 16,7 L16,9 L8,9 L8,7 C8,4.8 9.8,3 12,3 Z";
        _pathBar.SegmentClicked += OnPathSegmentClicked;
        _pathBar.PathCommitted += OnPathCommitted;
        _pathBar.BackClicked += () => GoHistory(_backStack, _forwardStack);
        _pathBar.ForwardClicked += () => GoHistory(_forwardStack, _backStack);
        _pathBar.UpClicked += () => NavigateRecording(() => _service?.NavigateUp());
        _pathBar.RefreshClicked += RefreshUi;
        _pathBar.SiblingProvider = parentId =>
        {
            if (_service == null) return Array.Empty<PathBreadcrumbItem>();
            return _service.ListFolders(parentId)
                .Select(f => new PathBreadcrumbItem
                {
                    Id = f.Id,
                    Title = f.Name,
                    ParentId = f.ParentId
                })
                .ToList();
        };
        AllowDrop = false;
        ApplyLabels();
        if (_service != null)
        {
            _ = _service.EnsureDefaultRoot();
            _service.SessionChanged += () => Dispatcher.Invoke(RefreshUi);
            _service.ProgressChanged += p => Dispatcher.Invoke(() =>
            {
                StatusText.Text = $"{p.Phase} {p.CurrentName} {p.Done}/{p.Total}";
                StatusReporter?.Invoke(StatusText.Text);
            });
        }
        RefreshUi();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        WpfDialogTheme.ApplyTo(this);
        if (Window.GetWindow(this) is { } win)
        {
            win.StateChanged -= HostWindow_StateChanged;
            win.StateChanged += HostWindow_StateChanged;
            HostWindow_StateChanged(win, EventArgs.Empty);
            var hwnd = new WindowInteropHelper(win).EnsureHandle();
            ExplorerFileDropTarget.Allow(hwnd);
            _explorerDrop?.Dispose();
            _explorerDrop = ExplorerFileDropTarget.TryAttachWpf(hwnd, AcceptExplorerDrop);
            if (_dropSource == null)
            {
                _dropSource = HwndSource.FromHwnd(hwnd);
                _dropHook = DropWndProc;
                _dropSource?.AddHook(_dropHook);
            }
            _ = Dispatcher.BeginInvoke(() => _explorerDrop?.Reaffirm());
        }
        await EnterRequirePasswordAsync();
    }

    private void HostWindow_StateChanged(object? sender, EventArgs e)
    {
        if (Window.GetWindow(this) is { } win)
            ChromeMaxBtn.Content = win.WindowState == WindowState.Maximized ? "❐" : "☐";
    }

    private void SearchBtn_Click(object sender, RoutedEventArgs e)
    {
        SearchBox.Focus();
        ApplySearchFilter();
    }

    private async void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_dropSource != null && _dropHook != null)
        {
            try { _dropSource.RemoveHook(_dropHook); } catch { /* ignore */ }
            _dropSource = null;
        }
        _explorerDrop?.Dispose();
        _explorerDrop = null;
        await LeaveLockAsync();
    }

    private async Task EnterRequirePasswordAsync()
    {
        if (_service == null || _forcingLock) return;
        _entered = true;
        _forcingLock = true;
        try
        {
            if (_service.IsUnlocked)
                await _service.LockAsync();
            else
                RefreshUi();
        }
        catch
        {
            RefreshUi();
        }
        finally
        {
            _forcingLock = false;
        }
    }

    private async Task LeaveLockAsync()
    {
        if (_service == null || !_entered || _forcingLock) return;
        _entered = false;
        _forcingLock = true;
        try
        {
            if (_service.IsUnlocked)
                await _service.LockAsync();
        }
        catch { /* ignore */ }
        finally
        {
            _forcingLock = false;
        }
    }

    private void OnPathSegmentClicked(PathBreadcrumbItem seg)
        => NavigateRecording(() => _service?.NavigateTo(seg.Id));

    private void OnPathCommitted(string path)
        => NavigateRecording(() => _service?.TryNavigatePath(path));

    private void NavigateRecording(Action? navigate)
    {
        if (_service == null || navigate == null) return;
        if (!_navHistorySuppress)
        {
            _backStack.Push(_service.CurrentFolderId);
            _forwardStack.Clear();
        }
        navigate();
        RefreshUi();
    }

    private void GoHistory(Stack<string?> from, Stack<string?> to)
    {
        if (_service == null || from.Count == 0) return;
        to.Push(_service.CurrentFolderId);
        _navHistorySuppress = true;
        _service.NavigateTo(from.Pop());
        _navHistorySuppress = false;
        RefreshUi();
    }

    private void ApplyLabels()
    {
        TabTitleText.Text = Loc.T("Tool.FileVault.Name");
        NewFolderBtnText.Text = Loc.T("Tool.FileVault.New");
        NewFolderBtn.ToolTip = Loc.T("Tool.FileVault.NewFolder");
        AddFileBtn.ToolTip = Loc.T("Tool.FileVault.AddFile");
        AddFolderBtn.ToolTip = Loc.T("Tool.FileVault.AddFolder");
        DeleteCmdBtn.ToolTip = Loc.T("Tool.FileVault.Delete");
        SortBtnText.Text = Loc.T("Tool.FileVault.Sort");
        ViewBtnText.Text = Loc.T("Tool.FileVault.View");
        SearchBox.Tag = Loc.T("Tool.FileVault.SearchIn");
        if (string.IsNullOrEmpty(SearchBox.Text))
            SearchBox.ToolTip = Loc.T("Tool.FileVault.SearchIn");
        _pathBar.SetNavTips(
            Loc.T("Tool.FileVault.Back"),
            Loc.T("Tool.FileVault.Forward"),
            Loc.T("Tool.FileVault.Up"),
            Loc.T("Tool.FileVault.Refresh"));

        MiCutText.Text = Loc.T("Tool.FileVault.Cut");
        MiCopyText.Text = Loc.T("Tool.FileVault.Copy");
        MiRenameText.Text = Loc.T("Tool.FileVault.Rename");
        MiDeleteTopText.Text = Loc.T("Tool.FileVault.Delete");
        MiOpen.Header = Loc.T("Tool.FileVault.Open");
        MiPaste.Header = Loc.T("Tool.FileVault.Paste");
        MiDelete.Header = Loc.T("Tool.FileVault.Delete");
        MiImport.Header = Loc.T("Tool.FileVault.Import");
        MiImportFile.Header = Loc.T("Tool.FileVault.ImportFile");
        MiImportFolder.Header = Loc.T("Tool.FileVault.ImportFolder");
        MiImportBackup.Header = Loc.T("Tool.FileVault.ImportBackup");
        MiExport.Header = Loc.T("Tool.FileVault.Export");
        MiExportFile.Header = Loc.T("Tool.FileVault.ExportFile");
        MiExportFolder.Header = Loc.T("Tool.FileVault.ExportFolder");
        MiExportBackup.Header = Loc.T("Tool.FileVault.ExportBackup");
        MiIdleLock.Header = Loc.T("Tool.FileVault.IdleLock");

        ColNameHeader.Text = Loc.T("Tool.FileVault.ColName");
        ColTypeHeader.Text = Loc.T("Tool.FileVault.ColType");
        ColSizeHeader.Text = Loc.T("Tool.FileVault.ColSize");
        ColModifiedHeader.Text = Loc.T("Tool.FileVault.ColModified");

        GatePassword.Placeholder = Loc.T("Tool.FileVault.PasswordPlaceholder");
        GatePasswordConfirm.Placeholder = Loc.T("Tool.FileVault.ConfirmPlaceholder");
        GatePassword.ShowPasswordTip = Loc.T("Sec.ShowPassword");
        GatePassword.HidePasswordTip = Loc.T("Sec.HidePassword");
        GatePasswordConfirm.ShowPasswordTip = Loc.T("Sec.ShowPassword");
        GatePasswordConfirm.HidePasswordTip = Loc.T("Sec.HidePassword");
    }

    private void TabCloseBtn_Click(object sender, RoutedEventArgs e) => CloseHostWindow();

    private void ChromeCloseBtn_Click(object sender, RoutedEventArgs e) => CloseHostWindow();

    private void ChromeMinBtn_Click(object sender, RoutedEventArgs e)
    {
        var win = Window.GetWindow(this);
        if (win != null) win.WindowState = WindowState.Minimized;
    }

    private void ChromeMaxBtn_Click(object sender, RoutedEventArgs e)
    {
        var win = Window.GetWindow(this);
        if (win == null) return;
        win.WindowState = win.WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
        ChromeMaxBtn.Content = win.WindowState == WindowState.Maximized ? "❐" : "☐";
    }

    private void CloseHostWindow()
    {
        try
        {
            _ = ToolService.Instance.Ui.RequestAsync(ToolIds.FileVault, Module.UiCloseWindow, null);
        }
        catch { /* ignore */ }
    }

    private void RefreshUi()
    {
        if (_service == null)
        {
            StatusText.Text = Loc.T("Tool.FileVault.NotReady");
            ShowGate(Loc.T("Tool.FileVault.NotReady"), Loc.T("Tool.FileVault.NotReady"), needsConfirm: false, actionEnabled: false);
            return;
        }

        var root = _service.EnsureDefaultRoot();
        var unlocked = _service.IsUnlocked;
        NewFolderBtn.IsEnabled = unlocked;
        AddFileBtn.IsEnabled = unlocked;
        AddFolderBtn.IsEnabled = unlocked;
        NavRow.IsEnabled = unlocked;
        SearchBox.IsEnabled = unlocked;

        if (!unlocked)
        {
            _rows.Clear();
            _allRows.Clear();
            _pathBar.SetItems([]);
            _pathBar.SetNavState(false, false, false);
            StatusText.Text = string.Format(Loc.T("Tool.FileVault.StatusRoot"), root);
            GatePassword.Password = "";
            GatePasswordConfirm.Password = "";
            GateError.Text = "";

            if (_service.HasVault)
            {
                ShowGate(Loc.T("Tool.FileVault.Name"), Loc.T("Tool.FileVault.UnlockPrompt"),
                    needsConfirm: false, actionEnabled: true);
                GateActionBtn.Content = Loc.T("Tool.FileVault.Unlock");
            }
            else
            {
                ShowGate(Loc.T("Tool.FileVault.Name"), Loc.T("Tool.FileVault.NoVaultPrompt"),
                    needsConfirm: true, actionEnabled: true);
                GateActionBtn.Content = Loc.T("Tool.FileVault.Enable");
            }
            return;
        }

        HideGate();
        var items = _service.ListCurrent();
        _pathBar.SetItems(_service.GetPathSegments());
        _pathBar.SetNavState(
            canBack: _backStack.Count > 0,
            canForward: _forwardStack.Count > 0,
            canUp: !string.IsNullOrEmpty(_service.CurrentFolderId));

        _allRows.Clear();
        foreach (var e in items)
        {
            _allRows.Add(new EntryRow
            {
                Id = e.Id,
                Name = e.Name,
                IsDir = e.IsDir,
                Icon = e.IsDir ? "📁" : "📄",
                TypeText = e.IsDir ? Loc.T("Tool.FileVault.TypeFolder") : Loc.T("Tool.FileVault.TypeFile"),
                SizeText = e.IsDir ? "" : FormatSize(e.Size),
                ModifiedText = e.ModifiedUtc.ToLocalTime().ToString("yyyy/MM/dd HH:mm")
            });
        }
        ApplySearchFilter();
        UpdateStatusBar();
    }

    private void ShowGate(string title, string hint, bool needsConfirm, bool actionEnabled)
    {
        _gateNeedsConfirm = needsConfirm;
        GateHost.Visibility = Visibility.Visible;
        ExplorerHost.Visibility = Visibility.Collapsed;
        NavRow.Visibility = Visibility.Collapsed;
        GateTitle.Text = title;
        GateHint.Text = hint;
        GatePasswordConfirm.Visibility = needsConfirm ? Visibility.Visible : Visibility.Collapsed;
        GateActionBtn.IsEnabled = actionEnabled;
        Dispatcher.BeginInvoke(() => GatePassword.FocusInput(), System.Windows.Threading.DispatcherPriority.Input);
    }

    private void HideGate()
    {
        GateHost.Visibility = Visibility.Collapsed;
        ExplorerHost.Visibility = Visibility.Visible;
        NavRow.Visibility = Visibility.Visible;
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        => ApplySearchFilter();

    private void ApplySearchFilter()
    {
        var q = (SearchBox.Text ?? "").Trim();
        _rows.Clear();
        foreach (var row in _allRows)
        {
            if (q.Length == 0 || row.Name.Contains(q, StringComparison.OrdinalIgnoreCase))
                _rows.Add(row);
        }
        UpdateStatusBar();
    }

    private void EntryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => UpdateStatusBar();

    private void UpdateStatusBar()
    {
        var sel = EntryList.SelectedItems.Count;
        var text = string.Format(Loc.T("Tool.FileVault.ItemsCount"), _rows.Count);
        if (sel > 0)
            text += "  " + string.Format(Loc.T("Tool.FileVault.SelectedCount"), sel);
        StatusText.Text = text;
        StatusReporter?.Invoke(text);
    }

    private async void GateActionBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_service == null) return;
        GateError.Text = "";
        var pwd = GatePassword.Password;
        try
        {
            if (_gateNeedsConfirm)
            {
                if (!VaultPasswordPolicy.TryValidate(pwd, out var errKey))
                {
                    GateError.Text = Loc.T(errKey);
                    return;
                }
                if (pwd != GatePasswordConfirm.Password)
                {
                    GateError.Text = Loc.T("Tool.FileVault.PasswordMismatch");
                    return;
                }
                await _service.CreateDefaultVaultAsync(pwd);
            }
            else
            {
                if (string.IsNullOrEmpty(pwd))
                {
                    GateError.Text = Loc.T("Tool.FileVault.PasswordEmpty");
                    return;
                }
                await _service.UnlockAsync(pwd);
            }
            SearchBox.Text = "";
            RefreshUi();
        }
        catch (Exception ex)
        {
            GateError.Text = ex.Message;
        }
    }

    private void VaultMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (_service == null) return;
        var row = EntryList.SelectedItem as EntryRow;
        var unlocked = _service.IsUnlocked;
        MiOpen.IsEnabled = row != null && unlocked;
        MiPaste.IsEnabled = unlocked && _service.HasClipboard;
        MiCutBtn.IsEnabled = row != null && unlocked;
        MiCopyBtn.IsEnabled = row != null && unlocked;
        MiRenameBtn.IsEnabled = row != null && unlocked;
        MiCutBtn.Opacity = MiCutBtn.IsEnabled ? 1 : 0.4;
        MiCopyBtn.Opacity = MiCopyBtn.IsEnabled ? 1 : 0.4;
        MiRenameBtn.Opacity = MiRenameBtn.IsEnabled ? 1 : 0.4;
        MiDelete.IsEnabled = row != null && unlocked;
        MiImport.IsEnabled = unlocked;
        MiImportFile.IsEnabled = unlocked;
        MiImportFolder.IsEnabled = unlocked;
        MiImportBackup.IsEnabled = true;
        MiExportFile.IsEnabled = unlocked && row is { IsDir: false };
        MiExportFolder.IsEnabled = unlocked && row is { IsDir: true };
        MiExportBackup.IsEnabled = _service.HasVault;
        MiIdleLock.Header = Loc.T("Tool.FileVault.IdleLock") + $" ({_service.Settings.IdleLockMinutes})";
    }

    private void EntryList_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var dep = e.OriginalSource as DependencyObject;
        while (dep != null && dep is not ListViewItem)
            dep = System.Windows.Media.VisualTreeHelper.GetParent(dep);
        if (dep is ListViewItem item)
            item.IsSelected = true;
    }

    private void MiIdleLock_Click(object sender, RoutedEventArgs e)
    {
        if (_service == null) return;
        var cur = _service.Settings.IdleLockMinutes.ToString();
        var text = Prompt(Loc.T("Tool.FileVault.IdleLock"), cur);
        if (text == null) return;
        if (!int.TryParse(text.Trim(), out var minutes))
            minutes = 10;
        minutes = Math.Clamp(minutes, 0, 24 * 60);
        _service.UpdateSettings(s => s.IdleLockMinutes = minutes);
    }

    private void MiCut_Click(object sender, RoutedEventArgs e) => StageClipboard(cut: true);

    private void MiCopy_Click(object sender, RoutedEventArgs e) => StageClipboard(cut: false);

    private void StageClipboard(bool cut)
    {
        if (_service == null) return;
        var ids = EntryList.SelectedItems.OfType<EntryRow>().Select(r => r.Id).ToList();
        if (ids.Count == 0) return;
        _service.StageClipboard(ids, cut);
        VaultMenu.IsOpen = false;
    }

    private async void MiPaste_Click(object sender, RoutedEventArgs e)
    {
        if (_service == null || !EnsureUnlocked()) return;
        try
        {
            await _service.PasteAsync();
            RefreshUi();
        }
        catch (Exception ex) { ShowErr(ex); }
    }

    private async void MiRename_Click(object sender, RoutedEventArgs e)
    {
        if (_service == null || EntryList.SelectedItem is not EntryRow row || !EnsureUnlocked()) return;
        VaultMenu.IsOpen = false;
        try
        {
            var name = Prompt(Loc.T("Tool.FileVault.Rename"), row.Name);
            if (string.IsNullOrWhiteSpace(name)) return;
            await _service.RenameAsync(row.Id, name.Trim());
            RefreshUi();
        }
        catch (Exception ex) { ShowErr(ex); }
    }

    private IntPtr DropWndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        _explorerDrop?.TryHandle(hwnd, msg, wParam, lParam, ref handled);
        return IntPtr.Zero;
    }

    private void AcceptExplorerDrop(IReadOnlyList<string> paths)
    {
        Dispatcher.InvokeAsync(async () =>
        {
            if (!EnsureUnlocked() || _service == null || paths.Count == 0) return;
            try
            {
                await _service.ImportPathsAsync(paths, removeSource: false);
                RefreshUi();
            }
            catch (Exception ex) { ShowErr(ex); }
        });
    }

    private async void MiOpen_Click(object sender, RoutedEventArgs e)
    {
        if (_service == null || EntryList.SelectedItem is not EntryRow row) return;
        try
        {
            if (row.IsDir)
                NavigateRecording(() => _service.NavigateTo(row.Id));
            else
            {
                await _service.OpenEntryAsync(row.Id);
                RefreshUi();
            }
        }
        catch (Exception ex) { ShowErr(ex); }
    }

    private bool EnsureUnlocked()
    {
        if (_service?.IsUnlocked == true) return true;
        MessageBox.Show(Window.GetWindow(this), Loc.T("Tool.FileVault.NeedUnlock"),
            Loc.T("Tool.FileVault.Name"), MessageBoxButton.OK, MessageBoxImage.Information);
        GatePassword.FocusInput();
        return false;
    }

    private async void ImportBtn_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureUnlocked() || _service == null) return;
        try
        {
            var dlg = new OpenFileDialog { Multiselect = true, Title = Loc.T("Tool.FileVault.ImportFile") };
            if (dlg.ShowDialog() != true) return;
            await _service.ImportPathsAsync(dlg.FileNames);
            RefreshUi();
        }
        catch (Exception ex) { ShowErr(ex); }
    }

    private async void ImportFolderBtn_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureUnlocked() || _service == null) return;
        try
        {
            var folder = PickFolder(Loc.T("Tool.FileVault.ImportFolder"));
            if (folder == null) return;
            await _service.ImportPathsAsync([folder]);
            RefreshUi();
        }
        catch (Exception ex) { ShowErr(ex); }
    }

    private async void ExportFile_Click(object sender, RoutedEventArgs e)
        => await ExportSelectedAsync(requireDir: false);

    private async void ExportFolder_Click(object sender, RoutedEventArgs e)
        => await ExportSelectedAsync(requireDir: true);

    private async Task ExportSelectedAsync(bool requireDir)
    {
        if (_service == null || EntryList.SelectedItem is not EntryRow row) return;
        if (row.IsDir != requireDir) return;
        try
        {
            var folder = PickFolder(Loc.T("Tool.FileVault.Export"));
            if (folder == null) return;
            await _service.ExportSelectedAsync(row.Id, System.IO.Path.Combine(folder, row.Name));
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

    private void NewMenu_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureUnlocked()) return;
        NewMenu.PlacementTarget = NewFolderBtn;
        NewMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        Dispatcher.BeginInvoke(new Action(() => NewMenu.IsOpen = true));
    }

    private void NewMenu_Opened(object sender, RoutedEventArgs e)
    {
        NewMenu.Items.Clear();
        foreach (var entry in VaultNewCatalog.Load())
        {
            var captured = entry;
            var item = new MenuItem { Header = captured.MenuTitle, Tag = captured };
            TrySetShellIcon(item, captured);
            item.Click += async (_, _) =>
            {
                try { await CreateFromMenuAsync(captured); }
                catch (Exception ex) { ShowErr(ex); }
            };
            NewMenu.Items.Add(item);
        }
    }

    private async Task CreateFromMenuAsync(VaultNewEntry entry)
    {
        if (!EnsureUnlocked() || _service == null) return;
        string? target = null;
        if (entry.Kind == VaultNewKind.Shortcut)
        {
            var dlg = new OpenFileDialog { Title = Loc.T("Tool.FileVault.NewShortcutTarget") };
            if (dlg.ShowDialog() != true || string.IsNullOrWhiteSpace(dlg.FileName)) return;
            target = dlg.FileName;
        }
        await _service.CreateNewAsync(entry, target);
        RefreshUi();
    }

    private static void TrySetShellIcon(MenuItem item, VaultNewEntry entry)
    {
        try
        {
            var brush = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(entry.IconColor));
            brush.Freeze();
            item.Icon = FlyScreen.Ui.Theme.Wpf.Icons.WpfIcon.Create(entry.IconCode, brush, 16);
        }
        catch
        {
            // 没有图标时仍显示文字
        }
    }

    private async void BackupBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_service == null) return;
        try
        {
            var dlg = new SaveFileDialog
            {
                Filter = "FlyVault|*.flyvault",
                FileName = "vault.flyvault",
                Title = Loc.T("Tool.FileVault.ExportBackup")
            };
            if (dlg.ShowDialog() != true) return;
            await _service.ExportBackupAsync(dlg.FileName);
            RefreshUi();
        }
        catch (Exception ex) { ShowErr(ex); }
    }

    private async void RestoreBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_service == null) return;
        try
        {
            var open = new OpenFileDialog
            {
                Filter = "FlyVault|*.flyvault",
                Title = Loc.T("Tool.FileVault.ImportBackup")
            };
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
        if (_service == null) return;
        // 命中行优先，避免双击单元格时 SelectedItem 仍为空
        var dep = e.OriginalSource as DependencyObject;
        while (dep != null && dep is not ListViewItem)
            dep = System.Windows.Media.VisualTreeHelper.GetParent(dep);
        if (dep is ListViewItem lvi)
        {
            lvi.IsSelected = true;
            EntryList.SelectedItem = lvi.DataContext;
        }
        if (EntryList.SelectedItem is not EntryRow row) return;
        try
        {
            if (row.IsDir)
                NavigateRecording(() => _service.NavigateTo(row.Id));
            else
            {
                await _service.OpenEntryAsync(row.Id);
                RefreshUi();
            }
        }
        catch (Exception ex) { ShowErr(ex); }
    }

    private static string? PickFolder(string title)
    {
        var dlg = new OpenFolderDialog { Title = title };
        return dlg.ShowDialog() == true ? dlg.FolderName : null;
    }

    private string? Prompt(string title, string? initial = null)
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
        var box = new TextBox { Margin = new Thickness(16), Text = initial ?? "" };
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
        box.SelectAll();
        box.Focus();
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
        public string Name { get; set; } = "";
        public bool IsDir { get; set; }
        public string Icon { get; set; } = "";
        public string TypeText { get; set; } = "";
        public string SizeText { get; set; } = "";
        public string ModifiedText { get; set; } = "";
    }
}
