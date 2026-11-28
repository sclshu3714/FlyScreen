using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
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
        _newFolderBtn.Click += (_, _) => ShowNewMenu();
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

        _list.ItemTemplate = new FuncDataTemplate<EntryRow>((row, _) =>
        {
            if (row == null) return new TextBlock();
            return new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,*,140,90,80"),
                Children =
                {
                    new TextBlock { Text = row.Icon, Width = 24, VerticalAlignment = VerticalAlignment.Center },
                    Col(new TextBlock { Text = row.Name, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center }, 1),
                    Col(new TextBlock { Text = row.ModifiedText, FontSize = 12, Opacity = 0.75, VerticalAlignment = VerticalAlignment.Center }, 2),
                    Col(new TextBlock { Text = row.TypeText, FontSize = 12, Opacity = 0.75, VerticalAlignment = VerticalAlignment.Center }, 3),
                    Col(new TextBlock { Text = row.SizeText, FontSize = 12, Opacity = 0.75, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center }, 4)
                }
            };
        }, true);
        _list.DoubleTapped += async (_, _) => await Safe(OpenSelectedAsync);
        _list.ContextMenu = BuildContextMenu();
        _list.BorderThickness = new Thickness(0);
        _list.Background = Brushes.Transparent;

        _explorerHost = new Border
        {
            Margin = new Thickness(8, 0),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Child = _list,
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
                        Children = { _newFolderBtn, _addFileBtn, _addFolderBtn }
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
            e.ModifiedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm"))));
        ApplySearchFilter();
    }

    private void ApplySearchFilter()
    {
        var q = (_searchBox.Text ?? "").Trim();
        var filtered = string.IsNullOrEmpty(q)
            ? _allRows
            : _allRows.Where(r => r.Name.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
        _list.ItemsSource = filtered;
        _status.Text = string.Format(Loc.T("Tool.FileVault.ItemsCount"), filtered.Count);
        StatusReporter?.Invoke(_status.Text);
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
        if (_service == null || _list.SelectedItem is not EntryRow row) return;
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
        if (_service == null || VisualRoot is not Window wnd || _list.SelectedItem is not EntryRow row) return;
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
        if (_service == null || _list.SelectedItem is not EntryRow row) return;
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
        if (_service == null || VisualRoot is not Window wnd || _list.SelectedItem is not EntryRow row) return;
        if (row.IsDir != requireDir) return;
        var folder = await PickFolder(wnd, Loc.T("Tool.FileVault.Export"));
        if (folder == null) return;
        await _service.ExportSelectedAsync(row.Id, Path.Combine(folder, row.Name));
        Refresh();
    }

    private async Task DeleteAsync()
    {
        if (_service == null || _list.SelectedItem is not EntryRow row) return;
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

    private sealed record EntryRow(string Id, string Name, bool IsDir, string Icon, string TypeText, string SizeText, string ModifiedText);
}
