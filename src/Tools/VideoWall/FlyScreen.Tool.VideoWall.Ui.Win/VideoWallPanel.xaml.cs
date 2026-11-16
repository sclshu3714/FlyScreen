using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using FlyScreen.Helpers.Localization;
using FlyScreen.Services.Tools;
using FlyScreen.Tool.VideoWall.Contracts;
using FlyScreen.Tool.VideoWall.Sources;
using FlyScreen.Tool.VideoWall.Wall;
using FlyScreen.ToolSdk;
namespace FlyScreen.Tool.VideoWall.Ui.Win;

public partial class VideoWallPanel : UserControl
{
    private enum PanelMode { Sources, Wall, Playback }
    private enum BrowseView { Card, List, Canvas }

    private readonly IVideoWallService? _service;
    private readonly ObservableCollection<SourceRowVm> _rows = new();
    private readonly Dictionary<int, VideoWallFrameSurface> _tileSurfaces = new();
    private readonly Dictionary<int, TextBlock> _tileTitleBlocks = new();
    private readonly Dictionary<int, TextBlock> _tileStatusBlocks = new();
    private readonly Dictionary<int, Border> _tileBorders = new();
    private readonly Dictionary<int, Button> _tileRestoreButtons = new();
    private PanelMode _mode = PanelMode.Sources;
    private BrowseView _browseView = BrowseView.List;
    private string _groupFilter = "";
    private bool _suppressCombo;
    private bool _suppressSegment;
    private bool _suppressCarouselToggle;
    private Window? _hostWindow;
    private readonly DispatcherTimer _carouselTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(WallCarousel.DefaultIntervalMs)
    };

    public Action<string>? StatusReporter
    {
        get => _statusReporter;
        set
        {
            _statusReporter = value;
            Player.StatusReporter = value;
        }
    }

    private Action<string>? _statusReporter;

    public VideoWallPanel()
    {
        InitializeComponent();
        _service = ToolService.Instance.GetToolService<IVideoWallService>();
        SourcesGrid.ItemsSource = _rows;
        SourcesCards.ItemsSource = _rows;
        Player.Closed += OnPlayerClosed;
        Player.RequestInAppMaximize += OnPlayerRequestInAppMaximize;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SearchBox.TextChanged += (_, _) =>
        {
            RefreshSources();
            if (_mode == PanelMode.Wall)
                _ = RefreshCanvasAsync();
        };
        GroupFilter.SelectionChanged += (_, _) =>
        {
            var sel = GroupFilter.SelectedItem as string;
            _groupFilter = string.Equals(sel, Loc.T("Tool.VideoWall.GroupAll"), StringComparison.Ordinal) ? "" : sel ?? "";
            RefreshSources();
            if (_mode == PanelMode.Wall)
                _ = RefreshCanvasAsync();
        };
        _carouselTimer.Tick += async (_, _) =>
        {
            if (_service is null || _mode != PanelMode.Wall || !_service.CarouselEnabled) return;
            await _service.AdvanceCarouselAsync();
            await PresentCarouselPageAsync();
            UpdateCarouselChrome();
        };
        ApplyLoc();

        if (_service is null)
        {
            ReportLog(Loc.T("Tool.VideoWall.BackendUnavailable"));
            return;
        }

        if (!_service.Backend.IsAvailable)
            ReportLog(Loc.T("Tool.VideoWall.BackendUnavailable"));

        SyncCombosFromState();
        RefreshSources();
    }

    private void ApplyLoc()
    {
        _suppressCombo = true;
        try
        {
            LayoutCombo.Items.Clear();
            LayoutCombo.Items.Add(new ComboBoxItem { Content = "1×1", Tag = 1 });
            LayoutCombo.Items.Add(new ComboBoxItem { Content = "2×2", Tag = 2 });
            LayoutCombo.Items.Add(new ComboBoxItem { Content = "3×3", Tag = 3 });
            LayoutCombo.Items.Add(new ComboBoxItem { Content = "4×4", Tag = 4 });
        }
        finally
        {
            _suppressCombo = false;
        }

        // 左=源(卡片)，右=列表；与原型「列表|卡片」分段一致，仅图标左右对调贴合标注
        ViewCardBtn.ToolTip = Loc.T("Tool.VideoWall.ViewCard");
        ViewListBtn.ToolTip = Loc.T("Tool.VideoWall.ViewList");
        ViewCanvasBtn.ToolTip = Loc.T("Tool.VideoWall.ViewCanvas");
        SearchBox.ToolTip = Loc.T("Tool.VideoWall.Search");
        AddBtn.ToolTip = Loc.T("Tool.VideoWall.Add");
        ScanBtn.ToolTip = Loc.T("Tool.VideoWall.Scan");
        DeleteBtn.ToolTip = Loc.T("Tool.VideoWall.Delete");
        LayoutCombo.ToolTip = Loc.T("Tool.VideoWall.Layout");
        CarouselToggle.Content = Loc.T("Tool.VideoWall.Carousel");
        CarouselToggle.ToolTip = Loc.T("Tool.VideoWall.CarouselHint");
        EmptyTitle.Text = Loc.T("Tool.VideoWall.EmptyTitle");
        EmptyHint.Text = Loc.T("Tool.VideoWall.EmptyHint");
        HeaderSelectAll.ToolTip = Loc.T("Tool.VideoWall.SelectAllTip");
        ColName.Header = Loc.T("Tool.VideoWall.ColName");
        ColKind.Header = Loc.T("Tool.VideoWall.ColKind");
        ColAddress.Header = Loc.T("Tool.VideoWall.Field.Address");
        ColGroup.Header = Loc.T("Tool.VideoWall.Field.Group");
        ColStatus.Header = Loc.T("Tool.VideoWall.ColStatus");
        ColActions.Header = Loc.T("Tool.VideoWall.ColActions");
    }

    private void SyncCombosFromState()
    {
        _suppressCombo = true;
        _suppressSegment = true;
        try
        {
            var browsing = _mode is PanelMode.Sources or PanelMode.Wall;
            ViewCardBtn.IsChecked = browsing && _browseView == BrowseView.Card;
            ViewListBtn.IsChecked = browsing && _browseView == BrowseView.List;
            ViewCanvasBtn.IsChecked = browsing && _browseView == BrowseView.Canvas;
            ViewCardBtn.IsEnabled = browsing;
            ViewListBtn.IsEnabled = browsing;
            ViewCanvasBtn.IsEnabled = browsing;
            var canvasChrome = _browseView == BrowseView.Canvas && browsing;
            LayoutCombo.Visibility = canvasChrome ? Visibility.Visible : Visibility.Collapsed;
            CarouselPanel.Visibility = canvasChrome ? Visibility.Visible : Visibility.Collapsed;
            _suppressCarouselToggle = true;
            try
            {
                CarouselToggle.IsChecked = _service?.CarouselEnabled == true;
            }
            finally
            {
                _suppressCarouselToggle = false;
            }

            UpdateCarouselChrome();
            ApplySegmentIconColors();

            var edge = _service?.Layout.Rows ?? 2;
            for (var i = 0; i < LayoutCombo.Items.Count; i++)
            {
                if (LayoutCombo.Items[i] is ComboBoxItem { Tag: int tag } && tag == edge)
                {
                    LayoutCombo.SelectedIndex = i;
                    break;
                }
            }

            if (LayoutCombo.SelectedIndex < 0 && LayoutCombo.Items.Count > 0)
                LayoutCombo.SelectedIndex = Math.Min(1, LayoutCombo.Items.Count - 1); // 默认 2×2
        }
        finally
        {
            _suppressCombo = false;
            _suppressSegment = false;
        }
    }

    private void ApplySegmentIconColors()
    {
        var active = TryFindResource("Blue") as System.Windows.Media.Brush
                     ?? System.Windows.Media.Brushes.DodgerBlue;
        var idle = TryFindResource("Text") as System.Windows.Media.Brush
                   ?? System.Windows.Media.Brushes.Black;
        ViewCardIcon.Fill = ViewCardBtn.IsChecked == true ? active : idle;
        ViewListIcon.Fill = ViewListBtn.IsChecked == true ? active : idle;
        ViewCanvasIcon.Fill = ViewCanvasBtn.IsChecked == true ? active : idle;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        InheritHostBrushes(Window.GetWindow(this) ?? (FrameworkElement?)Parent);
        HookHostWindow(Window.GetWindow(this));
        if (_service is null) return;
        _service.CatalogChanged += OnCatalogChanged;
        _service.SessionChanged += OnSessionChanged;
        Player.AttachService(_service);
        PopulateGroups();
        SyncCombosFromState();
        RefreshAll();
        FitSourcesColumns();
    }

    private void HookHostWindow(Window? window)
    {
        if (ReferenceEquals(_hostWindow, window)) return;
        UnhookHostWindow();
        _hostWindow = window;
        if (window is null) return;
        window.IsVisibleChanged += OnHostIsVisibleChanged;
        window.Closing += OnHostClosing;
    }

    private void UnhookHostWindow()
    {
        if (_hostWindow is null) return;
        _hostWindow.IsVisibleChanged -= OnHostIsVisibleChanged;
        _hostWindow.Closing -= OnHostClosing;
        _hostWindow = null;
    }

    private void OnHostIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_hostWindow?.IsVisible == true)
        {
            if (_mode == PanelMode.Playback)
                Player.RestoreNativeOverlays();
            return;
        }

        // 隐藏到托盘不会 Unload：收起单路 UI，并解绑墙格帧面
        ReleaseNativeSurfaces();
    }

    private void OnHostClosing(object? sender, System.ComponentModel.CancelEventArgs e)
        => ReleaseNativeSurfaces();

    private void ReleaseNativeSurfaces()
    {
        Player.SuspendPlaybackUi();
        DetachAllTileSurfaces();
    }

    /// <summary>从宿主窗口拷贝主题色，使工具区与主程序样式一致。</summary>
    private void InheritHostBrushes(FrameworkElement? host)
    {
        if (host is null) return;
            foreach (var key in new[]
                 {
                     "Blue", "BluePress", "BlueSoft", "BlueHover", "Muted", "Line", "Text",
                     "WinBg", "CardBg", "ListBg", "SubtleBg", "ControlBg", "ControlBorder", "ControlHover"
                 })
        {
            if (host.TryFindResource(key) is SolidColorBrush brush)
                Resources[key] = brush.Clone();
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _carouselTimer.Stop();
        UnhookHostWindow();
        ReleaseNativeSurfaces();
        Player.AttachService(null);
        _ = Player.StopAsync();
        if (_service is null) return;
        _service.CatalogChanged -= OnCatalogChanged;
        _service.SessionChanged -= OnSessionChanged;
    }

    private void SourcesGrid_SizeChanged(object sender, SizeChangedEventArgs e)
        => FitSourcesColumns();

    /// <summary>地址列吃满剩余宽度，避免横向滚动条。</summary>
    private void FitSourcesColumns()
    {
        if (SourcesGrid.ActualWidth <= 1 || ColAddress is null) return;
        // 复选/名称/类型/分组/状态/操作 + 纵向滚动条预留
        const double fixedCols = 40 + 120 + 88 + 80 + 64 + 108;
        var scroll = SystemParameters.VerticalScrollBarWidth + 16;
        var addressW = Math.Max(120, SourcesGrid.ActualWidth - scroll - fixedCols);
        if (Math.Abs(ColAddress.Width - addressW) > 0.5)
            ColAddress.Width = addressW;
    }

    private bool _suppressCheckSync;

    private void HeaderSelectAll_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox) return;
        e.Handled = true;
        // 单击：全选/取消；Shift+单击：反选
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            foreach (var row in _rows)
                row.IsSelected = !row.IsSelected;
        }
        else
        {
            var all = _rows.Count > 0 && _rows.All(r => r.IsSelected);
            var target = !all;
            foreach (var row in _rows)
                row.IsSelected = target;
        }

        SyncListSelectionFromChecks();
        SyncHeaderSelectAll();
        UpdateDeleteEnabled();
    }

    private void RowCheck_Click(object sender, RoutedEventArgs e)
    {
        // 等绑定写回后再同步 ListView 选中与表头
        Dispatcher.BeginInvoke(() =>
        {
            SyncListSelectionFromChecks();
            SyncHeaderSelectAll();
            UpdateDeleteEnabled();
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    private void SourcesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressCheckSync) return;
        _suppressCheckSync = true;
        try
        {
            var selected = SourcesGrid.SelectedItems.OfType<SourceRowVm>().ToHashSet();
            foreach (var row in _rows)
                row.IsSelected = selected.Contains(row);
            SyncHeaderSelectAll();
            UpdateDeleteEnabled();
        }
        finally
        {
            _suppressCheckSync = false;
        }
    }

    private void SyncListSelectionFromChecks()
    {
        if (_suppressCheckSync) return;
        _suppressCheckSync = true;
        try
        {
            SourcesGrid.SelectedItems.Clear();
            foreach (var row in _rows.Where(r => r.IsSelected))
                SourcesGrid.SelectedItems.Add(row);
        }
        finally
        {
            _suppressCheckSync = false;
        }
    }

    private void SyncHeaderSelectAll()
    {
        if (_rows.Count == 0)
        {
            HeaderSelectAll.IsChecked = false;
            return;
        }

        var n = _rows.Count(r => r.IsSelected);
        HeaderSelectAll.IsChecked = n == 0 ? false : n == _rows.Count ? true : null;
    }

    private void UpdateDeleteEnabled()
        => DeleteBtn.IsEnabled = _rows.Any(r => r.IsSelected);

    private void OnCatalogChanged()
        => Dispatcher.BeginInvoke(() =>
        {
            RefreshSources();
            if (_mode == PanelMode.Wall)
                _ = RefreshCanvasAsync();
        });

    private void OnSessionChanged()
        => Dispatcher.BeginInvoke(() =>
        {
            if (_mode == PanelMode.Wall)
                RefreshWallOverlays();
        });

    /// <summary>经宿主 <see cref="StatusReporter"/> 写日志页，插件内不展示错误条。</summary>
    private void ReportLog(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        StatusReporter?.Invoke(message);
    }

    private void PopulateGroups()
    {
        if (_service is null) return;
        GroupFilter.Items.Clear();
        GroupFilter.Items.Add(Loc.T("Tool.VideoWall.GroupAll"));
        foreach (var g in _service.Catalog.Groups)
            GroupFilter.Items.Add(g.Name);
        GroupFilter.SelectedIndex = 0;
    }

    private void RefreshAll()
    {
        RefreshSources();
        if (_mode == PanelMode.Wall)
            RebuildWallGrid();
        SyncCombosFromState();
    }

    private void RefreshSources()
    {
        if (_service is null) return;
        var q = (SearchBox.Text ?? "").Trim();
        _rows.Clear();
        foreach (var s in _service.Catalog.Sources)
        {
            if (!string.IsNullOrEmpty(_groupFilter) &&
                !string.Equals(_groupFilter, Loc.T("Tool.VideoWall.GroupAll"), StringComparison.Ordinal) &&
                !MatchesGroup(s, _groupFilter))
                continue;

            if (q.Length > 0 &&
                !s.Name.Contains(q, StringComparison.OrdinalIgnoreCase) &&
                !s.Endpoint.Address.Contains(q, StringComparison.OrdinalIgnoreCase))
                continue;

            _rows.Add(ToRow(s));
        }

        var empty = _rows.Count == 0;
        EmptyState.Visibility = empty && _mode == PanelMode.Sources ? Visibility.Visible : Visibility.Collapsed;
        SourcesGrid.Visibility = _browseView == BrowseView.List && !empty
            ? Visibility.Visible : Visibility.Collapsed;
        SourcesCardsHost.Visibility = _browseView == BrowseView.Card && !empty
            ? Visibility.Visible : Visibility.Collapsed;
        SyncHeaderSelectAll();
        UpdateDeleteEnabled();
    }

    private static bool MatchesGroup(VideoSourceDescriptor s, string groupName)
        => string.Equals(s.GroupId, groupName, StringComparison.OrdinalIgnoreCase);

    private SourceRowVm ToRow(VideoSourceDescriptor s)
    {
        var coverPath = s.CoverKey is not null && _service!.Covers.Exists(s.CoverKey)
            ? _service.Covers.PathForKey(s.CoverKey)
            : null;

        return new SourceRowVm
        {
            Id = s.Id,
            Name = s.Name,
            Kind = s.Kind,
            KindText = KindLabel(s.Kind),
            AddressRedacted = VideoEndpointParser.RedactAddress(s.Endpoint.Address),
            Group = s.GroupId,
            StatusText = ValidationLabel(s.ValidationState),
            CoverPath = coverPath,
            Source = s
        };
    }

    private static string KindLabel(VideoSourceKind kind) => kind switch
    {
        VideoSourceKind.Camera => Loc.T("Tool.VideoWall.Type.Camera"),
        VideoSourceKind.NetworkUrl => Loc.T("Tool.VideoWall.Type.Network"),
        VideoSourceKind.LocalFile => Loc.T("Tool.VideoWall.Type.LocalFile"),
        VideoSourceKind.NvrChannel => Loc.T("Tool.VideoWall.Type.Nvr"),
        _ => kind.ToString()
    };

    private static string ValidationLabel(SourceValidationState v) => v switch
    {
        SourceValidationState.Ok => "OK",
        SourceValidationState.Failed => "Failed",
        _ => "—"
    };

    private static string StatusTextFor(TilePlayState state) => state switch
    {
        TilePlayState.Connecting => Loc.T("Tool.VideoWall.StatusConnecting"),
        TilePlayState.Buffering => Loc.T("Tool.VideoWall.StatusBuffering"),
        TilePlayState.Stalled => Loc.T("Tool.VideoWall.StatusStalled"),
        TilePlayState.Reconnecting => Loc.T("Tool.VideoWall.StatusReconnecting"),
        TilePlayState.Paused => Loc.T("Tool.VideoWall.Pause"),
        TilePlayState.Stopped => Loc.T("Tool.VideoWall.StatusStopped"),
        _ => state.ToString()
    };

    private void SetMode(PanelMode mode)
    {
        _mode = mode;
        MainToolbar.Visibility = mode == PanelMode.Playback ? Visibility.Collapsed : Visibility.Visible;
        SourcesPane.Visibility = mode == PanelMode.Sources ? Visibility.Visible : Visibility.Collapsed;
        WallGrid.Visibility = mode == PanelMode.Wall ? Visibility.Visible : Visibility.Collapsed;
        Player.Visibility = mode == PanelMode.Playback ? Visibility.Visible : Visibility.Collapsed;
        // 离开播放态时再停一次，确保 LibVLC ForegroundWindow 已卸掉（避免浮层残留）
        if (mode != PanelMode.Playback)
            _ = Player.StopAsync();
        // 播放态铺满工具区，去掉外边距白边
        RootLayout.Margin = mode == PanelMode.Playback ? new Thickness(0) : new Thickness(4);
        if (mode == PanelMode.Wall)
            RebuildWallGrid();
        else if (mode == PanelMode.Sources)
            RefreshSources();

        SyncCombosFromState();
    }

    private async void ApplyBrowseView(BrowseView view)
    {
        if (_suppressSegment) return;
        var leavingCanvas = _browseView == BrowseView.Canvas && view != BrowseView.Canvas;
        _browseView = view;
        if (leavingCanvas)
        {
            _carouselTimer.Stop();
            await RecycleWallPlaybackAsync();
        }
        if (view == BrowseView.Canvas)
        {
            SetMode(PanelMode.Wall);
            await RefreshCanvasAsync();
        }
        else
            SetMode(PanelMode.Sources);
    }

    private void ViewCardBtn_Click(object sender, RoutedEventArgs e)
        => ApplyBrowseView(BrowseView.Card);

    private void ViewListBtn_Click(object sender, RoutedEventArgs e)
        => ApplyBrowseView(BrowseView.List);

    private void ViewCanvasBtn_Click(object sender, RoutedEventArgs e)
        => ApplyBrowseView(BrowseView.Canvas);

    private async void LayoutCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressCombo || _service is null) return;
        if (_browseView != BrowseView.Canvas) return;
        if (LayoutCombo.SelectedItem is not ComboBoxItem { Tag: int edge }) return;
        var layout = WallLayout.FromEdge(edge);
        if (layout is null) return;
        DetachAllTileSurfaces();
        await _service.SetLayoutAsync(layout);
        if (_mode != PanelMode.Wall)
            SetMode(PanelMode.Wall);
        else
            RebuildWallGrid();
        await RefreshCanvasAsync();
    }

    /// <summary>离开画布或改布局前：解绑帧面并停止全部格播放，避免残帧/占用。</summary>
    private async Task RecycleWallPlaybackAsync()
    {
        _carouselTimer.Stop();
        DetachAllTileSurfaces();
        if (_service is not null)
        {
            try { await _service.StopAllTilesAsync(); }
            catch { /* ignore */ }
        }

        WallGrid.Children.Clear();
        _tileSurfaces.Clear();
        _tileTitleBlocks.Clear();
        _tileStatusBlocks.Clear();
        _tileBorders.Clear();
        _tileRestoreButtons.Clear();
    }

    /// <summary>单路返回：画布模式回到墙并按轮播页重建；否则回到列表/卡片。</summary>
    private async void OnPlayerClosed()
    {
        if (_browseView == BrowseView.Canvas)
        {
            SetMode(PanelMode.Wall);
            await RefreshCanvasAsync();
        }
        else
        {
            SetMode(PanelMode.Sources);
        }
    }

    private async void CarouselToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressCarouselToggle || _service is null || _mode != PanelMode.Wall) return;
        var on = CarouselToggle.IsChecked == true;
        await _service.SetCarouselEnabledAsync(on);
        await PresentCarouselPageAsync();
        UpdateCarouselChrome();
        SyncCarouselTimer();
    }

    /// <summary>按当前分组/搜索同步源列表并铺当前轮播页。</summary>
    private async Task RefreshCanvasAsync()
    {
        if (_service is null || _mode != PanelMode.Wall) return;
        var ids = _rows.Select(r => r.Id).ToList();
        _service.SetCarouselSources(ids);
        await _service.ApplyCarouselPageAsync(autoStart: false);
        RebuildWallGrid();
        await PresentCarouselPageAsync();
        UpdateCarouselChrome();
        SyncCarouselTimer();
    }

    private async Task PresentCarouselPageAsync()
    {
        if (_service is null) return;
        for (var i = 0; i < _service.Layout.TileCount; i++)
        {
            PopulateTileCell(i, preserveOtherPlayers: true);
            var tile = i < _service.Tiles.Count ? _service.Tiles[i] : null;
            if (tile?.SourceId is null || !tile.HasPlayer) continue;
            AttachTileFrameSink(i);
            try { await _service.StartTileAsync(i); }
            catch (Exception ex) { ReportLog($"视频墙格{i}开播失败: {ex.Message}"); }
        }
    }

    private void SyncCarouselTimer()
    {
        if (_service is null || _mode != PanelMode.Wall || !_service.CarouselEnabled || _service.CarouselPageCount <= 1)
        {
            _carouselTimer.Stop();
            return;
        }

        _carouselTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(1000, _service.CarouselIntervalMs));
        _carouselTimer.Start();
    }

    private void UpdateCarouselChrome()
    {
        if (_service is null)
        {
            CarouselPageText.Text = "";
            return;
        }

        CarouselPageText.Text = string.Format(
            Loc.T("Tool.VideoWall.CarouselPage"),
            _service.CarouselPage + 1,
            Math.Max(1, _service.CarouselPageCount));
    }

    private void AddBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_service is null) return;
        var dlg = new AddSourceDialog(_service, Window.GetWindow(this));
        if (dlg.ShowDialog() != true) return;
        // 延后刷新，避免对话框关闭后的鼠标抬起穿透到列表「播放」按钮
        Dispatcher.BeginInvoke(new Action(RefreshAll), System.Windows.Threading.DispatcherPriority.Background);
    }

    private void ScanBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_service is null) return;
        var dlg = new ScanDevicesDialog(_service, Window.GetWindow(this));
        dlg.ShowDialog();
        Dispatcher.BeginInvoke(new Action(RefreshAll), System.Windows.Threading.DispatcherPriority.Background);
    }

    private async void DeleteBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_service is null) return;
        var rows = _rows.Where(r => r.IsSelected).ToList();
        if (rows.Count == 0)
            rows = SourcesGrid.SelectedItems.OfType<SourceRowVm>().ToList();
        if (rows.Count == 0) return;
        var label = rows.Count == 1 ? rows[0].Name : rows.Count.ToString();
        var ok = MessageBox.Show(
            string.Format(Loc.T("Tool.VideoWall.ConfirmDelete"), label),
            Loc.T("Tool.VideoWall.Delete"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (ok != MessageBoxResult.Yes) return;
        foreach (var row in rows)
            await _service.RemoveSourceAsync(row.Id);
        RefreshAll();
    }

    private void RowPlay_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string id })
            BeginPlayback(id);
    }

    private void RowEdit_Click(object sender, RoutedEventArgs e)
    {
        if (_service is null || sender is not Button { Tag: string id }) return;
        var src = _service.Catalog.Sources.FirstOrDefault(s => s.Id == id);
        if (src is null) return;
        var dlg = new AddSourceDialog(_service, Window.GetWindow(this), src);
        if (dlg.ShowDialog() != true) return;
        Dispatcher.BeginInvoke(new Action(RefreshAll), System.Windows.Threading.DispatcherPriority.Background);
    }

    private async void RowDelete_Click(object sender, RoutedEventArgs e)
    {
        if (_service is null || sender is not Button { Tag: string id }) return;
        var row = _rows.FirstOrDefault(r => r.Id == id);
        var ok = MessageBox.Show(
            string.Format(Loc.T("Tool.VideoWall.ConfirmDelete"), row?.Name ?? id),
            Loc.T("Tool.VideoWall.Delete"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (ok != MessageBoxResult.Yes) return;
        await _service.RemoveSourceAsync(id);
        RefreshAll();
    }

    private SourceRowVm? SelectedRow()
        => SourcesGrid.SelectedItem as SourceRowVm;

    private void RebuildWallGrid()
    {
        if (_service is null) return;
        DetachAllTileSurfaces();
        WallGrid.Children.Clear();
        _tileSurfaces.Clear();
        _tileTitleBlocks.Clear();
        _tileStatusBlocks.Clear();
        _tileBorders.Clear();
        _tileRestoreButtons.Clear();

        var layout = _service.Layout;
        WallGrid.Rows = layout.Rows;
        WallGrid.Columns = layout.Columns;

        for (var i = 0; i < layout.TileCount; i++)
        {
            var border = CreateTileBorderShell(i);
            _tileBorders[i] = border;
            WallGrid.Children.Add(border);
            PopulateTileCell(i, preserveOtherPlayers: false);
        }

        ApplyWallFocus();
    }

    /// <summary>
    /// 应用内最大化：只显示聚焦格并铺满整个 UniformGrid（其余格折叠，播放器保留不拆）。
    /// </summary>
    private void ApplyWallFocus()
    {
        if (_service is null) return;
        var layout = _service.Layout;
        var focus = _service.FocusedTileIndex;
        var focused = focus is int f && _tileBorders.ContainsKey(f);

        // UniformGrid 排布时跳过 Collapsed 子项；1×1 + 其余折叠 ⇒ 聚焦格铺满
        WallGrid.Rows = focused ? 1 : layout.Rows;
        WallGrid.Columns = focused ? 1 : layout.Columns;

        foreach (var (index, border) in _tileBorders)
            border.Visibility = !focused || index == focus ? Visibility.Visible : Visibility.Collapsed;

        foreach (var (index, button) in _tileRestoreButtons)
            button.Visibility = focused && index == focus ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void OnPlayerRequestInAppMaximize()
    {
        if (_service is null) return;
        var sourceId = _service.SinglePlayback.CurrentSourceId;
        if (string.IsNullOrWhiteSpace(sourceId)) return;

        try
        {
            // 单画面播放让位给墙格（同一源不要双播）
            await Player.StopAsync();

            _browseView = BrowseView.Canvas;
            SetMode(PanelMode.Wall);

            var tiles = _service.Tiles;
            var index = tiles.ToList().FindIndex(t => t.SourceId == sourceId);
            if (index < 0)
            {
                index = tiles.ToList().FindIndex(t => t.SourceId is null);
                if (index < 0) index = 0;
                await BindTileInPlaceAsync(index, sourceId);
            }
            else
            {
                index = tiles[index].Index;
            }

            await _service.FocusTileAsync(index);
            ApplyWallFocus();
        }
        catch (Exception ex)
        {
            ReportLog($"视频墙最大化失败: {ex.Message}");
        }
    }

    private async void WallRestoreButton_Click(object sender, RoutedEventArgs e)
    {
        if (_service is null) return;
        e.Handled = true;
        await _service.FocusTileAsync(null);
        ApplyWallFocus();
    }

    private Border CreateTileBorderShell(int index)
    {
        var border = new Border
        {
            Margin = new Thickness(3),
            BorderBrush = new SolidColorBrush(Color.FromRgb(229, 231, 235)),
            BorderThickness = new Thickness(1),
            Background = Brushes.Black,
            Cursor = Cursors.Hand,
            Tag = index,
            ClipToBounds = true
        };
        border.MouseLeftButtonDown += Tile_MouseLeftButtonDown;
        border.MouseRightButtonUp += Tile_MouseRightButtonUp;
        return border;
    }

    /// <summary>
    /// 只重建指定格的内容。绑定/清除时 MUST NOT 整墙重建，否则会拆掉其它格的帧面导致停播。
    /// </summary>
    private void PopulateTileCell(int index, bool preserveOtherPlayers)
    {
        if (_service is null) return;
        if (!_tileBorders.TryGetValue(index, out var border))
        {
            // 布局尚未建壳：退回整墙（仅布局切换时）
            if (!preserveOtherPlayers)
                RebuildWallGrid();
            return;
        }

        // 仅卸本格软件帧面
        if (_tileSurfaces.TryGetValue(index, out var oldSurface))
        {
            try { oldSurface.Clear(); } catch { /* ignore */ }
            if (index < _service.Tiles.Count)
                _service.Tiles[index].AttachFrameSink(null);
            _tileSurfaces.Remove(index);
        }

        _tileTitleBlocks.Remove(index);
        _tileStatusBlocks.Remove(index);
        _tileRestoreButtons.Remove(index);

        var tile = index < _service.Tiles.Count ? _service.Tiles[index] : null;
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var titleBlock = new TextBlock
        {
            Text = ResolveTileTitle(tile),
            Foreground = Brushes.White,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(6, 4, 6, 0)
        };
        var statusBlock = new TextBlock
        {
            Text = tile?.SourceId is null ? "" : StatusTextFor(tile.State),
            Foreground = Brushes.LightGray,
            FontSize = 11,
            Margin = new Thickness(6, 0, 6, 4)
        };
        var chrome = new StackPanel
        {
            Background = new SolidColorBrush(Color.FromArgb(0xAA, 0x11, 0x18, 0x27)),
            Tag = index
        };
        chrome.Children.Add(titleBlock);
        chrome.Children.Add(statusBlock);
        chrome.MouseLeftButtonDown += Tile_MouseLeftButtonDown;
        chrome.MouseRightButtonUp += Tile_MouseRightButtonUp;
        Grid.SetRow(chrome, 0);
        root.Children.Add(chrome);
        _tileTitleBlocks[index] = titleBlock;
        _tileStatusBlocks[index] = statusBlock;

        // 最大化后的「还原」入口（仅聚焦格可见）
        var restore = new Button
        {
            Content = Loc.T("Tool.VideoWall.MaximizeExit"),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 4, 6, 0),
            Padding = new Thickness(10, 2, 10, 2),
            Cursor = Cursors.Hand,
            Foreground = Brushes.White,
            Background = new SolidColorBrush(Color.FromArgb(0xCC, 0x37, 0x41, 0x51)),
            BorderThickness = new Thickness(0),
            Tag = index,
            Visibility = _service.FocusedTileIndex == index ? Visibility.Visible : Visibility.Collapsed
        };
        restore.Click += WallRestoreButton_Click;
        Panel.SetZIndex(restore, 3);
        Grid.SetRow(restore, 0);
        root.Children.Add(restore);
        _tileRestoreButtons[index] = restore;

        if (tile?.SourceId is not null)
        {
            // 与单路播放一致：软件帧 Image，避免 LibVLCSharp VideoView ForegroundWindow 黑屏
            var image = new System.Windows.Controls.Image
            {
                Tag = index,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch
            };
            Grid.SetRow(image, 1);
            root.Children.Add(image);
            var surface = new VideoWallFrameSurface(image);
            _tileSurfaces[index] = surface;
            AttachTileFrameSink(index);
        }
        else
        {
            var empty = new TextBlock
            {
                Text = Loc.T("Tool.VideoWall.EmptySlot"),
                Foreground = Brushes.Gray,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(8),
                IsHitTestVisible = false
            };
            Grid.SetRow(empty, 1);
            root.Children.Add(empty);
        }

        border.Child = root;
    }

    private string ResolveTileTitle(TileSession? tile)
    {
        if (tile?.SourceId is not { } sid || _service is null)
            return Loc.T("Tool.VideoWall.EmptySlot");
        return _service.Catalog.Sources.FirstOrDefault(s => s.Id == sid)?.Name ?? sid;
    }

    private void RefreshWallOverlays()
    {
        if (_service is null || _mode != PanelMode.Wall) return;
        ApplyWallFocus();
        var tiles = _service.Tiles;
        for (var i = 0; i < _service.Layout.TileCount; i++)
        {
            var tile = i < tiles.Count ? tiles[i] : null;
            var hasSource = tile?.SourceId is not null;
            var hasSurface = _tileSurfaces.ContainsKey(i);
            if (hasSource != hasSurface)
            {
                // 只修这一格，不动其它正在播的格
                PopulateTileCell(i, preserveOtherPlayers: true);
                continue;
            }

            if (_tileTitleBlocks.TryGetValue(i, out var title))
                title.Text = ResolveTileTitle(tile);
            if (_tileStatusBlocks.TryGetValue(i, out var status))
                status.Text = hasSource ? StatusTextFor(tile!.State) : "";
        }
    }

    private void AttachTileFrameSink(int index)
    {
        if (_service is null) return;
        if (!_tileSurfaces.TryGetValue(index, out var surface)) return;
        if (index >= _service.Tiles.Count) return;
        var tile = _service.Tiles[index];
        if (!tile.HasPlayer) return;
        try
        {
            tile.AttachFrameSink(surface);
        }
        catch (Exception ex)
        {
            ReportLog($"视频墙格{index}绑帧面失败: {ex.Message}");
        }
    }

    private void DetachAllTileSurfaces()
    {
        if (_service is not null)
        {
            foreach (var index in _tileSurfaces.Keys.ToList())
            {
                if (index < _service.Tiles.Count)
                {
                    try { _service.Tiles[index].AttachFrameSink(null); }
                    catch { /* ignore */ }
                }
            }
        }

        foreach (var surface in _tileSurfaces.Values)
        {
            try { surface.Clear(); } catch { /* ignore */ }
        }
    }

    private async Task BindTileInPlaceAsync(int index, string sourceId)
    {
        if (_service is null) return;
        // 先建播放器 → 铺软件帧面 → AttachFrameSink → Start（与单路出画路径一致）
        await _service.BindTileAsync(index, sourceId, autoStart: false).ConfigureAwait(true);
        PopulateTileCell(index, preserveOtherPlayers: true);
        AttachTileFrameSink(index);
        await _service.StartTileAsync(index).ConfigureAwait(true);
        if (_tileTitleBlocks.TryGetValue(index, out var title))
            title.Text = ResolveTileTitle(index < _service.Tiles.Count ? _service.Tiles[index] : null);
        if (_tileStatusBlocks.TryGetValue(index, out var status))
        {
            var tile = index < _service.Tiles.Count ? _service.Tiles[index] : null;
            status.Text = tile?.SourceId is null ? "" : StatusTextFor(tile.State);
        }
    }

    private void Tile_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_service is null) return;
        var index = ResolveTileIndex(sender);
        if (index < 0) return;

        // 单击忽略；有源双击进单路（画布由源列表自动铺格，不再双击添加）
        if (e.ClickCount < 2) return;
        e.Handled = true;

        var tile = index < _service.Tiles.Count ? _service.Tiles[index] : null;
        if (tile?.SourceId is { } id)
            BeginPlayback(id);
    }

    private void Tile_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        // 画布自动铺格：右键菜单绑源已取消
        e.Handled = true;
    }

    private static int ResolveTileIndex(object sender)
    {
        if (sender is FrameworkElement { Tag: int i }) return i;
        if (sender is FrameworkElement fe)
        {
            var border = fe as Border ?? FindAncestorBorder(fe);
            if (border?.Tag is int idx) return idx;
        }

        return -1;
    }

    private static Border? FindAncestorBorder(DependencyObject? d)
    {
        while (d != null)
        {
            if (d is Border b) return b;
            d = VisualTreeHelper.GetParent(d);
        }

        return null;
    }

    private void SourcesGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        var row = SelectedRow();
        if (row is not null)
            BeginPlayback(row.Id);
    }

    private void Card_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe)
        {
            SourcesGrid.SelectedItem = fe.DataContext;
            if (e.ClickCount >= 2 && fe.DataContext is SourceRowVm row)
            {
                BeginPlayback(row.Id);
                e.Handled = true;
            }
        }
    }

    private async void BeginPlayback(string sourceId)
    {
        // 画布进单路：停墙避免同路双开；返回时按轮播页重建
        if (_browseView == BrowseView.Canvas && _mode == PanelMode.Wall)
            await RecycleWallPlaybackAsync();

        SetMode(PanelMode.Playback);
        _ = Player.PlayAsync(sourceId, ResolvePlaybackTitle(sourceId));
    }

    private string ResolvePlaybackTitle(string sourceId)
    {
        var row = _rows.FirstOrDefault(r => r.Id == sourceId);
        if (!string.IsNullOrWhiteSpace(row?.Name))
            return row.Name;
        var src = _service?.Catalog.Sources.FirstOrDefault(s => s.Id == sourceId);
        if (!string.IsNullOrWhiteSpace(src?.Name))
            return src.Name;
        var address = src?.Endpoint.Address;
        if (!string.IsNullOrWhiteSpace(address))
        {
            try
            {
                var fileName = System.IO.Path.GetFileName(address);
                return string.IsNullOrWhiteSpace(fileName) ? address : fileName;
            }
            catch { return address; }
        }
        return sourceId;
    }
}
