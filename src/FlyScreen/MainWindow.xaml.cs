using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using FlyScreen.Helpers;
using FlyScreen.Models;
using FlyScreen.Services;
using FlyScreen.Services.Network;
using WinForms = System.Windows.Forms;

namespace FlyScreen;

public partial class MainWindow : Window
{
    private static readonly Uri AppIconUri = new("pack://application:,,,/Assets/wlogo_tu.ico");

    private readonly AppHost _host;
    private readonly ObservableCollection<MachineSlot> _slots = [];
    private WinForms.NotifyIcon? _tray;
    private System.Drawing.Icon? _trayIcon;
    private System.Windows.Point _dragStart;
    private object? _dragItem;
    private bool _syncingNav;

    public MainWindow()
    {
        InitializeComponent();
        _host = AppHost.EnsureCreated();
        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        InitTray();
        LoadUiFromSettings();
        MachineMatrix.ItemsSource = _slots;
        TransferList.ItemsSource = _host.Transfers.Items;
        RefreshNics();
        RefreshDiscovery();
        // 等可视化树就绪后再应用行列，避免 2×2 切换时图标丢失
        _ = Dispatcher.BeginInvoke(() =>
        {
            ApplyLayoutGrid();
            RedrawArrows();
        }, System.Windows.Threading.DispatcherPriority.Loaded);

        _host.StatusMessage += msg => Dispatcher.Invoke(() => StatusText.Text = msg);
        _host.Connections.PeerLinkChanged += OnPeerLinkChanged;
        _host.Connections.Discovery.PeersUpdated += _ => Dispatcher.Invoke(RefreshDiscovery);

        _host.Clipboard.Start(this);
        await _host.StartAsync();
        StatusText.Text = $"监听 {_host.Connections.BindIp}:{_host.Settings.TcpPort}";

        // 开机自启：隐藏到托盘，后台已自动连接
        if (AutoStartHelper.IsAutostartLaunch(Environment.GetCommandLineArgs()))
        {
            StatusText.Text = $"已自启并连接 · {_host.Connections.BindIp}:{_host.Settings.TcpPort}";
            Hide();
        }
    }

    private void InitTray()
    {
        _trayIcon = LoadAppIcon();
        _tray = new WinForms.NotifyIcon
        {
            Text = "飞鼠",
            Visible = true,
            Icon = _trayIcon ?? System.Drawing.SystemIcons.Application
        };
        _tray.DoubleClick += (_, _) =>
        {
            Show();
            WindowState = WindowState.Normal;
            Activate();
        };
        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("打开设置", null, (_, _) => { Show(); Activate(); });
        menu.Items.Add("重新连接", null, async (_, _) => await _host.Connections.ReconnectNowAsync());
        menu.Items.Add("退出", null, (_, _) => ExitApp());
        _tray.ContextMenuStrip = menu;
    }

