using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
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
        PathList.ItemsSource = _paths;
        PeerCombo.ItemsSource = _peers;
        FolderPresetCombo.ItemsSource = _presets;
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        RefreshPeers();
        if (_peers.Count == 0)
        {
            StatusText.Text = "当前没有已连接的目标电脑，请先在布局中连接对端。";
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
            StatusText.Text = $"正在查询 {peer} 的常用目录…";
            _presets.Clear();
            TargetPathBox.Text = "";

            var folders = await _host.Transfers.QueryRemoteFoldersAsync(peer).ConfigureAwait(true);
            _presets.Clear();

            if (folders == null)
            {
                StatusText.Text = $"无法获取 {peer} 的目录信息，请手动填写保存路径。";
                _presets.Add(new FolderPreset("自定义路径", ""));
                FolderPresetCombo.SelectedIndex = 0;
                return;
            }

            void Add(string label, string path)
            {
                if (!string.IsNullOrWhiteSpace(path))
                    _presets.Add(new FolderPreset(label, path.Trim()));
            }

            Add("桌面", folders.Desktop);
            Add("文档", folders.Documents);
            Add("下载", folders.Downloads);
            Add("玄鸟收件箱", folders.Inbox);
            _presets.Add(new FolderPreset("自定义路径…", folders.Downloads));

            FolderPresetCombo.SelectedIndex = 0;
            StatusText.Text = $"已连接 {peer}，共 {_paths.Count} 项待传。";
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
            Title = "选择要传输的文件",
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
            Title = "选择要传输的文件夹",
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
        StatusText.Text = $"已选 {_paths.Count} 项。";
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
            MessageBox.Show(this, "请先添加文件或文件夹。", "直接传输",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (PeerCombo.SelectedItem is not PeerOption peer)
        {
            MessageBox.Show(this, "请选择目标电脑。", "直接传输",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var target = TargetPathBox.Text?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(target))
        {
            MessageBox.Show(this, "请填写对端保存路径。", "直接传输",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!_host.Settings.TransferFile)
        {
            MessageBox.Show(this, "请先在设置中启用「文件传输」。", "直接传输",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        StartBtn.IsEnabled = false;
        StatusText.Text = $"已开始向 {peer.Name} 传输，可在传输列表查看进度。";

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
                    MessageBox.Show(ex.Message, "直接传输", MessageBoxButton.OK, MessageBoxImage.Error));
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
    }
}
