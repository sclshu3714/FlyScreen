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
using LibVLCSharp.WPF;
using VlcMediaPlayerNative = LibVLCSharp.Shared.MediaPlayer;

namespace FlyScreen.Tool.VideoWall.Ui.Win;

public partial class VideoWallPanel : UserControl
{
    private const long SeekStepMs = 10_000;
    private const double SeekBarMax = 1000;

    private enum PanelMode { Sources, Wall, Playback }
    private enum BrowseView { Card, List, Canvas }

    private readonly IVideoWallService? _service;
    private readonly ObservableCollection<SourceRowVm> _rows = new();
    private readonly DispatcherTimer _progressTimer;
    private readonly Dictionary<int, VideoView> _tileViews = new();
    private readonly Dictionary<int, TextBlock> _tileTitleBlocks = new();
    private readonly Dictionary<int, TextBlock> _tileStatusBlocks = new();
    private readonly Dictionary<int, Border> _tileBorders = new();
    private PanelMode _mode = PanelMode.Sources;
    private BrowseView _browseView = BrowseView.List;
    private string? _pendingPlayId;
    private string _groupFilter = "";
    private bool _suppressCombo;
    private bool _suppressSegment;
    private bool _seekDragging;
    private bool _volumeDragging;
    private bool _fullscreen;
    private Window? _fullscreenWindow;
    private readonly DispatcherTimer _fsHideTimer;

    public Action<string>? StatusReporter { get; set; }

