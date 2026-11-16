using System.Collections.ObjectModel;
using FlyScreen.Helpers.Localization;
using FlyScreen.Services.Tools;
using FlyScreen.Tool.VideoWall.Contracts;
using FlyScreen.Tool.VideoWall.Sources;
using FlyScreen.Tool.VideoWall.Wall;
using FlyScreen.ToolSdk;
using Microsoft.Maui.Controls.Shapes;
using Grid = Microsoft.Maui.Controls.Grid;
using Path = Microsoft.Maui.Controls.Shapes.Path;

namespace FlyScreen.Tool.VideoWall.Ui.Maui;

/// <summary>Maui 视频墙：对齐 Win 结构（源浏览 / 画布 / 单路回放）；Maui-Win 用 Win32 子 HWND 嵌入 LibVLC。</summary>
public sealed class VideoWallPanel : ContentView
{
    private static readonly int[] LayoutEdges = [1, 2, 3, 4];

    private enum PanelMode { Sources, Wall, Playback }
    private enum BrowseView { Card, List, Canvas }

    private readonly IVideoWallService? _service;
    private readonly ObservableCollection<SourceRowVm> _rows = new();
    private readonly Color _text;
    private readonly Color _muted;
    private readonly Color _line;
    private readonly Color _card;
    private readonly Color _blue;
    private readonly Color _warn;
    private readonly Color _listBg;

    private readonly Border _mainToolbar;
    private readonly Border _viewCardBtn;
    private readonly Border _viewListBtn;
    private readonly Border _viewCanvasBtn;
    private readonly Path _viewCardIcon;
    private readonly Path _viewListIcon;
    private readonly Path _viewCanvasIcon;
    private readonly VideoWallDropDown _layoutDrop;
    private readonly HorizontalStackLayout _carouselPanel;
    private readonly CheckBox _carouselToggle;
    private readonly Label _carouselPageText;
    private readonly VideoWallDropDown _groupDrop;
    private readonly Entry _search;
    private readonly Border _deleteBtn;
    private readonly IDispatcherTimer _carouselTimer;

    private readonly Grid _contentHost;
    private readonly Grid _sourcesPane;
    private readonly CollectionView _listView;
    private readonly CollectionView _cardsView;
    private readonly VerticalStackLayout _emptyState;
    private readonly Grid _wallGrid;
    private readonly VideoWallPlayerControl _player;
    private readonly Grid _mainPane;

    private readonly AbsoluteLayout _root;
    private readonly Border _dialogCard;
    private readonly Label _dialogTitle;
    private readonly VideoWallSourceForm _sourceForm;
    private readonly Label _formError;
    private readonly VideoWallFlyoutHost _flyout;

    private readonly Dictionary<int, Border> _tileBorders = new();
    private readonly Dictionary<int, Label> _tileTitleBlocks = new();
    private readonly Dictionary<int, Label> _tileStatusBlocks = new();
    private readonly Dictionary<int, Button> _tileRestoreButtons = new();
#if WINDOWS
    private readonly Dictionary<int, VideoWallHwndSurface> _tileSurfaces = new();
#endif

    private PanelMode _mode = PanelMode.Sources;
    private BrowseView _browseView = BrowseView.List;
    private string _groupFilter = "";
    private string? _editingId;
    private bool _suppressLayout;
    private bool _suppressCarouselToggle;

    public Action<string>? StatusReporter
    {
        get => _statusReporter;
        set
        {
            _statusReporter = value;
            _player.StatusReporter = value;
        }
    }

    private Action<string>? _statusReporter;

    public VideoWallPanel()
    {
        _service = ToolService.Instance.GetToolService<IVideoWallService>();

        var dark = Application.Current?.RequestedTheme == AppTheme.Dark;
        _text = Color.FromArgb(dark ? "#E2E8F0" : "#111827");
        _muted = Color.FromArgb(dark ? "#94A3B8" : "#6B7280");
        _line = Color.FromArgb(dark ? "#334155" : "#E5E7EB");
        _card = Color.FromArgb(dark ? "#1E293B" : "#FFFFFF");
        _listBg = Color.FromArgb(dark ? "#0F172A" : "#F9FAFB");
        _blue = Color.FromArgb("#2563EB");
        _warn = Color.FromArgb("#DC2626");

        _search = new Entry
        {
            Placeholder = Loc.T("Tool.VideoWall.Search"),
            FontSize = 13,
            HorizontalOptions = LayoutOptions.Fill,
            BackgroundColor = _card,
            TextColor = _text
        };
        _search.TextChanged += (_, _) =>
        {
            RefreshSources();
            if (_mode == PanelMode.Wall)
                _ = RefreshCanvasAsync();
        };

        _carouselTimer = (Application.Current?.Dispatcher ?? throw new InvalidOperationException("No dispatcher"))
            .CreateTimer();
        _carouselTimer.Interval = TimeSpan.FromMilliseconds(WallCarousel.DefaultIntervalMs);
        _carouselTimer.Tick += OnCarouselTimerTick;

        var addPair = VideoWallMauiIcons.CreateIconButton(
            VideoWallMauiIcons.Geo.Add, Colors.White, Loc.T("Tool.VideoWall.Add"),
            VideoWallMauiIcons.ToolbarKind.Primary, _text, _card, _line, _blue, _warn);
        VideoWallMauiIcons.WireClicked(addPair.Host, (_, _) => OpenDialog(edit: null));

        var deletePair = VideoWallMauiIcons.CreateIconButton(
            VideoWallMauiIcons.Geo.Delete, _warn, Loc.T("Tool.VideoWall.Delete"),
            VideoWallMauiIcons.ToolbarKind.Danger, _text, _card, _line, _blue, _warn,
            margin: new Thickness(0, 0, 8, 0));
        _deleteBtn = deletePair.Host;
        _deleteBtn.IsEnabled = false;
        VideoWallMauiIcons.WireClicked(_deleteBtn, async (_, _) => await DeleteSelectedAsync());

        var cardPair = VideoWallMauiIcons.CreateSegmentIconButton(
            VideoWallMauiIcons.Geo.ViewCard, Loc.T("Tool.VideoWall.ViewCard"),
            VideoWallMauiIcons.SegmentCorner.Left, _text, _card, _line);
        _viewCardBtn = cardPair.Host;
        _viewCardIcon = cardPair.Icon;
        VideoWallMauiIcons.WireClicked(_viewCardBtn, (_, _) => ApplyBrowseView(BrowseView.Card));

        var listPair = VideoWallMauiIcons.CreateSegmentIconButton(
            VideoWallMauiIcons.Geo.ViewList, Loc.T("Tool.VideoWall.ViewList"),
            VideoWallMauiIcons.SegmentCorner.Middle, _text, _card, _line);
        _viewListBtn = listPair.Host;
        _viewListIcon = listPair.Icon;
        VideoWallMauiIcons.WireClicked(_viewListBtn, (_, _) => ApplyBrowseView(BrowseView.List));

        var canvasPair = VideoWallMauiIcons.CreateSegmentIconButton(
            VideoWallMauiIcons.Geo.ViewCanvas, Loc.T("Tool.VideoWall.ViewCanvas"),
            VideoWallMauiIcons.SegmentCorner.Right, _text, _card, _line);
        _viewCanvasBtn = canvasPair.Host;
        _viewCanvasIcon = canvasPair.Icon;
        VideoWallMauiIcons.WireClicked(_viewCanvasBtn, (_, _) => ApplyBrowseView(BrowseView.Canvas));

        _layoutDrop = new VideoWallDropDown(_text, _muted, _line, _card);
        _layoutDrop.SetOptions(["1×1", "2×2", "3×3", "4×4"]);
        _layoutDrop.SelectedIndex = 1; // 默认 2×2
        _layoutDrop.IsVisible = false;
        _layoutDrop.SelectionChanged += async _ => await OnLayoutChangedAsync();

        _carouselToggle = new CheckBox
        {
            VerticalOptions = LayoutOptions.Center,
            Margin = new Thickness(0, 0, 4, 0)
        };
        var carouselLabel = new Label
        {
            Text = Loc.T("Tool.VideoWall.Carousel"),
            TextColor = _text,
            FontSize = 13,
            VerticalOptions = LayoutOptions.Center,
            VerticalTextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0)
        };
        carouselLabel.GestureRecognizers.Add(new TapGestureRecognizer
        {
            Command = new Command(() => _carouselToggle.IsChecked = !_carouselToggle.IsChecked)
        });
        ToolTipProperties.SetText(_carouselToggle, Loc.T("Tool.VideoWall.CarouselHint"));
        ToolTipProperties.SetText(carouselLabel, Loc.T("Tool.VideoWall.CarouselHint"));
        _carouselToggle.CheckedChanged += async (_, _) => await OnCarouselToggleChangedAsync();

