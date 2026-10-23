using System.Collections.ObjectModel;
using FlyScreen.AppMaui.Services;
using FlyScreen.Helpers.Localization;
#if WINDOWS
using FlyScreen.Win32Clipboard;
#endif

namespace FlyScreen.AppMaui;

public partial class DirectTransferPage : ContentPage
{
    private readonly MauiAppHost _host;
    private readonly ObservableCollection<PathItem> _paths = [];
    private readonly ObservableCollection<PeerOption> _peers = [];
    private readonly ObservableCollection<FolderPreset> _presets = [];
    private int _loadingFolders;
    private readonly TaskCompletionSource<bool> _tcs = new();
#if WINDOWS
    private ExplorerFileDropTarget? _explorerDrop;
#endif

    public Task<bool> Result => _tcs.Task;

    public DirectTransferPage(MauiAppHost host)
    {
        InitializeComponent();
        _host = host;
        Title = "";
        NavigationPage.SetHasNavigationBar(this, false);
        ApplyLocalizedUi();
        PathList.ItemsSource = _paths;
        PeerPicker.ItemsSource = _peers;
        FolderPresetPicker.ItemsSource = _presets;
        _paths.CollectionChanged += (_, _) => UpdateDropHint();
        Loaded += OnLoaded;
    }

    private void ApplyLocalizedUi()
    {
        AddFilesBtn.Text = Loc.T("DT.AddFiles");
        AddFolderBtn.Text = Loc.T("DT.AddFolder");
        RemoveSelectedBtn.Text = Loc.T("DT.Remove");
        ClearPathsBtn.Text = Loc.T("DT.Clear");
        ContentHintText.Text = Loc.T("DT.ContentHint");
        PathDropHint.Text = Loc.T("DT.DropHint");
        TargetHintText.Text = Loc.T("DT.TargetHint");
        PeerLabel.Text = Loc.T("DT.Peer");
        PresetLabel.Text = Loc.T("DT.Preset");
        PathLabel.Text = Loc.T("DT.Path");
        TargetPathBox.Placeholder = Loc.T("DT.PathTip");
        CancelBtn.Text = Loc.T("DT.Cancel");
        StartBtn.Text = Loc.T("DT.Start");
    }

    private void OnLoaded(object? sender, EventArgs e)
    {
        AttachExplorerDrop();
        RefreshPeers();
        if (_peers.Count == 0)
        {
            StatusText.Text = Loc.T("DT.NoPeers");
            StartBtn.IsEnabled = false;
        }
        else
        {
            PeerPicker.SelectedIndex = 0;
        }
    }

    private void AttachExplorerDrop()
    {
#if WINDOWS
        _explorerDrop?.Dispose();
        _explorerDrop = null;
        try
        {
            var win = Window ?? Application.Current?.Windows.FirstOrDefault();
            if (win?.Handler?.PlatformView is not Microsoft.UI.Xaml.Window native)
                return;
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(native);
            _explorerDrop = ExplorerFileDropTarget.TryAttach(hwnd, paths =>
            {
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    foreach (var p in paths)
                    {
                        if (File.Exists(p) || Directory.Exists(p))
                            AddPath(p);
                    }
                });
            });
        }
        catch
        {
            // ignore
        }
#endif
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
#if WINDOWS
        _explorerDrop?.Dispose();
        _explorerDrop = null;