    private void LoadUiFromSettings()
    {
        var s = _host.Settings;
        s.EnsureLocalMachineSlot();
        SetSecurityKey(s.SecurityKey);
        if (s.TwoRow) Layout2x2.IsChecked = true;
        else Layout1x4.IsChecked = true;
        WrapMouseCheck.IsChecked = s.WrapMouse;
        ShareClipboardCheck.IsChecked = s.ShareClipboard;
        TransferFileCheck.IsChecked = s.TransferFile;
        HideMouseCheck.IsChecked = s.HideMouseAtEdge;
        DrawCursorCheck.IsChecked = s.DrawMouseCursor;
        SameSubnetCheck.IsChecked = s.SameSubnetOnly;
        ShowStatusCheck.IsChecked = s.ShowStatusMessages;
        BlockSaverCheck.IsChecked = s.BlockScreenSaver;
        MoveRelCheck.IsChecked = s.MoveMouseRelatively;
        BlockCornerCheck.IsChecked = s.BlockMouseAtCorners;
        EasyMouseCheck.IsChecked = s.EasyMouse;
        // 注册表与设置任一为开则视为开启，并写回一致状态
        var autoStart = AutoStartHelper.IsEnabled() || s.AutoStart;
        AutoStartCheck.IsChecked = autoStart;
        s.AutoStart = autoStart;
        AutoStartHelper.SetEnabled(autoStart);
        TcpPortBox.Text = s.TcpPort.ToString();
        UdpPortBox.Text = s.UdpPort.ToString();
        HotF.IsChecked = s.SwitchHotkeyMode == 0;
        HotNum.IsChecked = s.SwitchHotkeyMode == 1;
        HotOff.IsChecked = s.SwitchHotkeyMode == 2;
        HkSettings.Text = s.HotkeySettings;
        HkReconnect.Text = s.HotkeyReconnect;
        HkExit.Text = s.HotkeyExit;
        HkLock.Text = s.HotkeyLock;

        _slots.Clear();
        for (var i = 0; i < 4; i++)
        {
            var name = i < s.MachineSlots.Count ? s.MachineSlots[i] : "";
            var isLocal = string.Equals(name, Environment.MachineName, StringComparison.OrdinalIgnoreCase);
            _slots.Add(new MachineSlot
            {
                Index = i,
                Name = string.IsNullOrWhiteSpace(name) ? "" : name,
                Enabled = !string.IsNullOrWhiteSpace(name),
                IsLocal = isLocal,
                StatusText = isLocal ? "本机" : "未连接"
            });
        }

        ShowKeyCheck.IsChecked = false;
        ShowKey_Changed(null!, null!);
        ApplyLayoutGrid();
        RefreshPositionLabels();
        ShowPage(0);
    }

    private void LayoutMode_Changed(object sender, RoutedEventArgs e) => ApplyLayoutGrid();

    private void ApplyLayoutGrid()
    {
        if (Layout1x4 == null || Layout2x2 == null || MachineMatrix == null) return;
        var twoRow = Layout2x2.IsChecked == true;

        // 只改现有 UniformGrid 行列，避免重建 ItemsPanel 导致卡片/图标丢失
        MachineMatrix.ApplyTemplate();
        MachineMatrix.UpdateLayout();
        var grid = FindVisualChild<UniformGrid>(MachineMatrix);
        if (grid != null)
        {
            grid.Rows = twoRow ? 2 : 1;
            grid.Columns = twoRow ? 2 : 4;
        }

        RefreshPositionLabels();
        Dispatcher.BeginInvoke(RedrawArrows, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(parent);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typed) return typed;
            var nested = FindVisualChild<T>(child);
            if (nested != null) return nested;
        }