        _carouselPageText = new Label
        {
            TextColor = _muted,
            FontSize = 12,
            VerticalOptions = LayoutOptions.Center,
            VerticalTextAlignment = TextAlignment.Center
        };

        _carouselPanel = new HorizontalStackLayout
        {
            Spacing = 0,
            VerticalOptions = LayoutOptions.Center,
            IsVisible = false,
            Margin = new Thickness(0, 0, 8, 0),
            Children = { _carouselToggle, carouselLabel, _carouselPageText }
        };

        _groupDrop = new VideoWallDropDown(_text, _muted, _line, _card);
        _groupDrop.SelectedIndex = 0;
        _groupDrop.SelectionChanged += __ =>
        {
            var v = _groupDrop.Value;
            _groupFilter = string.Equals(v, Loc.T("Tool.VideoWall.GroupAll"), StringComparison.Ordinal) ? "" : v;
            RefreshSources();
            if (_mode == PanelMode.Wall)
                _ = RefreshCanvasAsync();
        };

        _mainToolbar = new Border
        {
            Stroke = _line,
            BackgroundColor = _card,
            Padding = new Thickness(10, 8),
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 8 },
            Content = new Grid
            {
                ColumnDefinitions =
                [
                    new ColumnDefinition(GridLength.Auto),
                    new ColumnDefinition(GridLength.Auto),
                    new ColumnDefinition(GridLength.Auto),
                    new ColumnDefinition(GridLength.Auto),
                    new ColumnDefinition(GridLength.Auto),
                    new ColumnDefinition(GridLength.Auto),
                    new ColumnDefinition(GridLength.Star)
                ],
                ColumnSpacing = 6,
                Children =
                {
                    addPair.Host,
                    _deleteBtn.Also(v => Grid.SetColumn(v, 1)),
                    new HorizontalStackLayout
                    {
                        Spacing = 0,
                        Children = { _viewCardBtn, _viewListBtn, _viewCanvasBtn }
                    }.Also(v => Grid.SetColumn(v, 2)),
                    _layoutDrop.Also(v => Grid.SetColumn(v, 3)),
                    _carouselPanel.Also(v => Grid.SetColumn(v, 4)),
                    _groupDrop.Also(v => Grid.SetColumn(v, 5)),
                    _search.Also(v => Grid.SetColumn(v, 6))
                }
            }
        };

        _listView = new CollectionView
        {
            ItemsSource = _rows,
            SelectionMode = SelectionMode.Multiple,
            ItemTemplate = new DataTemplate(BuildListRow),
            Header = BuildListHeader(),
            BackgroundColor = _listBg
        };
        _listView.SelectionChanged += (_, _) => _deleteBtn.IsEnabled = _listView.SelectedItems.Count > 0;

        _cardsView = new CollectionView
        {
            ItemsSource = _rows,
            ItemTemplate = new DataTemplate(BuildCard),
            // 定宽卡片 + 按容器宽度算列数（勿写死 Span=2，否则全屏只两列被拉宽）
            ItemsLayout = CreateCardItemsLayout(span: 3),
            IsVisible = false,
            BackgroundColor = _listBg
        };
        _cardsView.SizeChanged += (_, _) => UpdateCardItemsLayout();

        _emptyState = new VerticalStackLayout
        {
            IsVisible = false,
            VerticalOptions = LayoutOptions.Center,
            HorizontalOptions = LayoutOptions.Center,
            Spacing = 8,
                Children =
                {
                new Label
                {
                    Text = Loc.T("Tool.VideoWall.EmptyTitle"),
                    FontSize = 15,
                    FontAttributes = FontAttributes.Bold,
                    TextColor = _text,
                    HorizontalTextAlignment = TextAlignment.Center
                },
                new Label
                {
                    Text = Loc.T("Tool.VideoWall.EmptyHint"),
                    FontSize = 13,
                    TextColor = _muted,
                    HorizontalTextAlignment = TextAlignment.Center,
                    LineBreakMode = LineBreakMode.WordWrap,
                    MaximumWidthRequest = 360
                }
            }
        };

        var sourcesBody = new Grid
        {
            Children =
            {
                _listView,
                _cardsView,
                _emptyState
            }
        };

        _sourcesPane = new Grid
        {
            Children =
            {
                new Border
                {
                    Stroke = _line,
                    BackgroundColor = _listBg,
                    StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 8 },
                    Content = sourcesBody
                }
            }
        };

        _wallGrid = new Grid
        {
            IsVisible = false,
            RowSpacing = 4,
            ColumnSpacing = 4,
            BackgroundColor = Colors.Black
        };

        _player = new VideoWallPlayerControl { IsVisible = false };
        _player.Closed += () => _ = OnPlayerClosedAsync();
        _player.RequestInAppMaximize += () => _ = OnPlayerRequestInAppMaximizeAsync();

        _contentHost = new Grid
        {
            Children = { _sourcesPane, _wallGrid, _player }
        };

        _mainPane = new Grid
        {
            Padding = 8,
            RowDefinitions = [new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Star)],
            Children =
            {
                _mainToolbar,
                _contentHost.Also(v => Grid.SetRow(v, 1))
            }
        };
        var main = _mainPane;

        // 浮层必须挂在根上：原先塞进 dialogHost，主界面关闭对话框时整层不可见，布局/分组下拉无法打开
        var flyoutLayer = new AbsoluteLayout { IsVisible = false, ZIndex = 400 };
        _flyout = new VideoWallFlyoutHost(flyoutLayer);

        _sourceForm = new VideoWallSourceForm(
            _flyout,
            () => _service?.Catalog.Groups.Select(g => g.Name).ToList()
                  ?? (IReadOnlyList<string>)Array.Empty<string>(),
            _text, _muted, _line, _card);
        _formError = new Label { TextColor = _warn, FontSize = 12, IsVisible = false };
        _dialogTitle = new Label { FontAttributes = FontAttributes.Bold, FontSize = 16, TextColor = _text };

        var saveBtn = MakeBtn(Loc.T("Tool.VideoWall.Save"), _blue, Colors.White);
        saveBtn.Clicked += async (_, _) => await SaveDialogAsync();
        var cancelBtn = MakeBtn(Loc.T("Tool.VideoWall.Cancel"), _card, _text, _line);
        cancelBtn.Clicked += (_, _) => CloseDialog();

        // WinUI 滚动条常叠在内容上；右侧留白避免挡住「…」等尾部控件
        _sourceForm.Padding = new Thickness(0, 0, 20, 4);
        var formScroll = new ScrollView
        {
            Content = _sourceForm,
            MaximumHeightRequest = 360,
            Padding = new Thickness(0, 0, 4, 0),
            VerticalScrollBarVisibility = ScrollBarVisibility.Default
        };

        _dialogCard = new Border
        {
            Stroke = _line,
            BackgroundColor = _card,
            Padding = 16,
            WidthRequest = 480,
            MaximumWidthRequest = 560,
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center,
            Margin = new Thickness(24),
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 12 },
            Shadow = new Shadow { Brush = Colors.Black, Offset = new Point(0, 8), Radius = 24, Opacity = 0.22f },
            Content = new Grid
        {
            RowDefinitions =
            [
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Star),
                new RowDefinition(GridLength.Auto)
            ],
            RowSpacing = 10,
            Children =
            {
                _dialogTitle,
                formScroll.Also(v => Grid.SetRow(v, 1)),
                    new VerticalStackLayout
                    {
                        Spacing = 8,
                        Children =
                        {
                            _formError,
                            new HorizontalStackLayout
                            {
                                Spacing = 8,
                                HorizontalOptions = LayoutOptions.End,
                                Children = { cancelBtn, saveBtn }
                            }
                        }
                    }.Also(v => Grid.SetRow(v, 2))
                }
            }
        };
        _dialogCard.GestureRecognizers.Add(new TapGestureRecognizer());

        var dim = new BoxView { Color = Color.FromArgb("#80000000") };
        dim.GestureRecognizers.Add(new TapGestureRecognizer { Command = new Command(CloseDialog) });

        var dialogHost = new Grid
        {
            IsVisible = false,
            ClassId = "dialogHost",
            Children = { dim, _dialogCard }
        };
        dialogHost.SizeChanged += (_, _) =>
        {
            var max = Math.Max(280, dialogHost.Height - 48);
            _dialogCard.MaximumHeightRequest = Math.Min(560, max);
        };

        _layoutDrop.BindFlyout(_flyout);
        _groupDrop.BindFlyout(_flyout);

        _root = new AbsoluteLayout();
        AbsoluteLayout.SetLayoutBounds(main, new Rect(0, 0, 1, 1));
        AbsoluteLayout.SetLayoutFlags(main, Microsoft.Maui.Layouts.AbsoluteLayoutFlags.All);
        AbsoluteLayout.SetLayoutBounds(dialogHost, new Rect(0, 0, 1, 1));
        AbsoluteLayout.SetLayoutFlags(dialogHost, Microsoft.Maui.Layouts.AbsoluteLayoutFlags.All);
        AbsoluteLayout.SetLayoutBounds(flyoutLayer, new Rect(0, 0, 1, 1));
        AbsoluteLayout.SetLayoutFlags(flyoutLayer, Microsoft.Maui.Layouts.AbsoluteLayoutFlags.All);
        _root.Children.Add(main);
        _root.Children.Add(dialogHost);
        _root.Children.Add(flyoutLayer);

        Content = _root;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;

        if (_service is null)
            ReportLog(Loc.T("Tool.VideoWall.BackendUnavailable"));
        else if (!_service.Backend.IsAvailable)
            ReportLog(Loc.T("Tool.VideoWall.BackendUnavailable"));

        SyncBrowseChrome();
        RefreshSources();
    }

    private void OnLoaded(object? sender, EventArgs e)
    {
        if (_service is null) return;
        _service.CatalogChanged += OnCatalogChanged;
        _service.SessionChanged += OnSessionChanged;
        _player.AttachService(_service);
        PopulateGroups();
        SyncLayoutFromService();
        SyncBrowseChrome();
        RefreshAll();
    }

    private void OnUnloaded(object? sender, EventArgs e)
    {
        _carouselTimer.Stop();
        _player.AttachService(null);
        _ = _player.StopAsync();
#if WINDOWS
        DetachAllTileSurfaces();
#endif
        if (_service is null) return;
        _service.CatalogChanged -= OnCatalogChanged;
        _service.SessionChanged -= OnSessionChanged;
    }

    private void OnCatalogChanged()
        => MainThread.BeginInvokeOnMainThread(() =>
        {
            RefreshSources();
            PopulateGroups();
            if (_mode == PanelMode.Wall)
                _ = RefreshCanvasAsync();
        });

    private void OnSessionChanged()
        => MainThread.BeginInvokeOnMainThread(() =>
        {
            if (_mode == PanelMode.Wall)
                RefreshWallOverlays();
        });

    private void ReportLog(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        StatusReporter?.Invoke(message);
    }

    private void PopulateGroups()
    {
        if (_service is null) return;
        var options = new List<string> { Loc.T("Tool.VideoWall.GroupAll") };
        options.AddRange(_service.Catalog.Groups.Select(g => g.Name));
        _groupDrop.SetOptions(options.ToArray());
        if (_groupDrop.SelectedIndex < 0)
            _groupDrop.SelectedIndex = 0;
    }

    private void SyncLayoutFromService()
    {
        if (_service is null) return;
        _suppressLayout = true;
        try
        {
            var edge = _service.Layout.Rows;
            var idx = Array.IndexOf(LayoutEdges, edge);
            _layoutDrop.SelectedIndex = idx >= 0 ? idx : 1;
        }
        finally { _suppressLayout = false; }
    }

    private void RefreshAll()
    {
        RefreshSources();
        if (_mode == PanelMode.Wall)
            _ = RefreshCanvasAsync();
        SyncBrowseChrome();
    }

    private void RefreshSources()
    {
        if (_service is null) return;
        var q = (_search.Text ?? "").Trim();
        _rows.Clear();
        foreach (var s in _service.Catalog.Sources)
        {
            if (!string.IsNullOrEmpty(_groupFilter) && !MatchesGroup(s, _groupFilter))
                continue;
            if (q.Length > 0 &&
                !s.Name.Contains(q, StringComparison.OrdinalIgnoreCase) &&
                !s.Endpoint.Address.Contains(q, StringComparison.OrdinalIgnoreCase))
                continue;

            _rows.Add(ToRow(s));
        }

        var empty = _rows.Count == 0;
        _emptyState.IsVisible = empty && _mode == PanelMode.Sources;
        _listView.IsVisible = _browseView == BrowseView.List && !empty;
        _cardsView.IsVisible = _browseView == BrowseView.Card && !empty;
    }

    private static bool MatchesGroup(VideoSourceDescriptor s, string groupName)
        => string.Equals(s.GroupId, groupName, StringComparison.OrdinalIgnoreCase);

    private SourceRowVm ToRow(VideoSourceDescriptor s) => new()
    {
        Id = s.Id,
        Name = s.Name,
        KindText = KindLabel(s.Kind),
        AddressRedacted = VideoEndpointParser.RedactAddress(s.Endpoint.Address),
        StatusText = ValidationLabel(s.ValidationState)
    };

    private void SetMode(PanelMode mode)
    {
        _mode = mode;
        _mainToolbar.IsVisible = mode != PanelMode.Playback;
        _sourcesPane.IsVisible = mode == PanelMode.Sources;
        _wallGrid.IsVisible = mode == PanelMode.Wall;
        _player.IsVisible = mode == PanelMode.Playback;
        if (mode != PanelMode.Playback)
            _ = _player.StopAsync();
        _mainPane.Padding = mode == PanelMode.Playback ? new Thickness(0) : new Thickness(8);
        if (mode == PanelMode.Wall)
            RebuildWallGrid();
        else if (mode == PanelMode.Sources)
            RefreshSources();

        SyncBrowseChrome();
    }

    private async void ApplyBrowseView(BrowseView view)
    {
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

    /// <summary>单路返回：画布模式回到墙并按轮播页重建；否则回到列表/卡片。</summary>
    private async Task OnPlayerClosedAsync()
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

    private void SyncBrowseChrome()
    {
        var browsing = _mode is PanelMode.Sources or PanelMode.Wall;
        _viewCardBtn.IsEnabled = browsing;
        _viewListBtn.IsEnabled = browsing;
        _viewCanvasBtn.IsEnabled = browsing;
        VideoWallMauiIcons.SetSegmentActive(_viewCardBtn, _viewCardIcon, browsing && _browseView == BrowseView.Card, _blue, _muted, _card, _line);
        VideoWallMauiIcons.SetSegmentActive(_viewListBtn, _viewListIcon, browsing && _browseView == BrowseView.List, _blue, _muted, _card, _line);
        VideoWallMauiIcons.SetSegmentActive(_viewCanvasBtn, _viewCanvasIcon, browsing && _browseView == BrowseView.Canvas, _blue, _muted, _card, _line);
        var canvasChrome = browsing && _browseView == BrowseView.Canvas;
        _layoutDrop.IsVisible = canvasChrome;
        _carouselPanel.IsVisible = canvasChrome;
        _suppressCarouselToggle = true;
        try { _carouselToggle.IsChecked = _service?.CarouselEnabled == true; }
        finally { _suppressCarouselToggle = false; }
        UpdateCarouselChrome();
        _layoutDrop.BindFlyout(_flyout);
        _groupDrop.BindFlyout(_flyout);
    }

    private async Task OnLayoutChangedAsync()
    {
        if (_suppressLayout || _service is null || _browseView != BrowseView.Canvas) return;
        var idx = _layoutDrop.SelectedIndex;
        if (idx < 0 || idx >= LayoutEdges.Length) return;
        var layout = WallLayout.FromEdge(LayoutEdges[idx]);
        if (layout is null) return;
#if WINDOWS
        DetachAllTileSurfaces();
#endif
        await _service.SetLayoutAsync(layout);
        if (_mode != PanelMode.Wall)
            SetMode(PanelMode.Wall);
        else
            RebuildWallGrid();
        await RefreshCanvasAsync();
    }

    private async Task RecycleWallPlaybackAsync()
    {
        _carouselTimer.Stop();
#if WINDOWS
        DetachAllTileSurfaces();
#endif
        if (_service is not null)
        {
            try { await _service.StopAllTilesAsync(); }
            catch { /* ignore */ }
        }

        _wallGrid.Children.Clear();
        _wallGrid.RowDefinitions.Clear();
        _wallGrid.ColumnDefinitions.Clear();
        _tileBorders.Clear();
        _tileTitleBlocks.Clear();
        _tileStatusBlocks.Clear();
        _tileRestoreButtons.Clear();
#if WINDOWS
        _tileSurfaces.Clear();
#endif
    }

    private async void OnCarouselTimerTick(object? sender, EventArgs e)
    {
        if (_service is null || _mode != PanelMode.Wall || !_service.CarouselEnabled) return;
        await _service.AdvanceCarouselAsync();
        await PresentCarouselPageAsync();
        UpdateCarouselChrome();
    }

    private async Task OnCarouselToggleChangedAsync()
    {
        if (_suppressCarouselToggle || _service is null || _mode != PanelMode.Wall) return;
        await _service.SetCarouselEnabledAsync(_carouselToggle.IsChecked == true);
        await PresentCarouselPageAsync();
        UpdateCarouselChrome();
        SyncCarouselTimer();
    }

    /// <summary>按当前分组/搜索同步源列表并铺当前轮播页。</summary>
    private async Task RefreshCanvasAsync()
    {
        if (_service is null || _mode != PanelMode.Wall) return;
        _service.SetCarouselSources(_rows.Select(r => r.Id).ToList());
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
#if WINDOWS
            if (!_tileSurfaces.TryGetValue(i, out var surface))
                continue;
            var hwnd = await WaitForSurfaceHwndAsync(surface).ConfigureAwait(true);
            if (hwnd == IntPtr.Zero)
            {
                ReportLog($"视频墙格{i}: HWND 未就绪，跳过开播以免弹出独立窗口");
                continue;
            }

            tile.SetRenderTarget(hwnd);
#endif
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
            _carouselPageText.Text = "";
            return;
        }

        _carouselPageText.Text = string.Format(
            Loc.T("Tool.VideoWall.CarouselPage"),
            _service.CarouselPage + 1,
            Math.Max(1, _service.CarouselPageCount));
    }

    private void RebuildWallGrid()
    {
        if (_service is null) return;
#if WINDOWS
        DetachAllTileSurfaces();
#endif
        _wallGrid.Children.Clear();
        _wallGrid.RowDefinitions.Clear();
        _wallGrid.ColumnDefinitions.Clear();
        _tileBorders.Clear();
        _tileTitleBlocks.Clear();
        _tileStatusBlocks.Clear();
        _tileRestoreButtons.Clear();
#if WINDOWS
        _tileSurfaces.Clear();
#endif

        var layout = _service.Layout;
        for (var r = 0; r < layout.Rows; r++)
            _wallGrid.RowDefinitions.Add(new RowDefinition(GridLength.Star));
        for (var c = 0; c < layout.Columns; c++)
            _wallGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));

        for (var i = 0; i < layout.TileCount; i++)
        {
            var border = CreateTileBorderShell(i);
            _tileBorders[i] = border;
            Grid.SetRow(border, i / layout.Columns);
            Grid.SetColumn(border, i % layout.Columns);
            _wallGrid.Children.Add(border);
            PopulateTileCell(i, preserveOtherPlayers: false);
        }

        ApplyWallFocus();
    }

    /// <summary>应用内最大化：聚焦格跨满整个 Grid，其余格隐藏（播放器保留，由服务层暂停）。</summary>
    private void ApplyWallFocus()
    {
        if (_service is null) return;
        var layout = _service.Layout;
        var focus = _service.FocusedTileIndex;
        var focused = focus is int f && _tileBorders.ContainsKey(f);

        foreach (var (index, border) in _tileBorders)
        {
            if (focused)
            {
                var isFocus = index == focus;
                border.IsVisible = isFocus;
                Grid.SetRow(border, 0);
                Grid.SetColumn(border, 0);
                Grid.SetRowSpan(border, isFocus ? layout.Rows : 1);
                Grid.SetColumnSpan(border, isFocus ? layout.Columns : 1);
            }
            else
            {
                border.IsVisible = true;
                Grid.SetRow(border, index / layout.Columns);
                Grid.SetColumn(border, index % layout.Columns);
                Grid.SetRowSpan(border, 1);
                Grid.SetColumnSpan(border, 1);
            }
        }

        foreach (var (index, button) in _tileRestoreButtons)
            button.IsVisible = focused && index == focus;
    }

    private async Task OnPlayerRequestInAppMaximizeAsync()
    {
        if (_service is null) return;
        var sourceId = _service.SinglePlayback.CurrentSourceId;
        if (string.IsNullOrWhiteSpace(sourceId)) return;

        try
        {
#if WINDOWS
            // 单画面播放让位给墙格（同一源不要双播）
            await _player.StopAsync();
#endif
            _browseView = BrowseView.Canvas;
            SetMode(PanelMode.Wall);

            var tiles = _service.Tiles.ToList();
            var index = tiles.FindIndex(t => t.SourceId == sourceId);
            if (index < 0)
            {
                index = tiles.FindIndex(t => t.SourceId is null);
                if (index < 0) index = 0;
#if WINDOWS
                await BindTileInPlaceAsync(index, sourceId);
#else
                // 无墙内视频面的端：只占位，单画面播放保持不中断
                await _service.BindTileAsync(index, sourceId, autoStart: false);
                PopulateTileCell(index, preserveOtherPlayers: true);
#endif
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

    private Border CreateTileBorderShell(int index)
    {
        var border = new Border
        {
            Margin = 3,
            Stroke = _line,
            StrokeThickness = 1,
            BackgroundColor = Colors.Black,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 4 },
            ClassId = index.ToString()
        };
        var doubleTap = new TapGestureRecognizer { NumberOfTapsRequired = 2 };
        doubleTap.Tapped += (_, _) => OnTileDoubleTapped(index);
        border.GestureRecognizers.Add(doubleTap);

        return border;
    }

    private void PopulateTileCell(int index, bool preserveOtherPlayers)
    {
        if (_service is null) return;
        if (!_tileBorders.TryGetValue(index, out var border))
        {
            if (!preserveOtherPlayers)
                RebuildWallGrid();
            return;
        }

#if WINDOWS
        if (_tileSurfaces.TryGetValue(index, out _))
        {
            if (index < _service.Tiles.Count)
                _service.Tiles[index].SetRenderTarget(IntPtr.Zero);
            _tileSurfaces.Remove(index);
        }
#endif

        _tileTitleBlocks.Remove(index);
        _tileStatusBlocks.Remove(index);
        _tileRestoreButtons.Remove(index);

        var tile = index < _service.Tiles.Count ? _service.Tiles[index] : null;
        var root = new Grid
        {
            RowDefinitions =
            [
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Star)
            ]
        };

        var title = new Label
        {
            Text = ResolveTileTitle(tile),
            TextColor = Colors.White,
            FontSize = 12,
            FontAttributes = FontAttributes.Bold,
            LineBreakMode = LineBreakMode.TailTruncation,
            Margin = new Thickness(6, 4, 6, 0)
        };
        var status = new Label
        {
            Text = tile?.SourceId is null ? "" : StatusTextFor(tile.State),
            TextColor = Colors.LightGray,
            FontSize = 11,
            Margin = new Thickness(6, 0, 6, 4)
        };

        // 最大化后的「还原」入口（仅聚焦格可见）
        var restoreBtn = new Button
        {
            Text = Loc.T("Tool.VideoWall.MaximizeExit"),
            FontSize = 11,
            Padding = new Thickness(8, 0),
            BackgroundColor = Color.FromArgb("#CC374151"),
            TextColor = Colors.White,
            HorizontalOptions = LayoutOptions.End,
            VerticalOptions = LayoutOptions.Start,
            IsVisible = _service.FocusedTileIndex == index
        };
        restoreBtn.Clicked += async (_, _) =>
        {
            await _service.FocusTileAsync(null);
            ApplyWallFocus();
        };
        _tileRestoreButtons[index] = restoreBtn;

        var chrome = new Grid
        {
            BackgroundColor = Color.FromArgb("#AA111827"),
            ColumnDefinitions =
            [
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Auto)
            ],
            Children =
            {
                new VerticalStackLayout { Children = { title, status } },
                restoreBtn.Also(v => Grid.SetColumn(v, 1))
            }
        };
        Grid.SetRow(chrome, 0);
        root.Children.Add(chrome);
        _tileTitleBlocks[index] = title;
        _tileStatusBlocks[index] = status;

        var body = new Grid();
        if (tile?.SourceId is null)
        {
            body.Children.Add(new Label
            {
                Text = Loc.T("Tool.VideoWall.EmptySlot"),
                TextColor = Colors.Gray,
                HorizontalOptions = LayoutOptions.Center,
                VerticalOptions = LayoutOptions.Center,
                HorizontalTextAlignment = TextAlignment.Center,
                LineBreakMode = LineBreakMode.WordWrap,
                Margin = new Thickness(8),
                InputTransparent = true
            });
        }
#if WINDOWS
        else
        {
            var surface = new VideoWallHwndSurface
            {
                HorizontalOptions = LayoutOptions.Fill,
                VerticalOptions = LayoutOptions.Fill
            };
            body.Children.Add(surface);
            _tileSurfaces[index] = surface;
        }
#else
        else
        {
            body.Children.Add(new Label
            {
                Text = Loc.T("Tool.VideoWall.DesktopOnly"),
                TextColor = Colors.Gray,
                FontSize = 11,
                HorizontalOptions = LayoutOptions.Center,
                VerticalOptions = LayoutOptions.Center,
                HorizontalTextAlignment = TextAlignment.Center,
                LineBreakMode = LineBreakMode.WordWrap,
                Margin = new Thickness(8),
                InputTransparent = true
            });
        }
#endif

        Grid.SetRow(body, 1);
        root.Children.Add(body);
        border.Content = root;
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
#if WINDOWS
            var hasSurface = _tileSurfaces.ContainsKey(i);
            if (hasSource != hasSurface)
            {
                PopulateTileCell(i, preserveOtherPlayers: true);
                if (hasSource && _tileSurfaces.TryGetValue(i, out var surface))
                    _ = RebindTileSurfaceAsync(i, surface);
                continue;
            }
#endif
            if (_tileTitleBlocks.TryGetValue(i, out var title))
                title.Text = ResolveTileTitle(tile);
            if (_tileStatusBlocks.TryGetValue(i, out var status))
                status.Text = hasSource ? StatusTextFor(tile!.State) : "";
        }
    }

