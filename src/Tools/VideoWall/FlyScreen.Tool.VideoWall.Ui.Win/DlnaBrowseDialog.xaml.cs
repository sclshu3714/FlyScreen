using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FlyScreen.Helpers.Localization;
using FlyScreen.Tool.VideoWall.Sources.Dlna;
using FlyScreen.Tool.VideoWall.Wall;
using FlyScreen.Ui.Theme.Wpf;

namespace FlyScreen.Tool.VideoWall.Ui.Win;

public partial class DlnaBrowseDialog : Window
{
    private readonly IVideoWallService _service;
    private readonly ObservableCollection<ServerVm> _servers = new();
    private readonly ObservableCollection<NodeVm> _nodes = new();
    private readonly Stack<string> _pathStack = new();
    private readonly Stack<string> _titleStack = new();
    private readonly DlnaImportService _importer = new();
    private DlnaMediaServerClient? _client;
    private DlnaBoundServer? _bound;
    private CancellationTokenSource? _cts;
    private int _nextIndex;
    private bool _hasMore;
    private bool _busy;
    private bool _suppressHeaderSelect;

    public DlnaBrowseDialog(IVideoWallService service, Window? owner)
    {
        InitializeComponent();
        _service = service;
        Owner = owner;
        ServerList.ItemsSource = _servers;
        NodeList.ItemsSource = _nodes;
        ApplyLoc();
        WpfDialogTheme.ApplyTo(this);

        Loaded += async (_, _) =>
        {
            WpfDialogTheme.ApplyTo(this);
            await DiscoverAsync();
        };
        Closed += (_, _) =>
        {
            try { _cts?.Cancel(); } catch { /* ignore */ }
            _cts?.Dispose();
            _client?.Dispose();
        };
    }

    private void ApplyLoc()
    {
        Title = Loc.T("Tool.VideoWall.Dlna.Title");
        HdrTitle.Text = Loc.T("Tool.VideoWall.Dlna.Title");
        HdrServers.Text = Loc.T("Tool.VideoWall.Dlna.Servers");
        DiscoverBtnText.Text = Loc.T("Tool.VideoWall.Dlna.Discover");
        DiscoverBtn.ToolTip = Loc.T("Tool.VideoWall.Dlna.Discover");
        UpBtn.Content = Loc.T("Tool.VideoWall.Dlna.Up");
        LoadMoreBtn.Content = Loc.T("Tool.VideoWall.Dlna.LoadMore");
        AddSourcesBtn.Content = Loc.T("Tool.VideoWall.Dlna.AddSources");
        CloseBtn.Content = Loc.T("Tool.VideoWall.Dlna.Close");
        ColName.Header = Loc.T("Tool.VideoWall.ColName");
        ColKind.Header = Loc.T("Tool.VideoWall.ColKind");
        HeaderSelectAll.ToolTip = Loc.T("Tool.VideoWall.SelectAllTip");
        SelectedText.Text = string.Format(Loc.T("Tool.VideoWall.Dlna.Selected"), 0);
        UpdateNavEnabled();
    }

    private void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();

    private async void DiscoverBtn_Click(object sender, RoutedEventArgs e)
        => await DiscoverAsync();

    private async Task DiscoverAsync()
    {
        if (_busy) return;
        _busy = true;
        DiscoverBtn.IsEnabled = false;
        StatusText.Text = Loc.T("Tool.VideoWall.Dlna.Discovering");
        _servers.Clear();
        _nodes.Clear();
        _bound = null;
        ResetPath();

        try { _cts?.Cancel(); } catch { /* ignore */ }
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        _client?.Dispose();
        _client = new DlnaMediaServerClient();

        ServerVm? first = null;
        try
        {
            var hits = await new DlnaSsdpClient().DiscoverAsync(ct: ct).ConfigureAwait(true);
            var bound = await _client.BindManyAsync(hits, ct).ConfigureAwait(true);
            foreach (var s in bound)
            {
                var vm = new ServerVm(s);
                _servers.Add(vm);
                first ??= vm;
            }

            StatusText.Text = _servers.Count == 0
                ? Loc.T("Tool.VideoWall.Dlna.EmptyServers")
                : string.Format(Loc.T("Tool.VideoWall.Scan.Done"), _servers.Count);
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = Loc.T("Tool.VideoWall.Dlna.EmptyServers");
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
        }
        finally
        {
            _busy = false;
            DiscoverBtn.IsEnabled = true;
        }

        if (first is not null)
        {
            ServerList.SelectedItem = first;
            await OpenServerAsync(first.Bound);
        }
    }

