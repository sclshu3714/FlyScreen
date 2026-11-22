using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using FlyScreen.Helpers.Localization;
using FlyScreen.Tool.VideoWall.Sources.Dlna;
using FlyScreen.Tool.VideoWall.Wall;
using FlyScreen.Ui.Maui;
using FlyScreen.Ui.Theme.Icons;

namespace FlyScreen.Tool.VideoWall.Ui.Maui;

/// <summary>DLNA 发现 / 浏览 / 多选挂墙（主窗 Modal）。</summary>
public sealed class VideoWallDlnaPage : ContentPage
{
    private readonly IVideoWallService _service;
    private readonly ObservableCollection<ServerRow> _servers = new();
    private readonly ObservableCollection<NodeRow> _nodes = new();
    private readonly Stack<string> _pathStack = new();
    private readonly Stack<string> _titleStack = new();
    private readonly DlnaImportService _importer = new();
    private readonly Label _status;
    private readonly Label _path;
    private readonly Label _selected;
    private readonly View _discoverHost;
    private readonly Button _up;
    private readonly Button _loadMore;
    private readonly Button _addSources;
    private readonly CollectionView _serverList;
    private readonly CollectionView _nodeList;
    private readonly CheckBox _headerSelectAll;
    private readonly Color _text;
    private readonly Color _muted;
    private readonly Color _line;
    private readonly Color _listBg;
    private readonly Color _blue;
    private DlnaMediaServerClient? _client;
    private DlnaBoundServer? _bound;
    private CancellationTokenSource? _cts;
    private int _nextIndex;
    private bool _hasMore;
    private bool _busy;
    private bool _closing;
    private bool _suppressHeaderSelect;

    public event Action? SourcesChanged;

    public VideoWallDlnaPage(IVideoWallService service)
    {
        _service = service;
        MauiDialogShell.PrepareOverlayPage(this);
        Title = Loc.T("Tool.VideoWall.Dlna.Title");

        _text = ColorOf("Text") ?? Colors.Black;
        _muted = ColorOf("Muted") ?? Color.FromArgb("#6B7280");
        _line = ColorOf("Line") ?? Color.FromArgb("#E5E7EB");
        _listBg = ColorOf("ListBg") ?? Color.FromArgb("#F9FAFB");
        _blue = ColorOf("Blue") ?? Color.FromArgb("#2563EB");
        var text = _text;
        var muted = _muted;
        var line = _line;
        var listBg = _listBg;
        var blue = _blue;

        _status = new Label
        {
            TextColor = muted,
            FontSize = 12,
            LineBreakMode = LineBreakMode.TailTruncation,
            MaxLines = 1,
            VerticalOptions = LayoutOptions.Center,
            HorizontalOptions = LayoutOptions.Fill
        };
        _path = new Label
        {
            TextColor = muted,
            FontSize = 12,
            LineBreakMode = LineBreakMode.TailTruncation,
            VerticalOptions = LayoutOptions.Center
        };
        _selected = new Label
        {
            TextColor = muted,
            FontSize = 12,
            VerticalOptions = LayoutOptions.Center
        };

        var discoverIcon = VideoWallMauiIcons.CreatePath(IconCatalog.GetPath(IconCodes.Refresh), Colors.White, 14);
        var discoverContent = new HorizontalStackLayout
        {
            Spacing = 6,
            VerticalOptions = LayoutOptions.Center,
            HorizontalOptions = LayoutOptions.End,
            Children =
            {
                discoverIcon,
                new Label
                {
                    Text = Loc.T("Tool.VideoWall.Dlna.Discover"),
                    TextColor = Colors.White,
                    FontSize = 12,
                    VerticalOptions = LayoutOptions.Center
                }
            }
        };
        _discoverHost = new Border
        {
            BackgroundColor = blue,
            StrokeThickness = 0,
            Padding = new Thickness(10, 4),
            Margin = new Thickness(0, 0, 6, 0),
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 6 },
            VerticalOptions = LayoutOptions.Center,
            HorizontalOptions = LayoutOptions.End,
            Content = discoverContent
        };
        var discoverTap = new TapGestureRecognizer();
        discoverTap.Tapped += async (_, _) => await DiscoverAsync();
        _discoverHost.GestureRecognizers.Add(discoverTap);