#if WINDOWS
    private async Task<IntPtr> WaitForSurfaceHwndAsync(VideoWallHwndSurface surface, int timeoutMs = 1000)
    {
        if (surface.Hwnd != IntPtr.Zero)
            return surface.Hwnd;

        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnHwndReady() => ready.TrySetResult();
        surface.HwndReady += OnHwndReady;
        try
        {
            var deadline = Environment.TickCount64 + timeoutMs;
            while (surface.Hwnd == IntPtr.Zero && Environment.TickCount64 < deadline)
            {
                var waitMs = (int)Math.Min(50, deadline - Environment.TickCount64);
                if (waitMs <= 0)
                    break;
                var completed = await Task.WhenAny(ready.Task, Task.Delay(waitMs)).ConfigureAwait(true);
                if (completed == ready.Task || surface.Hwnd != IntPtr.Zero)
                    break;
            }

            return surface.Hwnd;
        }
        finally
        {
            surface.HwndReady -= OnHwndReady;
        }
    }

    private void DetachAllTileSurfaces()
    {
        if (_service is null) return;
        foreach (var (index, _) in _tileSurfaces)
        {
            if (index < _service.Tiles.Count)
                _service.Tiles[index].SetRenderTarget(IntPtr.Zero);
        }
    }

    private async Task RebindTileSurfaceAsync(int index, VideoWallHwndSurface surface)
    {
        if (_service is null) return;
        var hwnd = await WaitForSurfaceHwndAsync(surface).ConfigureAwait(true);
        if (hwnd == IntPtr.Zero)
        {
            ReportLog($"视频墙格{index}: HWND 未就绪，跳过绑定以免弹出独立窗口");
            return;
        }

        if (index >= _service.Tiles.Count)
            return;
        _service.Tiles[index].SetRenderTarget(hwnd);
    }
