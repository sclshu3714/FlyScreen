using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using FlyScreen.DesktopUi.Shell;
using FlyScreen.Helpers.Localization;
using FlyScreen.Services.Network;

namespace FlyScreen.DesktopUi;

public partial class DirectTransferWindow : Window
{
    private readonly IDesktopShellHost _host;
    private readonly ObservableCollection<string> _paths = [];
    private readonly ObservableCollection<PeerOption> _peers = [];
    private readonly ObservableCollection<FolderPreset> _presets = [];
    private int _loadingFolders;

    public DirectTransferWindow() : this(null!)
    {
        if (!Design.IsDesignMode)
            throw new InvalidOperationException("DirectTransferWindow requires IDesktopShellHost.");
    }

    public DirectTransferWindow(IDesktopShellHost host)
    {
        _host = host;
        InitializeComponent();
        if (Design.IsDesignMode) return;

        Title = Loc.T("DT.Title");
        PathList.ItemsSource = _paths;
        PeerCombo.ItemsSource = _peers;
        FolderPresetCombo.ItemsSource = _presets;
        Opened += (_, _) =>
        {
            RefreshPeers();
            if (_peers.Count == 0)
            {
                StatusText.Text = Loc.T("DT.NoPeers");
                StartBtn.IsEnabled = false;
            }
            else PeerCombo.SelectedIndex = 0;
        };
    }

    private void RefreshPeers()
    {
        _peers.Clear();
        foreach (var name in _host.Connections.ConfiguredPeers())
        {
            if (!_host.Connections.IsPeerReachable(name)) continue;
            _peers.Add(new PeerOption(name));
        }
    }

    private async void PeerCombo_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (PeerCombo.SelectedItem is not PeerOption peer) return;
        await LoadRemoteFoldersAsync(peer.Name).ConfigureAwait(true);
    }

    private async Task LoadRemoteFoldersAsync(string peer)
    {
        if (Interlocked.Exchange(ref _loadingFolders, 1) == 1) return;
        try
        {
            StatusText.Text = string.Format(Loc.T("DT.Querying"), peer);
            _presets.Clear();
            TargetPathBox.Text = "";

            var folders = await _host.Transfers.QueryRemoteFoldersAsync(peer).ConfigureAwait(true);
            if (folders == null)
            {
                StatusText.Text = string.Format(Loc.T("DT.QueryFail"), peer);
                _presets.Add(new FolderPreset(Loc.T("DT.CustomPath"), ""));
                FolderPresetCombo.SelectedIndex = 0;
                return;
            }

            void Add(string label, string path)
            {
                if (!string.IsNullOrWhiteSpace(path))
                    _presets.Add(new FolderPreset(label, path.Trim()));
            }

            Add(Loc.T("DT.Desktop"), folders.Desktop);
            Add(Loc.T("DT.Documents"), folders.Documents);
            Add(Loc.T("DT.Downloads"), folders.Downloads);
            Add(Loc.T("DT.Inbox"), folders.Inbox);
            _presets.Add(new FolderPreset(Loc.T("DT.CustomPath"), folders.Downloads));
            FolderPresetCombo.SelectedIndex = 0;
            StatusText.Text = string.Format(Loc.T("DT.Connected"), peer, _paths.Count);
        }
        finally
        {
            Interlocked.Exchange(ref _loadingFolders, 0);
        }
    }

    private void FolderPreset_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (FolderPresetCombo.SelectedItem is FolderPreset p && !string.IsNullOrWhiteSpace(p.Path))
            TargetPathBox.Text = p.Path;
    }

    private async void AddFiles_Click(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Loc.T("DT.AddFiles"),
            AllowMultiple = true
        }).ConfigureAwait(true);
        foreach (var f in files)
        {
            var path = f.TryGetLocalPath();
            if (!string.IsNullOrEmpty(path) && !_paths.Contains(path))
                _paths.Add(path);
        }
    }

    private async void AddFolder_Click(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = Loc.T("DT.AddFolder"),
            AllowMultiple = false
        }).ConfigureAwait(true);
        var folder = folders.FirstOrDefault();
        var path = folder?.TryGetLocalPath();
        if (!string.IsNullOrEmpty(path) && !_paths.Contains(path))
            _paths.Add(path);
    }

    private void ClearPaths_Click(object? sender, RoutedEventArgs e) => _paths.Clear();

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close();

    private async void Start_Click(object? sender, RoutedEventArgs e)
    {
        if (PeerCombo.SelectedItem is not PeerOption peer)
        {
            StatusText.Text = Loc.T("DT.NoPeers");
            return;
        }

        if (_paths.Count == 0)
        {
            StatusText.Text = Loc.T("DT.ContentHint");
            return;
        }

        var dest = TargetPathBox.Text?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(dest))
        {
            StatusText.Text = Loc.T("DT.PathTip");
            return;
        }

        StartBtn.IsEnabled = false;
        try
        {
            var snapshot = _paths.ToList();
            await _host.Transfers.StartDirectTransferAsync(peer.Name, dest, snapshot).ConfigureAwait(true);
            Close();
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
            StartBtn.IsEnabled = true;
        }
    }

    private sealed record PeerOption(string Name)
    {
        public override string ToString() => Name;
    }

    private sealed record FolderPreset(string Label, string Path)
    {
        public override string ToString() => string.IsNullOrEmpty(Path) ? Label : $"{Label} — {Path}";
    }
}
