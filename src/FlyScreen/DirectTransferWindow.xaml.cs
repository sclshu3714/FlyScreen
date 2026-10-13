using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using FlyScreen.Helpers;
using FlyScreen.Helpers.Localization;
using FlyScreen.Services;
using FlyScreen.Services.Network;

namespace FlyScreen;

public partial class DirectTransferWindow : Window
{
    private readonly AppHost _host;
    private readonly ObservableCollection<string> _paths = [];
    private readonly ObservableCollection<PeerOption> _peers = [];
    private readonly ObservableCollection<FolderPreset> _presets = [];
    private int _loadingFolders;

    public DirectTransferWindow(AppHost host)
    {
        InitializeComponent();
        _host = host;
        ThemeService.ApplyToChildWindow(this);
        ApplyLocalizedUi();
        PathList.ItemsSource = _paths;
        PeerCombo.ItemsSource = _peers;
        FolderPresetCombo.ItemsSource = _presets;
        Loaded += OnLoaded;
    }

    private void ApplyLocalizedUi()
    {
        Title = Loc.T("DT.Title");
        AddFilesBtn.Content = Loc.T("DT.AddFiles");
        AddFolderBtn.Content = Loc.T("DT.AddFolder");
        RemoveSelectedBtn.Content = Loc.T("DT.Remove");
        ClearPathsBtn.Content = Loc.T("DT.Clear");
        ContentHintText.Text = Loc.T("DT.ContentHint");
        TargetHintText.Text = Loc.T("DT.TargetHint");
        PeerLabel.Text = Loc.T("DT.Peer");
        PresetLabel.Text = Loc.T("DT.Preset");
        PathLabel.Text = Loc.T("DT.Path");
        TargetPathBox.ToolTip = Loc.T("DT.PathTip");
        CancelBtn.Content = Loc.T("DT.Cancel");
        StartBtn.Content = Loc.T("DT.Start");
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ThemeService.ApplyToChildWindow(this);
        RefreshPeers();
        if (_peers.Count == 0)
        {
            StatusText.Text = Loc.T("DT.NoPeers");
            StartBtn.IsEnabled = false;
        }
        else
        {
            PeerCombo.SelectedIndex = 0;
        }
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

    private async void PeerCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
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
            _presets.Clear();

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

    private void FolderPreset_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (FolderPresetCombo.SelectedItem is not FolderPreset preset) return;
        if (!string.IsNullOrWhiteSpace(preset.Path))
            TargetPathBox.Text = preset.Path;
    }

    private void AddFiles_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = Loc.T("DT.PickFiles"),
            Multiselect = true,
            CheckFileExists = true
        };
        if (dlg.ShowDialog(this) != true) return;
        foreach (var f in dlg.FileNames)
            AddPath(f);
    }

    private void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = Loc.T("DT.PickFolder"),
            Multiselect = true
        };
        if (dlg.ShowDialog(this) != true) return;
        foreach (var f in dlg.FolderNames)
            AddPath(f);
    }

    private void AddPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        path = path.Trim();
        if (_paths.Any(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase)))
            return;
        _paths.Add(path);
        StatusText.Text = string.Format(Loc.T("DT.Selected"), _paths.Count);
    }

    private void RemoveSelected_Click(object sender, RoutedEventArgs e)
    {
        var selected = PathList.SelectedItems.Cast<string>().ToList();
        foreach (var s in selected)
            _paths.Remove(s);
    }

    private void ClearPaths_Click(object sender, RoutedEventArgs e) => _paths.Clear();

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (_paths.Count == 0)
        {
            MessageBox.Show(this, Loc.T("DT.NeedPaths"), Loc.T("DT.Title"),
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (PeerCombo.SelectedItem is not PeerOption peer)
        {
            MessageBox.Show(this, Loc.T("DT.NeedPeer"), Loc.T("DT.Title"),
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var target = TargetPathBox.Text?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(target))
        {
            MessageBox.Show(this, Loc.T("DT.NeedPath"), Loc.T("DT.Title"),
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!_host.Settings.TransferFile)
        {
            MessageBox.Show(this, Loc.T("DT.NeedEnable"), Loc.T("DT.Title"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        StartBtn.IsEnabled = false;
        StatusText.Text = string.Format(Loc.T("DT.Started"), peer.Name);

        var snapshot = _paths.ToList();
        var peerName = peer.Name;
        var dest = target;
        _ = Task.Run(async () =>
        {
            try
            {
                await _host.Transfers.StartDirectTransferAsync(peerName, dest, snapshot).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await Dispatcher.InvokeAsync(() =>
                    MessageBox.Show(ex.Message, Loc.T("DT.Title"), MessageBoxButton.OK, MessageBoxImage.Error));
            }
        });

        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private sealed class PeerOption
    {
        public PeerOption(string name) => Name = name;
        public string Name { get; }
        public string Display => Name;
        public override string ToString() => Display;
    }

    private sealed class FolderPreset
    {
        public FolderPreset(string label, string path)
        {
            Label = label;
            Path = path;
        }

        public string Label { get; }
        public string Path { get; }
        public override string ToString() => Label;
    }
}