    public VideoWallPanel()
    {
        InitializeComponent();
        _service = ToolService.Instance.GetToolService<IVideoWallService>();
        SourcesGrid.ItemsSource = _rows;
        SourcesCards.ItemsSource = _rows;
        _progressTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _progressTimer.Tick += (_, _) => RefreshPlaybackProgress();
        _fsHideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _fsHideTimer.Tick += (_, _) => HideFullscreenChrome();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SearchBox.TextChanged += (_, _) => RefreshSources();
        GroupFilter.SelectionChanged += (_, _) =>
        {
            var sel = GroupFilter.SelectedItem as string;
            _groupFilter = string.Equals(sel, Loc.T("Tool.VideoWall.GroupAll"), StringComparison.Ordinal) ? "" : sel ?? "";
            RefreshSources();
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
        DeleteBtn.ToolTip = Loc.T("Tool.VideoWall.Delete");
        LayoutCombo.ToolTip = Loc.T("Tool.VideoWall.Layout");
        PlaybackBackBtn.ToolTip = Loc.T("Tool.VideoWall.Back");
        PlaybackSeekBackBtn.ToolTip = Loc.T("Tool.VideoWall.SeekBack");
        PlaybackSeekFwdBtn.ToolTip = Loc.T("Tool.VideoWall.SeekForward");
        PlaybackStopBtn.ToolTip = Loc.T("Tool.VideoWall.Stop");
        PlaybackPlayPauseBtn.ToolTip = Loc.T("Tool.VideoWall.Pause");
        PlaybackMuteBtn.ToolTip = Loc.T("Tool.VideoWall.Mute");
        PlaybackVolumeBar.ToolTip = Loc.T("Tool.VideoWall.Volume");
        PlaybackFullscreenBtn.ToolTip = Loc.T("Tool.VideoWall.Fullscreen");
        EmptyTitle.Text = Loc.T("Tool.VideoWall.EmptyTitle");
        EmptyHint.Text = Loc.T("Tool.VideoWall.EmptyHint");
        ColName.Header = Loc.T("Tool.VideoWall.ColName");
        ColKind.Header = Loc.T("Tool.VideoWall.ColKind");
        ColAddress.Header = Loc.T("Tool.VideoWall.Field.Address");
        ColGroup.Header = Loc.T("Tool.VideoWall.Field.Group");
        ColStatus.Header = Loc.T("Tool.VideoWall.ColStatus");
        ColActions.Header = Loc.T("Tool.VideoWall.ColActions");
        if (string.IsNullOrWhiteSpace(PlaybackTitle.Text))
            PlaybackTitle.Text = "";
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
            LayoutCombo.Visibility = _browseView == BrowseView.Canvas && browsing
                ? Visibility.Visible
                : Visibility.Collapsed;
            ApplySegmentIconColors();

            var edge = _service?.Layout.Rows ?? 3;
            for (var i = 0; i < LayoutCombo.Items.Count; i++)
            {
                if (LayoutCombo.Items[i] is ComboBoxItem { Tag: int tag } && tag == edge)
                {
                    LayoutCombo.SelectedIndex = i;
                    break;
                }
            }

            if (LayoutCombo.SelectedIndex < 0 && LayoutCombo.Items.Count > 0)
                LayoutCombo.SelectedIndex = Math.Min(2, LayoutCombo.Items.Count - 1);
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
        if (_service is null) return;
        _service.CatalogChanged += OnCatalogChanged;
        _service.SessionChanged += OnSessionChanged;
        _service.SinglePlayback.StatsChanged += OnStatsChanged;
        _service.SinglePlayback.StateChanged += OnPlaybackState;
        PopulateGroups();
        SyncCombosFromState();
        RefreshAll();
        FitSourcesColumns();
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
        _progressTimer.Stop();
        _fsHideTimer.Stop();
        ExitFullscreen(restoreUi: false);
        DetachPlayerView();
        if (_service is null) return;
        DetachAllTileViews();
        _service.CatalogChanged -= OnCatalogChanged;
        _service.SessionChanged -= OnSessionChanged;
        _service.SinglePlayback.StatsChanged -= OnStatsChanged;
        _service.SinglePlayback.StateChanged -= OnPlaybackState;
        _ = _service.SinglePlayback.StopAsync();
    }

    private void SourcesGrid_SizeChanged(object sender, SizeChangedEventArgs e)
        => FitSourcesColumns();

    /// <summary>地址列吃满剩余宽度，避免横向滚动条。</summary>
    private void FitSourcesColumns()
    {
        if (SourcesGrid.ActualWidth <= 1 || ColAddress is null) return;
        // 名称/类型/分组/状态/操作 + 纵向滚动条预留
        const double fixedCols = 120 + 88 + 80 + 64 + 108;
        var scroll = SystemParameters.VerticalScrollBarWidth + 16;
        var addressW = Math.Max(120, SourcesGrid.ActualWidth - scroll - fixedCols);
        if (Math.Abs(ColAddress.Width - addressW) > 0.5)
            ColAddress.Width = addressW;
    }

    private void OnCatalogChanged()
        => Dispatcher.BeginInvoke(() =>
        {
            RefreshSources();
            if (_mode == PanelMode.Wall)
                RefreshWallOverlays();
        });

    private void OnSessionChanged()
        => Dispatcher.BeginInvoke(() =>
        {
            if (_mode == PanelMode.Wall)
                RefreshWallOverlays();
        });

    private void OnStatsChanged(MediaStats stats)
        => Dispatcher.BeginInvoke(() =>
        {
            // 成功播放时的码率信息可写日志；界面不刷技术态
            if (stats.Width > 0)
                ReportLog(
                    $"视频墙播放中 {PlaybackTitle.Text}: {stats.Width}×{stats.Height} @ {stats.Fps:F1} fps");
        });

    private void OnPlaybackState(TilePlayState state)
        => Dispatcher.BeginInvoke(() =>
        {
            UpdateTransportChrome(state);
            if (state is TilePlayState.Playing or TilePlayState.Paused or TilePlayState.Buffering)
                _progressTimer.Start();
            else if (state is TilePlayState.Stopped or TilePlayState.Failed or TilePlayState.Idle)
                _progressTimer.Stop();

            if (state is TilePlayState.Failed or TilePlayState.Stalled)
            {
                var code = _service?.SinglePlayback.ErrorCode ?? VideoErrorCode.None;
                var title = string.IsNullOrWhiteSpace(PlaybackTitle.Text) ? "" : $"「{PlaybackTitle.Text}」";
                ReportLog(code == VideoErrorCode.None
                    ? $"视频墙播放失败{title}: {StatusTextFor(state)}"
                    : $"视频墙播放失败{title}: {StatusTextFor(state)} ({code})");
            }
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
        PlaybackPane.Visibility = mode == PanelMode.Playback ? Visibility.Visible : Visibility.Collapsed;
        if (mode != PanelMode.Playback)
            _progressTimer.Stop();
        if (mode == PanelMode.Wall)
            RebuildWallGrid();
        else if (mode == PanelMode.Sources)
            RefreshSources();
        else if (mode == PanelMode.Playback)
            UpdateTransportChrome(_service?.SinglePlayback.State ?? TilePlayState.Idle);
        SyncCombosFromState();
    }

    private async void ApplyBrowseView(BrowseView view)
    {
        if (_suppressSegment) return;
        var leavingCanvas = _browseView == BrowseView.Canvas && view != BrowseView.Canvas;
        _browseView = view;
        if (leavingCanvas)
            await RecycleWallPlaybackAsync();
        if (view == BrowseView.Canvas)
            SetMode(PanelMode.Wall);
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
        // 改布局：先拆 VideoView，再停旧格并重建空壳
        DetachAllTileViews();
        await _service.SetLayoutAsync(layout);
        if (_mode != PanelMode.Wall)
            SetMode(PanelMode.Wall);
        else
            RebuildWallGrid();
    }

    /// <summary>离开画布或改布局前：解绑 HWND 并停止全部格播放，避免残帧/占用。</summary>
    private async Task RecycleWallPlaybackAsync()
    {
        DetachAllTileViews();
        if (_service is not null)
        {
            try { await _service.StopAllTilesAsync(); }
            catch { /* ignore */ }
        }

        WallGrid.Children.Clear();
        _tileViews.Clear();
        _tileTitleBlocks.Clear();
        _tileStatusBlocks.Clear();
        _tileBorders.Clear();
    }

    private void AddBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_service is null) return;
        var dlg = new AddSourceDialog(_service, Window.GetWindow(this));
        if (dlg.ShowDialog() == true)
            RefreshAll();
    }