        return null;
    }

    private void RefreshPositionLabels()
    {
        var twoRow = Layout2x2?.IsChecked == true;
        for (var i = 0; i < _slots.Count; i++)
        {
            _slots[i].Index = i;
            _slots[i].SetLayoutHint(twoRow);
        }
    }

    private void ApplySettingsFromUi()
    {
        var s = _host.Settings;
        s.SecurityKey = GetSecurityKey().Trim();
        s.TwoRow = Layout2x2.IsChecked == true;
        s.WrapMouse = WrapMouseCheck.IsChecked == true;
        s.ShareClipboard = ShareClipboardCheck.IsChecked == true;
        s.TransferFile = TransferFileCheck.IsChecked == true;
        s.HideMouseAtEdge = HideMouseCheck.IsChecked == true;
        s.DrawMouseCursor = DrawCursorCheck.IsChecked == true;
        s.SameSubnetOnly = SameSubnetCheck.IsChecked == true;
        s.ShowStatusMessages = ShowStatusCheck.IsChecked == true;
        s.BlockScreenSaver = BlockSaverCheck.IsChecked == true;
        s.MoveMouseRelatively = MoveRelCheck.IsChecked == true;
        s.BlockMouseAtCorners = BlockCornerCheck.IsChecked == true;
        s.EasyMouse = EasyMouseCheck.IsChecked == true;
        s.AutoStart = AutoStartCheck.IsChecked == true;
        if (!AutoStartHelper.SetEnabled(s.AutoStart))
            StatusText.Text = s.AutoStart ? "开机自启写入失败，请检查权限" : "关闭开机自启失败";
        s.SwitchHotkeyMode = HotNum.IsChecked == true ? 1 : HotOff.IsChecked == true ? 2 : 0;
        s.HotkeySettings = HkSettings.Text.Trim();
        s.HotkeyReconnect = HkReconnect.Text.Trim();
        s.HotkeyExit = HkExit.Text.Trim();
        s.HotkeyLock = HkLock.Text.Trim();
        if (int.TryParse(TcpPortBox.Text, out var tcp)) s.TcpPort = tcp;
        if (int.TryParse(UdpPortBox.Text, out var udp)) s.UdpPort = udp;

        if (NicCombo.SelectedItem is NicInfo nic)
        {
            s.PreferredNicId = nic.Id;
            s.PreferredLocalIp = nic.IPv4;
        }

        s.MachineSlots = _slots.Select(x => x.Enabled ? x.Name.Trim() : "").ToList();
        while (s.MachineSlots.Count < 4) s.MachineSlots.Add("");
        s.EnsureLocalMachineSlot();
        s.Save();

        _host.Input.EasyMouseEnabled = s.EasyMouse;
    }

    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        ApplySettingsFromUi();
        await _host.RestartNetworkingAsync();
        StatusText.Text = "设置已应用并重新连接";
        SyncSlotLocalFlags();
    }

    private void SyncSlotLocalFlags()
    {
        foreach (var slot in _slots)
        {
            slot.IsLocal = string.Equals(slot.Name, Environment.MachineName, StringComparison.OrdinalIgnoreCase);
            if (slot.IsLocal) slot.RefreshStatus();
        }
    }

    private void Hide_Click(object sender, RoutedEventArgs e) => Hide();

    private void Exit_Click(object sender, RoutedEventArgs e) => ExitApp();

    private async void ExitApp()
    {
        if (_tray != null)
        {
            _tray.Visible = false;
            _tray.Dispose();
            _tray = null;
        }

        _trayIcon?.Dispose();
        _trayIcon = null;
        await _host.DisposeAsync();
        System.Windows.Application.Current.Shutdown();
    }

    private static System.Drawing.Icon? LoadAppIcon()
    {
        try
        {
            var info = System.Windows.Application.GetResourceStream(AppIconUri);
            if (info?.Stream == null) return null;
            // NotifyIcon 需要独立副本，流关闭后图标仍可用
            using var stream = info.Stream;
            using var temp = new System.Drawing.Icon(stream);
            return (System.Drawing.Icon)temp.Clone();
        }
        catch
        {
            return null;
        }
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        e.Cancel = true;
        Hide();
    }

    private void NewKey_Click(object sender, RoutedEventArgs e)
    {
        SetSecurityKey(AppSettings.GenerateKey());
        ShowKeyCheck.IsChecked = true;
        ShowKey_Changed(null!, null!);
    }

    private void ShowKey_Changed(object sender, RoutedEventArgs e)
    {
        var show = ShowKeyCheck.IsChecked == true;
        if (show)
        {
            SecurityKeyBox.Text = SecurityKeyPassword.Password;
            SecurityKeyBox.Visibility = Visibility.Visible;
            SecurityKeyPassword.Visibility = Visibility.Collapsed;
            SecurityKeyBox.Focus();
            SecurityKeyBox.CaretIndex = SecurityKeyBox.Text.Length;
        }
        else
        {
            SecurityKeyPassword.Password = SecurityKeyBox.Text;
            SecurityKeyPassword.Visibility = Visibility.Visible;
            SecurityKeyBox.Visibility = Visibility.Collapsed;
        }
    }

    private string GetSecurityKey()
        => ShowKeyCheck.IsChecked == true ? SecurityKeyBox.Text : SecurityKeyPassword.Password;

    private void SetSecurityKey(string key)
    {
        SecurityKeyBox.Text = key;
        SecurityKeyPassword.Password = key;
        ShowKey_Changed(null!, null!);
    }

    private async void Reconnect_Click(object sender, RoutedEventArgs e)
    {
        ApplySettingsFromUi();
        await _host.Connections.ReconnectNowAsync();
        StatusText.Text = "正在重新连接…";
    }

    private void RefreshNics_Click(object sender, RoutedEventArgs e) => RefreshNics();

    private void RefreshNics()
    {
        var nics = NicService.ListUsableIpv4Nics();
        NicCombo.ItemsSource = nics;
        var preferred = NicService.ResolvePreferred(_host.Settings.PreferredNicId, _host.Settings.PreferredLocalIp);
        if (preferred != null)
            NicCombo.SelectedItem = nics.FirstOrDefault(n => n.Id == preferred.Id && n.IPv4 == preferred.IPv4)
                                    ?? nics.FirstOrDefault();
        else if (nics.Count > 0)
            NicCombo.SelectedIndex = 0;
    }

    private void RefreshDiscovery_Click(object sender, RoutedEventArgs e) => RefreshDiscovery();

    private void RefreshDiscovery()
    {
        DiscoveryList.ItemsSource = UnusedDiscoveredPeers();
        foreach (var slot in _slots.Where(s => s.Enabled && !s.IsLocal))
        {
            var ip = _host.Connections.Discovery.FindIp(slot.Name);
            if (ip != null) slot.ResolvedIp = ip;
        }
    }

    private List<DiscoveredPeer> UnusedDiscoveredPeers()
    {
        var used = new HashSet<string>(
            _slots.Where(s => !string.IsNullOrWhiteSpace(s.Name)).Select(s => s.Name),
            StringComparer.OrdinalIgnoreCase);
        used.Add(Environment.MachineName);
        return _host.Connections.Discovery.GetPeers()
            .Where(p => !string.IsNullOrWhiteSpace(p.MachineName) && !used.Contains(p.MachineName))
            .ToList();
    }

    private void DiscoveryList_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DiscoveryList.SelectedItem is not DiscoveredPeer peer) return;
        TryAddPeer(peer);
    }

    private void AddDiscovered_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;
        if (fe.Tag is DiscoveredPeer peer)
            TryAddPeer(peer);
        else if (DiscoveryList.SelectedItem is DiscoveredPeer selected)
            TryAddPeer(selected);
    }

    private void AddMachine_Click(object sender, RoutedEventArgs e)
    {
        var unused = UnusedDiscoveredPeers();
        if (unused.Count == 0)
        {
            ShowPage(2);
            RefreshDiscovery();
            StatusText.Text = "暂无新发现的电脑，请先刷新发现或确认对端已启动";
            return;
        }

        var menu = new ContextMenu();
        foreach (var peer in unused)
        {
            var item = new MenuItem
            {
                Header = string.IsNullOrEmpty(peer.Ip) ? peer.MachineName : $"{peer.MachineName}  ({peer.Ip})",
                Tag = peer
            };
            item.Click += (_, _) =>
            {
                if (item.Tag is DiscoveredPeer p) TryAddPeer(p);
            };
            menu.Items.Add(item);
        }

        menu.PlacementTarget = sender as UIElement;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void RemoveSlot_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        var slot = FindSlotFromElement(sender as DependencyObject);
        if (slot == null || !slot.CanRemove) return;

        var name = slot.Name;
        slot.Name = "";
        slot.Enabled = false;
        slot.IsLocal = false;
        slot.ResolvedIp = "";
        slot.OutboundOk = false;
        slot.InboundOk = false;
        slot.RefreshStatus();
        PersistSlots();
        RefreshDiscovery();
        RedrawArrows();
        StatusText.Text = $"已移除 {name}，可在局域网自动发现中再添加";
        _ = RestartAfterSlotChangeAsync();
    }

    private void TryAddPeer(DiscoveredPeer peer)
    {
        if (string.Equals(peer.MachineName, Environment.MachineName, StringComparison.OrdinalIgnoreCase))
        {
            StatusText.Text = "不能添加本机";
            return;
        }

        if (_slots.Any(s => string.Equals(s.Name, peer.MachineName, StringComparison.OrdinalIgnoreCase)))
        {
            StatusText.Text = $"{peer.MachineName} 已在布局中";
            return;
        }

        var empty = _slots.FirstOrDefault(s => string.IsNullOrWhiteSpace(s.Name));
        if (empty == null)
        {
            System.Windows.MessageBox.Show("没有空的机器槽位，请先移除一台电脑。", "飞鼠");
            return;
        }

        empty.Enabled = true;
        empty.Name = peer.MachineName;
        empty.ResolvedIp = peer.Ip;
        empty.IsLocal = false;
        empty.OutboundOk = false;
        empty.InboundOk = false;
        empty.RefreshStatus();
        PersistSlots();
        RefreshDiscovery();
        RedrawArrows();
        StatusText.Text = $"已添加 {peer.MachineName}";
        _ = RestartAfterSlotChangeAsync();
    }

    private void PersistSlots()
    {
        var s = _host.Settings;
        s.MachineSlots = _slots
            .Select(x => string.IsNullOrWhiteSpace(x.Name) ? "" : x.Name.Trim())
            .ToList();
        while (s.MachineSlots.Count < 4) s.MachineSlots.Add("");
        s.EnsureLocalMachineSlot();
        s.Save();
        SyncSlotLocalFlags();
    }

    private async Task RestartAfterSlotChangeAsync()
    {
        try
        {
            await _host.RestartNetworkingAsync().ConfigureAwait(true);
        }
        catch
        {
            // ignore reconnect errors
        }
    }

    private void OnPeerLinkChanged(string name, bool outbound, bool inbound)
    {
        Dispatcher.Invoke(() =>
        {
            var slot = _slots.FirstOrDefault(s =>
                string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
            if (slot == null) return;
            slot.OutboundOk = outbound;
            slot.InboundOk = inbound;
            var ip = _host.Connections.Discovery.FindIp(name);
            if (ip != null) slot.ResolvedIp = ip;
        });
    }

    private void OpenInbox_Click(object sender, RoutedEventArgs e)
    {
        var dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "FlyScreen", "Inbox");
        Directory.CreateDirectory(dir);
        Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
    }

    private void ClearTransfers_Click(object sender, RoutedEventArgs e) => _host.Transfers.ClearCompleted();

    private void DeleteTransfer_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement { Tag: TransferItem item })
            _host.Transfers.RemoveItem(item, deleteCache: true);
    }

    private void Matrix_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(null);
        // 点在按钮上不开始拖拽
        if (IsOverInteractive(e.OriginalSource as DependencyObject))
        {
            _dragItem = null;
            return;
        }

        _dragItem = FindSlotFromElement(e.OriginalSource as DependencyObject);
    }

    private void Matrix_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragItem is not MachineSlot) return;
        // 在 TextBox/CheckBox 上拖动时不启动，避免干扰输入
        if (e.OriginalSource is System.Windows.Controls.Primitives.TextBoxBase
            or System.Windows.Controls.Primitives.ButtonBase
            || IsOverInteractive(e.OriginalSource as DependencyObject))
            return;

        var pos = e.GetPosition(null);
        if (Math.Abs(pos.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(pos.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        DragDrop.DoDragDrop(MachineMatrix, _dragItem, System.Windows.DragDropEffects.Move);
    }

    private void Matrix_DragOver(object sender, System.Windows.DragEventArgs e)
    {
        e.Effects = System.Windows.DragDropEffects.Move;
        e.Handled = true;
    }

    private void Matrix_Drop(object sender, System.Windows.DragEventArgs e)
    {
        if (_dragItem is not MachineSlot src) return;
        var target = FindSlotFromElement(e.OriginalSource as DependencyObject);
        if (target == null || ReferenceEquals(src, target)) return;

        // 互换两个格子内容，保持矩阵位置语义
        SwapSlotData(src, target);
        SyncSlotLocalFlags();
        RefreshPositionLabels();
        RedrawArrows();
        e.Handled = true;
    }

    private static void SwapSlotData(MachineSlot a, MachineSlot b)
    {
        (a.Name, b.Name) = (b.Name, a.Name);
        (a.Enabled, b.Enabled) = (b.Enabled, a.Enabled);
        (a.IsLocal, b.IsLocal) = (b.IsLocal, a.IsLocal);
        (a.ResolvedIp, b.ResolvedIp) = (b.ResolvedIp, a.ResolvedIp);
        var outA = a.OutboundOk;
        var inA = a.InboundOk;
        a.OutboundOk = b.OutboundOk;
        a.InboundOk = b.InboundOk;
        b.OutboundOk = outA;
        b.InboundOk = inA;
        a.RefreshStatus();
        b.RefreshStatus();
    }

    private static MachineSlot? FindSlotFromElement(DependencyObject? element)
    {
        while (element != null)
        {
            if (element is FrameworkElement fe)
            {
                if (fe.DataContext is MachineSlot slot)
                    return slot;
                if (fe.Tag is MachineSlot tagged)
                    return tagged;
            }

            element = VisualTreeHelper.GetParent(element);
        }

        return null;
    }

    private static bool IsOverInteractive(DependencyObject? element)
    {
        while (element != null)
        {
            if (element is System.Windows.Controls.Primitives.ButtonBase) return true;
            element = VisualTreeHelper.GetParent(element);
        }

        return false;
    }

    private void Nav_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _syncingNav) return;
        if (NavNetwork.IsChecked == true) ShowPage(2);
        else if (NavShare.IsChecked == true) ShowPage(3);
        else if (NavAdvanced.IsChecked == true) ShowPage(1);
        else ShowPage(0);
    }

    private void TopTab_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _syncingNav) return;
        if (TabOptions.IsChecked == true) ShowPage(1);
        else if (TabNetwork.IsChecked == true) ShowPage(2);
        else if (TabTransfer.IsChecked == true) ShowPage(3);
        else ShowPage(0);
    }

    private void ShowPage(int index)
    {
        PageBasic.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
        PageOptions.Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed;
        PageNetwork.Visibility = index == 2 ? Visibility.Visible : Visibility.Collapsed;
        PageTransfer.Visibility = index == 3 ? Visibility.Visible : Visibility.Collapsed;

        _syncingNav = true;
        try
        {
            switch (index)
            {
                case 0:
                    TabBasic.IsChecked = true;
                    if (NavSecurity.IsChecked != true) NavBasic.IsChecked = true;
                    break;
                case 1:
                    TabOptions.IsChecked = true;
                    NavAdvanced.IsChecked = true;
                    break;
                case 2:
                    TabNetwork.IsChecked = true;
                    NavNetwork.IsChecked = true;
                    break;
                case 3:
                    TabTransfer.IsChecked = true;
                    NavShare.IsChecked = true;
                    break;
            }
        }
        finally { _syncingNav = false; }

        if (index == 0)
            Dispatcher.BeginInvoke(RedrawArrows, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var q = SearchBox.Text.Trim();
        if (string.IsNullOrEmpty(q)) return;
        if (q.Contains("网络") || q.Contains("网卡") || q.Contains("发现"))
            ShowPage(2);
        else if (q.Contains("文件") || q.Contains("传输") || q.Contains("共享") || q.Contains("剪贴"))
            ShowPage(3);
        else if (q.Contains("快捷") || q.Contains("高级") || q.Contains("选项"))
            ShowPage(1);
        else if (q.Contains("密钥") || q.Contains("布局") || q.Contains("安全"))
            ShowPage(0);
    }

    private void Help_Click(object sender, RoutedEventArgs e)
    {
        System.Windows.MessageBox.Show(
            "使用说明：\n\n" +
            "1. 两台电脑设置相同的安全密钥\n" +
            "2. 在「网络设置」选择正确网卡\n" +
            "3. 在「电脑布局」中按真实摆放拖拽电脑位置\n" +
            "4. 点击「应用」后，鼠标移出屏幕边缘即可切换\n\n" +
            "快捷键：Ctrl+Alt+M 打开设置，Ctrl+Alt+R 重连，Ctrl+Alt+Q 退出",
            "飞鼠 — 帮助");
    }

    private void MachineMatrix_SizeChanged(object sender, SizeChangedEventArgs e) => RedrawArrows();

    private void RedrawArrows()
    {
        if (ArrowCanvas == null || MachineMatrix == null) return;
        ArrowCanvas.Children.Clear();
        // 窗口初始化时 SizeChanged 可能早于槽位填充
        if (_slots.Count < 4) return;

        var twoRow = Layout2x2.IsChecked == true;
        var cols = twoRow ? 2 : 4;
        var rows = twoRow ? 2 : 1;
        var w = MachineMatrix.ActualWidth;
        var h = MachineMatrix.ActualHeight;
        if (w < 10 || h < 10) return;

        ArrowCanvas.Width = w;
        ArrowCanvas.Height = h;

        var brush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x60, 0xA5, 0xFA));
        brush.Freeze();

        void DrawLink(int from, int to)
        {
            if (from < 0 || to < 0 || from >= _slots.Count || to >= _slots.Count) return;
            if (!_slots[from].Enabled || !_slots[to].Enabled) return;

            var (fx, fy) = CellCenter(from, cols, rows, w, h);
            var (tx, ty) = CellCenter(to, cols, rows, w, h);
            // 缩短一点，避免压住卡片
            var dx = tx - fx;
            var dy = ty - fy;
            var len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 1) return;
            var ux = dx / len;
            var uy = dy / len;
            const double inset = 36;
            var x1 = fx + ux * inset;
            var y1 = fy + uy * inset;
            var x2 = tx - ux * inset;
            var y2 = ty - uy * inset;

            var line = new Line
            {
                X1 = x1, Y1 = y1, X2 = x2, Y2 = y2,
                Stroke = brush,
                StrokeThickness = 1.5,
                StrokeDashArray = new DoubleCollection { 4, 3 }
            };
            ArrowCanvas.Children.Add(line);

            var angle = Math.Atan2(uy, ux);
            const double size = 7;
            var poly = new Polygon
            {
                Fill = brush,
                Points =
                [
                    new System.Windows.Point(x2, y2),
                    new System.Windows.Point(x2 - size * Math.Cos(angle - 0.4), y2 - size * Math.Sin(angle - 0.4)),
                    new System.Windows.Point(x2 - size * Math.Cos(angle + 0.4), y2 - size * Math.Sin(angle + 0.4))
                ]
            };
            ArrowCanvas.Children.Add(poly);
        }

        if (twoRow)
        {
            DrawLink(0, 1);
            DrawLink(2, 3);
            DrawLink(0, 2);
            DrawLink(1, 3);
        }
        else
        {
            DrawLink(0, 1);
            DrawLink(1, 2);
            DrawLink(2, 3);
        }
    }

    private static (double x, double y) CellCenter(int index, int cols, int rows, double w, double h)
    {
        var r = index / cols;
        var c = index % cols;
        var cw = w / cols;
        var ch = h / rows;
        return (c * cw + cw / 2, r * ch + ch / 2);
    }
}