#endif
        _tcs.TrySetResult(false);
    }

    private void UpdateDropHint()
        => PathDropHint.IsVisible = _paths.Count == 0;

    private IEnumerable<string> PathValues()
        => _paths.Select(p => p.Path);

    private void RefreshPeers()
    {
        _peers.Clear();
        foreach (var name in _host.Connections.ConfiguredPeers())
        {
            if (!_host.Connections.IsPeerReachable(name)) continue;
            _peers.Add(new PeerOption(name));
        }
    }

    private async void PeerPicker_SelectedIndexChanged(object? sender, EventArgs e)
    {
        if (PeerPicker.SelectedItem is not PeerOption peer) return;
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
                FolderPresetPicker.SelectedIndex = 0;
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

            FolderPresetPicker.SelectedIndex = 0;
            StatusText.Text = string.Format(Loc.T("DT.Connected"), peer, _paths.Count);
        }
        finally
        {
            Interlocked.Exchange(ref _loadingFolders, 0);
        }
    }

    private void FolderPreset_SelectedIndexChanged(object? sender, EventArgs e)
    {
        if (FolderPresetPicker.SelectedItem is not FolderPreset preset) return;
        if (!string.IsNullOrWhiteSpace(preset.Path))
            TargetPathBox.Text = preset.Path;
    }

    private async void AddFiles_Clicked(object? sender, EventArgs e)
    {
        try
        {
            List<string> files;
#if WINDOWS
            files = await MainThread.InvokeOnMainThreadAsync(() =>
                Platforms.Windows.Win32PathPicker.PickFiles(multiSelect: true)
                    .Where(File.Exists)
                    .ToList());
#else
            var picked = await FilePicker.Default.PickMultipleAsync(
                new PickOptions { PickerTitle = Loc.T("DT.PickFiles") }).ConfigureAwait(true);
            files = (picked ?? [])
                .Select(f => f?.FullPath)
                .Where(p => !string.IsNullOrEmpty(p) && File.Exists(p!))
                .Cast<string>()
                .ToList();
#endif
            foreach (var f in files)
                AddPath(f);
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
        }
    }

    private async void AddFolder_Clicked(object? sender, EventArgs e)
    {
        try
        {
#if WINDOWS
            var folder = await MainThread.InvokeOnMainThreadAsync(() =>
                Platforms.Windows.Win32PathPicker.PickFolder());
            if (!string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder))
                AddPath(folder);
#else
            // 未引入 CommunityToolkit FolderPicker；手机端请用「添加文件」
            await DisplayAlertAsync(Loc.T("DT.Title"), Loc.T("DT.FolderUnsupported"), "OK");
#endif
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
        }
    }

    private void AddPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        path = path.Trim();
        if (_paths.Any(p => string.Equals(p.Path, path, StringComparison.OrdinalIgnoreCase)))
            return;
        _paths.Add(new PathItem(path));
        UpdateDropHint();
        StatusText.Text = string.Format(Loc.T("DT.Selected"), _paths.Count);
    }

    private void RemoveSelected_Clicked(object? sender, EventArgs e)
    {
        var selected = _paths.Where(p => p.IsSelected).ToList();
        foreach (var s in selected)
            _paths.Remove(s);
        UpdateDropHint();
        StatusText.Text = string.Format(Loc.T("DT.Selected"), _paths.Count);
    }

    private void ClearPaths_Clicked(object? sender, EventArgs e)
    {
        _paths.Clear();
        UpdateDropHint();
        StatusText.Text = string.Format(Loc.T("DT.Selected"), 0);
    }

    private async void Start_Clicked(object? sender, EventArgs e)
    {
        if (_paths.Count == 0)
        {
            await DisplayAlertAsync(Loc.T("DT.Title"), Loc.T("DT.NeedPaths"), "OK");
            return;
        }

        if (PeerPicker.SelectedItem is not PeerOption peer)
        {
            await DisplayAlertAsync(Loc.T("DT.Title"), Loc.T("DT.NeedPeer"), "OK");
            return;
        }

        var target = TargetPathBox.Text?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(target))
        {
            await DisplayAlertAsync(Loc.T("DT.Title"), Loc.T("DT.NeedPath"), "OK");
            return;
        }

        if (!_host.Settings.TransferFile)
        {
            await DisplayAlertAsync(Loc.T("DT.Title"), Loc.T("DT.NeedEnable"), "OK");
            return;
        }

        StartBtn.IsEnabled = false;
        StatusText.Text = string.Format(Loc.T("DT.Started"), peer.Name);

        var snapshot = PathValues().ToList();
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
                await MainThread.InvokeOnMainThreadAsync(async () =>
                    await DisplayAlertAsync(Loc.T("DT.Title"), ex.Message, "OK"));
            }
        });

        _tcs.TrySetResult(true);
        await Navigation.PopModalAsync();
    }

    private async void Cancel_Clicked(object? sender, EventArgs e)
    {
        _tcs.TrySetResult(false);
        await Navigation.PopModalAsync();
    }

    private sealed class PathItem(string path) : System.ComponentModel.INotifyPropertyChanged
    {
        private bool _selected = true; // 添加/拖入默认勾选，便于「移除所选」
        public string Path { get; } = path;
        public bool IsSelected
        {
            get => _selected;
            set
            {
                if (_selected == value) return;
                _selected = value;
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsSelected)));
            }
        }

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    }

    private sealed class PeerOption(string name)
    {
        public string Name { get; } = name;
        public override string ToString() => Name;
    }

    private sealed class FolderPreset(string label, string path)
    {
        public string Label { get; } = label;
        public string Path { get; } = path;
        public override string ToString() => Label;
    }
}
