using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using FlyScreen.Helpers.Localization;
using FlyScreen.Models;
using FlyScreen.Services.Tools;
using FlyScreen.Tool.FileVault.Store;
using FlyScreen.ToolSdk;
using FlyScreen.Ui.Avalonia;

namespace FlyScreen.Tool.FileVault.Ui.Desktop;

public sealed class FileVaultPanel : UserControl
{
    private readonly IFileVaultService? _service;
    private readonly ListBox _list = new();
    private readonly TextBlock _state = new()
    {
        VerticalAlignment = VerticalAlignment.Center,
        TextTrimming = TextTrimming.CharacterEllipsis,
        MaxWidth = 200,
        Margin = new Thickness(12, 0, 4, 0)
    };
    private readonly TextBlock _status = new() { FontSize = 12 };
    private readonly Button _newFolderBtn = new();
    private readonly Button _addFileBtn = new();
    private readonly Button _addFolderBtn = new();
    private readonly Button _sortBtn = new();
    private readonly Button _viewBtn = new();
    private readonly VaultSortState _sort = new();
    private readonly VaultViewState _view = new();
    private readonly TreeView _navTree = new();
    private readonly Border _navPane = new();
    private readonly Border _sidePane = new();
    private readonly TextBlock _sideIcon = new();
    private readonly TextBlock _sideTitle = new();
    private readonly TextBlock _sideMeta = new();
    private readonly Grid _explorerShell = new();
    private readonly FlyPathBreadcrumbBar _pathBar = new();
    private readonly Border _explorerHost;
    private readonly Border _gateHost;
    private readonly Border _searchHost = new();
    private readonly TextBox _searchBox = new()
    {
        BorderThickness = new Thickness(0),
        Background = Brushes.Transparent,
        FontSize = 13,
        VerticalContentAlignment = VerticalAlignment.Center
    };
    private readonly Button _searchBtn = new();
    private readonly TextBlock _gateTitle = new() { FontSize = 20, FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly TextBlock _gateHint = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center };
    private readonly SecurePasswordBox _gatePassword = new();
    private readonly SecurePasswordBox _gateConfirm = new();
    private readonly Button _gateAction = new();
    private readonly TextBlock _gateError = new() { FontSize = 12, Foreground = Brush.Parse("#EF4444"), TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap };
    private readonly Stack<string?> _backStack = new();
    private readonly Stack<string?> _forwardStack = new();
    private readonly List<EntryRow> _allRows = new();
    private bool _navHistorySuppress;
    private bool _gateNeedsConfirm;
    private bool _forcingLock;
    private bool _entered;
    private Border? _titleTabBar;
    private Border? _titleTab;

    public Action<string>? StatusReporter { get; set; }

    public FileVaultPanel()
    {
        _service = ToolService.Instance.GetToolService<IFileVaultService>();
        _service?.EnsureDefaultRoot();
        AttachedToVisualTree += async (_, _) => await EnterRequirePasswordAsync();
        DetachedFromVisualTree += async (_, _) => await LeaveLockAsync();

        StyleCmdBtn(_newFolderBtn, "＋", Loc.T("Tool.FileVault.New"), Loc.T("Tool.FileVault.NewFolder"));
        StyleCmdBtn(_addFileBtn, "📄", Loc.T("Tool.FileVault.AddFile"), Loc.T("Tool.FileVault.AddFile"));
        StyleCmdBtn(_addFolderBtn, "📁", Loc.T("Tool.FileVault.AddFolder"), Loc.T("Tool.FileVault.AddFolder"));
        StyleCmdBtn(_sortBtn, "⇅", Loc.T("Tool.FileVault.Sort"), Loc.T("Tool.FileVault.Sort"));
        StyleCmdBtn(_viewBtn, "☰", Loc.T("Tool.FileVault.View"), Loc.T("Tool.FileVault.View"));
        _newFolderBtn.Click += (_, _) => ShowNewMenu();
        _sortBtn.Click += (_, _) => ShowSortMenu();
        _viewBtn.Click += (_, _) => ShowViewMenu();
        _addFileBtn.Click += async (_, _) => await Safe(ImportFileAsync);
        _addFolderBtn.Click += async (_, _) => await Safe(ImportFolderAsync);

        _pathBar.RootGlyph = "🔐";
        _pathBar.Margin = new Thickness(8, 8, 8, 6);
        _pathBar.SetNavTips(
            Loc.T("Tool.FileVault.Back"),
            Loc.T("Tool.FileVault.Forward"),
            Loc.T("Tool.FileVault.Up"),
            Loc.T("Tool.FileVault.Refresh"));
        _pathBar.SegmentClicked += seg => NavigateRecording(() => _service?.NavigateTo(seg.Id));
        _pathBar.PathCommitted += path => NavigateRecording(() => _service?.TryNavigatePath(path));
        _pathBar.BackClicked += () => GoHistory(_backStack, _forwardStack);
        _pathBar.ForwardClicked += () => GoHistory(_forwardStack, _backStack);
        _pathBar.UpClicked += () => NavigateRecording(() => _service?.NavigateUp());
        _pathBar.RefreshClicked += Refresh;
        _pathBar.SiblingProvider = parentId =>
        {
            if (_service == null) return Array.Empty<PathBreadcrumbItem>();
            return _service.ListFolders(parentId)
                .Select(f => new PathBreadcrumbItem { Id = f.Id, Title = f.Name, ParentId = f.ParentId })
                .ToList();
        };

        _searchBox.Watermark = Loc.T("Tool.FileVault.SearchIn");
        _searchBtn.Content = "🔍";
        _searchBtn.Width = 32;
        _searchBtn.Height = 28;
        _searchBtn.Padding = new Thickness(0);
        _searchBtn.Background = Brushes.Transparent;
        _searchBtn.BorderThickness = new Thickness(0);
        _searchBtn.Click += (_, _) =>
        {
            _searchBox.Focus();
            ApplySearchFilter();
        };
        _searchBox.TextChanged += (_, _) => ApplySearchFilter();
        _searchHost.Height = 32;
        _searchHost.CornerRadius = new CornerRadius(6);
        _searchHost.BorderThickness = new Thickness(1);
        _searchHost.Padding = new Thickness(4, 0, 2, 0);
        _searchHost.Child = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Children = { _searchBox, Col(_searchBtn, 1) }
        };