#endif

    private async Task BindTileInPlaceAsync(int index, string sourceId)
    {
        if (_service is null) return;
        await _service.BindTileAsync(index, sourceId, autoStart: false);
        PopulateTileCell(index, preserveOtherPlayers: true);
#if WINDOWS
        if (!_tileSurfaces.TryGetValue(index, out var surface))
            return;
        var hwnd = await WaitForSurfaceHwndAsync(surface).ConfigureAwait(true);
        if (hwnd == IntPtr.Zero)
        {
            ReportLog($"视频墙格{index}: HWND 未就绪，跳过开播以免弹出独立窗口");
            return;
        }

        if (index < _service.Tiles.Count)
            _service.Tiles[index].SetRenderTarget(hwnd);
#endif
        await _service.StartTileAsync(index);
        if (_tileTitleBlocks.TryGetValue(index, out var title))
            title.Text = ResolveTileTitle(index < _service.Tiles.Count ? _service.Tiles[index] : null);
        if (_tileStatusBlocks.TryGetValue(index, out var status))
        {
            var tile = index < _service.Tiles.Count ? _service.Tiles[index] : null;
            status.Text = tile?.SourceId is null ? "" : StatusTextFor(tile.State);
        }
    }

    private void OnTileDoubleTapped(int index)
    {
        if (_service is null) return;
        // 有源双击进单路；画布由过滤源列表自动铺格，不再双击添加
        var tile = index < _service.Tiles.Count ? _service.Tiles[index] : null;
        if (tile?.SourceId is { } id)
            BeginPlayback(id);
    }

    private void BeginPlayback(string sourceId)
    {
        _ = BeginPlaybackCoreAsync(sourceId);
    }

    private async Task BeginPlaybackCoreAsync(string sourceId)
    {
        // 画布进单路：停墙与轮播，避免同路双开；返回时按轮播页重建
        if (_browseView == BrowseView.Canvas && _mode == PanelMode.Wall)
            await RecycleWallPlaybackAsync();

        SetMode(PanelMode.Playback);
        // 软件帧出画不依赖 HWND 布局；仍让出一帧以便播放页可见
        await Task.Yield();
        await _player.PlayAsync(sourceId, ResolvePlaybackTitle(sourceId)).ConfigureAwait(true);
    }

    private string ResolvePlaybackTitle(string sourceId)
    {
        var row = _rows.FirstOrDefault(r => r.Id == sourceId);
        if (!string.IsNullOrWhiteSpace(row?.Name)) return row.Name;
        var src = _service?.Catalog.Sources.FirstOrDefault(s => s.Id == sourceId);
        if (!string.IsNullOrWhiteSpace(src?.Name)) return src.Name;
        return sourceId;
    }

    private async Task DeleteSelectedAsync()
    {
        if (_service is null) return;
        var rows = _listView.SelectedItems.OfType<SourceRowVm>().ToList();
        if (rows.Count == 0) return;

        var page = GetPage();
        if (page is not null)
        {
            var label = rows.Count == 1 ? rows[0].Name : rows.Count.ToString();
            var ok = await page.DisplayAlertAsync(
                Loc.T("Tool.VideoWall.Delete"),
                string.Format(Loc.T("Tool.VideoWall.ConfirmDelete"), label),
                Loc.T("Tool.VideoWall.Delete"),
                Loc.T("Tool.VideoWall.Cancel"));
            if (!ok) return;
        }

        foreach (var row in rows)
        {
            if (string.Equals(_service.SinglePlayback.CurrentSourceId, row.Id, StringComparison.Ordinal))
                await _player.StopAsync();
            await _service.RemoveSourceAsync(row.Id);
        }

        RefreshAll();
    }

    private void OpenDialog(string? edit)
    {
        _editingId = edit;
        _formError.IsVisible = false;
        _flyout.Close();
        _dialogTitle.Text = edit is null
            ? Loc.T("Tool.VideoWall.AddSource")
            : Loc.T("Tool.VideoWall.Editing");

        if (edit is not null && _service is not null)
        {
            var src = _service.Catalog.Sources.FirstOrDefault(s => s.Id == edit);
            if (src is not null)
                _sourceForm.BeginEdit(src, _service.Credentials);
        }
        else
            _sourceForm.BeginAdd();

        SetDialogVisible(true);
    }

    private void CloseDialog()
    {
        _editingId = null;
        _flyout.Close();
        SetDialogVisible(false);
    }

    private void SetDialogVisible(bool visible)
    {
        foreach (var child in _root.Children)
        {
            if (child is Grid { ClassId: "dialogHost" } host)
                host.IsVisible = visible;
        }
    }

    private async Task SaveDialogAsync()
    {
        if (_service is null) return;
        _formError.IsVisible = false;

        var kind = _editingId is not null
            ? _service.Catalog.Sources.FirstOrDefault(s => s.Id == _editingId)?.Kind ?? _sourceForm.Kind
            : _sourceForm.Kind;

        var descriptor = SourceDescriptorBuilder.TryBuild(kind, _sourceForm.Values, _service.Credentials, out var err);
        if (descriptor is null)
        {
            _formError.Text = err ?? "无法保存";
            _formError.IsVisible = true;
            return;
        }

        if (_editingId is not null)
            descriptor = descriptor with { Id = _editingId };

        _service.Credentials.Save();
        var result = _editingId is null
            ? await _service.AddSourceAsync(descriptor)
            : await _service.UpdateSourceAsync(descriptor);

        if (!result.Added)
        {
            _formError.Text = result.Reason;
            _formError.IsVisible = true;
            return;
        }

        _ = await _service.RefreshCoverAsync(descriptor);
        CloseDialog();
        RefreshAll();
        ReportLog(_editingId is null ? Loc.T("Tool.VideoWall.AddSource") + " OK" : Loc.T("Tool.VideoWall.Editing") + " OK");
    }

    private View BuildListHeader()
    {
        var hdr = new Grid
        {
            Padding = new Thickness(12, 10),
            BackgroundColor = Color.FromArgb(Application.Current?.RequestedTheme == AppTheme.Dark ? "#0F172A" : "#F3F4F6"),
            ColumnDefinitions =
            [
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(new GridLength(88)),
                new ColumnDefinition(new GridLength(1.4, GridUnitType.Star)),
                new ColumnDefinition(new GridLength(72)),
                new ColumnDefinition(new GridLength(200))
            ],
            ColumnSpacing = 8,
            Children =
            {
                Header(Loc.T("Tool.VideoWall.ColName")),
                Header(Loc.T("Tool.VideoWall.ColKind")).Also(v => Grid.SetColumn(v, 1)),
                Header(Loc.T("Tool.VideoWall.Field.Address")).Also(v => Grid.SetColumn(v, 2)),
                Header(Loc.T("Tool.VideoWall.ColStatus")).Also(v => Grid.SetColumn(v, 3)),
                Header(Loc.T("Tool.VideoWall.ColActions")).Also(v => Grid.SetColumn(v, 4))
            }
        };
        return hdr;

        Label Header(string t) => new()
        {
            Text = t,
            FontSize = 12,
            FontAttributes = FontAttributes.Bold,
            TextColor = _muted
        };
    }

    private View BuildListRow()
    {
        static void CenterLabel(Label l)
        {
            l.VerticalOptions = LayoutOptions.Center;
            l.VerticalTextAlignment = TextAlignment.Center;
        }

        var name = new Label { FontSize = 13, TextColor = _text, LineBreakMode = LineBreakMode.TailTruncation };
        name.SetBinding(Label.TextProperty, nameof(SourceRowVm.Name));
        CenterLabel(name);
        var kind = new Label { FontSize = 12, TextColor = _muted };
        kind.SetBinding(Label.TextProperty, nameof(SourceRowVm.KindText));
        CenterLabel(kind);
        var addr = new Label { FontSize = 12, TextColor = _muted, LineBreakMode = LineBreakMode.TailTruncation };
        addr.SetBinding(Label.TextProperty, nameof(SourceRowVm.AddressRedacted));
        CenterLabel(addr);
        var status = new Label { FontSize = 12, TextColor = _muted };
        status.SetBinding(Label.TextProperty, nameof(SourceRowVm.StatusText));
        CenterLabel(status);

        var playGreen = Color.FromArgb("#059669");
        var play = VideoWallMauiIcons.CreateRowIconButton(
            VideoWallMauiIcons.Geo.Play, playGreen, Loc.T("Tool.VideoWall.Play"), _card, _line).Host;
        play.SetBinding(VisualElement.ClassIdProperty, nameof(SourceRowVm.Id));
        VideoWallMauiIcons.WireClicked(play, (s, _) =>
        {
            if (s is Border { ClassId: { Length: > 0 } id })
                BeginPlayback(id);
        });

        var edit = VideoWallMauiIcons.CreateRowIconButton(
            VideoWallMauiIcons.Geo.Edit, _blue, Loc.T("Tool.VideoWall.Editing"), _card, _line).Host;
        edit.SetBinding(VisualElement.ClassIdProperty, nameof(SourceRowVm.Id));
        VideoWallMauiIcons.WireClicked(edit, (s, _) =>
        {
            if (s is Border { ClassId: { Length: > 0 } id })
                OpenDialog(id);
        });

        var del = VideoWallMauiIcons.CreateRowIconButton(
            VideoWallMauiIcons.Geo.Delete, _warn, Loc.T("Tool.VideoWall.Delete"), _card, _line).Host;
        del.Margin = 0;
        del.SetBinding(VisualElement.ClassIdProperty, nameof(SourceRowVm.Id));
        VideoWallMauiIcons.WireClicked(del, async (s, _) =>
        {
            if (s is Border { ClassId: { Length: > 0 } id })
                await DeleteOneAsync(id);
        });

        var row = new Border
        {
            Padding = new Thickness(12, 10),
            StrokeThickness = 0,
            Content = new Grid
            {
                MinimumHeightRequest = 44,
                ColumnDefinitions =
                [
                    new ColumnDefinition(GridLength.Star),
                    new ColumnDefinition(new GridLength(88)),
                    new ColumnDefinition(new GridLength(1.4, GridUnitType.Star)),
                    new ColumnDefinition(new GridLength(72)),
                    new ColumnDefinition(new GridLength(108))
                ],
                ColumnSpacing = 8,
                Children =
                {
                    name,
                    kind.Also(v => Grid.SetColumn(v, 1)),
                    addr.Also(v => Grid.SetColumn(v, 2)),
                    status.Also(v => Grid.SetColumn(v, 3)),
                    new HorizontalStackLayout
                    {
                        Spacing = 0,
                        VerticalOptions = LayoutOptions.Center,
                        Children = { play, edit, del }
                    }.Also(v => Grid.SetColumn(v, 4))
                }
            }
        };

        var dbl = new TapGestureRecognizer { NumberOfTapsRequired = 2 };
        dbl.Tapped += (_, _) =>
        {
            if (row.BindingContext is SourceRowVm vm)
                BeginPlayback(vm.Id);
        };
        row.GestureRecognizers.Add(dbl);
        return row;
    }

    private const double CardWidth = 220;
    private const double CardGap = 12;
    private int _cardSpan = 3;

    private static GridItemsLayout CreateCardItemsLayout(int span) => new(
        Math.Max(1, span), ItemsLayoutOrientation.Vertical)
    {
        HorizontalItemSpacing = CardGap,
        VerticalItemSpacing = CardGap
    };

    private void UpdateCardItemsLayout()
    {
        var width = _cardsView.Width;
        if (width <= 0 || double.IsNaN(width)) return;
        var span = Math.Max(1, (int)Math.Floor((width + CardGap) / (CardWidth + CardGap)));
        if (span == _cardSpan && _cardsView.ItemsLayout is GridItemsLayout) return;
        _cardSpan = span;
        _cardsView.ItemsLayout = CreateCardItemsLayout(span);
    }

    private View BuildCard()
    {
        var name = new Label { FontSize = 13, FontAttributes = FontAttributes.Bold, TextColor = _text, LineBreakMode = LineBreakMode.TailTruncation };
        name.SetBinding(Label.TextProperty, nameof(SourceRowVm.Name));
        var status = new Label { FontSize = 11, TextColor = _muted };
        status.SetBinding(Label.TextProperty, nameof(SourceRowVm.StatusText));
        var addr = new Label { FontSize = 10, TextColor = _muted, LineBreakMode = LineBreakMode.TailTruncation };
        addr.SetBinding(Label.TextProperty, nameof(SourceRowVm.AddressRedacted));

        var playGreen = Color.FromArgb("#059669");
        var play = VideoWallMauiIcons.CreateRowIconButton(
            VideoWallMauiIcons.Geo.Play, playGreen, Loc.T("Tool.VideoWall.Play"), _card, _line).Host;
        play.SetBinding(VisualElement.ClassIdProperty, nameof(SourceRowVm.Id));
        VideoWallMauiIcons.WireClicked(play, (s, _) =>
        {
            if (s is Border { ClassId: { Length: > 0 } id })
                BeginPlayback(id);
        });
        var edit = VideoWallMauiIcons.CreateRowIconButton(
            VideoWallMauiIcons.Geo.Edit, _blue, Loc.T("Tool.VideoWall.Editing"), _card, _line).Host;
        edit.SetBinding(VisualElement.ClassIdProperty, nameof(SourceRowVm.Id));
        VideoWallMauiIcons.WireClicked(edit, (s, _) =>
        {
            if (s is Border { ClassId: { Length: > 0 } id })
                OpenDialog(id);
        });
        var del = VideoWallMauiIcons.CreateRowIconButton(
            VideoWallMauiIcons.Geo.Delete, _warn, Loc.T("Tool.VideoWall.Delete"), _card, _line).Host;
        del.Margin = 0;
        del.SetBinding(VisualElement.ClassIdProperty, nameof(SourceRowVm.Id));
        VideoWallMauiIcons.WireClicked(del, async (s, _) =>
        {
            if (s is Border { ClassId: { Length: > 0 } id })
                await DeleteOneAsync(id);
        });

        // 定宽 220；格内左对齐，避免 GridItemsLayout 把卡片横向拉满
        var card = new Border
        {
            WidthRequest = CardWidth,
            MinimumHeightRequest = 132,
            MaximumWidthRequest = CardWidth,
            HorizontalOptions = LayoutOptions.Start,
            VerticalOptions = LayoutOptions.Start,
            Margin = 0,
            Padding = 10,
            Stroke = _line,
            BackgroundColor = _card,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 8 },
            Content = new Grid
            {
                RowDefinitions = [new RowDefinition(GridLength.Star), new RowDefinition(GridLength.Auto)],
                Children =
                {
                    new VerticalStackLayout { Spacing = 4, Children = { name, status, addr } },
                    new HorizontalStackLayout
                    {
                        Spacing = 4,
                        HorizontalOptions = LayoutOptions.End,
                        Margin = new Thickness(0, 10, 0, 0),
                        Children = { play, edit, del }
                    }.Also(v => Grid.SetRow(v, 1))
                }
            }
        };

        var dbl = new TapGestureRecognizer { NumberOfTapsRequired = 2 };
        dbl.Tapped += (_, _) =>
        {
            if (card.BindingContext is SourceRowVm vm)
                BeginPlayback(vm.Id);
        };
        card.GestureRecognizers.Add(dbl);
        return card;
    }

    private async Task DeleteOneAsync(string id)
    {
        if (_service is null) return;
        var page = GetPage();
        if (page is not null)
        {
            var src = _service.Catalog.Sources.FirstOrDefault(s => s.Id == id);
            var ok = await page.DisplayAlertAsync(
                Loc.T("Tool.VideoWall.Delete"),
                string.Format(Loc.T("Tool.VideoWall.ConfirmDelete"), src?.Name ?? id),
                Loc.T("Tool.VideoWall.Delete"),
                Loc.T("Tool.VideoWall.Cancel"));
            if (!ok) return;
        }

        if (string.Equals(_service.SinglePlayback.CurrentSourceId, id, StringComparison.Ordinal))
            await _player.StopAsync();
            await _service.RemoveSourceAsync(id);
        RefreshAll();
    }

    private Page? GetPage()
        => Application.Current?.Windows.FirstOrDefault()?.Page
           ?? Parent as Page
           ?? this.FindParentPage();

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

    private Button MakeBtn(string text, Color bg, Color fg, Color? border = null)
        => new()
        {
            Text = text,
            BackgroundColor = bg,
            TextColor = fg,
            FontSize = 13,
            Padding = new Thickness(14, 8),
            BorderColor = border ?? Colors.Transparent,
            BorderWidth = border is null ? 0 : 1,
            CornerRadius = 8
        };

    private sealed class SourceRowVm
    {
        public string Id { get; init; } = "";
        public string Name { get; init; } = "";
        public string KindText { get; init; } = "";
        public string AddressRedacted { get; init; } = "";
        public string StatusText { get; init; } = "";
    }
}

file static class VideoWallMauiExt
{
    public static T Also<T>(this T view, Action<T> configure)
    {
        configure(view);
        return view;
    }

    public static Page? FindParentPage(this Element element)
    {
        for (var p = element.Parent; p is not null; p = p.Parent)
        {
            if (p is Page page)
                return page;
        }

        return null;
    }
}