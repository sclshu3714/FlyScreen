using System.Collections.ObjectModel;
using FlyScreen.Helpers.Localization;
using FlyScreen.Models;
using FlyScreen.Services.Tools;
using FlyScreen.Tool.FileVault.Store;
using FlyScreen.Ui.Maui;
using FlyScreen.Ui.Theme;

namespace FlyScreen.Tool.FileVault.Ui.Maui;

public sealed class FileVaultPanel : ContentView
{
    private readonly IFileVaultService? _service;
    private readonly ObservableCollection<EntryRow> _rows = new();
    private readonly Label _status = new() { FontSize = 12 };
    private Border _newFolderBtn = new();
    private Border _addFileBtn = new();
    private Border _addFolderBtn = new();
    private Border _deleteBtn = new();
    private readonly FlyPathBreadcrumbBar _pathBar = new();
    private readonly CollectionView _list;
    private readonly Border _explorerHost;
    private readonly Border _gateHost;
    private readonly Grid _root = new();
    private Border? _ctxMenu;
    private BoxView? _ctxScrim;
    private View? _titleDrag;
    private View? _captionTabs;
    private bool _captionMounted;
    private readonly VaultWindowDrop _fileDrop = new();

    /// <summary>标题栏拖拽区（Maui-Win SetTitleBar）。嵌入系统标题栏时为空，由系统拖窗。</summary>
    public View? TitleDragRegion => _titleDrag;
    private readonly Label _gateTitle = new() { FontSize = 20, FontAttributes = FontAttributes.Bold, HorizontalTextAlignment = TextAlignment.Center };
    private readonly Label _gateHint = new() { FontSize = 13, HorizontalTextAlignment = TextAlignment.Center };
    private readonly SecurePasswordBox _gatePassword = new() { HeightRequest = 40 };
    private readonly SecurePasswordBox _gateConfirm = new() { HeightRequest = 40 };
    private readonly Button _gateAction = new();
    private readonly Label _gateError = new() { FontSize = 12, TextColor = Color.FromArgb("#EF4444"), HorizontalTextAlignment = TextAlignment.Center };
    private readonly Stack<string?> _backStack = new();
    private readonly Stack<string?> _forwardStack = new();
    private bool _navHistorySuppress;
    private bool _gateNeedsConfirm;
    private bool _forcingLock;
    private bool _entered;
    private readonly Entry _searchBox = new()
    {
        FontSize = 13,
        BackgroundColor = Colors.Transparent,
        VerticalOptions = LayoutOptions.Center
    };
    private readonly Border _searchHost = new();
    private readonly List<EntryRow> _allRows = new();
    private Action? _sessionChangedHandler;
    private Action<VaultProgress>? _progressChangedHandler;
    private bool _unloaded;

    public Action<string>? StatusReporter { get; set; }

    public FileVaultPanel()
    {
        VerticalOptions = LayoutOptions.Fill;
        HorizontalOptions = LayoutOptions.Fill;
        _service = ToolService.Instance.GetToolService<IFileVaultService>();
        _service?.EnsureDefaultRoot();
        Loaded += async (_, _) =>
        {
            ApplyTheme();
            AttachFileDrop();
            await EnterRequirePasswordAsync();
        };
        Unloaded += OnUnloaded;

        _pathBar.RootGlyph = "🔐";
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
        _searchBox.Placeholder = Loc.T("Tool.FileVault.SearchIn");
        _searchBox.TextChanged += (_, _) => ApplySearchFilter();
        var searchBtn = new Button
        {
            Text = "🔍",
            FontSize = 12,
            WidthRequest = 32,
            HeightRequest = 28,
            Padding = 0,
            Margin = new Thickness(0, 0, 2, 0),
            BackgroundColor = Colors.Transparent,
            BorderWidth = 0,
            TextColor = TryMuted(),
            VerticalOptions = LayoutOptions.Center
        };
        ToolTipProperties.SetText(searchBtn, Loc.T("Tool.FileVault.SearchIn"));
        searchBtn.Clicked += (_, _) =>
        {
            _searchBox.Focus();
            ApplySearchFilter();
        };
        _searchHost.HeightRequest = 32;
        _searchHost.StrokeThickness = 1;
        _searchHost.StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 6 };
        _searchHost.Padding = new Thickness(4, 0, 2, 0);
        _searchHost.Content = new Grid
        {
            ColumnDefinitions = new ColumnDefinitionCollection
            {
                new(GridLength.Star),
                new(GridLength.Auto)
            },
            Children =
            {
                _searchBox,
                PlaceCol(searchBtn, 1)
            }
        };

