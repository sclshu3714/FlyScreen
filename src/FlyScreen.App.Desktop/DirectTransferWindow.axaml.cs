using System.Collections.ObjectModel;
using System.IO;
using Avalonia.Controls;
using Avalonia.Input;
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

        ApplyLocalizedUi();
        PathList.ItemsSource = _paths;
        PeerCombo.ItemsSource = _peers;
        FolderPresetCombo.ItemsSource = _presets;
        _paths.CollectionChanged += (_, _) => UpdateDropHint();
        PathDropBorder.AddHandler(DragDrop.DragOverEvent, PathList_DragOver, RoutingStrategies.Tunnel);
        PathDropBorder.AddHandler(DragDrop.DropEvent, PathList_Drop, RoutingStrategies.Tunnel);
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

    private void ApplyLocalizedUi()
    {
        Title = Loc.T("DT.Title");
        AddFilesBtn.Content = Loc.T("DT.AddFiles");
        AddFolderBtn.Content = Loc.T("DT.AddFolder");
        RemoveSelectedBtn.Content = Loc.T("DT.Remove");
        ClearPathsBtn.Content = Loc.T("DT.Clear");
        ContentHintText.Text = Loc.T("DT.ContentHint");
        PathDropHint.Text = Loc.T("DT.DropHint");
        TargetHintText.Text = Loc.T("DT.TargetHint");
        PeerLabel.Text = Loc.T("DT.Peer");
        PresetLabel.Text = Loc.T("DT.Preset");
        PathLabel.Text = Loc.T("DT.Path");
        TargetPathBox.Watermark = Loc.T("DT.PathTip");
        CancelBtn.Content = Loc.T("DT.Cancel");
        StartBtn.Content = Loc.T("DT.Start");
    }

    private void UpdateDropHint()
        => PathDropHint.IsVisible = _paths.Count == 0;

    private void PathList_DragOver(object? sender, DragEventArgs e)
    {
        if (e.Data.Contains(DataFormats.Files))
        {
            var allow = e.DragEffects;
            if ((allow & DragDropEffects.Copy) != 0)
                e.DragEffects = DragDropEffects.Copy;
            else if ((allow & DragDropEffects.Link) != 0)
                e.DragEffects = DragDropEffects.Link;
            else if ((allow & DragDropEffects.Move) != 0)
                e.DragEffects = DragDropEffects.Move;
            else
                e.DragEffects = DragDropEffects.None;
        }
        else
        {
            e.DragEffects = DragDropEffects.None;
        }

        e.Handled = true;
    }

    private async void PathList_Drop(object? sender, DragEventArgs e)
    {
        if (!e.Data.Contains(DataFormats.Files))
        {
            e.Handled = true;
            return;
        }

        var items = e.Data.GetFiles();
        if (items == null)
        {
            e.Handled = true;
            return;
        }

        StatusText.Text = Loc.T("DT.Preparing");
        var before = _paths.Count;
        foreach (var item in items)
        {
            if (item is IStorageFolder folder)
                await TryAddStorageFolderAsync(folder).ConfigureAwait(true);
            else if (item is IStorageFile file)
                await TryAddStorageFileAsync(file).ConfigureAwait(true);
            else
            {
                var path = item.TryGetLocalPath();
                if (!string.IsNullOrWhiteSpace(path)
                    && (File.Exists(path) || Directory.Exists(path)))
                    AddPath(path);
            }
        }

        if (_paths.Count > before)
            StatusText.Text = string.Format(Loc.T("DT.Selected"), _paths.Count);
        e.Handled = true;
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
        StatusText.Text = Loc.T("DT.Preparing");
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Loc.T("DT.PickFiles"),
            AllowMultiple = true
        }).ConfigureAwait(true);
        foreach (var f in files)
            await TryAddStorageFileAsync(f).ConfigureAwait(true);
        StatusText.Text = string.Format(Loc.T("DT.Selected"), _paths.Count);
    }

    private async void AddFolder_Click(object? sender, RoutedEventArgs e)
    {
        StatusText.Text = Loc.T("DT.Preparing");
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = Loc.T("DT.PickFolder"),
            AllowMultiple = true
        }).ConfigureAwait(true);
        foreach (var folder in folders)
            await TryAddStorageFolderAsync(folder).ConfigureAwait(true);
        StatusText.Text = string.Format(Loc.T("DT.Selected"), _paths.Count);
    }

    /// <summary>Mac/Linux 门户选择可能无本地路径，需拷到临时目录再传。</summary>
    private async Task TryAddStorageFileAsync(IStorageFile file)
    {
        var local = file.TryGetLocalPath();
        if (!string.IsNullOrEmpty(local) && File.Exists(local))
        {
            AddPath(local);
            return;
        }

        try
        {
            var name = string.IsNullOrWhiteSpace(file.Name) ? "file.bin" : Path.GetFileName(file.Name);
            foreach (var c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            var dir = Path.Combine(Path.GetTempPath(), "FlyScreenDt", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var dest = Path.Combine(dir, name);
            await using (var src = await file.OpenReadAsync().ConfigureAwait(true))
            await using (var dst = File.Create(dest))
                await src.CopyToAsync(dst).ConfigureAwait(true);
            AddPath(dest);
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
        }
    }

    private async Task TryAddStorageFolderAsync(IStorageFolder folder)
    {
        var local = folder.TryGetLocalPath();
        if (!string.IsNullOrEmpty(local) && Directory.Exists(local))
        {
            AddPath(local);
            return;
        }

        try
        {
            var name = string.IsNullOrWhiteSpace(folder.Name) ? "folder" : Path.GetFileName(folder.Name);
            foreach (var c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            var destRoot = Path.Combine(Path.GetTempPath(), "FlyScreenDt", Guid.NewGuid().ToString("N"), name);
            await CopyStorageFolderAsync(folder, destRoot).ConfigureAwait(true);
            if (Directory.Exists(destRoot))
                AddPath(destRoot);
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
        }
    }

    private static async Task CopyStorageFolderAsync(IStorageFolder folder, string destDir)
    {
        Directory.CreateDirectory(destDir);
        await foreach (var item in folder.GetItemsAsync().ConfigureAwait(false))
        {
            if (item is IStorageFile file)
            {
                var name = string.IsNullOrWhiteSpace(file.Name) ? Guid.NewGuid().ToString("N") : Path.GetFileName(file.Name);
                foreach (var c in Path.GetInvalidFileNameChars())
                    name = name.Replace(c, '_');
                var dest = Path.Combine(destDir, name);
                await using var src = await file.OpenReadAsync().ConfigureAwait(false);
                await using var dst = File.Create(dest);
                await src.CopyToAsync(dst).ConfigureAwait(false);
            }
            else if (item is IStorageFolder sub)
            {
                var name = string.IsNullOrWhiteSpace(sub.Name) ? Guid.NewGuid().ToString("N") : Path.GetFileName(sub.Name);
                foreach (var c in Path.GetInvalidFileNameChars())
                    name = name.Replace(c, '_');
                await CopyStorageFolderAsync(sub, Path.Combine(destDir, name)).ConfigureAwait(false);
            }
        }
    }

    private void AddPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        path = path.Trim();
        if (_paths.Any(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase)))
            return;
        _paths.Add(path);
        UpdateDropHint();
        StatusText.Text = string.Format(Loc.T("DT.Selected"), _paths.Count);
    }

    private void RemoveSelected_Click(object? sender, RoutedEventArgs e)
    {
        var selected = PathList.SelectedItems?.OfType<string>().ToList() ?? [];
        foreach (var s in selected)
            _paths.Remove(s);
        UpdateDropHint();
    }

    private void ClearPaths_Click(object? sender, RoutedEventArgs e)
    {
        _paths.Clear();
        UpdateDropHint();
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close();

    private async void Start_Click(object? sender, RoutedEventArgs e)
    {
        if (_paths.Count == 0)
        {
            StatusText.Text = Loc.T("DT.NeedPaths");
            return;
        }

        if (PeerCombo.SelectedItem is not PeerOption peer)
        {
            StatusText.Text = Loc.T("DT.NeedPeer");
            return;
        }

        var dest = TargetPathBox.Text?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(dest))
        {
            StatusText.Text = Loc.T("DT.NeedPath");
            return;
        }

        if (!_host.Settings.TransferFile)
        {
            StatusText.Text = Loc.T("DT.NeedEnable");
            return;
        }

        StartBtn.IsEnabled = false;
        StatusText.Text = string.Format(Loc.T("DT.Started"), peer.Name);
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
        public override string ToString() => Label;
    }
}