        _up = new Button
        {
            Text = Loc.T("Tool.VideoWall.Dlna.Up"),
            Style = TryStyle("DlgNormalBtn"),
            HeightRequest = 32,
            Padding = new Thickness(10, 4)
        };
        _up.Clicked += async (_, _) => await GoUpAsync();
        _loadMore = new Button
        {
            Text = Loc.T("Tool.VideoWall.Dlna.LoadMore"),
            Style = TryStyle("DlgNormalBtn"),
            HeightRequest = 32,
            Padding = new Thickness(10, 4)
        };
        _loadMore.Clicked += async (_, _) => await BrowseAsync(reset: false);
        _addSources = new Button
        {
            Text = Loc.T("Tool.VideoWall.Dlna.AddSources"),
            BackgroundColor = blue,
            TextColor = Colors.White,
            Style = TryStyle("DlgPrimaryBtn"),
            HeightRequest = 34,
            Padding = new Thickness(12, 6)
        };
        _addSources.Clicked += async (_, _) => await ImportAsync();

        _serverList = new CollectionView
        {
            ItemsSource = _servers,
            SelectionMode = SelectionMode.Single,
            BackgroundColor = listBg,
            ItemTemplate = new DataTemplate(() =>
            {
                var title = new Label { TextColor = text, FontAttributes = FontAttributes.Bold };
                title.SetBinding(Label.TextProperty, nameof(ServerRow.FriendlyName));
                var host = new Label { TextColor = muted, FontSize = 11 };
                host.SetBinding(Label.TextProperty, nameof(ServerRow.HostHint));
                var card = new Border
                {
                    Padding = new Thickness(10, 8),
                    StrokeThickness = 0,
                    BackgroundColor = Colors.Transparent,
                    Content = new VerticalStackLayout { Spacing = 2, Children = { title, host } }
                };
                var tap = new TapGestureRecognizer();
                tap.Tapped += async (_, _) =>
                {
                    if (card.BindingContext is ServerRow row)
                        await OpenServerAsync(row.Bound);
                };
                card.GestureRecognizers.Add(tap);
                return card;
            })
        };
        _serverList.SelectionChanged += async (_, e) =>
        {
            if (e.CurrentSelection.FirstOrDefault() is ServerRow row)
                await OpenServerAsync(row.Bound);
        };

        _headerSelectAll = new CheckBox
        {
            Color = blue,
            VerticalOptions = LayoutOptions.Center,
            HorizontalOptions = LayoutOptions.Center,
            Margin = 0
        };
        ToolTipProperties.SetText(_headerSelectAll, Loc.T("Tool.VideoWall.SelectAllTip"));
        _headerSelectAll.CheckedChanged += HeaderSelectAll_CheckedChanged;

        _nodeList = new CollectionView
        {
            ItemsSource = _nodes,
            SelectionMode = SelectionMode.None,
            BackgroundColor = listBg,
            Header = BuildNodeTableHeader(),
            ItemTemplate = new DataTemplate(BuildNodeTableRow)
        };

        var serversHdr = new Label
        {
            Text = Loc.T("Tool.VideoWall.Dlna.Servers"),
            FontSize = 12,
            TextColor = muted,
            Margin = new Thickness(10, 8)
        };
        var leftGrid = new Grid
        {
            RowDefinitions = new RowDefinitionCollection
            {
                new(GridLength.Auto),
                new(GridLength.Star)
            }
        };
        leftGrid.Add(serversHdr, 0, 0);
        leftGrid.Add(_serverList, 0, 1);
        var left = new Border
        {
            Stroke = line,
            StrokeThickness = 1,
            BackgroundColor = listBg,
            Padding = 0,
            Content = leftGrid
        };

        var nav = new Grid
        {
            ColumnDefinitions = new ColumnDefinitionCollection
            {
                new(GridLength.Auto),
                new(GridLength.Star),
                new(GridLength.Auto)
            },
            ColumnSpacing = 8,
            Padding = new Thickness(8, 6),
            Children = { _up, _path, _loadMore }
        };
        Grid.SetColumn(_path, 1);
        Grid.SetColumn(_loadMore, 2);
        var rightGrid = new Grid
        {
            RowDefinitions = new RowDefinitionCollection
            {
                new(GridLength.Auto),
                new(GridLength.Star)
            }
        };
        rightGrid.Add(nav, 0, 0);
        rightGrid.Add(_nodeList, 0, 1);
        var right = new Border
        {
            Stroke = line,
            StrokeThickness = 1,
            BackgroundColor = listBg,
            Content = rightGrid
        };

        var split = new Grid
        {
            ColumnDefinitions = new ColumnDefinitionCollection
            {
                new(new GridLength(0.35, GridUnitType.Star)),
                new(new GridLength(8)),
                new(new GridLength(0.65, GridUnitType.Star))
            }
        };
        split.Add(left, 0);
        split.Add(right, 2);