        _list = new CollectionView
        {
            ItemsSource = _rows,
            SelectionMode = SelectionMode.Single,
            VerticalOptions = LayoutOptions.Fill,
            BackgroundColor = Colors.Transparent,
            EmptyView = new Label
            {
                Text = Loc.T("Tool.FileVault.Empty"),
                Margin = new Thickness(12, 24),
                HorizontalOptions = LayoutOptions.Center,
                TextColor = TryMuted()
            },
            ItemTemplate = new DataTemplate(() =>
            {
                var icon = new Label { FontSize = 15, WidthRequest = 24, VerticalOptions = LayoutOptions.Center };
                icon.SetBinding(Label.TextProperty, nameof(EntryRow.Icon));
                var name = new Label { FontSize = 13, VerticalOptions = LayoutOptions.Center, LineBreakMode = LineBreakMode.TailTruncation };
                name.SetBinding(Label.TextProperty, nameof(EntryRow.Name));
                var modified = new Label { FontSize = 12, WidthRequest = 120, TextColor = TryMuted(), VerticalOptions = LayoutOptions.Center };
                modified.SetBinding(Label.TextProperty, nameof(EntryRow.ModifiedText));
                var type = new Label { FontSize = 12, WidthRequest = 72, TextColor = TryMuted(), VerticalOptions = LayoutOptions.Center };
                type.SetBinding(Label.TextProperty, nameof(EntryRow.TypeText));
                var size = new Label { FontSize = 12, WidthRequest = 64, TextColor = TryMuted(), HorizontalTextAlignment = TextAlignment.End, VerticalOptions = LayoutOptions.Center };
                size.SetBinding(Label.TextProperty, nameof(EntryRow.SizeText));
                var row = new Grid
                {
                    Padding = new Thickness(10, 8),
                    ColumnDefinitions =
                    {
                        new(GridLength.Auto),
                        new(GridLength.Star),
                        new(GridLength.Auto),
                        new(GridLength.Auto),
                        new(GridLength.Auto)
                    },
                    Children = { icon, PlaceCol(name, 1), PlaceCol(modified, 2), PlaceCol(type, 3), PlaceCol(size, 4) }
                };
                var dbl = new TapGestureRecognizer { NumberOfTapsRequired = 2 };
                dbl.Tapped += async (_, _) =>
                {
                    if (row.BindingContext is EntryRow entry)
                    {
                        _list.SelectedItem = entry;
                        try { await OpenSelectedAsync(); }
                        catch (Exception ex) { await Alert(ex.Message); }
                    }
                };
                row.GestureRecognizers.Add(dbl);
                return row;
            })
        };