        ApplyListTemplate();
        _list.DoubleTapped += async (_, _) => await Safe(OpenSelectedAsync);
        _list.SelectionChanged += (_, _) => UpdateSidePane();
        _list.ContextMenu = BuildContextMenu();
        _list.BorderThickness = new Thickness(0);
        _list.Background = Brushes.Transparent;

        _navPane.Width = 180;
        _navPane.IsVisible = false;
        _navPane.BorderThickness = new Thickness(0, 0, 1, 0);
        _navTree.BorderThickness = new Thickness(0);
        _navTree.Background = Brushes.Transparent;
        _navTree.SelectionChanged += (_, _) =>
        {
            if (_navTree.SelectedItem is TreeViewItem { Tag: var tag })
                NavigateRecording(() => _service?.NavigateTo(tag as string));
        };
        _navPane.Child = _navTree;
        _sidePane.Width = 220;
        _sidePane.IsVisible = false;
        _sidePane.BorderThickness = new Thickness(1, 0, 0, 0);
        _sidePane.Padding = new Thickness(12);
        _sideIcon.FontSize = 48;
        _sideIcon.HorizontalAlignment = HorizontalAlignment.Center;
        _sideTitle.FontWeight = FontWeight.SemiBold;
        _sideTitle.TextWrapping = TextWrapping.Wrap;
        _sideMeta.FontSize = 12;
        _sideMeta.Opacity = 0.75;
        _sideMeta.TextWrapping = TextWrapping.Wrap;
        _sidePane.Child = new StackPanel
        {
            Spacing = 8,
            Children = { _sideIcon, _sideTitle, _sideMeta }
        };
        _explorerShell.ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto");
        _explorerShell.Children.Add(_navPane);
        Grid.SetColumn(_list, 1);
        _explorerShell.Children.Add(_list);
        Grid.SetColumn(_sidePane, 2);
        _explorerShell.Children.Add(_sidePane);

        _explorerHost = new Border
        {
            Margin = new Thickness(8, 0),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Child = _explorerShell,
            ClipToBounds = true
        };
        DragDrop.SetAllowDrop(_explorerHost, true);
        DragDrop.SetAllowDrop(_list, true);
        _explorerHost.AddHandler(DragDrop.DragOverEvent, OnFileDragOver);
        _explorerHost.AddHandler(DragDrop.DropEvent, OnFileDrop);

