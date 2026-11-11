using System.Collections.ObjectModel;
using FlyScreen.AppMaui.Helpers;
using FlyScreen.AppMaui.Services;
using FlyScreen.Helpers.Localization;
using FlyScreen.Services.Clipboard;
#if WINDOWS
using FlyScreen.Win32Clipboard;
#endif
#if ANDROID
using FlyScreen.AppMaui.Platforms.Android;
#endif

namespace FlyScreen.AppMaui;

public partial class DirectTransferPage : ContentPage
{
    private const string IcAddFiles = "＋";
    private const string IcAddFolder = "📁";
    private const string IcRemove = "－";
    private const string IcClear = "✕";

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
        SizeChanged += (_, _) => ApplyToolbarIcons();
        Loaded += OnLoaded;
    }

    private bool IsPhoneOrTabletUi()
    {
        var idiom = DeviceInfo.Idiom;
        return (idiom == DeviceIdiom.Phone || idiom == DeviceIdiom.Tablet)
               && DeviceInfo.Platform != DevicePlatform.WinUI;
    }

    private void ApplyLocalizedUi()
    {
        TargetHintText.Text = Loc.T("DT.TargetHint");
        PeerLabel.Text = Loc.T("DT.Peer");
        PresetLabel.Text = Loc.T("DT.Preset");
        PathLabel.Text = Loc.T("DT.Path");
        TargetPathBox.Placeholder = Loc.T("DT.PathTip");
        CancelBtn.Text = Loc.T("DT.Cancel");
        StartBtn.Text = Loc.T("DT.Start");
        ApplyToolbarIcons();

        if (IsPhoneOrTabletUi())
        {
#if ANDROID
            ContentHintText.Text = Loc.T("DT.MobileContentHintAndroid");
            PathDropHint.Text = Loc.T("DT.MobileDropHintAndroid");
#elif IOS || MACCATALYST
            ContentHintText.Text = Loc.T("DT.MobileContentHint");
            PathDropHint.Text = Loc.T("DT.MobileDropHint");
            AddFolderBtn.IsVisible = false;
#else
            ContentHintText.Text = Loc.T("DT.MobileContentHint");
            PathDropHint.Text = Loc.T("DT.MobileDropHint");
#endif
        }
        else
        {
            ContentHintText.Text = Loc.T("DT.ContentHint");
            PathDropHint.Text = Loc.T("DT.DropHint");
        }
    }

    /// <summary>窄屏/手机：工具栏只留符号，避免裁切；宽屏符号+文字。</summary>
    private void ApplyToolbarIcons()
    {
        var narrow = IsPhoneOrTabletUi() || Width > 0 && Width < 640;
        AddFilesBtn.Text = narrow ? IcAddFiles : $"{IcAddFiles}  {Loc.T("DT.AddFiles")}";
        AddFolderBtn.Text = narrow ? IcAddFolder : $"{IcAddFolder}  {Loc.T("DT.AddFolder")}";
        RemoveSelectedBtn.Text = narrow ? IcRemove : $"{IcRemove}  {Loc.T("DT.Remove")}";
        ClearPathsBtn.Text = narrow ? IcClear : $"{IcClear}  {Loc.T("DT.Clear")}";

        var pad = narrow ? new Thickness(0) : new Thickness(10, 0);
        foreach (var b in new[] { AddFilesBtn, AddFolderBtn, RemoveSelectedBtn, ClearPathsBtn })
        {
            b.Padding = pad;
            b.MinimumWidthRequest = narrow ? 34 : 36;
            b.WidthRequest = narrow ? 34 : -1;
            ToolTipProperties.SetText(b, b == AddFilesBtn ? Loc.T("DT.AddFiles")
                : b == AddFolderBtn ? Loc.T("DT.AddFolder")
                : b == RemoveSelectedBtn ? Loc.T("DT.Remove")
                : Loc.T("DT.Clear"));
        }
    }

    private async void OnLoaded(object? sender, EventArgs e)
    {
        AttachExplorerDrop();
        ApplyToolbarIcons();
        // 避免 WinUI 残留 Title 浮标
        SyncAndroidPickerTitle(PeerPicker, "");
        SyncAndroidPickerTitle(FolderPresetPicker, "");
        RefreshPeers();
        if (_peers.Count == 0)
        {
            StatusText.Text = Loc.T("DT.NoPeers");
            StartBtn.IsEnabled = false;
            return;
        }

        // Android Picker 程序化设 SelectedIndex 常不触发 SelectedIndexChanged，须显式拉目录
        PeerPicker.SelectedIndex = 0;
        if (PeerPicker.SelectedItem is PeerOption peer)
            await LoadRemoteFoldersAsync(peer.Name).ConfigureAwait(true);
        else if (_peers.Count > 0)
            await LoadRemoteFoldersAsync(_peers[0].Name).ConfigureAwait(true);
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
        SyncAndroidPickerTitle(PeerPicker, peer.Name);
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
            var info = _host.Connections.GetPeerRemoteInfo(peer);
            _presets.Clear();
            foreach (var (label, path) in DirectTransferDestinations.BuildPresets(folders, info))
                _presets.Add(new FolderPreset(label, path));

            if (folders == null)
                StatusText.Text = string.Format(Loc.T("DT.QueryFail"), peer);
            else
                StatusText.Text = string.Format(Loc.T("DT.Connected"), peer, _paths.Count);

            ApplyFirstPresetToPath();
        }
        finally
        {
            Interlocked.Exchange(ref _loadingFolders, 0);
        }
    }

    private void ApplyFirstPresetToPath()
    {
        if (_presets.Count == 0)
        {
            TargetPathBox.Text = "";
            SyncAndroidPickerTitle(FolderPresetPicker, Loc.T("DT.Preset"));
            return;
        }

        FolderPresetPicker.SelectedIndex = 0;
        var first = _presets[0];
        TargetPathBox.Text = first.Path;
        // Title 仅 Android 兜底；WinUI 上 Title 会浮在框外且颜色发灰，破坏布局
        SyncAndroidPickerTitle(FolderPresetPicker, first.Label);
        if (PeerPicker.SelectedItem is PeerOption peer)
            SyncAndroidPickerTitle(PeerPicker, peer.Name);
        else if (_peers.Count > 0)
            SyncAndroidPickerTitle(PeerPicker, _peers[0].Name);
    }

    private void FolderPreset_SelectedIndexChanged(object? sender, EventArgs e)
    {
        if (FolderPresetPicker.SelectedItem is not FolderPreset preset) return;
        TargetPathBox.Text = preset.Path ?? "";
        SyncAndroidPickerTitle(FolderPresetPicker, preset.Label);
    }

    /// <summary>
    /// Android Picker 常不画 SelectedItem，用 Title 显示当前值。
    /// WinUI/桌面必须保持 Title 为空，否则会出现框外浮字 + 框内空白。
    /// </summary>
    private static void SyncAndroidPickerTitle(Picker picker, string text)
    {
#if ANDROID
        picker.Title = text ?? "";
#else
        picker.Title = "";
#endif
    }

    private async void AddFiles_Clicked(object? sender, EventArgs e)
    {
        try
        {
            StatusText.Text = Loc.T("DT.Preparing");
            List<string> files;
#if WINDOWS
            files = await MainThread.InvokeOnMainThreadAsync(() =>
                Platforms.Windows.Win32PathPicker.PickFiles(multiSelect: true)
                    .Where(File.Exists)
                    .ToList());
#else
            var picked = await FilePicker.Default.PickMultipleAsync(
                new PickOptions { PickerTitle = Loc.T("DT.PickFiles") }).ConfigureAwait(true);
            files = (await MauiPickedFileStore.MaterializeManyAsync(picked ?? [])
                .ConfigureAwait(true)).ToList();
#endif
            foreach (var f in files)
                AddPath(f);
            if (files.Count == 0 && _paths.Count == 0)
                StatusText.Text = Loc.T("DT.NeedPaths");
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
#elif ANDROID
            StatusText.Text = Loc.T("DT.Preparing");
            var local = await AndroidFolderMaterializer.PickAndMaterializeAsync().ConfigureAwait(true);
            if (!string.IsNullOrWhiteSpace(local) && Directory.Exists(local))
                AddPath(local);
            else if (_paths.Count == 0)
                StatusText.Text = Loc.T("DT.NeedPaths");
#else
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