    private async void ServerList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ServerList.SelectedItem is not ServerVm vm)
            return;
        await OpenServerAsync(vm.Bound);
    }

    private async Task OpenServerAsync(DlnaBoundServer bound)
    {
        if (_busy) return;
        _bound = bound;
        ResetPath();
        await BrowseAsync(reset: true);
    }

    private async void UpBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_pathStack.Count <= 1) return;
        _pathStack.Pop();
        if (_titleStack.Count > 0) _titleStack.Pop();
        await BrowseAsync(reset: true);
    }

    private async void LoadMoreBtn_Click(object sender, RoutedEventArgs e)
        => await BrowseAsync(reset: false);

    private async void NodeList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (NodeList.SelectedItem is not NodeVm vm || !vm.Node.IsFolder)
            return;
        _pathStack.Push(vm.Node.Id);
        _titleStack.Push(vm.Node.Title);
        await BrowseAsync(reset: true);
    }

    private async Task BrowseAsync(bool reset)
    {
        if (_bound is null || _client is null)
        {
            StatusText.Text = Loc.T("Tool.VideoWall.Dlna.NeedServer");
            return;
        }

        if (_busy) return;
        _busy = true;
        StatusText.Text = Loc.T("Tool.VideoWall.Dlna.Browsing");
        UpdateNavEnabled();

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
                _nodes.Add(new NodeVm(n, OnSelectionChanged));

            _nextIndex += page.NumberReturned;
            _hasMore = page.HasMore;
            PathText.Text = string.Join(" / ", _titleStack.Reverse());
            StatusText.Text = _nodes.Count == 0
                ? Loc.T("Tool.VideoWall.Dlna.EmptyFolder")
                : $"{_bound.Server.FriendlyName} · {_nodes.Count}";
            RefreshSelectedCount();
        }
        catch (OperationCanceledException)
        {
            /* ignore */
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
        }
        finally
        {
            _busy = false;
            UpdateNavEnabled();
        }
    }

    private async void AddSourcesBtn_Click(object sender, RoutedEventArgs e)
        => await ImportAsync();

    private async Task ImportAsync()
    {
        if (_bound is null)
        {
            StatusText.Text = Loc.T("Tool.VideoWall.Dlna.NeedServer");
            return;
        }

        var selected = _nodes.Where(n => n.IsSelected && !n.Node.IsFolder).Select(n => n.Node).ToList();
        if (selected.Count == 0)
        {
            StatusText.Text = Loc.T("Tool.VideoWall.Dlna.NeedSelect");
            return;
        }

        try
        {
            var result = await _importer.ImportAsync(_service, _bound.Server, selected).ConfigureAwait(true);
            StatusText.Text = result.Message;
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
        }
    }

    private void ResetPath()
    {
        _pathStack.Clear();
        _titleStack.Clear();
        _pathStack.Push("0");
        _titleStack.Push(_bound?.Server.FriendlyName ?? "/");
        PathText.Text = "";
        _nextIndex = 0;
        _hasMore = false;
    }

    private void OnSelectionChanged() => RefreshSelectedCount();

    private void HeaderSelectAll_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressHeaderSelect) return;
        _suppressHeaderSelect = true;
        try
        {
            var target = HeaderSelectAll.IsChecked == true;
            foreach (var row in _nodes)
            {
                if (row.CanSelect)
                    row.IsSelected = target;
            }
        }
        finally
        {
            _suppressHeaderSelect = false;
            RefreshSelectedCount();
        }
    }

    private void RowCheck_Click(object sender, RoutedEventArgs e)
        => RefreshSelectedCount();

    private void RefreshSelectedCount()
    {
        var n = _nodes.Count(x => x.IsSelected && !x.Node.IsFolder);
        SelectedText.Text = string.Format(Loc.T("Tool.VideoWall.Dlna.Selected"), n);
        if (_suppressHeaderSelect) return;
        _suppressHeaderSelect = true;
        try
        {
            var selectable = _nodes.Where(x => x.CanSelect).ToList();
            HeaderSelectAll.IsChecked = selectable.Count > 0 && selectable.All(x => x.IsSelected);
        }
        finally
        {
            _suppressHeaderSelect = false;
        }
    }

    private void UpdateNavEnabled()
    {
        UpBtn.IsEnabled = !_busy && _pathStack.Count > 1;
        LoadMoreBtn.IsEnabled = !_busy && _hasMore && _bound is not null;
        AddSourcesBtn.IsEnabled = !_busy;
    }

    private sealed class ServerVm(DlnaBoundServer bound)
    {
        public DlnaBoundServer Bound { get; } = bound;
        public string FriendlyName => Bound.Server.FriendlyName;
        public string HostHint => Bound.Server.Location.Host;
    }

    private sealed class NodeVm : INotifyPropertyChanged
    {
        private bool _selected;
        private readonly Action _onChanged;

        public NodeVm(DlnaNode node, Action onChanged)
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