        _gateAction.Click += async (_, _) =>
        {
            try { await GateActionAsync(); }
            catch (Exception ex) { _gateError.Text = ex.Message; }
        };
        StylePrimary(_gateAction);
        _gatePassword.Placeholder = Loc.T("Tool.FileVault.PasswordPlaceholder");
        _gateConfirm.Placeholder = Loc.T("Tool.FileVault.ConfirmPlaceholder");
        _gatePassword.ShowPasswordTip = Loc.T("Sec.ShowPassword");
        _gatePassword.HidePasswordTip = Loc.T("Sec.HidePassword");
        _gateConfirm.ShowPasswordTip = Loc.T("Sec.ShowPassword");
        _gateConfirm.HidePasswordTip = Loc.T("Sec.HidePassword");
        _gateHost = new Border
        {
            Margin = new Thickness(8, 0),
            BorderThickness = new Thickness(0),
            IsVisible = false,
            Child = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Width = 360,
                Margin = new Thickness(24),
                Spacing = 10,
                Children = { _gateTitle, _gateHint, _gatePassword, _gateConfirm, _gateAction, _gateError }
            }
        };

        var tabClose = new Button
        {
            Content = "✕",
            Width = 22,
            Height = 22,
            Padding = new Thickness(0),
            FontSize = 10,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0)
        };
        tabClose.Click += (_, _) =>
        {
            try { _ = ToolService.Instance.Ui.RequestAsync(ToolIds.FileVault, Module.UiCloseWindow, null); }
            catch { /* ignore */ }
        };

        var tab = new Border
        {
            MinWidth = 140,
            Height = 32,
            Padding = new Thickness(10, 0, 4, 0),
            CornerRadius = new CornerRadius(8, 8, 0, 0),
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Bottom,
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    new TextBlock { Text = "🔐", FontSize = 13, VerticalAlignment = VerticalAlignment.Center },
                    new TextBlock
                    {
                        Text = Loc.T("Tool.FileVault.Name"),
                        FontSize = 12,
                        VerticalAlignment = VerticalAlignment.Center
                    },
                    tabClose
                }
            }
        };

        var maxGlyph = new TextBlock
        {
            Text = "☐",
            FontSize = 11,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        Button ChromeBtn(string tip, object content, EventHandler<Avalonia.Interactivity.RoutedEventArgs> click)
        {
            var b = new Button
            {
                Width = 46,
                Height = 40,
                Padding = new Thickness(0),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Content = content,
                FontSize = 12
            };
            ToolTip.SetTip(b, tip);
            b.Click += click;
            return b;
        }

        void CloseVault()
        {
            try { _ = ToolService.Instance.Ui.RequestAsync(ToolIds.FileVault, Module.UiCloseWindow, null); }
            catch { /* ignore */ }
        }

        Window? Host() => TopLevel.GetTopLevel(this) as Window;

        var minBtn = ChromeBtn("最小化", "─", (_, _) =>
        {
            var w = Host();
            if (w != null) w.WindowState = WindowState.Minimized;
        });
        var maxBtn = ChromeBtn("最大化", maxGlyph, (_, _) =>
        {
            var w = Host();
            if (w == null) return;
            w.WindowState = w.WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
            maxGlyph.Text = w.WindowState == WindowState.Maximized ? "❐" : "☐";
        });
        var closeBtn = ChromeBtn("关闭", "✕", (_, _) => CloseVault());

        var drag = new Border { Background = Brush.Parse("#01000000") };
        drag.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(drag).Properties.IsLeftButtonPressed) return;
            try { Host()?.BeginMoveDrag(e); } catch { /* ignore */ }
        };

        var tabBar = new Border
        {
            Height = 40,
            BorderThickness = new Thickness(0),
            Child = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
                Children =
                {
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 2,
                        Margin = new Thickness(8, 0, 0, 0),
                        VerticalAlignment = VerticalAlignment.Bottom,
                        Children =
                        {
                            tab,
                            new TextBlock
                            {
                                Text = "＋",
                                Opacity = 0.4,
                                Margin = new Thickness(8, 0, 0, 4),
                                VerticalAlignment = VerticalAlignment.Bottom,
                                FontSize = 14
                            }
                        }
                    },
                    Col(drag, 1),
                    Col(new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Children = { minBtn, maxBtn, closeBtn }
                    }, 2)
                }
            }
        };
        _titleTabBar = tabBar;
        _titleTab = tab;

        var navRow = new Grid
        {
            Margin = new Thickness(8, 4, 8, 4),
            ColumnDefinitions = new ColumnDefinitions("*,220"),
            Children = { _pathBar, Col(_searchHost, 1) }
        };

        // 对齐资源管理器：新建 + 图标按钮
        _newFolderBtn.Content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Children =
            {
                new TextBlock { Text = "＋", FontWeight = FontWeight.Bold, Foreground = Brush.Parse("#2563EB") },
                new TextBlock { Text = Loc.T("Tool.FileVault.New") },
                new TextBlock { Text = "▾", FontSize = 10, Opacity = 0.7 }
            }
        };
        _addFileBtn.Content = "📄";
        _addFileBtn.Width = 36;
        _addFolderBtn.Content = "📁";
        _addFolderBtn.Width = 36;

        var cmdBar = new Border
        {
            Padding = new Thickness(4, 2, 8, 2),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = new DockPanel
            {
                LastChildFill = false,
                Children =
                {
                    _state,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 2,
                        Children = { _newFolderBtn, _addFileBtn, _addFolderBtn, _sortBtn, _viewBtn }
                    }
                }
            }
        };
        DockPanel.SetDock(_state, Dock.Right);

        var statusBar = new Border
        {
            Padding = new Thickness(12, 6),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Child = _status
        };

        var body = new Panel { Children = { _explorerHost, _gateHost } };
        var top = new StackPanel { Children = { tabBar, navRow, cmdBar } };
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(statusBar, Dock.Bottom);
        Content = new DockPanel
        {
            LastChildFill = true,
            Children = { top, statusBar, body }
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
        ApplyTheme();
        Refresh();
    }

    public void ApplyTheme()
    {
        try
        {
            var winBg = TryBrush("WinBg") ?? Brush.Parse("#F7F8FA");
            var cardBg = TryBrush("CardBg") ?? Brush.Parse("#FFFFFF");
            var line = TryBrush("Line") ?? Brush.Parse("#E5E7EB");
            var text = TryBrush("Text") ?? Brush.Parse("#111827");
            var muted = TryBrush("Muted") ?? Brush.Parse("#6B7280");
            Background = winBg;
            if (_titleTabBar != null) _titleTabBar.Background = winBg;
            if (_titleTab != null)
            {
                _titleTab.Background = cardBg;
                _titleTab.BorderBrush = line;
            }
            _explorerHost.Background = cardBg;
            _explorerHost.BorderBrush = line;
            _gateHost.Background = cardBg;
            _searchHost.Background = cardBg;
            _searchHost.BorderBrush = line;
            _status.Foreground = muted;
            _gateTitle.Foreground = text;
            _gateHint.Foreground = muted;
            _state.Foreground = muted;
        }
        catch
        {
            // ignore
        }
    }

    private static IBrush? TryBrush(string key)
    {
        if (Avalonia.Application.Current?.TryGetResource(key, Avalonia.Application.Current.ActualThemeVariant, out var res) == true
            && res is IBrush b)
            return b;
        if (Avalonia.Application.Current?.Resources.TryGetResource(key, null, out var r) == true && r is IBrush rb)
            return rb;
        return null;
    }

    private static Control Col(Control c, int col)
    {
        Grid.SetColumn(c, col);
        return c;
    }

    private ContextMenu BuildContextMenu()
    {
        MenuItem Item(string title, Func<Task> action)
        {
            var mi = new MenuItem { Header = title };
            mi.Click += async (_, _) => await Safe(action);
            return mi;
        }
        MenuItem Sub(string title, params MenuItem[] children)
        {
            var mi = new MenuItem { Header = title };
            foreach (var c in children) mi.Items.Add(c);
            return mi;
        }
        return new ContextMenu
        {
            Padding = new Thickness(4),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            BorderBrush = Brush.Parse("#33000000"),
            Background = Brushes.White,
            Items =
            {
                Item(Loc.T("Tool.FileVault.Cut"), () => { StageClipboard(true); return Task.CompletedTask; }),
                Item(Loc.T("Tool.FileVault.Copy"), () => { StageClipboard(false); return Task.CompletedTask; }),
                Item(Loc.T("Tool.FileVault.Rename"), RenameAsync),
                Item(Loc.T("Tool.FileVault.Paste"), PasteAsync),
                new Separator(),
                Item(Loc.T("Tool.FileVault.Open"), OpenSelectedAsync),
                new Separator(),
                Item(Loc.T("Tool.FileVault.Delete"), DeleteAsync),
                new Separator(),
                Sub(Loc.T("Tool.FileVault.Import"),
                    Item(Loc.T("Tool.FileVault.ImportFile"), ImportFileAsync),
                    Item(Loc.T("Tool.FileVault.ImportFolder"), ImportFolderAsync),
                    Item(Loc.T("Tool.FileVault.ImportBackup"), ImportBackupAsync)),
                Sub(Loc.T("Tool.FileVault.Export"),
                    Item(Loc.T("Tool.FileVault.ExportFile"), () => ExportSelectedAsync(false)),
                    Item(Loc.T("Tool.FileVault.ExportFolder"), () => ExportSelectedAsync(true)),
                    Item(Loc.T("Tool.FileVault.ExportBackup"), ExportBackupAsync)),
                new Separator(),
                Item(Loc.T("Tool.FileVault.IdleLock"), EditIdleLockAsync)
            }
        };
    }

    private async Task EnterRequirePasswordAsync()
    {
        if (_service == null || _forcingLock) return;
        _entered = true;
        _forcingLock = true;
        try
        {
            if (_service.IsUnlocked) await _service.LockAsync();
            else Refresh();
        }
        catch { Refresh(); }
        finally { _forcingLock = false; }
    }

    private async Task LeaveLockAsync()
    {
        if (_service == null || !_entered || _forcingLock) return;
        _entered = false;
        _forcingLock = true;
        try
        {
            if (_service.IsUnlocked) await _service.LockAsync();
        }
        catch { /* ignore */ }
        finally { _forcingLock = false; }
    }

    private static void StyleCmdBtn(Button b, string glyph, string label, string tip)
    {
        b.Content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Children =
            {
                new TextBlock { Text = glyph, FontSize = 14, VerticalAlignment = VerticalAlignment.Center },
                new TextBlock { Text = label, FontSize = 12, VerticalAlignment = VerticalAlignment.Center }
            }
        };
        b.Padding = new Thickness(8, 4);
        b.MinHeight = 32;
        b.Background = Brushes.Transparent;
        b.BorderThickness = new Thickness(0);
        b.CornerRadius = new CornerRadius(4);
        ToolTip.SetTip(b, tip);
    }

    private static void StylePrimary(Button b)
    {
        b.Background = Brush.Parse("#2563EB");
        b.Foreground = Brushes.White;
        b.Padding = new Thickness(16, 8);
        b.CornerRadius = new CornerRadius(4);
        b.HorizontalAlignment = HorizontalAlignment.Stretch;
    }

    private async Task Safe(Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { await Alert(ex.Message); }
    }

    private void NavigateRecording(Action? navigate)
    {
        if (_service == null || navigate == null) return;
        if (!_navHistorySuppress)
        {
            _backStack.Push(_service.CurrentFolderId);
            _forwardStack.Clear();
        }
        navigate();
        Refresh();
    }

    private void GoHistory(Stack<string?> from, Stack<string?> to)
    {
        if (_service == null || from.Count == 0) return;
        to.Push(_service.CurrentFolderId);
        _navHistorySuppress = true;
        _service.NavigateTo(from.Pop());
        _navHistorySuppress = false;
        Refresh();
    }

    private void Refresh()
    {
        if (_service == null)
        {
            _status.Text = Loc.T("Tool.FileVault.NotReady");
            ShowGate(Loc.T("Tool.FileVault.NotReady"), Loc.T("Tool.FileVault.NotReady"), false, false);
            return;
        }

        var root = _service.EnsureDefaultRoot();
        var unlocked = _service.IsUnlocked;
        _newFolderBtn.IsEnabled = unlocked;
        _addFileBtn.IsEnabled = unlocked;
        _addFolderBtn.IsEnabled = unlocked;
        _sortBtn.IsEnabled = unlocked;
        _viewBtn.IsEnabled = unlocked;
        _pathBar.IsEnabled = unlocked;
        _searchBox.IsEnabled = unlocked;
        _searchBtn.IsEnabled = unlocked;

        if (!unlocked)
        {
            _allRows.Clear();
            _list.ItemsSource = Array.Empty<EntryRow>();
            _pathBar.SetItems([]);
            _pathBar.SetNavState(false, false, false);
            _status.Text = string.Format(Loc.T("Tool.FileVault.StatusRoot"), root);
            _gatePassword.Password = "";
            _gateConfirm.Password = "";
            _gateError.Text = "";
            if (_service.HasVault)
            {
                ShowGate(Loc.T("Tool.FileVault.Name"), Loc.T("Tool.FileVault.UnlockPrompt"), false, true);
                _gateAction.Content = Loc.T("Tool.FileVault.Unlock");
            }
            else
            {
                ShowGate(Loc.T("Tool.FileVault.Name"), Loc.T("Tool.FileVault.NoVaultPrompt"), true, true);
                _gateAction.Content = Loc.T("Tool.FileVault.Enable");
            }
            return;
        }

        HideGate();
        var items = _service.ListCurrent();
        _pathBar.SetItems(_service.GetPathSegments());
        _pathBar.SetNavState(_backStack.Count > 0, _forwardStack.Count > 0, !string.IsNullOrEmpty(_service.CurrentFolderId));
        _allRows.Clear();
        _allRows.AddRange(items.Select(e => new EntryRow(
            e.Id, e.Name, e.IsDir,
            e.IsDir ? "📁" : "📄",
            e.IsDir ? Loc.T("Tool.FileVault.TypeFolder") : Loc.T("Tool.FileVault.TypeFile"),
            e.IsDir ? "" : FormatSize(e.Size),
            e.ModifiedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
            false,
            e.Size,
            e.ModifiedUtc,
            VaultView.DisplayName(e.Name, e.IsDir, _view.ShowExtensions))));
        RebuildNavTree();
        ApplyViewChrome();
        ApplySearchFilter();
    }

    private void ApplySearchFilter()
    {
        var q = (_searchBox.Text ?? "").Trim();
        var matched = string.IsNullOrEmpty(q)
            ? _allRows
            : _allRows.Where(r => r.Name.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
        var arranged = VaultSort.Arrange(matched, r => new VaultSortFacts(r.Name, r.IsDir, r.SizeBytes, r.ModifiedUtc), _sort);
        var rows = new List<EntryRow>(arranged.Count);
        foreach (var piece in arranged)
        {
            if (piece.IsHeader)
                rows.Add(new EntryRow("", piece.Header, false, "", "", "", "", true, DisplayName: piece.Header));
            else if (piece.Item is EntryRow row)
            {
                rows.Add(row with
                {
                    DisplayName = VaultView.DisplayName(row.Name, row.IsDir, _view.ShowExtensions)
                });
            }
        }
        _list.ItemsSource = rows;
        var shown = rows.Count(r => !r.IsHeader);
        _status.Text = string.Format(Loc.T("Tool.FileVault.ItemsCount"), shown);
        StatusReporter?.Invoke(_status.Text);
        UpdateSidePane();
    }

    private void ShowSortMenu()
    {
        var menu = new ContextMenu();
        foreach (var key in VaultSort.Primary)
            menu.Items.Add(SortKeyItem(key, false));
        var more = new MenuItem { Header = Loc.T("Tool.FileVault.SortMore") };
        foreach (var key in VaultSort.More)
            more.Items.Add(SortKeyItem(key, false));
        menu.Items.Add(more);
        menu.Items.Add(new Separator());
        menu.Items.Add(SortOrderItem(VaultSortOrder.Ascending, Loc.T("Tool.FileVault.SortAsc")));
        menu.Items.Add(SortOrderItem(VaultSortOrder.Descending, Loc.T("Tool.FileVault.SortDesc")));
        menu.Items.Add(new Separator());
        var group = new MenuItem { Header = Loc.T("Tool.FileVault.SortGroup") };
        foreach (var key in VaultSort.GroupKeys)
            group.Items.Add(SortKeyItem(key, true));
        menu.Items.Add(group);
        _sortBtn.ContextMenu = menu;
        menu.Open(_sortBtn);
    }

    private MenuItem SortKeyItem(VaultSortKey key, bool grouping)
    {
        var item = new MenuItem
        {
            Header = VaultSort.Title(key),
            ToggleType = MenuItemToggleType.Radio,
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

    private MenuItem SortOrderItem(VaultSortOrder order, string title)
    {
        var item = new MenuItem
        {
            Header = title,
            ToggleType = MenuItemToggleType.Radio,
            IsChecked = _sort.Order == order
        };
        item.Click += (_, _) =>
        {
            _sort.Order = order;
            ApplySearchFilter();
        };
        return item;
    }

    private void ShowViewMenu()
    {
        var menu = new ContextMenu();
        foreach (var mode in VaultView.Layouts)
        {
            var captured = mode;
            var item = new MenuItem
            {
                Header = VaultView.Title(captured),
                ToggleType = MenuItemToggleType.Radio,
                IsChecked = _view.Layout == captured,
                Icon = ViewMenuIcon(VaultView.IconCode(captured))
            };
            item.Click += (_, _) =>
            {
                _view.Layout = captured;
                ApplyListTemplate();
                ApplySearchFilter();
            };
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        menu.Items.Add(SidePaneItem(VaultSidePane.Details, Loc.T("Tool.FileVault.ViewDetailsPane"), VaultView.DetailsPaneIcon));
        menu.Items.Add(SidePaneItem(VaultSidePane.Preview, Loc.T("Tool.FileVault.ViewPreviewPane"), VaultView.PreviewPaneIcon));
        menu.Items.Add(new Separator());
        var show = new MenuItem
        {
            Header = Loc.T("Tool.FileVault.ViewShow"),
            Icon = ViewMenuIcon(VaultView.ShowIcon)
        };
        show.Items.Add(ToggleItem(Loc.T("Tool.FileVault.ViewNavPane"), VaultView.NavPaneIcon, _view.NavigationPane, v =>
        {
            _view.NavigationPane = v;
            ApplyViewChrome();
            RebuildNavTree();
        }));
        show.Items.Add(ToggleItem(Loc.T("Tool.FileVault.ViewCompact"), VaultView.CompactIcon, _view.CompactView, v =>
        {
            _view.CompactView = v;
            ApplyListTemplate();
        }));
        show.Items.Add(ToggleItem(Loc.T("Tool.FileVault.ViewCheckBoxes"), VaultView.CheckBoxesIcon, _view.ItemCheckBoxes, v =>
        {
            _view.ItemCheckBoxes = v;
            ApplyListTemplate();
        }));
        show.Items.Add(ToggleItem(Loc.T("Tool.FileVault.ViewExtensions"), VaultView.ExtensionsIcon, _view.ShowExtensions, v =>
        {
            _view.ShowExtensions = v;
            ApplySearchFilter();
        }));
        show.Items.Add(ToggleItem(Loc.T("Tool.FileVault.ViewHidden"), VaultView.HiddenIcon, _view.ShowHidden, v => _view.ShowHidden = v));
        menu.Items.Add(show);
        _viewBtn.ContextMenu = menu;
        menu.Open(_viewBtn);
    }

    private MenuItem SidePaneItem(VaultSidePane pane, string title, string iconCode)
    {
        var item = new MenuItem
        {
            Header = title,
            ToggleType = MenuItemToggleType.CheckBox,
            IsChecked = _view.SidePane == pane,
            Icon = ViewMenuIcon(iconCode)
        };
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
        var item = new MenuItem
        {
            Header = title,
            ToggleType = MenuItemToggleType.CheckBox,
            IsChecked = on,
            Icon = ViewMenuIcon(iconCode)
        };
        item.Click += (_, _) => set(item.IsChecked);
        return item;
    }

    private static Control ViewMenuIcon(string iconCode) => new Avalonia.Controls.Shapes.Path
    {
        Data = AvaloniaIcon.Parse(iconCode),
        Fill = Brush.Parse("#94A3B8"),
        Width = 16,
        Height = 16,
        Stretch = Stretch.Uniform
    };

    private void ApplyViewChrome()
    {
        _navPane.IsVisible = _view.NavigationPane;
        _sidePane.IsVisible = _view.SidePane != VaultSidePane.None;
        _sidePane.Width = _view.SidePane == VaultSidePane.Preview ? 260 : 220;
        _sideIcon.FontSize = _view.SidePane == VaultSidePane.Preview ? 72 : 48;
    }

    private void RebuildNavTree()
    {
        _navTree.Items.Clear();
        if (_service == null || !_view.NavigationPane)
            return;
        var root = new TreeViewItem
        {
            Header = "🔐 " + Loc.T("Tool.FileVault.BreadcrumbRoot"),
            Tag = (string?)null,
            IsExpanded = true
        };
        AddNavChildren(root, null);
        _navTree.Items.Add(root);
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

    private void UpdateSidePane()
    {
        if (_view.SidePane == VaultSidePane.None)
            return;
        if (_list.SelectedItem is not EntryRow { IsHeader: false } row)
        {
            _sideIcon.Text = "";
            _sideTitle.Text = Loc.T("Tool.FileVault.ViewNoSelection");
            _sideMeta.Text = "";
            return;
        }
        _sideIcon.Text = row.Icon;
        _sideTitle.Text = string.IsNullOrEmpty(row.DisplayName) ? row.Name : row.DisplayName;
        _sideMeta.Text = string.Join('\n', new[]
        {
            Loc.T("Tool.FileVault.ColType") + ": " + row.TypeText,
            string.IsNullOrEmpty(row.SizeText) ? null : Loc.T("Tool.FileVault.ColSize") + ": " + row.SizeText,
            Loc.T("Tool.FileVault.ColModified") + ": " + row.ModifiedText
        }.Where(s => s != null));
    }

    private void ApplyListTemplate()
    {
        var mode = _view.Layout;
        var iconSize = VaultView.IconSize(mode);
        var pad = VaultView.RowPadding(_view);
        var cellW = VaultView.CellWidth(mode);
        _list.ItemsPanel = VaultView.IsIconLayout(mode)
            ? new FuncTemplate<Panel?>(() => new WrapPanel { ItemWidth = cellW })
            : new FuncTemplate<Panel?>(() => new VirtualizingStackPanel());
        // 图标模式必须让 ListBoxItem 不要拉满整行，否则 WrapPanel 永远一列
        _list.Styles.Clear();
        if (VaultView.IsIconLayout(mode))
        {
            _list.Styles.Add(new Style(x => x.OfType<ListBoxItem>())
            {
                Setters =
                {
                    new Setter(Layoutable.HorizontalAlignmentProperty, HorizontalAlignment.Left),
                    new Setter(Layoutable.WidthProperty, cellW),
                    new Setter(Layoutable.MarginProperty, new Thickness(4))
                }
            });
        }
        _list.ItemTemplate = new FuncDataTemplate<EntryRow>((row, _) =>
        {
            if (row == null) return new TextBlock();
            if (row.IsHeader)
            {
                return new TextBlock
                {
                    Text = row.DisplayName.Length > 0 ? row.DisplayName : row.Name,
                    FontWeight = FontWeight.SemiBold,
                    Margin = new Thickness(4, 10, 0, 2)
                };
            }
            var title = string.IsNullOrEmpty(row.DisplayName) ? row.Name : row.DisplayName;
            if (VaultView.IsIconLayout(mode))
            {
                var stack = new StackPanel
                {
                    Width = cellW - 8,
                    Margin = new Thickness(4),
                    Spacing = 4
                };
                if (_view.ItemCheckBoxes)
                    stack.Children.Add(new TextBlock { Text = "☐", FontSize = 11 });
                stack.Children.Add(new TextBlock
                {
                    Text = row.Icon,
                    FontSize = iconSize * 0.55,
                    HorizontalAlignment = HorizontalAlignment.Center
                });
                stack.Children.Add(new TextBlock
                {
                    Text = title,
                    FontSize = 12,
                    TextAlignment = TextAlignment.Center,
                    TextWrapping = TextWrapping.Wrap
                });
                if (mode == VaultLayoutMode.Tiles)
                {
                    stack.Children.Add(new TextBlock
                    {
                        Text = row.TypeText,
                        FontSize = 11,
                        Opacity = 0.75,
                        TextAlignment = TextAlignment.Center
                    });
                }
                return stack;
            }

            if (mode == VaultLayoutMode.List)
            {
                return new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Margin = new Thickness(8, pad, 8, pad),
                    Children =
                    {
                        new TextBlock { Text = _view.ItemCheckBoxes ? "☐" : "", Width = _view.ItemCheckBoxes ? 16 : 0 },
                        new TextBlock { Text = row.Icon, Width = 24 },
                        new TextBlock { Text = title, TextTrimming = TextTrimming.CharacterEllipsis }
                    }
                };
            }

            if (mode == VaultLayoutMode.Content)
            {
                return new StackPanel
                {
                    Margin = new Thickness(8, pad, 8, pad),
                    Spacing = 2,
                    Children =
                    {
                        new StackPanel
                        {
                            Orientation = Orientation.Horizontal,
                            Spacing = 8,
                            Children =
                            {
                                new TextBlock { Text = row.Icon, Width = 28, FontSize = 18 },
                                new TextBlock { Text = title, FontWeight = FontWeight.SemiBold }
                            }
                        },
                        new TextBlock { Text = row.ModifiedText + "  " + row.SizeText, FontSize = 11, Opacity = 0.75, Margin = new Thickness(36, 0, 0, 0) }
                    }
                };
            }

            return new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,*,140,90,80"),
                Margin = new Thickness(4, pad, 4, pad),
                Children =
                {
                    new TextBlock { Text = row.Icon, Width = 24, VerticalAlignment = VerticalAlignment.Center },
                    Col(new TextBlock { Text = title, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center }, 1),
                    Col(new TextBlock { Text = row.ModifiedText, FontSize = 12, Opacity = 0.75, VerticalAlignment = VerticalAlignment.Center }, 2),
                    Col(new TextBlock { Text = row.TypeText, FontSize = 12, Opacity = 0.75, VerticalAlignment = VerticalAlignment.Center }, 3),
                    Col(new TextBlock { Text = row.SizeText, FontSize = 12, Opacity = 0.75, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center }, 4)
                }
            };
        }, true);
    }

    private void ShowGate(string title, string hint, bool needsConfirm, bool enabled)
    {
        _gateNeedsConfirm = needsConfirm;
        _gateHost.IsVisible = true;
        _explorerHost.IsVisible = false;
        _gateTitle.Text = title;
        _gateHint.Text = hint;
        _gateConfirm.IsVisible = needsConfirm;
        _gateAction.IsEnabled = enabled;
    }

    private void HideGate()
    {
        _gateHost.IsVisible = false;
        _explorerHost.IsVisible = true;
    }

    private async Task GateActionAsync()
    {
        if (_service == null) return;
        _gateError.Text = "";
        var pwd = _gatePassword.Password ?? "";
        if (_gateNeedsConfirm)
        {
            if (!VaultPasswordPolicy.TryValidate(pwd, out var errKey))
            {
                _gateError.Text = Loc.T(errKey);
                return;
            }
            if (pwd != (_gateConfirm.Password ?? ""))
            {
                _gateError.Text = Loc.T("Tool.FileVault.PasswordMismatch");
                return;
            }
            await _service.CreateDefaultVaultAsync(pwd);
        }
        else
        {
            if (string.IsNullOrEmpty(pwd))
            {
                _gateError.Text = Loc.T("Tool.FileVault.PasswordEmpty");
                return;
            }
            await _service.UnlockAsync(pwd);
        }
        Refresh();
    }

    private void EnsureUnlocked()
    {
        if (_service?.IsUnlocked == true) return;
        throw new InvalidOperationException(Loc.T("Tool.FileVault.NeedUnlock"));
    }

    private void StageClipboard(bool cut)
    {
        if (_service == null || _list.SelectedItem is not EntryRow row || row.IsHeader) return;
        _service.StageClipboard([row.Id], cut);
    }

    private async Task PasteAsync()
    {
        EnsureUnlocked();
        if (_service == null) return;
        await _service.PasteAsync();
        Refresh();
    }

    private async Task RenameAsync()
    {
        EnsureUnlocked();
        if (_service == null || VisualRoot is not Window wnd || _list.SelectedItem is not EntryRow row || row.IsHeader) return;
        var name = await FileVaultDialogs.PromptText(wnd, Loc.T("Tool.FileVault.Rename"), row.Name);
        if (string.IsNullOrWhiteSpace(name)) return;
        await _service.RenameAsync(row.Id, name.Trim());
        Refresh();
    }

    private void OnFileDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.Data.Contains(DataFormats.Files) ? DragDropEffects.Copy : DragDropEffects.None;
    }

    private async void OnFileDrop(object? sender, DragEventArgs e)
    {
        var paths = e.Data.GetFiles()?.Select(f => f.Path.LocalPath).Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        if (paths == null || paths.Count == 0) return;
        await Safe(async () =>
        {
            EnsureUnlocked();
            if (_service == null) return;
            await _service.ImportPathsAsync(paths, removeSource: false);
            Refresh();
        });
    }

    private async Task OpenSelectedAsync()
    {
        if (_service == null || _list.SelectedItem is not EntryRow row || row.IsHeader) return;
        if (row.IsDir) NavigateRecording(() => _service.NavigateTo(row.Id));
        else { await _service.OpenEntryAsync(row.Id); Refresh(); }
    }

    private async Task EditIdleLockAsync()
    {
        if (_service == null || VisualRoot is not Window wnd) return;
        var text = await FileVaultDialogs.PromptText(wnd, Loc.T("Tool.FileVault.IdleLock"));
        if (text == null) return;
        if (!int.TryParse(text.Trim(), out var minutes)) minutes = 10;
        _service.UpdateSettings(s => s.IdleLockMinutes = Math.Clamp(minutes, 0, 24 * 60));
    }

    private async Task ImportFileAsync()
    {
        EnsureUnlocked();
        if (_service == null || VisualRoot is not Window wnd) return;
        var files = await wnd.StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
        {
            AllowMultiple = true,
            Title = Loc.T("Tool.FileVault.ImportFile")
        });
        if (files.Count == 0) return;
        await _service.ImportPathsAsync(files.Select(f => f.Path.LocalPath));
        Refresh();
    }

    private async Task ImportFolderAsync()
    {
        EnsureUnlocked();
        if (_service == null || VisualRoot is not Window wnd) return;
        var folder = await PickFolder(wnd, Loc.T("Tool.FileVault.ImportFolder"));
        if (folder == null) return;
        await _service.ImportPathsAsync([folder]);
        Refresh();
    }

    private async Task ExportSelectedAsync(bool requireDir)
    {
        if (_service == null || VisualRoot is not Window wnd || _list.SelectedItem is not EntryRow row || row.IsHeader) return;
        if (row.IsDir != requireDir) return;
        var folder = await PickFolder(wnd, Loc.T("Tool.FileVault.Export"));
        if (folder == null) return;
        await _service.ExportSelectedAsync(row.Id, Path.Combine(folder, row.Name));
        Refresh();
    }

    private async Task DeleteAsync()
    {
        if (_service == null || _list.SelectedItem is not EntryRow row || row.IsHeader) return;
        await _service.DeleteAsync(row.Id);
        Refresh();
    }

    private void ShowNewMenu()
    {
        var flyout = new MenuFlyout();
        foreach (var entry in VaultNewCatalog.Load())
        {
            var captured = entry;
            var item = new MenuItem
            {
                Header = captured.MenuTitle,
                Icon = new Avalonia.Controls.Shapes.Path
                {
                    Data = AvaloniaIcon.Parse(captured.IconCode),
                    Fill = SolidColorBrush.Parse(captured.IconColor),
                    Width = 16,
                    Height = 16,
                    Stretch = Stretch.Uniform
                }
            };
            item.Click += async (_, _) => await Safe(() => CreateNewAsync(captured));
            flyout.Items.Add(item);
        }
        flyout.ShowAt(_newFolderBtn);
    }

    private async Task CreateNewAsync(VaultNewEntry entry)
    {
        EnsureUnlocked();
        if (_service == null || VisualRoot is not Window wnd) return;
        string? target = null;
        if (entry.Kind == VaultNewKind.Shortcut)
        {
            var files = await wnd.StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
            {
                AllowMultiple = false,
                Title = Loc.T("Tool.FileVault.NewShortcutTarget")
            });
            if (files.Count == 0) return;
            target = files[0].Path.LocalPath;
        }
        await _service.CreateNewAsync(entry, target);
        Refresh();
    }

    private async Task NewFolderAsync()
    {
        EnsureUnlocked();
        if (_service == null || VisualRoot is not Window wnd) return;
        var name = await FileVaultDialogs.PromptText(wnd, Loc.T("Tool.FileVault.NewFolder"));
        if (string.IsNullOrWhiteSpace(name)) return;
        await _service.NewFolderAsync(name);
        Refresh();
    }

    private async Task ExportBackupAsync()
    {
        if (_service == null || VisualRoot is not Window wnd) return;
        var file = await wnd.StorageProvider.SaveFilePickerAsync(new Avalonia.Platform.Storage.FilePickerSaveOptions
        {
            SuggestedFileName = "vault.flyvault",
            DefaultExtension = "flyvault",
            Title = Loc.T("Tool.FileVault.ExportBackup")
        });
        if (file == null) return;
        await _service.ExportBackupAsync(file.Path.LocalPath);
        Refresh();
    }

    private async Task ImportBackupAsync()
    {
        if (_service == null || VisualRoot is not Window wnd) return;
        var files = await wnd.StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
        {
            AllowMultiple = false,
            Title = Loc.T("Tool.FileVault.ImportBackup")
        });
        if (files.Count == 0) return;
        await _service.ImportBackupAsync(files[0].Path.LocalPath, _service.EnsureDefaultRoot());
        Refresh();
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
            Content = new TextBlock { Text = message, Margin = new Thickness(16), TextWrapping = TextWrapping.Wrap }
        };
        await win.ShowDialog(wnd);
    }

    private static string FormatSize(long size)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double v = size;
        var i = 0;
        while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
        return $"{v:0.##} {units[i]}";
    }

    private sealed record EntryRow(
        string Id, string Name, bool IsDir, string Icon, string TypeText, string SizeText, string ModifiedText,
        bool IsHeader = false, long SizeBytes = 0, DateTimeOffset ModifiedUtc = default, string DisplayName = "");
}