        var body = new Grid
        {
            RowDefinitions = new RowDefinitionCollection
            {
                new(GridLength.Star)
            },
            Children = { split }
        };

        var leftFooter = new HorizontalStackLayout
        {
            Spacing = 8,
            VerticalOptions = LayoutOptions.Center,
            HorizontalOptions = LayoutOptions.Start,
            Children = { _selected, _status }
        };
        var rightFooter = new HorizontalStackLayout
        {
            Spacing = 8,
            HorizontalOptions = LayoutOptions.End,
            VerticalOptions = LayoutOptions.Center,
            Children = { _addSources }
        };
        var footer = new Grid
        {
            ColumnDefinitions = new ColumnDefinitionCollection
            {
                new(GridLength.Star),
                new(GridLength.Auto)
            },
            ColumnSpacing = 12,
            HeightRequest = 36,
            VerticalOptions = LayoutOptions.Center,
            Children = { leftFooter, rightFooter }
        };
        Grid.SetColumn(rightFooter, 1);

        var shell = new MauiDialogShell
        {
            Title = Loc.T("Tool.VideoWall.Dlna.Title"),
            ShowClose = true,
            SizeMode = MauiDialogSize.Fixed,
            IdealWidth = 860,
            IdealHeight = 560,
            TitleAccessory = _discoverHost,
            Body = body,
            Footer = footer
        };
        shell.FooterBar.Padding = new Thickness(16, 8);
        shell.CloseRequested += async (_, _) => await CloseAsync();
        Content = shell;
        _selected.Text = string.Format(Loc.T("Tool.VideoWall.Dlna.Selected"), 0);
        UpdateNav();

