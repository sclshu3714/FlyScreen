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
    private readonly VaultSortState _sort = new();
    private readonly VaultViewState _view = new();
    private GridView? _detailsView;
    private Style? _defaultListItemStyle;
    private ExplorerFileDropTarget? _explorerDrop;
    private HwndSource? _dropSource;
    private HwndSourceHook? _dropHook;

    public Action<string>? StatusReporter { get; set; }

    public FileVaultPanel()
    {
        InitializeComponent();
        _detailsView = EntryList.View as GridView;
        _defaultListItemStyle = EntryList.ItemContainerStyle;
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
                ModifiedText = e.ModifiedUtc.ToLocalTime().ToString("yyyy/MM/dd HH:mm"),
                SizeBytes = e.Size,
                ModifiedUtc = e.ModifiedUtc,
                DisplayName = VaultView.DisplayName(e.Name, e.IsDir, _view.ShowExtensions)
            });
        }
        RebuildNavTree();
        ApplyViewChrome();
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
        var matched = _allRows
            .Where(r => q.Length == 0 || r.Name.Contains(q, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var arranged = VaultSort.Arrange(matched, r => new VaultSortFacts(r.Name, r.IsDir, r.SizeBytes, r.ModifiedUtc), _sort);
        _rows.Clear();
        foreach (var piece in arranged)
        {
            if (piece.IsHeader)
            {
                _rows.Add(new EntryRow { Name = piece.Header, DisplayName = piece.Header, IsHeader = true });
            }
            else if (piece.Item is EntryRow row)
            {
                row.DisplayName = VaultView.DisplayName(row.Name, row.IsDir, _view.ShowExtensions);
                _rows.Add(row);
            }
        }
        UpdateStatusBar();
        UpdateSidePane();
    }

    private void SortBtn_Click(object sender, RoutedEventArgs e)
    {
        if (!SortBtn.IsEnabled) return;
        Dispatcher.BeginInvoke(new Action(() => SortMenu.IsOpen = true));
    }

    private void SortMenu_Opened(object sender, RoutedEventArgs e)
    {
        SortMenu.Items.Clear();
        foreach (var key in VaultSort.Primary)
            SortMenu.Items.Add(KeyItem(key, false));
        var more = new MenuItem { Header = Loc.T("Tool.FileVault.SortMore") };
        foreach (var key in VaultSort.More)
            more.Items.Add(KeyItem(key, false));
        SortMenu.Items.Add(more);
        SortMenu.Items.Add(new Separator());
        SortMenu.Items.Add(OrderItem(VaultSortOrder.Ascending, Loc.T("Tool.FileVault.SortAsc")));
        SortMenu.Items.Add(OrderItem(VaultSortOrder.Descending, Loc.T("Tool.FileVault.SortDesc")));
        SortMenu.Items.Add(new Separator());
        var group = new MenuItem { Header = Loc.T("Tool.FileVault.SortGroup") };
        foreach (var key in VaultSort.GroupKeys)
            group.Items.Add(KeyItem(key, true));
        SortMenu.Items.Add(group);
    }

    private MenuItem KeyItem(VaultSortKey key, bool grouping)
    {
        var item = new MenuItem
        {
            Header = VaultSort.Title(key),
            IsCheckable = true,
            IsChecked = grouping ? _sort.Group == key : _sort.Key == key
        };
        item.Click += (_, _) =>
        {
            if (grouping)
                _sort.Group = _sort.Group == key ? null : key;
            else
                _sort.Key = key;
            ApplySearchFilter();
        };
        return item;
    }

    private MenuItem OrderItem(VaultSortOrder order, string title)
    {
        var item = new MenuItem
        {
            Header = title,
            IsCheckable = true,
            IsChecked = _sort.Order == order
        };
        item.Click += (_, _) =>
        {
            _sort.Order = order;
            ApplySearchFilter();
        };
        return item;
    }

    private void ViewBtn_Click(object sender, RoutedEventArgs e)
    {
        if (!ViewBtn.IsEnabled) return;
        Dispatcher.BeginInvoke(new Action(() => ViewMenu.IsOpen = true));
    }

    private void ViewMenu_Opened(object sender, RoutedEventArgs e)
    {
        ViewMenu.Items.Clear();
        foreach (var mode in VaultView.Layouts)
        {
            var captured = mode;
            var item = new MenuItem
            {
                Header = VaultView.Title(captured),
                IsCheckable = true,
                IsChecked = _view.Layout == captured
            };
            TrySetViewIcon(item, VaultView.IconCode(captured));
            item.Click += (_, _) =>
            {
                _view.Layout = captured;
                ApplyListLayout();
                ApplySearchFilter();
            };
            ViewMenu.Items.Add(item);
        }
        ViewMenu.Items.Add(new Separator());
        ViewMenu.Items.Add(SidePaneItem(VaultSidePane.Details, Loc.T("Tool.FileVault.ViewDetailsPane"), VaultView.DetailsPaneIcon));
        ViewMenu.Items.Add(SidePaneItem(VaultSidePane.Preview, Loc.T("Tool.FileVault.ViewPreviewPane"), VaultView.PreviewPaneIcon));
        ViewMenu.Items.Add(new Separator());
        var show = new MenuItem { Header = Loc.T("Tool.FileVault.ViewShow") };
        TrySetViewIcon(show, VaultView.ShowIcon);
        show.Items.Add(ToggleItem(Loc.T("Tool.FileVault.ViewNavPane"), VaultView.NavPaneIcon, _view.NavigationPane, v =>
        {
            _view.NavigationPane = v;
            ApplyViewChrome();
            RebuildNavTree();
        }));
        show.Items.Add(ToggleItem(Loc.T("Tool.FileVault.ViewCompact"), VaultView.CompactIcon, _view.CompactView, v =>
        {
            _view.CompactView = v;
            ApplyListLayout();
        }));
        show.Items.Add(ToggleItem(Loc.T("Tool.FileVault.ViewCheckBoxes"), VaultView.CheckBoxesIcon, _view.ItemCheckBoxes, v =>
        {
            _view.ItemCheckBoxes = v;
            ApplyListLayout();
        }));
        show.Items.Add(ToggleItem(Loc.T("Tool.FileVault.ViewExtensions"), VaultView.ExtensionsIcon, _view.ShowExtensions, v =>
        {
            _view.ShowExtensions = v;
            ApplySearchFilter();
        }));
        show.Items.Add(ToggleItem(Loc.T("Tool.FileVault.ViewHidden"), VaultView.HiddenIcon, _view.ShowHidden, v => _view.ShowHidden = v));
        ViewMenu.Items.Add(show);
    }

    private MenuItem SidePaneItem(VaultSidePane pane, string title, string iconCode)
    {
        var item = new MenuItem
        {
            Header = title,
            IsCheckable = true,
            IsChecked = _view.SidePane == pane
        };
        TrySetViewIcon(item, iconCode);
        item.Click += (_, _) =>
        {
            VaultView.ToggleSidePane(_view, pane);
            ApplyViewChrome();
            UpdateSidePane();
        };
        return item;
    }

    private MenuItem ToggleItem(string title, string iconCode, bool on, Action<bool> set)
    {
        var item = new MenuItem { Header = title, IsCheckable = true, IsChecked = on };
        TrySetViewIcon(item, iconCode);
        item.Click += (_, _) => set(item.IsChecked);
        return item;
    }

    private void TrySetViewIcon(MenuItem item, string iconCode)
    {
        try
        {
            var brush = (TryFindResource("Muted") as System.Windows.Media.Brush)
                        ?? System.Windows.Media.Brushes.Gray;
            item.Icon = FlyScreen.Ui.Theme.Wpf.Icons.WpfIcon.Create(iconCode, brush, 16);
        }
        catch
        {
            // 没有图标时仍显示文字
        }
    }

    private void ApplyViewChrome()
    {
        NavTree.Visibility = _view.NavigationPane ? Visibility.Visible : Visibility.Collapsed;
        SidePane.Visibility = _view.SidePane == VaultSidePane.None ? Visibility.Collapsed : Visibility.Visible;
        SidePane.Width = _view.SidePane == VaultSidePane.Preview ? 260 : 220;
        SideIcon.FontSize = _view.SidePane == VaultSidePane.Preview ? 72 : 48;
    }

    private void RebuildNavTree()
    {
        NavTree.Items.Clear();
        if (_service == null || !_view.NavigationPane)
            return;
        var root = new TreeViewItem
        {
            Header = "🔐 " + Loc.T("Tool.FileVault.BreadcrumbRoot"),
            Tag = (string?)null,
            IsExpanded = true
        };
        AddNavChildren(root, null);
        NavTree.Items.Add(root);
    }

    private void AddNavChildren(TreeViewItem parent, string? parentId)
    {
        if (_service == null) return;
        foreach (var folder in _service.ListFolders(parentId))
        {
            var node = new TreeViewItem
            {
                Header = "📁 " + folder.Name,
                Tag = folder.Id,
                IsExpanded = true
            };
            AddNavChildren(node, folder.Id);
            parent.Items.Add(node);
        }
    }

    private void NavTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (NavTree.SelectedItem is not TreeViewItem { Tag: var tag })
            return;
        NavigateRecording(() => _service?.NavigateTo(tag as string));
    }

    private void UpdateSidePane()
    {
        if (_view.SidePane == VaultSidePane.None)
            return;
        if (EntryList.SelectedItem is not EntryRow { IsHeader: false } row)
        {
            SideIcon.Text = "";
            SideTitle.Text = Loc.T("Tool.FileVault.ViewNoSelection");
            SideMeta.Text = "";
            return;
        }
        SideIcon.Text = row.Icon;
        SideTitle.Text = string.IsNullOrEmpty(row.DisplayName) ? row.Name : row.DisplayName;
        SideMeta.Text = string.Join(Environment.NewLine, new[]
        {
            Loc.T("Tool.FileVault.ColType") + ": " + row.TypeText,
            string.IsNullOrEmpty(row.SizeText) ? null : Loc.T("Tool.FileVault.ColSize") + ": " + row.SizeText,
            Loc.T("Tool.FileVault.ColModified") + ": " + row.ModifiedText
        }.Where(s => s != null));
    }

    private void ApplyListLayout()
    {
        _detailsView ??= EntryList.View as GridView;
        var mode = _view.Layout;
        var iconSize = VaultView.IconSize(mode);
        if (VaultView.IsIconLayout(mode))
        {
            EntryList.View = null;
            var cellW = VaultView.CellWidth(mode);
            var container = new Style(typeof(ListViewItem));
            container.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Left));
            container.Setters.Add(new Setter(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Left));
            container.Setters.Add(new Setter(FrameworkElement.WidthProperty, cellW));
            container.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(4)));
            container.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0)));
            EntryList.ItemContainerStyle = container;
            var panel = new FrameworkElementFactory(typeof(WrapPanel));
            panel.SetValue(WrapPanel.OrientationProperty, Orientation.Horizontal);
            panel.SetValue(WrapPanel.ItemWidthProperty, cellW);
            EntryList.ItemsPanel = new ItemsPanelTemplate { VisualTree = panel };
            EntryList.ItemTemplate = BuildIconTemplate(iconSize, mode == VaultLayoutMode.Tiles);
        }
        else
        {
            EntryList.ItemContainerStyle = _defaultListItemStyle;
            var panel = new FrameworkElementFactory(typeof(VirtualizingStackPanel));
            EntryList.ItemsPanel = new ItemsPanelTemplate { VisualTree = panel };
            EntryList.ItemTemplate = mode is VaultLayoutMode.List or VaultLayoutMode.Content
                ? BuildLineTemplate(mode)
                : null;
            EntryList.View = mode == VaultLayoutMode.Details ? _detailsView : null;
            if (mode == VaultLayoutMode.Details && _detailsView != null)
                EntryList.View = _detailsView;
        }
    }

    private DataTemplate BuildIconTemplate(double iconSize, bool tiles)
    {
        var root = new FrameworkElementFactory(typeof(StackPanel));
        root.SetValue(StackPanel.OrientationProperty, Orientation.Vertical);
        root.SetValue(FrameworkElement.WidthProperty, VaultView.CellWidth(_view.Layout) - 8);
        root.SetValue(FrameworkElement.MarginProperty, new Thickness(4));
        if (_view.ItemCheckBoxes)
        {
            var check = new FrameworkElementFactory(typeof(TextBlock));
            check.SetValue(TextBlock.TextProperty, "☐");
            check.SetValue(TextBlock.FontSizeProperty, 11.0);
            root.AppendChild(check);
        }
        var icon = new FrameworkElementFactory(typeof(TextBlock));
        icon.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(EntryRow.Icon)));
        icon.SetValue(TextBlock.FontSizeProperty, iconSize * 0.55);
        icon.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        root.AppendChild(icon);
        var name = new FrameworkElementFactory(typeof(TextBlock));
        name.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(EntryRow.DisplayName)));
        name.SetValue(TextBlock.TextAlignmentProperty, TextAlignment.Center);
        name.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
        name.SetValue(TextBlock.FontSizeProperty, 12.0);
        root.AppendChild(name);
        if (tiles)
        {
            var type = new FrameworkElementFactory(typeof(TextBlock));
            type.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(EntryRow.TypeText)));
            type.SetValue(TextBlock.TextAlignmentProperty, TextAlignment.Center);
            type.SetValue(TextBlock.FontSizeProperty, 11.0);
            type.SetValue(TextBlock.ForegroundProperty, FindResource("Muted") as System.Windows.Media.Brush);
            root.AppendChild(type);
        }
        return new DataTemplate { VisualTree = root };
    }

    private DataTemplate BuildLineTemplate(VaultLayoutMode mode)
    {
        var root = new FrameworkElementFactory(typeof(DockPanel));
        root.SetValue(FrameworkElement.MarginProperty, new Thickness(8, VaultView.RowPadding(_view), 8, VaultView.RowPadding(_view)));
        if (_view.ItemCheckBoxes)
        {
            var check = new FrameworkElementFactory(typeof(TextBlock));
            check.SetValue(TextBlock.TextProperty, "☐");
            check.SetValue(FrameworkElement.WidthProperty, 16.0);
            check.SetValue(DockPanel.DockProperty, Dock.Left);
            root.AppendChild(check);
        }
        var icon = new FrameworkElementFactory(typeof(TextBlock));
        icon.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(EntryRow.Icon)));
        icon.SetValue(FrameworkElement.WidthProperty, 24.0);
        icon.SetValue(DockPanel.DockProperty, Dock.Left);
        root.AppendChild(icon);
        if (mode == VaultLayoutMode.Content)
        {
            var meta = new FrameworkElementFactory(typeof(TextBlock));
            meta.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(EntryRow.ModifiedText)));
            meta.SetValue(TextBlock.FontSizeProperty, 11.0);
            meta.SetValue(DockPanel.DockProperty, Dock.Bottom);
            root.AppendChild(meta);
        }
        var name = new FrameworkElementFactory(typeof(TextBlock));
        name.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(EntryRow.DisplayName)));
        name.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        root.AppendChild(name);
        return new DataTemplate { VisualTree = root };
    }

    private void EntryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateStatusBar();
        UpdateSidePane();
    }

    private void UpdateStatusBar()
    {
        var sel = EntryList.SelectedItems.Count;
        var text = string.Format(Loc.T("Tool.FileVault.ItemsCount"), _rows.Count(r => !r.IsHeader));
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
        if (_service == null || EntryList.SelectedItem is not EntryRow row || row.IsHeader || !EnsureUnlocked()) return;
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
        if (_service == null || EntryList.SelectedItem is not EntryRow row || row.IsHeader) return;
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
        if (_service == null || EntryList.SelectedItem is not EntryRow row || row.IsHeader) return;
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
        if (_service == null || EntryList.SelectedItem is not EntryRow row || row.IsHeader) return;
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
        if (EntryList.SelectedItem is not EntryRow row || row.IsHeader) return;
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
        public bool IsHeader { get; set; }
        public long SizeBytes { get; set; }
        public DateTimeOffset ModifiedUtc { get; set; }
        public string DisplayName { get; set; } = "";
    }
}