        _explorerHost = new Border
        {
            Margin = new Thickness(8, 0),
            Stroke = TryLine(),
            StrokeThickness = 1,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 4 },
            Content = _list,
            VerticalOptions = LayoutOptions.Fill
        };
        // 右键：Win11 样式菜单（顶栏剪切/复制/重命名/删除）
        var rightClick = new TapGestureRecognizer { Buttons = ButtonsMask.Secondary };
        rightClick.Tapped += (_, e) =>
        {
            try
            {
                var pos = e.GetPosition(_root) ?? new Point(80, 120);
                ShowWin11ContextMenu(pos);
            }
            catch (Exception ex) { _ = Alert(ex.Message); }
        };
        _explorerHost.GestureRecognizers.Add(rightClick);
        _list.GestureRecognizers.Add(rightClick);

        _gatePassword.Placeholder = Loc.T("Tool.FileVault.PasswordPlaceholder");
        _gateConfirm.Placeholder = Loc.T("Tool.FileVault.ConfirmPlaceholder");
        _gatePassword.ShowPasswordTip = Loc.T("Sec.ShowPassword");
        _gatePassword.HidePasswordTip = Loc.T("Sec.HidePassword");
        _gateConfirm.ShowPasswordTip = Loc.T("Sec.ShowPassword");
        _gateConfirm.HidePasswordTip = Loc.T("Sec.HidePassword");
        StylePrimary(_gateAction);
        _gateAction.Clicked += async (_, _) =>
        {
            try { await GateActionAsync(); }
            catch (Exception ex) { _gateError.Text = ex.Message; }
        };
        _gateHost = new Border
        {
            StrokeThickness = 0,
            IsVisible = false,
            Content = new VerticalStackLayout
            {
                VerticalOptions = LayoutOptions.Center,
                HorizontalOptions = LayoutOptions.Center,
                Spacing = 10,
                Padding = 24,
                MaximumWidthRequest = 380,
                Children = { _gateTitle, _gateHint, _gatePassword, _gateConfirm, _gateAction, _gateError }
            }
        };

        void CloseVault()
        {
            try
            {
                _ = ToolService.Instance.Ui.RequestAsync(ToolIds.FileVault, Module.UiCloseWindow, null);
            }
            catch { /* ignore */ }
        }

        Window? HostWindow() =>
            Window
            ?? Application.Current?.Windows.FirstOrDefault(w =>
                w.Page is ContentPage cp && ReferenceEquals(cp.Content, this));

        var embedInCaption = OperatingSystem.IsWindows();
        var (tabBar, drag) = VaultExplorerChrome.BuildTitleTabBar(
            onCloseWindow: CloseVault,
            onMinimize: () =>
            {
                var w = HostWindow();
                if (w != null) FileVaultMauiTitleChrome.Minimize(w);
            },
            onToggleMaximize: () =>
            {
                var w = HostWindow();
                if (w != null) FileVaultMauiTitleChrome.ToggleMaximize(w);
            },
            isMaximized: () =>
            {
                var w = HostWindow();
                return w != null && FileVaultMauiTitleChrome.IsMaximized(w);
            },
            embedInCaption: embedInCaption);
        if (embedInCaption)
            _captionTabs = tabBar;
        else
            _titleDrag = drag;

        var navRow = new Grid
        {
            Margin = new Thickness(8, 4, 8, 4),
            ColumnDefinitions = new ColumnDefinitionCollection
            {
                new(GridLength.Star),
                new(new GridLength(240))
            },
            Children =
            {
                _pathBar,
                PlaceCol(_searchHost, 1)
            }
        };

        var cmdBar = VaultExplorerChrome.BuildCommandBar(
            entry => Safe(() => CreateNewAsync(entry)),
            () => Safe(ImportFileAsync),
            () => Safe(ImportFolderAsync),
            () => Safe(DeleteAsync),
            async () => ShowWin11ContextMenu(new Point(120, 140)),
            out _newFolderBtn,
            out _addFileBtn,
            out _addFolderBtn,
            out _deleteBtn);

        var statusBar = new Border
        {
            Padding = new Thickness(12, 6),
            Content = _status
        };

        if (embedInCaption)
        {
            // Tab 进系统标题栏，页面从导航行开始。锁定闸门不盖标题栏。
            _root.RowDefinitions = new RowDefinitionCollection
            {
                new(GridLength.Auto),
                new(GridLength.Auto),
                new(GridLength.Star),
                new(GridLength.Auto)
            };
            _root.Children.Add(Place(navRow, 0));
            _root.Children.Add(Place(cmdBar, 1));
            _root.Children.Add(Place(_explorerHost, 2));
            _root.Children.Add(Place(statusBar, 3));
            _root.Children.Add(Place(_gateHost, 0));
            Grid.SetRowSpan(_gateHost, 4);
        }
        else
        {
            _root.RowDefinitions = new RowDefinitionCollection
            {
                new(GridLength.Auto),
                new(GridLength.Auto),
                new(GridLength.Auto),
                new(GridLength.Star),
                new(GridLength.Auto)
            };
            _root.Children.Add(tabBar);
            _root.Children.Add(Place(navRow, 1));
            _root.Children.Add(Place(cmdBar, 2));
            _root.Children.Add(Place(_explorerHost, 3));
            _root.Children.Add(Place(statusBar, 4));
            _root.Children.Add(Place(_gateHost, 1));
            Grid.SetRowSpan(_gateHost, 4);
        }
        Content = _root;

        if (_service != null)
        {
            _sessionChangedHandler = () => MainThread.BeginInvokeOnMainThread(Refresh);
            _progressChangedHandler = p => MainThread.BeginInvokeOnMainThread(() =>
            {
                if (_unloaded) return;
                _status.Text = $"{p.Phase} {p.CurrentName} {p.Done}/{p.Total}";
                StatusReporter?.Invoke(_status.Text);
            });
            _service.SessionChanged += _sessionChangedHandler;
            _service.ProgressChanged += _progressChangedHandler;
        }
        ApplyTheme();
        Refresh();
    }

    /// <summary>把 Tab 放进窗口标题栏，替换浅色默认标题条。</summary>
    public void MountCaptionTabs(Window window)
    {
        if (_captionTabs == null || _captionMounted)
            return;
        _captionMounted = true;
        AttachFileDrop(window);
        window.TitleBar = new TitleBar
        {
            LeadingContent = _captionTabs,
            BackgroundColor = BackgroundColor,
            IsVisible = true
        };
    }

    public void ApplyTheme()
    {
        try
        {
            // 宿主 ApplyWindowTheme 会写 Page.Resources；此处再落 ForDialog 到本控件，避免独立窗缺色
            var themeId = ResolveThemeId();
            var colors = ThemePalette.ForDialog(themeId, maui: true);
            foreach (var (key, hex) in ThemePalette.ToResourceMap(colors))
                Resources[key] = Color.FromArgb(hex);

            BackgroundColor = Color.FromArgb(colors.WinBg);
            if (Window?.TitleBar is TitleBar caption)
                caption.BackgroundColor = BackgroundColor;
            _explorerHost.BackgroundColor = Color.FromArgb(colors.CardBg);
            _explorerHost.Stroke = Color.FromArgb(colors.Line);
            _gateHost.BackgroundColor = Color.FromArgb(colors.CardBg);
            _searchHost.Stroke = Color.FromArgb(colors.Line);
            _searchHost.BackgroundColor = Color.FromArgb(colors.CardBg);
            _searchBox.TextColor = Color.FromArgb(colors.Text);
            _searchBox.PlaceholderColor = Color.FromArgb(colors.Muted);
            _gateTitle.TextColor = Color.FromArgb(colors.Text);
            _gateHint.TextColor = Color.FromArgb(colors.Muted);
            _status.TextColor = Color.FromArgb(colors.Muted);
            var text = Color.FromArgb(colors.Text);
            var muted = Color.FromArgb(colors.Muted);
            var line = Color.FromArgb(colors.Line);
            var card = Color.FromArgb(colors.CardBg);
            _gatePassword.ApplyTheme(text, muted, line, card);
            _gateConfirm.ApplyTheme(text, muted, line, card);
        }
        catch
        {
            // ignore
        }
    }

    private static string ResolveThemeId()
        => string.IsNullOrWhiteSpace(MauiDialogShell.ActiveThemeId)
            ? ThemePalette.LightName
            : MauiDialogShell.ActiveThemeId;

    private static View Sep() => new BoxView
    {
        WidthRequest = 1,
        HeightRequest = 28,
        Color = TryLine(),
        VerticalOptions = LayoutOptions.Center,
        Margin = new Thickness(6, 0)
    };

    private void DismissContextMenu()
    {
        if (_ctxScrim != null) { _root.Children.Remove(_ctxScrim); _ctxScrim = null; }
        if (_ctxMenu != null) { _root.Children.Remove(_ctxMenu); _ctxMenu = null; }
    }

    private void ShowWin11ContextMenu(Point at)
    {
        DismissContextMenu();
        var unlocked = _service?.IsUnlocked == true;
        var hasSel = _list.SelectedItem is EntryRow;
        var row = _list.SelectedItem as EntryRow;

        _ctxScrim = new BoxView { Color = Colors.Transparent };
        var scrimTap = new TapGestureRecognizer();
        scrimTap.Tapped += (_, _) => DismissContextMenu();
        _ctxScrim.GestureRecognizers.Add(scrimTap);
        var span = Math.Max(1, _root.RowDefinitions.Count);
        Grid.SetRowSpan(_ctxScrim, span);
        _ctxScrim.ZIndex = 40;
        _root.Children.Add(_ctxScrim);

        _ctxMenu = VaultExplorerChrome.BuildWin11ContextMenu(
            hasSel,
            unlocked,
            async () => await Safe(OpenSelectedAsync),
            async () => await Safe(DeleteAsync),
            async () => await Safe(ImportFileAsync),
            async () => await Safe(ImportFolderAsync),
            async () => await Safe(ImportBackupAsync),
            async () => await Safe(() => ExportSelectedAsync(false)),
            async () => await Safe(() => ExportSelectedAsync(true)),
            async () => await Safe(ExportBackupAsync),
            async () => await Safe(EditIdleLockAsync),
            () => { StageClipboard(cut: true); return Task.CompletedTask; },
            () => { StageClipboard(cut: false); return Task.CompletedTask; },
            async () => await Safe(RenameAsync),
            async () => await Safe(PasteAsync),
            _service?.HasClipboard == true,
            DismissContextMenu);

        var x = Math.Max(8, Math.Min(at.X, Math.Max(8, _root.Width - 290)));
        var y = Math.Max(8, Math.Min(at.Y, Math.Max(8, _root.Height - 460)));
        _ctxMenu.Margin = new Thickness(x, y, 0, 0);
        _ctxMenu.HorizontalOptions = LayoutOptions.Start;
        _ctxMenu.VerticalOptions = LayoutOptions.Start;
        _ctxMenu.ZIndex = 41;
        Grid.SetRowSpan(_ctxMenu, span);
        _root.Children.Add(_ctxMenu);
    }

    private async Task Safe(Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { await Alert(ex.Message); }
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

    private void OnUnloaded(object? sender, EventArgs e)
    {
        // 服务是单例、生命周期长于本面板；窗关闭后必须退订，避免后台事件把 Refresh
        // 排到已释放的 UI/DI 上下文，触发 ObjectDisposedException。
        _unloaded = true;
        _fileDrop.Detach();
        if (_service != null)
        {
            if (_sessionChangedHandler != null) _service.SessionChanged -= _sessionChangedHandler;
            if (_progressChangedHandler != null) _service.ProgressChanged -= _progressChangedHandler;
        }
        _ = LeaveLockAsync();
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

    private void StyleCmdBtn(Border b, string glyph, string label, string tip, Func<Task> onClick)
    {
        b.StrokeThickness = 0;
        b.BackgroundColor = Colors.Transparent;
        b.Padding = new Thickness(8, 4);
        b.MinimumHeightRequest = 32;
        b.StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 4 };
        b.Content = new HorizontalStackLayout
        {
            Spacing = 6,
            Children =
            {
                new Label { Text = glyph, FontSize = 14, VerticalOptions = LayoutOptions.Center, TextColor = TryText() },
                new Label { Text = label, FontSize = 12, VerticalOptions = LayoutOptions.Center, TextColor = TryText() }
            }
        };
        ToolTipProperties.SetText(b, tip);
        var tap = new TapGestureRecognizer();
        tap.Tapped += async (_, _) =>
        {
            if (b.Opacity < 0.9) return;
            try { await onClick(); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { await Alert(ex.Message); }
        };
        b.GestureRecognizers.Add(tap);
    }

    private static void SetCmdEnabled(Border b, bool enabled)
    {
        b.Opacity = enabled ? 1 : 0.38;
        b.InputTransparent = !enabled;
    }

    private void StylePrimary(Button b)
    {
        b.BackgroundColor = Color.FromArgb("#2563EB");
        b.TextColor = Colors.White;
        b.CornerRadius = 4;
        b.Padding = new Thickness(16, 8);
        b.FontSize = 13;
    }

    private static Color TryText() =>
        Application.Current?.Resources.TryGetValue("Text", out var v) == true && v is Color c ? c : Color.FromArgb("#111827");

    private static Color TryMuted() =>
        Application.Current?.Resources.TryGetValue("Muted", out var v) == true && v is Color c ? c : Color.FromArgb("#6B7280");

    private static Color TryLine() =>
        Application.Current?.Resources.TryGetValue("Line", out var v) == true && v is Color c ? c : Color.FromArgb("#E5E7EB");

    private static View Place(View view, int row) { Grid.SetRow(view, row); return view; }
    private static View PlaceCol(View view, int col) { Grid.SetColumn(view, col); return view; }

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
        if (_unloaded) return;
        if (_service == null)
        {
            _status.Text = Loc.T("Tool.FileVault.NotReady");
            ShowGate(Loc.T("Tool.FileVault.NotReady"), Loc.T("Tool.FileVault.NotReady"), false, false);
            return;
        }

        var root = _service.EnsureDefaultRoot();
        var unlocked = _service.IsUnlocked;
        SetCmdEnabled(_newFolderBtn, unlocked);
        SetCmdEnabled(_addFileBtn, unlocked);
        SetCmdEnabled(_addFolderBtn, unlocked);
        SetCmdEnabled(_deleteBtn, unlocked);
        _pathBar.IsEnabled = unlocked;
        _searchBox.IsEnabled = unlocked;

        if (!unlocked)
        {
            _rows.Clear();
            _allRows.Clear();
            _pathBar.SetItems([]);
            _pathBar.SetNavState(false, false, false);
            _status.Text = string.Format(Loc.T("Tool.FileVault.StatusRoot"), root);
            _gatePassword.Password = "";
            _gateConfirm.Password = "";
            _gateError.Text = "";
            if (_service.HasVault)
            {
                ShowGate(Loc.T("Tool.FileVault.Name"), Loc.T("Tool.FileVault.UnlockPrompt"), false, true);
                _gateAction.Text = Loc.T("Tool.FileVault.Unlock");
            }
            else
            {
                ShowGate(Loc.T("Tool.FileVault.Name"), Loc.T("Tool.FileVault.NoVaultPrompt"), true, true);
                _gateAction.Text = Loc.T("Tool.FileVault.Enable");
            }
            return;
        }

        HideGate();
        var items = _service.ListCurrent();
        _pathBar.SetItems(_service.GetPathSegments());
        _pathBar.SetNavState(_backStack.Count > 0, _forwardStack.Count > 0, !string.IsNullOrEmpty(_service.CurrentFolderId));
        _allRows.Clear();
        foreach (var e in items)
        {
            _allRows.Add(new EntryRow(
                e.Id, e.Name, e.IsDir,
                e.IsDir ? "📁" : "📄",
                e.IsDir ? Loc.T("Tool.FileVault.TypeFolder") : Loc.T("Tool.FileVault.TypeFile"),
                e.IsDir ? "" : FormatSize(e.Size),
                e.ModifiedUtc.ToLocalTime().ToString("yyyy/MM/dd HH:mm")));
        }
        ApplySearchFilter();
    }

    private void ApplySearchFilter()
    {
        var q = (_searchBox.Text ?? "").Trim();
        _rows.Clear();
        foreach (var row in _allRows)
        {
            if (q.Length == 0 || row.Name.Contains(q, StringComparison.OrdinalIgnoreCase))
                _rows.Add(row);
        }
        var sel = _list.SelectedItem != null ? 1 : 0;
        var text = string.Format(Loc.T("Tool.FileVault.ItemsCount"), _rows.Count);
        if (sel > 0) text += "  " + string.Format(Loc.T("Tool.FileVault.SelectedCount"), sel);
        _status.Text = text;
        StatusReporter?.Invoke(text);
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

    private async Task EnsureUnlockedAsync()
    {
        if (_service?.IsUnlocked == true) return;
        await Alert(Loc.T("Tool.FileVault.NeedUnlock"));
        throw new OperationCanceledException();
    }

    private async Task OpenSelectedAsync()
    {
        if (_service == null || _list.SelectedItem is not EntryRow row) return;
        if (row.IsDir) NavigateRecording(() => _service.NavigateTo(row.Id));
        else { await _service.OpenEntryAsync(row.Id); Refresh(); }
    }

    private async Task EditIdleLockAsync()
    {
        if (_service == null) return;
        var page = GetPage();
        if (page == null) return;
        var text = await page.DisplayPromptAsync(Loc.T("Tool.FileVault.IdleLock"), "",
            initialValue: _service.Settings.IdleLockMinutes.ToString(), keyboard: Keyboard.Numeric);
        if (text == null) return;
        if (!int.TryParse(text.Trim(), out var minutes)) minutes = 10;
        _service.UpdateSettings(s => s.IdleLockMinutes = Math.Clamp(minutes, 0, 24 * 60));
    }

    private void AttachFileDrop(Window? window = null)
    {
        var win = window ?? VaultWindow();
        if (win == null)
            return;
        _fileDrop.Attach(win, paths => _ = ImportDroppedPaths(paths));
        foreach (var ms in new[] { 300, 800, 1600, 3000 })
        {
            Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(ms), () =>
            {
                if (!_unloaded)
                    _fileDrop.Attach(VaultWindow(), paths => _ = ImportDroppedPaths(paths));
            });
        }
    }

    private async Task CreateNewAsync(VaultNewEntry entry)
    {
        await EnsureUnlockedAsync();
        if (_service == null)
            return;
        string? target = null;
        if (entry.Kind == VaultNewKind.Shortcut)
        {
            var files = await FileVaultMauiDialogs.PickFilesAsync(
                VaultWindow(), Loc.T("Tool.FileVault.NewShortcutTarget"));
            if (files.Count == 0)
                return;
            target = files[0];
        }
        await _service.CreateNewAsync(entry, target);
        Refresh();
    }

    public Task ImportDroppedPaths(IReadOnlyList<string> paths) => Safe(async () =>
    {
        await EnsureUnlockedAsync();
        if (_service == null || paths == null || paths.Count == 0) return;
        await _service.ImportPathsAsync(paths, removeSource: false);
        Refresh();
    });

    private void StageClipboard(bool cut)
    {
        if (_service == null || _list.SelectedItem is not EntryRow row) return;
        _service.StageClipboard([row.Id], cut);
    }

    private async Task PasteAsync()
    {
        if (_service == null) return;
        await _service.PasteAsync();
        Refresh();
    }

    private async Task RenameAsync()
    {
        if (_service == null || _list.SelectedItem is not EntryRow row) return;
        var page = GetPage();
        if (page == null) return;
        var name = await page.DisplayPromptAsync(Loc.T("Tool.FileVault.Rename"), "", initialValue: row.Name);
        if (string.IsNullOrWhiteSpace(name)) return;
        await _service.RenameAsync(row.Id, name.Trim());
        Refresh();
    }

    private async Task ImportFileAsync()
    {
        await EnsureUnlockedAsync();
        if (_service == null) return;
        var files = await FileVaultMauiDialogs.PickFilesAsync(VaultWindow(), Loc.T("Tool.FileVault.ImportFile"));
        if (files.Count == 0) return;
        await _service.ImportPathsAsync(files);
        Refresh();
    }

    private async Task ImportFolderAsync()
    {
        await EnsureUnlockedAsync();
        if (_service == null) return;
        var folder = await FileVaultMauiDialogs.PickFolderAsync(Loc.T("Tool.FileVault.ImportFolder"), VaultWindow());
        if (string.IsNullOrEmpty(folder)) return;
        await _service.ImportPathsAsync([folder]);
        Refresh();
    }

    private async Task ExportSelectedAsync(bool requireDir)
    {
        if (_service == null || _list.SelectedItem is not EntryRow row || row.IsDir != requireDir) return;
        var folder = await FileVaultMauiDialogs.PickFolderAsync(Loc.T("Tool.FileVault.Export"), VaultWindow());
        if (string.IsNullOrEmpty(folder)) return;
        await _service.ExportSelectedAsync(row.Id, Path.Combine(folder, row.Name));
        Refresh();
    }

    private async Task DeleteAsync()
    {
        if (_service == null || _list.SelectedItem is not EntryRow row) return;
        await _service.DeleteAsync(row.Id);
        Refresh();
    }

    private async Task NewFolderAsync()
    {
        await EnsureUnlockedAsync();
        if (_service == null) return;
        var page = GetPage();
        if (page == null) return;
        var name = await page.DisplayPromptAsync(Loc.T("Tool.FileVault.NewFolder"), "");
        if (string.IsNullOrWhiteSpace(name)) return;
        await _service.NewFolderAsync(name);
        Refresh();
    }

    private async Task ExportBackupAsync()
    {
        if (_service == null) return;
        var folder = await FileVaultMauiDialogs.PickFolderAsync(Loc.T("Tool.FileVault.ExportBackup"), VaultWindow());
        if (string.IsNullOrEmpty(folder)) return;
        await _service.ExportBackupAsync(Path.Combine(folder, "vault.flyvault"));
        Refresh();
    }

    private async Task ImportBackupAsync()
    {
        if (_service == null) return;
        var files = await FileVaultMauiDialogs.PickFilesAsync(
            VaultWindow(), Loc.T("Tool.FileVault.ImportBackup"), "保险箱备份 (*.flyvault)", "*.flyvault");
        if (files.Count == 0) return;
        var dest = _service.EnsureDefaultRoot();
        await _service.ImportBackupAsync(files[0], dest);
        Refresh();
    }

    private Window? VaultWindow() =>
        Window
        ?? Application.Current?.Windows.FirstOrDefault(w =>
            w.Page is ContentPage cp && ReferenceEquals(cp.Content, this));

    private Page? GetPage()
    {
        var win = VaultWindow();
        if (win == null)
            return null;
        try { Application.Current?.ActivateWindow(win); }
        catch { /* ignore */ }
        return win.Page;
    }

    private async Task Alert(string message)
    {
        var page = GetPage();
        if (page != null)
            await page.DisplayAlertAsync(Loc.T("Tool.FileVault.Name"), message, Loc.T("Tool.FileVault.Ok"));
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