    private async void DeleteBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_service is null) return;
        var rows = SourcesGrid.SelectedItems.OfType<SourceRowVm>().ToList();
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
        if (dlg.ShowDialog() == true)
            RefreshAll();
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
        DetachAllTileViews();
        WallGrid.Children.Clear();
        _tileViews.Clear();
        _tileTitleBlocks.Clear();
        _tileStatusBlocks.Clear();
        _tileBorders.Clear();

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
    /// 只重建指定格的内容。绑定/清除时 MUST NOT 整墙重建，否则会拆掉其它格的 VideoView 导致停播。
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

        // 仅卸本格 VideoView
        if (_tileViews.TryGetValue(index, out var oldView))
        {
            try { oldView.MediaPlayer = null; } catch { /* ignore */ }
            _tileViews.Remove(index);
        }

        _tileTitleBlocks.Remove(index);
        _tileStatusBlocks.Remove(index);

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

        if (tile?.SourceId is not null)
        {
            var view = new VideoView
            {
                Tag = index,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch
            };
            Grid.SetRow(view, 1);
            root.Children.Add(view);
            _tileViews[index] = view;
            var captureIndex = index;
            view.Loaded += (_, _) => AttachTilePlayer(captureIndex);
        }
        else
        {
            var empty = new TextBlock
            {
                Text = Loc.T("Tool.VideoWall.EmptyTile"),
                Foreground = Brushes.White,
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
            return Loc.T("Tool.VideoWall.EmptyTile");
        return _service.Catalog.Sources.FirstOrDefault(s => s.Id == sid)?.Name ?? sid;
    }

    private void RefreshWallOverlays()
    {
        if (_service is null || _mode != PanelMode.Wall) return;
        var tiles = _service.Tiles;
        for (var i = 0; i < _service.Layout.TileCount; i++)
        {
            var tile = i < tiles.Count ? tiles[i] : null;
            var hasSource = tile?.SourceId is not null;
            var hasView = _tileViews.ContainsKey(i);
            if (hasSource != hasView)
            {
                // 只修这一格，不动其它正在播的格
                PopulateTileCell(i, preserveOtherPlayers: true);
                if (hasSource)
                    AttachTilePlayer(i);
                continue;
            }

            if (_tileTitleBlocks.TryGetValue(i, out var title))
                title.Text = ResolveTileTitle(tile);
            if (_tileStatusBlocks.TryGetValue(i, out var status))
                status.Text = hasSource ? StatusTextFor(tile!.State) : "";
        }
    }

    private void AttachTilePlayer(int index)
    {
        if (_service is null) return;
        if (!_tileViews.TryGetValue(index, out var view)) return;
        var tile = index < _service.Tiles.Count ? _service.Tiles[index] : null;
        if (tile?.NativePlaybackEngine is not VlcMediaPlayerNative mp)
        {
            try { view.MediaPlayer = null; } catch { /* ignore */ }
            return;
        }

        try
        {
            if (!ReferenceEquals(view.MediaPlayer, mp))
                view.MediaPlayer = mp;
        }
        catch { /* VideoView 可能正在重建 */ }
    }

    private void DetachAllTileViews()
    {
        foreach (var view in _tileViews.Values)
        {
            try { view.MediaPlayer = null; } catch { /* ignore */ }
        }
    }

    private async Task BindTileInPlaceAsync(int index, string sourceId)
    {
        if (_service is null) return;
        // 先建/换本格 VideoView，再 Start；禁止 RebuildWallGrid
        await _service.BindTileAsync(index, sourceId, autoStart: false).ConfigureAwait(true);
        PopulateTileCell(index, preserveOtherPlayers: true);
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        AttachTilePlayer(index);
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

        // 单击忽略；仅双击
        if (e.ClickCount < 2) return;
        e.Handled = true;

        var tile = index < _service.Tiles.Count ? _service.Tiles[index] : null;
        if (tile?.SourceId is { } id)
            BeginPlayback(id); // 有源：进单画面
        else
            ShowTileBindMenu(index, sender as FrameworkElement); // 空格：选源绑定
    }

    private void Tile_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_service is null) return;
        var index = ResolveTileIndex(sender);
        if (index < 0) return;
        e.Handled = true;
        ShowTileBindMenu(index, sender as FrameworkElement);
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

    private void ShowTileBindMenu(int index, FrameworkElement? placement)
    {
        if (_service is null) return;
        var menu = new ContextMenu();
        if (placement != null)
            menu.PlacementTarget = placement;

        if (_service.Catalog.Sources.Count == 0)
        {
            var empty = new MenuItem { Header = Loc.T("Tool.VideoWall.EmptyTitle"), IsEnabled = false };
            menu.Items.Add(empty);
        }
        else
        {
            foreach (var s in _service.Catalog.Sources)
            {
                var item = new MenuItem { Header = s.Name, Tag = (index, s.Id) };
                item.Click += async (_, _) =>
                {
                    if (item.Tag is not ValueTuple<int, string> t) return;
                    await BindTileInPlaceAsync(t.Item1, t.Item2);
                };
                menu.Items.Add(item);
            }
        }

        menu.Items.Add(new Separator());
        var clear = new MenuItem { Header = Loc.T("Tool.VideoWall.Stop") };
        clear.Click += async (_, _) =>
        {
            await _service.ClearTileAsync(index);
            PopulateTileCell(index, preserveOtherPlayers: true);
        };
        menu.Items.Add(clear);
        menu.IsOpen = true;
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

    private void BeginPlayback(string sourceId)
    {
        _pendingPlayId = sourceId;
        PlaybackTitle.Text = ResolvePlaybackTitle(sourceId);
        SetMode(PanelMode.Playback);
        ReportLog($"视频墙连接中: {PlaybackTitle.Text}");
        if (PlayerView.IsLoaded)
            _ = StartPlaybackAsync(sourceId);
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

    private async void PlayerView_Loaded(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(_pendingPlayId))
            await StartPlaybackAsync(_pendingPlayId);
    }

    private async Task StartPlaybackAsync(string sourceId)
    {
        if (_service is null) return;
        PlaybackTitle.Text = ResolvePlaybackTitle(sourceId);
        try
        {
            DetachPlayerView();
            // 不传窗口 HWND：须绑到 VideoView 子句柄，否则本机文件也会 Failed
            await _service.SinglePlayback.PlaySourceAsync(sourceId);
            AttachPlayerView();
            UpdateTransportChrome(_service.SinglePlayback.State);
            _progressTimer.Start();
            ReportLog($"视频墙播放: {PlaybackTitle.Text}");
        }
        catch (Exception ex)
        {
            DetachPlayerView();
            _progressTimer.Stop();
            if (string.IsNullOrWhiteSpace(PlaybackTitle.Text))
                PlaybackTitle.Text = ResolvePlaybackTitle(sourceId);
            ReportLog($"视频墙播放异常「{PlaybackTitle.Text}」: {ex.Message}");
        }
    }

    private void AttachPlayerView()
    {
        if (_service?.SinglePlayback.NativePlaybackEngine is VlcMediaPlayerNative mp)
            PlayerView.MediaPlayer = mp;
    }

    private void DetachPlayerView()
    {
        try { PlayerView.MediaPlayer = null; }
        catch { /* VideoView 可能已卸载 */ }
    }

    private async void PlaybackBackBtn_Click(object sender, RoutedEventArgs e)
    {
        ExitFullscreen();
        _progressTimer.Stop();
        DetachPlayerView();
        if (_service is not null)
            await _service.SinglePlayback.StopAsync();
        _pendingPlayId = null;
        PlaybackTitle.Text = "";
        SetMode(PanelMode.Sources);
    }

    private void PlaybackPlayPause_Click(object sender, RoutedEventArgs e)
    {
        if (_service is null) return;
        var state = _service.SinglePlayback.State;
        if (state == TilePlayState.Paused)
        {
            _service.SinglePlayback.Resume();
            UpdateTransportChrome(_service.SinglePlayback.State);
        }
        else if (state is TilePlayState.Stopped or TilePlayState.Idle or TilePlayState.Failed)
        {
            // Stop 会卸掉播放器实例，Resume 无效；需按上次源重新开播
            var id = _pendingPlayId ?? _service.SinglePlayback.CurrentSourceId;
            if (!string.IsNullOrWhiteSpace(id))
                _ = StartPlaybackAsync(id);
        }
        else
        {
            _service.SinglePlayback.Pause();
            UpdateTransportChrome(_service.SinglePlayback.State);
        }

        BumpFullscreenChrome();
    }

    private async void PlaybackStop_Click(object sender, RoutedEventArgs e)
    {
        if (_service is null) return;
        _progressTimer.Stop();
        DetachPlayerView();
        // 保留 _pendingPlayId，便于停止后再点播放重新开播
        await _service.SinglePlayback.StopAsync();
        ResetSeekUi();
        UpdateTransportChrome(TilePlayState.Stopped);
        BumpFullscreenChrome();
    }

    private void PlaybackSeekBack_Click(object sender, RoutedEventArgs e)
    {
        _service?.SinglePlayback.SeekBy(-SeekStepMs);
        RefreshPlaybackProgress();
        BumpFullscreenChrome();
    }

    private void PlaybackSeekFwd_Click(object sender, RoutedEventArgs e)
    {
        _service?.SinglePlayback.SeekBy(SeekStepMs);
        RefreshPlaybackProgress();
        BumpFullscreenChrome();
    }

    private void PlaybackMute_Click(object sender, RoutedEventArgs e)
    {
        if (_service is null) return;
        _service.SinglePlayback.ToggleMute();
        SyncVolumeUiFromService();
        BumpFullscreenChrome();
    }

    private void PlaybackVolumeBar_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        => _volumeDragging = true;

    private void PlaybackVolumeBar_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        ApplyVolumeFromBar();
        _volumeDragging = false;
        BumpFullscreenChrome();
    }

    private void PlaybackVolumeBar_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_volumeDragging) return;
        ApplyVolumeFromBar();
        BumpFullscreenChrome();
    }

    private void ApplyVolumeFromBar()
    {
        if (_service is null) return;
        _service.SinglePlayback.SetVolume((int)Math.Round(PlaybackVolumeBar.Value));
        UpdateMuteChrome();
    }

    private void PlaybackFullscreen_Click(object sender, RoutedEventArgs e)
    {
        if (_fullscreen) ExitFullscreen();
        else EnterFullscreen();
    }

    private void PlaybackSeekBar_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        => _seekDragging = true;

    private void PlaybackSeekBar_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_service is null) return;
        ApplySeekFromBar();
        _seekDragging = false;
        BumpFullscreenChrome();
    }

    private void PlaybackSeekBar_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_seekDragging || _service is null) return;
        var len = _service.SinglePlayback.LengthMs;
        if (len <= 0) return;
        var ms = (long)(e.NewValue / SeekBarMax * len);
        PlaybackTimeText.Text = FormatClock(ms);
    }

    private void ApplySeekFromBar()
    {
        if (_service is null) return;
        var len = _service.SinglePlayback.LengthMs;
        if (len <= 0 || !_service.SinglePlayback.CanSeek) return;
        var ms = (long)(PlaybackSeekBar.Value / SeekBarMax * len);
        _service.SinglePlayback.SeekTo(ms);
        RefreshPlaybackProgress();
    }

    private void RefreshPlaybackProgress()
    {
        if (_service is null || _seekDragging) return;
        var time = _service.SinglePlayback.TimeMs;
        var len = _service.SinglePlayback.LengthMs;
        PlaybackTimeText.Text = FormatClock(time);
        PlaybackLengthText.Text = len > 0 ? FormatClock(len) : "--:--";
        PlaybackSeekBar.IsEnabled = _service.SinglePlayback.CanSeek && len > 0;
        if (len > 0)
            PlaybackSeekBar.Value = Math.Clamp(time * SeekBarMax / len, 0, SeekBarMax);
        if (!_volumeDragging)
            SyncVolumeUiFromService();
    }

    private void ResetSeekUi()
    {
        PlaybackTimeText.Text = "00:00";
        PlaybackLengthText.Text = "00:00";
        PlaybackSeekBar.Value = 0;
    }

    private void UpdateTransportChrome(TilePlayState state)
    {
        var pausedOrStopped = state is TilePlayState.Paused or TilePlayState.Stopped
            or TilePlayState.Idle or TilePlayState.Failed;
        PlaybackPlayPauseIcon.Data = pausedOrStopped
            ? (Geometry)FindResource("GeoPlay")
            : (Geometry)FindResource("GeoPause");
        PlaybackPlayPauseBtn.ToolTip = pausedOrStopped
            ? Loc.T(state == TilePlayState.Paused ? "Tool.VideoWall.Resume" : "Tool.VideoWall.Play")
            : Loc.T("Tool.VideoWall.Pause");
        UpdateMuteChrome();
        SyncVolumeUiFromService();
        RefreshPlaybackProgress();
        UpdateFullscreenChrome();
    }

    private void SyncVolumeUiFromService()
    {
        if (_service is null || _volumeDragging) return;
        var vol = _service.SinglePlayback.Volume;
        if (Math.Abs(PlaybackVolumeBar.Value - vol) > 0.5)
            PlaybackVolumeBar.Value = vol;
        UpdateMuteChrome();
    }

    private void UpdateMuteChrome()
    {
        var muted = _service?.SinglePlayback.IsMuted == true;
        PlaybackMuteIcon.Data = muted
            ? (Geometry)FindResource("GeoMute")
            : (Geometry)FindResource("GeoVolume");
        PlaybackMuteBtn.ToolTip = Loc.T(muted ? "Tool.VideoWall.Unmute" : "Tool.VideoWall.Mute");
    }

    private void UpdateFullscreenChrome()
    {
        PlaybackFullscreenIcon.Data = _fullscreen
            ? (Geometry)FindResource("GeoFullscreenExit")
            : (Geometry)FindResource("GeoFullscreen");
        PlaybackFullscreenBtn.ToolTip = Loc.T(
            _fullscreen ? "Tool.VideoWall.FullscreenExit" : "Tool.VideoWall.Fullscreen");
    }

    private void EnterFullscreen()
    {
        if (_fullscreen || _fullscreenWindow != null) return;

        var owner = Window.GetWindow(this);
        var win = new Window
        {
            Owner = owner,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            WindowState = WindowState.Maximized,
            Background = Brushes.Black,
            Topmost = true,
            ShowInTaskbar = false,
            Title = string.IsNullOrWhiteSpace(PlaybackTitle.Text)
                ? Loc.T("Tool.VideoWall.Name")
                : PlaybackTitle.Text
        };

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        PlaybackVideoSlot.Child = null;
        PlaybackTransportSlot.Child = null;
        PlaybackVideoHost.CornerRadius = new CornerRadius(0);
        PlaybackVideoHost.Margin = new Thickness(0);
        PlaybackTransportBar.CornerRadius = new CornerRadius(0);
        PlaybackTransportBar.BorderThickness = new Thickness(0);
        PlaybackTransportBar.Background = new SolidColorBrush(Color.FromArgb(0xCC, 0x11, 0x18, 0x27));

        Grid.SetRow(PlaybackVideoHost, 0);
        Grid.SetRow(PlaybackTransportBar, 1);
        root.Children.Add(PlaybackVideoHost);
        root.Children.Add(PlaybackTransportBar);
        win.Content = root;

        win.PreviewKeyDown += FullscreenWindow_PreviewKeyDown;
        win.MouseMove += FullscreenWindow_MouseMove;
        win.Closed += (_, _) =>
        {
            if (_fullscreenWindow == win)
                ExitFullscreen();
        };

        _fullscreenWindow = win;
        _fullscreen = true;
        UpdateFullscreenChrome();
        // 换父级后 VideoView HWND 会重建，需重新绑定 MediaPlayer
        DetachPlayerView();
        win.Show();
        win.Activate();
        AttachPlayerView();
        BumpFullscreenChrome();
    }

    private void ExitFullscreen(bool restoreUi = true)
    {
        _fsHideTimer.Stop();
        var win = _fullscreenWindow;
        _fullscreenWindow = null;
        _fullscreen = false;

        if (win != null)
        {
            win.PreviewKeyDown -= FullscreenWindow_PreviewKeyDown;
            win.MouseMove -= FullscreenWindow_MouseMove;
            if (win.Content is Grid g)
                g.Children.Clear();
            try { win.Close(); } catch { /* ignore */ }
        }

        if (restoreUi)
        {
            PlaybackVideoHost.CornerRadius = new CornerRadius(8);
            PlaybackVideoHost.Margin = new Thickness(0);
            PlaybackTransportBar.CornerRadius = new CornerRadius(8);
            PlaybackTransportBar.BorderThickness = new Thickness(1);
            PlaybackTransportBar.Background = (Brush)FindResource("CardBg");
            PlaybackTransportBar.Visibility = Visibility.Visible;
            PlaybackVideoSlot.Child = PlaybackVideoHost;
            PlaybackTransportSlot.Child = PlaybackTransportBar;
            Cursor = Cursors.Arrow;
            DetachPlayerView();
            AttachPlayerView();
        }

        UpdateFullscreenChrome();
    }

    private void FullscreenWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            ExitFullscreen();
            e.Handled = true;
        }
        else
        {
            BumpFullscreenChrome();
        }
    }

    private void FullscreenWindow_MouseMove(object sender, MouseEventArgs e)
        => BumpFullscreenChrome();

    private void BumpFullscreenChrome()
    {
        if (!_fullscreen || _fullscreenWindow == null) return;
        PlaybackTransportBar.Visibility = Visibility.Visible;
        _fullscreenWindow.Cursor = Cursors.Arrow;
        _fsHideTimer.Stop();
        _fsHideTimer.Start();
    }

    private void HideFullscreenChrome()
    {
        _fsHideTimer.Stop();
        if (!_fullscreen || _fullscreenWindow == null) return;
        PlaybackTransportBar.Visibility = Visibility.Collapsed;
        _fullscreenWindow.Cursor = Cursors.None;
    }

    private static string FormatClock(long ms)
    {
        if (ms < 0) ms = 0;
        var t = TimeSpan.FromMilliseconds(ms);
        return t.TotalHours >= 1
            ? $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}"
            : $"{t.Minutes:00}:{t.Seconds:00}";
    }
}