        Appearing += (_, _) => _ = DiscoverAsync();
    }

    public static async Task ShowAsync(IVideoWallService service, Action? onSourcesChanged = null)
    {
        var page = new VideoWallDlnaPage(service);
        if (onSourcesChanged != null)
            page.SourcesChanged += onSourcesChanged;
        var nav = ResolveNavigation();
        if (nav == null) return;
        await nav.PushModalAsync(page, animated: true);
    }

    private static INavigation? ResolveNavigation()
    {
        var page = Application.Current?.Windows.FirstOrDefault()?.Page;
        if (page == null) return null;
        if (page is NavigationPage nav) return nav.Navigation;
        return page.Navigation;
    }

    private async Task CloseAsync()
    {
        if (_closing) return;
        _closing = true;
        try { _cts?.Cancel(); } catch { /* ignore */ }
        _client?.Dispose();
        try { await Navigation.PopModalAsync(); } catch { /* ignore */ }
    }

    private async Task DiscoverAsync()
    {
        if (_busy) return;
        _busy = true;
        _discoverHost.Opacity = 0.5;
        _discoverHost.IsEnabled = false;
        _status.Text = Loc.T("Tool.VideoWall.Dlna.Discovering");
        _servers.Clear();
        _nodes.Clear();
        _bound = null;
        ResetPath();

        try { _cts?.Cancel(); } catch { /* ignore */ }
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        _client?.Dispose();
        _client = new DlnaMediaServerClient();

        ServerRow? first = null;
        try
        {
            var hits = await new DlnaSsdpClient().DiscoverAsync(ct: _cts.Token).ConfigureAwait(true);
            var bound = await _client.BindManyAsync(hits, _cts.Token).ConfigureAwait(true);
            foreach (var s in bound)
            {
                var row = new ServerRow(s);
                _servers.Add(row);
                first ??= row;
            }

            _status.Text = _servers.Count == 0
                ? Loc.T("Tool.VideoWall.Dlna.EmptyServers")
                : string.Format(Loc.T("Tool.VideoWall.Scan.Done"), _servers.Count);
        }
        catch (OperationCanceledException)
        {
            _status.Text = Loc.T("Tool.VideoWall.Dlna.EmptyServers");
        }
        catch (Exception ex)
        {
            _status.Text = ex.Message;
        }
        finally
        {
            _busy = false;
            _discoverHost.Opacity = 1;
            _discoverHost.IsEnabled = true;
            UpdateNav();
        }

        // 必须在 _busy=false 之后再 Browse，否则会被 Busy 守卫直接跳过
        if (first is not null)
        {
            _serverList.SelectedItem = first;
            await OpenServerAsync(first.Bound);
        }
    }

    private async Task OpenServerAsync(DlnaBoundServer bound)
    {
        if (_busy) return;
        _bound = bound;
        ResetPath();
        await BrowseAsync(reset: true);
    }

    private async Task GoUpAsync()
    {
        if (_pathStack.Count <= 1) return;
        _pathStack.Pop();
        if (_titleStack.Count > 0) _titleStack.Pop();
        await BrowseAsync(reset: true);
    }

    private async Task BrowseAsync(bool reset)
    {
        if (_bound is null || _client is null)
        {
            _status.Text = Loc.T("Tool.VideoWall.Dlna.NeedServer");
            return;
        }

        if (_busy) return;
        _busy = true;
        _status.Text = Loc.T("Tool.VideoWall.Dlna.Browsing");
        UpdateNav();

        try
        {
            if (reset)
            {
                _nodes.Clear();
                _nextIndex = 0;
                _hasMore = false;
            }

            var objectId = _pathStack.Count == 0 ? "0" : _pathStack.Peek();
            var page = await _client.BrowseAsync(_bound, objectId, _nextIndex, 50, _cts?.Token ?? default)
                .ConfigureAwait(true);
            foreach (var n in page.Nodes)
                _nodes.Add(new NodeRow(n, RefreshSelected));
            _nextIndex += page.NumberReturned;
            _hasMore = page.HasMore;
            _path.Text = string.Join(" / ", _titleStack.Reverse());
            _status.Text = _nodes.Count == 0
                ? Loc.T("Tool.VideoWall.Dlna.EmptyFolder")
                : $"{_bound.Server.FriendlyName} · {_nodes.Count}";
            RefreshSelected();
        }
        catch (OperationCanceledException) { /* ignore */ }
        catch (Exception ex)
        {
            _status.Text = ex.Message;
        }
        finally
        {
            _busy = false;
            UpdateNav();
        }
    }

    private async Task ImportAsync()
    {
        if (_bound is null)
        {
            _status.Text = Loc.T("Tool.VideoWall.Dlna.NeedServer");
            return;
        }

        var selected = _nodes.Where(n => n.IsSelected && !n.Node.IsFolder).Select(n => n.Node).ToList();
        if (selected.Count == 0)
        {
            _status.Text = Loc.T("Tool.VideoWall.Dlna.NeedSelect");
            return;
        }

        try
        {
            var result = await _importer.ImportAsync(_service, _bound.Server, selected).ConfigureAwait(true);
            _status.Text = result.Message;
            SourcesChanged?.Invoke();
        }
        catch (Exception ex)
        {
            _status.Text = ex.Message;
        }
    }

    private void ResetPath()
    {
        _pathStack.Clear();
        _titleStack.Clear();
        _pathStack.Push("0");
        _titleStack.Push(_bound?.Server.FriendlyName ?? "/");
        _path.Text = "";
        _nextIndex = 0;
        _hasMore = false;
    }

    private void RefreshSelected()
    {
        var n = _nodes.Count(x => x.IsSelected && !x.Node.IsFolder);
        _selected.Text = string.Format(Loc.T("Tool.VideoWall.Dlna.Selected"), n);
        SyncHeaderSelectAll();
    }

    private void UpdateNav()
    {
        _up.IsEnabled = !_busy && _pathStack.Count > 1;
        _loadMore.IsEnabled = !_busy && _hasMore && _bound is not null;
        _addSources.IsEnabled = !_busy;
    }

    private View BuildNodeTableHeader()
    {
        var hdr = new Grid
        {
            Padding = new Thickness(12, 8),
            BackgroundColor = Color.FromArgb(
                Application.Current?.RequestedTheme == AppTheme.Dark ? "#0F172A" : "#F3F4F6"),
            ColumnSpacing = 8,
            ColumnDefinitions = new ColumnDefinitionCollection
            {
                new(new GridLength(40)),
                new(GridLength.Star),
                new(new GridLength(72))
            }
        };
        hdr.Add(_headerSelectAll, 0);
        hdr.Add(new Label
        {
            Text = Loc.T("Tool.VideoWall.ColName"),
            FontSize = 12,
            FontAttributes = FontAttributes.Bold,
            TextColor = _muted,
            VerticalOptions = LayoutOptions.Center
        }, 1);
        hdr.Add(new Label
        {
            Text = Loc.T("Tool.VideoWall.ColKind"),
            FontSize = 12,
            FontAttributes = FontAttributes.Bold,
            TextColor = _muted,
            VerticalOptions = LayoutOptions.Center
        }, 2);
        return hdr;
    }

    private View BuildNodeTableRow()
    {
        var check = new CheckBox
        {
            Color = _blue,
            VerticalOptions = LayoutOptions.Center,
            HorizontalOptions = LayoutOptions.Center,
            Margin = 0
        };
        check.SetBinding(CheckBox.IsCheckedProperty, nameof(NodeRow.IsSelected));
        check.SetBinding(VisualElement.IsEnabledProperty, nameof(NodeRow.CanSelect));
        check.CheckedChanged += (_, _) =>
        {
            if (_suppressHeaderSelect) return;
            RefreshSelected();
        };

        var title = new Label
        {
            TextColor = _text,
            FontSize = 13,
            VerticalOptions = LayoutOptions.Center,
            VerticalTextAlignment = TextAlignment.Center,
            LineBreakMode = LineBreakMode.TailTruncation
        };
        title.SetBinding(Label.TextProperty, nameof(NodeRow.Title));

        var kind = new Label
        {
            TextColor = _muted,
            FontSize = 12,
            VerticalOptions = LayoutOptions.Center,
            VerticalTextAlignment = TextAlignment.Center
        };
        kind.SetBinding(Label.TextProperty, nameof(NodeRow.KindLabel));

        var grid = new Grid
        {
            MinimumHeightRequest = 40,
            ColumnSpacing = 8,
            ColumnDefinitions = new ColumnDefinitionCollection
            {
                new(new GridLength(40)),
                new(GridLength.Star),
                new(new GridLength(72))
            }
        };
        grid.Add(check, 0);
        grid.Add(title, 1);
        grid.Add(kind, 2);

        var row = new Border
        {
            Padding = new Thickness(12, 8),
            StrokeThickness = 0,
            BackgroundColor = Colors.Transparent,
            Content = grid
        };

        var open = new TapGestureRecognizer();
        open.Tapped += async (_, _) =>
        {
            if (row.BindingContext is NodeRow nr && nr.Node.IsFolder)
            {
                _pathStack.Push(nr.Node.Id);
                _titleStack.Push(nr.Node.Title);
                await BrowseAsync(reset: true);
            }
        };
        row.GestureRecognizers.Add(open);
        return row;
    }

    private void HeaderSelectAll_CheckedChanged(object? sender, CheckedChangedEventArgs e)
    {
        if (_suppressHeaderSelect) return;
        _suppressHeaderSelect = true;
        try
        {
            foreach (var row in _nodes)
            {
                if (row.CanSelect)
                    row.IsSelected = e.Value;
            }
        }
        finally
        {
            _suppressHeaderSelect = false;
            RefreshSelected();
        }
    }

    private void SyncHeaderSelectAll()
    {
        if (_suppressHeaderSelect) return;
        _suppressHeaderSelect = true;
        try
        {
            var selectable = _nodes.Where(r => r.CanSelect).ToList();
            _headerSelectAll.IsChecked = selectable.Count > 0 && selectable.All(r => r.IsSelected);
        }
        finally
        {
            _suppressHeaderSelect = false;
        }
    }

    private static Color? ColorOf(string key)
        => Application.Current?.Resources.TryGetValue(key, out var v) == true && v is Color c ? c : null;

    private static Style? TryStyle(string key)
        => Application.Current?.Resources.TryGetValue(key, out var v) == true && v is Style s ? s : null;

    private sealed class ServerRow(DlnaBoundServer bound)
    {
        public DlnaBoundServer Bound { get; } = bound;
        public string FriendlyName => Bound.Server.FriendlyName;
        public string HostHint => Bound.Server.Location.Host;
    }

    private sealed class NodeRow : INotifyPropertyChanged
    {
        private bool _selected;
        private readonly Action _onChanged;

        public NodeRow(DlnaNode node, Action onChanged)
        {
            Node = node;
            _onChanged = onChanged;
        }

        public DlnaNode Node { get; }
        public string Title => Node.Title;
        public bool CanSelect => !Node.IsFolder;
        public string KindLabel => Node.IsFolder
            ? Loc.T("Tool.VideoWall.Dlna.Folder")
            : Loc.T("Tool.VideoWall.Dlna.Video");

        public bool IsSelected
        {
            get => _selected;
            set
            {
                if (!CanSelect)
                {
                    if (_selected)
                    {
                        _selected = false;
                        OnPropertyChanged();
                    }
                    return;
                }

                if (_selected == value) return;
                _selected = value;
                OnPropertyChanged();
                _onChanged();
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
