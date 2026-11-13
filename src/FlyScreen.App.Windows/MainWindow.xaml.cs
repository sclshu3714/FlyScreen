using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using FlyScreen.Helpers;
using FlyScreen.Helpers.Localization;
using FlyScreen.Models;
using FlyScreen.Services;
using FlyScreen.Services.Network;
using FlyScreen.Services.Programs;
using FlyScreen.Services.ScreenExtend;
using FlyScreen.Services.Tools;
using FlyScreen.Services.Tools.Hosting;
using FlyScreen.Services.Update;
using FlyScreen.Tray;
using WinForms = System.Windows.Forms;
using Media = System.Windows.Media;

namespace FlyScreen;

public partial class MainWindow : Window
{
    private static readonly Uri AppIconUri = new("pack://application:,,,/Assets/wlogo_tu.ico");

    private readonly AppHost _host;
    private readonly ObservableCollection<MachineSlot> _slots = [];
    private readonly ObservableCollection<ConnectionEndpoint> _endpoints = [];
    private readonly ObservableCollection<DeviceInventoryItem> _deviceInventory = [];
    private string _networkSubPage = "conn"; // conn | devices
    private readonly Controls.FlyBreadcrumbBar _networkBreadcrumb = new();
    private readonly Controls.FlyBreadcrumbBar _toolsBreadcrumb = new();
    private bool _toolsBreadcrumbWired;
    private int _discoveryRefreshQueued;
    private bool _refreshDiscoveryRunning;
    private bool _refreshDiscoveryPending;
    private WinForms.NotifyIcon? _tray;
    private System.Drawing.Icon? _trayIcon;
    private System.Windows.Point _dragStart;
    private object? _dragItem;
    private bool _syncingNav;
    private readonly GitHubUpdateService _helpUpdater = new();
    private UpdateCheckResult? _helpLastCheck;
    private bool _helpUpdateReady;
    private bool _helpFixReady;
    private bool _progToolModeUpdating;
    private bool _optionsFeatStacked;

    public static readonly DependencyProperty ProgToolsCompactProperty =
        DependencyProperty.Register(nameof(ProgToolsCompact), typeof(bool), typeof(MainWindow), new PropertyMetadata(false));

    public bool ProgToolsCompact
    {
        get => (bool)GetValue(ProgToolsCompactProperty);
        set => SetValue(ProgToolsCompactProperty, value);
    }

    public static readonly DependencyProperty LogToolsCompactProperty =
        DependencyProperty.Register(nameof(LogToolsCompact), typeof(bool), typeof(MainWindow), new PropertyMetadata(false));

    public bool LogToolsCompact
    {
        get => (bool)GetValue(LogToolsCompactProperty);
        set => SetValue(LogToolsCompactProperty, value);
    }

    private bool _logToolModeUpdating;
    private string? _logProgramId;
    private string? _logProgramName;
    private bool _openProgramLog;
    private int _logPageFromLatest;
    private int _logTotalPages;
    private DateTime? _logFrom;
    private DateTime? _logTo;
    private readonly ObservableCollection<AppLogEntry> _logItems = new();
    private bool _logStickToBottom = true;
    private bool _logFilterReady;
    private int _logKnownTotal;
    private AppLogEntry? _logWatermark;

    public MainWindow()
    {
        InitializeComponent();
        ToolIconImageFactory.ClearCache();
        ToolIconRegistry.EnsureFromLoadedAssemblies();
        ApplyWindowIcon();
        _host = AppHost.EnsureCreated();
        _host.ScreenExtendUi.ListAddablePeers = () => ProjectionAddTargets.List(
            _host.Connections,
            _host.Connections.Discovery.GetPeers().Select(p => p.MachineName),
            _host.ScreenExtend.GetSinkPeers());
        _host.ScreenExtendUi.StartExtraProjection = async remote =>
        {
            var choice = RemoteProjectChoice.FromSettings(_host.Settings, LocalPeerIdentity.MachineName);
            await _host.Connections.EnsureAdHocPeerAsync(remote).ConfigureAwait(true);
            var (ok, err) = await _host.ScreenExtend.RequestPeerBecomeHostAsync(
                remote, LocalPeerIdentity.MachineName,
                preferredWidth: choice.Width, preferredHeight: choice.Height,
                fps: choice.Fps, remoteControl: true, jpegQuality: choice.JpegQuality)
                .ConfigureAwait(true);
            if (!ok)
                StatusText.Text = string.Format(Loc.T("Remote.ProjectFail"), err ?? "");
        };
        // 开机自启：先藏主窗再挂托盘，避免未渲染完的黑块闪一下/卡住
        if (AutoStartHelper.IsAutostartLaunch(Environment.GetCommandLineArgs()))
        {
            ShowInTaskbar = false;
            Visibility = System.Windows.Visibility.Hidden;
            WindowState = WindowState.Minimized;
        }
        Loaded += OnLoaded;
        Closing += OnClosing;
        SourceInitialized += (_, _) =>
        {
            try
            {
                var helper = new System.Windows.Interop.WindowInteropHelper(this);
                FlyScreen.WinApi.WindowsAppUserModel.ApplyToWindow(
                    helper.Handle,
                    FlyScreen.WinApi.WindowsAppUserModel.MainAppId,
                    FlyScreen.WinApi.WindowsAppUserModel.MainDisplayName,
                    System.IO.Path.Combine("Assets", "wlogo_tu.ico"),
                    "wlogo_tu.ico");
                ApplyWindowRoundedShadow();
            }
            catch
            {
                // 任务栏显示名 / DWM 圆角失败不挡主窗
            }
        };
        UpdateTitleMaxGlyph();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            return;
        }

        if (e.ChangedButton == MouseButton.Left)
            DragMove();
    }

    private bool _titleProjectHovered;

    private void TitleProject_Click(object sender, RoutedEventArgs e)
        => _ = OpenProjectionFromUiAsync();

    /// <summary>标题栏 / 托盘「投影」：与主界面同一流程（进行中则停止，接收中则提示，否则弹窗）。</summary>
    private async Task OpenProjectionFromUiAsync()
    {
        if (_host.ScreenExtend.IsActive && _host.ScreenExtend.Role == ScreenExtendRole.Host)
        {
            await _host.StopProjectionAsync();
            RefreshTitleProjectButton();
            return;
        }

        if (_host.ScreenExtend.IsActive && _host.ScreenExtend.Role == ScreenExtendRole.Sink)
        {
            MessageBox.Show(this,
                ScreenExtendConflict.DescribeLocalBusy(
                    ScreenExtendRole.Sink, _host.ScreenExtend.Peer),
                "投影", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        ShowFromTray();
        var dlg = new ProjectionDialog(_host) { Owner = this };
        dlg.ShowDialog();
        RefreshTitleProjectButton();
    }

    private void EnsureTitleProjectHoverHooks()
    {
        if (TitleProjectBtn == null || TitleProjectBtn.Tag as string == "projHover") return;
        TitleProjectBtn.Tag = "projHover";
        TitleProjectBtn.MouseEnter += (_, _) =>
        {
            _titleProjectHovered = true;
            RefreshTitleProjectButton();
        };
        TitleProjectBtn.MouseLeave += (_, _) =>
        {
            _titleProjectHovered = false;
            RefreshTitleProjectButton();
        };
    }

    private void RefreshTitleProjectButton()
    {
        if (TitleProjectBtn == null) return;
        EnsureTitleProjectHoverHooks();
        var phase = ProjectionUi.Resolve(_host.ScreenExtend);
        var chrome = ProjectionUi.Title(phase, _titleProjectHovered);
        TitleProjectBtn.ToolTip = chrome.Tooltip;
        TitleProjectBtn.Width = chrome.SuggestedWidth;
        TitleProjectBtn.Padding = new Thickness(8, 0, 8, 0);
        TitleProjectBtn.Foreground = TryFindResource(chrome.Emphasized ? "Blue" : "Text") as System.Windows.Media.Brush
            ?? System.Windows.Media.Brushes.Black;
        TitleProjectBtn.Content = BuildProjectionTitleContent(chrome);
    }

    private System.Windows.Controls.StackPanel BuildProjectionTitleContent(ProjectionUiChrome chrome)
    {
        var fg = TitleProjectBtn.Foreground;
        var icon = new System.Windows.Controls.TextBlock
        {
            Text = chrome.Mdl2Glyph,
            FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"),
            FontSize = 13,
            Foreground = fg,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 5, 0)
        };
        var label = new System.Windows.Controls.TextBlock
        {
            Text = chrome.Label,
            FontFamily = new System.Windows.Media.FontFamily("Segoe UI, Microsoft YaHei UI"),
            FontSize = 11,
            Foreground = fg,
            VerticalAlignment = VerticalAlignment.Center
        };
        var row = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
        row.Children.Add(icon);
        row.Children.Add(label);
        return row;
    }

    private void ApplyWindowIcon()
    {
        var icon = AppIconImage.Load();
        if (icon == null) return;
        Icon = icon;
        if (TitleBarLogo != null)
            TitleBarLogo.Source = icon;
    }

    private void TitleMin_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void TitleMax_Click(object sender, RoutedEventArgs e)
        => ToggleMaximize();

    private void TitleClose_Click(object sender, RoutedEventArgs e)
        => Close();

    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        UpdateTitleMaxGlyph();
        UpdateWindowOuterBorder();
        ApplyWindowRoundedShadow();
    }

    private void ToggleMaximize()
        => WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;

    private void UpdateTitleMaxGlyph()
    {
        if (TitleMaxBtn == null) return;
        // 最大化后显示「还原」样式
        TitleMaxBtn.Content = WindowState == WindowState.Maximized ? "❐" : "☐";
    }

    private void UpdateWindowOuterBorder()
    {
        if (WindowOuterBorder == null) return;
        var maximized = WindowState == WindowState.Maximized;
        // 最大化时去掉自绘外框/圆角，避免贴边露缝；还原态对齐 Maui-Win 圆角边框
        WindowOuterBorder.BorderThickness = maximized ? new Thickness(0) : new Thickness(1);
        WindowOuterBorder.CornerRadius = maximized ? new CornerRadius(0) : new CornerRadius(8);
    }

    /// <summary>Win11 DWM 圆角 + 投影（GlassFrameThickness=0 时补阴影）。</summary>
    private void ApplyWindowRoundedShadow()
    {
        try
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            Native.NativeMethods.ApplyWindowRoundedShadow(hwnd, WindowState == WindowState.Maximized);
        }
        catch
        {
            // Win10 / 旧 DWM 无圆角属性时忽略
        }
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        UpdateWindowOuterBorder();
        ApplyWindowRoundedShadow();
        InitTray();
        // 先挂上面包屑再本地化/刷主题色，避免未入树时 FindResource 抛错
        EndpointList.ItemsSource = _endpoints;
        DeviceInventoryList.ItemsSource = _deviceInventory;
        NetworkBreadcrumbHost.Children.Clear();
        NetworkBreadcrumbHost.Children.Add(_networkBreadcrumb);
        _networkBreadcrumb.SegmentClicked -= OnNetworkBreadcrumbClick;
        _networkBreadcrumb.SegmentClicked += OnNetworkBreadcrumbClick;
        LoadUiFromSettings();
        MachineMatrix.ItemsSource = _slots;
        TransferList.ItemsSource = _host.Transfers.Items;
        RefreshNetworkBreadcrumb();
        RefreshNics();
        RefreshDiscovery();
        // 等可视化树就绪后再应用行列，避免 2×2 切换时图标丢失
        _ = Dispatcher.BeginInvoke(() =>
        {
            ApplyLayoutGrid();
            RedrawArrows();
        }, System.Windows.Threading.DispatcherPriority.Loaded);

        // BeginInvoke：避免网络线程 Sync Invoke 卡在 UI 刷新上，拖死信令/中继建连
        _host.StatusMessage += msg => Dispatcher.BeginInvoke(() => StatusText.Text = msg);
        _host.ProjectionUserNotice += msg => Dispatcher.Invoke(() =>
            MessageBox.Show(this, msg, "投影", MessageBoxButton.OK, MessageBoxImage.Warning));
        _host.ScreenExtend.HostSessionReady += () => Dispatcher.Invoke(() =>
        {
            RefreshTitleProjectButton();
            RefreshDiscovery();
        });
        _host.ScreenExtend.SinkSessionReady += _ => Dispatcher.Invoke(() =>
        {
            RefreshTitleProjectButton();
            RefreshDiscovery();
        });
        _host.ScreenExtend.SessionEnded += _ => Dispatcher.Invoke(() =>
        {
            RefreshTitleProjectButton();
            OnDeviceProjectSessionEnded();
        });
        RefreshTitleProjectButton();
        AppLog.Changed += () => Dispatcher.Invoke(() =>
        {
            // 仅在「实时」页增量追加，避免整表重绑闪烁回顶
            if (PageLog.Visibility == Visibility.Visible && IsLogLiveMode())
                RefreshLogPageLive();
        });
        LogList.ItemsSource = _logItems;
        ProgramService.Instance.Catalog.Changed += () => Dispatcher.BeginInvoke(RefreshProgramsPage);
        RegisterToolUiBridge();
        ToolService.Instance.Catalog.Changed += () => Dispatcher.BeginInvoke(RefreshToolsPage);
        ToolService.Instance.InstallProgress.Changed += () => Dispatcher.BeginInvoke(RefreshToolsPage);
        ToolService.Instance.Workspace.Changed += () => Dispatcher.BeginInvoke(RefreshToolsWorkspace);
        foreach (var module in ToolService.Instance.Host.All)
            module.StateChanged += () => Dispatcher.BeginInvoke(RefreshToolsPage);
        _host.Connections.PeerLinkChanged += OnPeerLinkChanged;
        // 发现表高频更新：合并到 UI 队列，避免 Clear/Add 与 ListBox 枚举重入抛 Collection was modified
        _host.Connections.Discovery.PeersUpdated += _ => QueueDiscoveryRefresh();
        _host.LayoutSyncedFromPeer += OnLayoutSyncedFromPeer;
        _host.BehaviorSyncedFromPeer += OnBehaviorSyncedFromPeer;
        _host.RemoteBindingSyncedFromPeer += OnRemoteBindingSyncedFromPeer;
        _host.SecurityKeySyncedFromPeer += OnSecurityKeySyncedFromPeer;

        _host.Clipboard.Start(this);
        await _host.StartAsync();
        StatusText.Text = $"监听 {_host.Connections.BindIp}:{_host.Settings.TcpPort}";

        // 开机自启：隐藏到托盘，后台已自动连接（构造时已 Hidden，此处再 Hide 一次兜底）
        if (AutoStartHelper.IsAutostartLaunch(Environment.GetCommandLineArgs()))
        {
            StatusText.Text = $"已自启并连接 · {_host.Connections.BindIp}:{_host.Settings.TcpPort}";
            ShowInTaskbar = false;
            Hide();
        }

        if (_host.Settings.CheckUpdatesOnStartup)
            _ = CheckUpdatesOnStartupQuietAsync();
    }

    private async Task CheckUpdatesOnStartupQuietAsync()
    {
        try
        {
            var result = await _helpUpdater.CheckAsync().ConfigureAwait(true);
            if (result is { Success: true, UpdateAvailable: true })
            {
                StatusText.Text = $"发现新版本 {result.RemoteVersion}，可在「帮助」中自动更新";
                _helpLastCheck = result;
                _helpUpdateReady = true;
                RefreshHelpToolbarLabels();
            }
        }
        catch
        {
            // 静默忽略
        }
    }

    private void InitTray()
    {
        _trayIcon = LoadAppIcon();
        _tray = new WinForms.NotifyIcon
        {
            Text = Loc.T("Tray.Tip"),
            Visible = true,
            Icon = _trayIcon ?? System.Drawing.SystemIcons.Application
        };
        _tray.DoubleClick += (_, _) => ShowFromTray();
        // 不用 WinForms ContextMenuStrip（圆角/系统样式难控）；改用 WPF 直角投影菜单
        _tray.MouseUp += (_, e) =>
        {
            if (e.Button != WinForms.MouseButtons.Right) return;
            Dispatcher.BeginInvoke(new Action(ShowTrayContextMenu));
        };
    }

    private void ShowFromTray()
    {
        ShowInTaskbar = true;
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void ShowTrayContextMenu()
    {
        var s = _host.Settings;
        var machines = TraySwitchMenu.BuildItems(s)
            .Select(x => (x.Label, (Action)(() => TraySwitchToMachine(x.MachineName))))
            .ToList();
        var tools = TrayToolsMenu.BuildItems()
            .Select(x =>
            {
                var toolId = x.ToolId;
                return (x.Label, (Action)(() => LaunchTrayToolShortcut(toolId)),
                    ToolIconImageFactory.TryGet(x.IconKey));
            })
            .ToList();
        TrayContextMenuWindow.ShowNearCursor(
            onOpen: ShowFromTray,
            onReconnect: () => _ = _host.Connections.ReconnectNowAsync(),
            onProject: () => _ = OpenProjectionFromUiAsync(),
            projectPhase: ProjectionUi.Resolve(_host.ScreenExtend),
            onAbout: () =>
            {
                ShowFromTray();
                ShowPage(5);
            },
            onExit: ExitApp,
            switchMachines: machines,
            disableLabel: TraySwitchMenu.DisableLabel(s.SwitchHotkeyMode == 2),
            onToggleDisable: ToggleTraySwitchHotkeyDisable,
            toolShortcuts: tools);
    }

    private void LaunchTrayToolShortcut(string toolId)
    {
        ShowFromTray();
        ShowPage(9);
        var row = TrayToolsMenu.ResolveRow(toolId);
        if (row == null) return;
        _ = HandleToolActionAsync(row, primary: true);
    }

    private void TraySwitchToMachine(string machineName)
    {
        if (TraySwitchMenu.EnsureSwitchHotkeyEnabled(_host.Settings))
        {
            ApplySwitchHotkeyRadiosFromSettings();
            _ = _host.BroadcastBehaviorSyncAsync();
        }

        _host.Input.TrySwitchToMachine(machineName);
    }

    private void ToggleTraySwitchHotkeyDisable()
    {
        TraySwitchMenu.ToggleSwitchHotkeyDisabled(_host.Settings);
        ApplySwitchHotkeyRadiosFromSettings();
        _ = _host.BroadcastBehaviorSyncAsync();
    }

    private void ApplySwitchHotkeyRadiosFromSettings()
    {
        var mode = _host.Settings.SwitchHotkeyMode;
        HotF.IsChecked = mode == 0;
        HotNum.IsChecked = mode == 1;
        HotOff.IsChecked = mode == 2;
    }

    private void ApplyTrayLocalized()
    {
        if (_tray != null)
            _tray.Text = Loc.T("Tray.Tip");
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
        LoadEdgeCrossModeUi(s.EdgeCrossMode);
        // 以计划任务实际状态为准；禁止 settings OR 强制重建（否则取消失败后下次启动又写回开启）
        var autoStart = AutoStartHelper.IsEnabled();
        AutoStartCheck.IsChecked = autoStart;
        if (s.AutoStart != autoStart)
        {
            s.AutoStart = autoStart;
            s.Save();
        }
        TcpPortBox.Text = s.TcpPort.ToString();
        UdpPortBox.Text = s.UdpPort.ToString();
        if (ConnectionEndpointStore.EnsureFromBindings(s) > 0)
            s.Save();
        ReloadEndpointListFromSettings();
        HotF.IsChecked = s.SwitchHotkeyMode == 0;
        HotNum.IsChecked = s.SwitchHotkeyMode == 1;
        HotOff.IsChecked = s.SwitchHotkeyMode == 2;
        HkSettings.Text = s.HotkeySettings;
        HkReconnect.Text = s.HotkeyReconnect;
        HkExit.Text = s.HotkeyExit;
        HkLock.Text = s.HotkeyLock;

        LanguageCombo.ItemsSource = Loc.Languages;
        var lang = Loc.Languages.FirstOrDefault(l =>
                       string.Equals(l.Code, s.UiLanguage, StringComparison.OrdinalIgnoreCase))
                   ?? Loc.Languages[0];
        LanguageCombo.SelectedItem = lang;
        Loc.SetCulture(lang.Code);

        if (string.Equals(s.UiTheme, ThemeService.Dark, StringComparison.OrdinalIgnoreCase))
            ThemeDark.IsChecked = true;
        else
            ThemeLight.IsChecked = true;
        ThemeService.Apply(s.UiTheme, this);

        ApplyLocalizedUi();

        _slots.Clear();
        for (var i = 0; i < 4; i++)
        {
            var name = i < s.MachineSlots.Count ? s.MachineSlots[i] : "";
            var isLocal = string.Equals(name, LocalPeerIdentity.MachineName, StringComparison.OrdinalIgnoreCase);
            _slots.Add(new MachineSlot
            {
                Index = i,
                Name = string.IsNullOrWhiteSpace(name) ? "" : name,
                Enabled = !string.IsNullOrWhiteSpace(name),
                IsLocal = isLocal,
                StatusText = isLocal ? Loc.T("Slot.Local") : Loc.T("Slot.Disconnected")
            });
        }

        ShowKeyCheck.IsChecked = false;
        ShowKey_Changed(null!, null!);
        ApplyLayoutGrid();
        RefreshPositionLabels();
        RefreshSlotBoundRemotes();
        var local = _slots.FirstOrDefault(s => s.IsLocal) ?? _slots.FirstOrDefault();
        if (local != null) SelectSlot(local);
        ShowPage(0);
    }

    private void RefreshSlotBoundRemotes()
        => RemoteBindingHelper.ApplyToSlots(_slots, _host.Settings);

    private void RemoteShadowMenu_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (sender is not FrameworkElement fe) return;
        var slot = ResolveMachineSlot(fe);
        if (slot == null || !slot.HasActiveRemote) return;
        OpenRemoteShadowMenu(fe, slot);
    }

    private void RemoteShadowMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;
        var slot = ResolveMachineSlot(fe);
        if (slot == null || !slot.HasActiveRemote) return;
        OpenRemoteShadowMenu(fe, slot);
        e.Handled = true;
    }

    private static MachineSlot? ResolveMachineSlot(FrameworkElement fe)
    {
        if (fe.Tag is MachineSlot tagged) return tagged;
        if (fe.DataContext is MachineSlot ctx) return ctx;
        for (DependencyObject? p = fe; p != null; p = VisualTreeHelper.GetParent(p))
        {
            if (p is FrameworkElement { DataContext: MachineSlot s })
                return s;
            if (p is FrameworkElement { Tag: MachineSlot t })
                return t;
        }

        return null;
    }

    private void OpenRemoteShadowMenu(FrameworkElement placementTarget, MachineSlot slot)
    {
        var menu = new System.Windows.Controls.ContextMenu();
        if (TryFindResource("ToolCardContextMenu") is Style menuStyle)
            menu.Style = menuStyle;
        if (TryFindResource("ToolCardMenuItem") is Style itemStyle)
            menu.ItemContainerStyle = itemStyle;

        menu.PlacementTarget = placementTarget;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.VerticalOffset = 2;
        placementTarget.ContextMenu = menu;

        if (slot.CanSwitchRemote)
        {
            var switchItem = new System.Windows.Controls.MenuItem { Header = Loc.T("Remote.Switch") };
            switchItem.Click += (_, _) => PromptSwitchRemote(slot);
            menu.Items.Add(switchItem);
        }

        var rebindItem = new System.Windows.Controls.MenuItem { Header = Loc.T("Remote.Rebind") };
        rebindItem.Click += (_, _) => PromptRebindRemote(slot);
        menu.Items.Add(rebindItem);

        var projecting = slot.ActiveRemoteIsProjecting;
        var projectItem = new System.Windows.Controls.MenuItem
        {
            Header = projecting ? Loc.T("Remote.StopProject") : Loc.T("Remote.Project")
        };
        projectItem.Click += async (_, _) =>
        {
            if (projecting) await StopRemoteProjectAsync(slot);
            else await StartRemoteProjectAsync(slot);
        };
        menu.Items.Add(projectItem);

        menu.IsOpen = true;
    }

    private void PromptSwitchRemote(MachineSlot slot)
    {
        if (slot.ActiveRemoteIsProjecting)
        {
            StatusText.Text = Loc.T("Remote.SwitchNeedStop");
            return;
        }

        var pool = RemoteBindingHelper.ListPool(_host.Settings, slot.Name);
        if (pool.Count <= 1) return;

        var menu = new System.Windows.Controls.ContextMenu();
        if (TryFindResource("ToolCardContextMenu") is Style menuStyle)
            menu.Style = menuStyle;
        if (TryFindResource("ToolCardMenuItem") is Style itemStyle)
            menu.ItemContainerStyle = itemStyle;

        foreach (var b in pool)
        {
            var mode = b.Mode == RemoteBinding.ModeP2P ? "P2P" : "中转";
            var mark = string.Equals(b.RemoteId, slot.ActiveRemoteId, StringComparison.OrdinalIgnoreCase) ? "● " : "○ ";
            var rtt = b.RttMs is int ms ? $" {ms}ms" : "";
            var item = new System.Windows.Controls.MenuItem
            {
                Header = $"{mark}{b.RemoteName}  [{mode}]{rtt}",
                Tag = b.RemoteId
            };
            item.Click += (_, _) =>
            {
                if (item.Tag is not string id) return;
                if (!RemoteBindingHelper.SetActive(_host.Settings, slot.Name, id))
                {
                    StatusText.Text = Loc.T("Remote.SwitchNeedStop");
                    return;
                }

                _host.Settings.Save();
                RefreshSlotBoundRemotes();
                SyncRemoteBindingsToPeers();
                StatusText.Text = string.Format(Loc.T("Remote.Switched"), b.RemoteName);
            };
            menu.Items.Add(item);
        }

        menu.IsOpen = true;
    }

    private async void PromptRebindRemote(MachineSlot slot)
    {
        var binding = RemoteBindingHelper.FindById(_host.Settings, slot.ActiveRemoteId)
                      ?? RemoteBindingHelper.GetActive(_host.Settings, slot.Name);
        if (binding == null) return;

        if (binding.State == RemoteBinding.StateProjecting)
            await StopRemoteProjectAsync(slot);

        var targets = RemoteBindingHelper.ListLayoutTargets(_slots);
        if (targets.Count == 0)
        {
            StatusText.Text = Loc.T("Net.BindNoLayout");
            return;
        }

        var selected = RemoteBindDialog.Show(this, binding.RemoteName, targets, binding.BoundLocalDeviceId);
        if (string.IsNullOrWhiteSpace(selected)) return;
        if (string.Equals(selected, binding.BoundLocalDeviceId, StringComparison.OrdinalIgnoreCase))
            return;

        RemoteBindingHelper.Rebind(_host.Settings, binding, selected);
        _host.Settings.Save();
        RefreshSlotBoundRemotes();
        RefreshDiscovery();
        _ = _host.Connections.RefreshConfiguredPeersAsync();
        SyncRemoteBindingsToPeers("远程投影关联已同步到布局内电脑");
        var pos = targets.FirstOrDefault(t =>
            string.Equals(t.DeviceId, selected, StringComparison.OrdinalIgnoreCase)).Label;
        if (string.IsNullOrEmpty(pos)) pos = selected;
        StatusText.Text = string.Format(Loc.T("Remote.Rebound"), binding.RemoteName, pos);
    }

    private async Task StartRemoteProjectAsync(MachineSlot slot)
    {
        var binding = RemoteBindingHelper.FindById(_host.Settings, slot.ActiveRemoteId)
                      ?? RemoteBindingHelper.GetActive(_host.Settings, slot.Name);
        if (binding == null) return;

        await _host.Connections.RefreshConfiguredPeersAsync();
        var remote = binding.RemoteName.Trim();
        var st = _host.Connections.GetPeerStatus(remote);
        if (!st.AnyAlive && !st.Bidirectional)
        {
            StatusText.Text = string.Format(Loc.T("Remote.ProjectNeedPeer"), remote);
            return;
        }

        // 投影到本机槽位时统一用握手本机名，避免 Offer/首帧等待对不上
        var sink = binding.BoundLocalDeviceId.Trim();
        var localSlot = _slots.FirstOrDefault(s =>
            string.Equals(s.Name, sink, StringComparison.OrdinalIgnoreCase));
        if (localSlot?.IsLocal == true
            || string.Equals(sink, LocalPeerIdentity.MachineName, StringComparison.OrdinalIgnoreCase))
            sink = LocalPeerIdentity.MachineName;
        try
        {
            // 请远程作为 Host，把画面推到绑定电脑（本机=绑定端时打开 Sink 播放器）
            var remembered = RemoteProjectChoice.FromSettings(_host.Settings, sink);
            var (ok, err) = await _host.ScreenExtend.RequestPeerBecomeHostAsync(
                remote,
                sink,
                preferredWidth: remembered.Width,
                preferredHeight: remembered.Height,
                fps: remembered.Fps,
                remoteControl: true,
                jpegQuality: remembered.JpegQuality);
            if (!ok)
            {
                StatusText.Text = string.Format(Loc.T("Remote.ProjectFail"), err ?? "");
                return;
            }

            RemoteBindingHelper.SetProjecting(_host.Settings, binding, true);
            _host.Settings.Save();
            RefreshSlotBoundRemotes();
            SyncRemoteBindingsToPeers();
            StatusText.Text = string.Format(Loc.T("Remote.ProjectStarted"), remote, sink);
        }
        catch (Exception ex) when (ex is OperationCanceledException or TaskCanceledException)
        {
            StatusText.Text = string.Format(Loc.T("Remote.ProjectFail"), "操作超时或已取消");
        }
        catch (Exception ex)
        {
            StatusText.Text = string.Format(Loc.T("Remote.ProjectFail"), ex.Message);
        }
    }

    private async Task StopRemoteProjectAsync(MachineSlot slot)
    {
        var binding = RemoteBindingHelper.FindById(_host.Settings, slot.ActiveRemoteId)
                      ?? RemoteBindingHelper.GetActive(_host.Settings, slot.Name);
        if (binding == null) return;

        var remote = binding.RemoteName.Trim();
        var sink = binding.BoundLocalDeviceId.Trim();
        try
        {
            if (_host.ScreenExtend.IsActive)
                await _host.StopProjectionAsync();
            else
                await _host.ScreenExtend.RequestPeerEndHostAsync(remote, sink);
        }
        catch
        {
            // ignore stop errors; still clear projecting state
        }

        RemoteBindingHelper.SetProjecting(_host.Settings, binding, false);
        _host.Settings.Save();
        RefreshSlotBoundRemotes();
        SyncRemoteBindingsToPeers();
        StatusText.Text = string.Format(Loc.T("Remote.ProjectStopped"), binding.RemoteName);
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
        s.EdgeCrossMode = ReadEdgeCrossModeUi();
        // 先落地穿边/键鼠，再处理后台自启（schtasks 不可挡 UI）
        _host.Input.EasyMouseEnabled = s.EasyMouse;
        _host.Input.RefreshEdgeCrossFromSettings();

        var wantAutoStart = AutoStartCheck.IsChecked == true;
        s.AutoStart = wantAutoStart;
        TraySwitchMenu.ApplyMode(s, HotNum.IsChecked == true ? 1 : HotOff.IsChecked == true ? 2 : 0);
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

        // 附加连接配置：仅本机；缺省空列表，旧端忽略
        s.ConnectionEndpoints = _endpoints.ToList();

        if (LanguageCombo.SelectedItem is LanguageOption lang)
            s.UiLanguage = lang.Code;
        else
            s.UiLanguage = Loc.DefaultCulture;

        s.UiTheme = ThemeDark.IsChecked == true ? ThemeService.Dark : ThemeService.Light;
        ThemeService.Apply(s.UiTheme, this);

        Loc.SetCulture(s.UiLanguage);
        ApplyLocalizedUi();

        s.MachineSlots = _slots.Select(x => x.Enabled ? x.Name.Trim() : "").ToList();
        while (s.MachineSlots.Count < 4) s.MachineSlots.Add("");
        s.EnsureLocalMachineSlot();
        s.Save();

        // 开机自启改后台写入，完成后回写勾选/状态；失败不拖住「应用」与穿边
        _ = ApplyAutoStartAsync(wantAutoStart);
    }

    private async Task ApplyAutoStartAsync(bool wantAutoStart)
    {
        var ok = await Task.Run(() => AutoStartHelper.SetEnabled(wantAutoStart)).ConfigureAwait(true);
        var actual = AutoStartHelper.IsEnabled();
        var s = _host.Settings;
        if (s.AutoStart != actual)
        {
            s.AutoStart = actual;
            s.Save();
        }

        AutoStartCheck.IsChecked = actual;
        if (!ok || actual != wantAutoStart)
        {
            StatusText.Text = wantAutoStart
                ? "开机自启设置失败（需管理员权限创建计划任务）"
                : "关闭开机自启失败";
        }
        else if (wantAutoStart)
            StatusText.Text = "已启用开机自启（登录后约 15 秒以管理员启动）";
    }

    private void ApplyLocalizedUi()
    {
        // DataTemplate 内文案（StringBox 可通知绑定刷新）
        SetStr("Str.DeleteTip", Loc.T("Tip.DeleteTransfer"));
        SetStr("Str.Add", Loc.T("Btn.Add"));

        AboutBtnText.Text = Loc.T("Btn.About");
        HelpBtnText.Text = Loc.T("Btn.Help").Replace("？ ", "").Replace("? ", "");
        HelpDocBtn.Content = Loc.T("Help.ShowDoc");
        RefreshHelpToolbarLabels();
        RefreshAboutPage();
        if (PageHelp.Visibility == Visibility.Visible)
            ShowHelpDocument();
        ApplyBtn.Content = Loc.T("Btn.Apply");
        HideTrayBtn.Content = Loc.T("Btn.HideTray");
        ExitBtn.Content = Loc.T("Btn.Exit");
        ApplyTrayLocalized();
        Title = Loc.T("App.Name");

        NavBasicText.Text = Loc.T("Nav.Basic");
        NavNetworkText.Text = Loc.T("Nav.Network");
        NavShareText.Text = Loc.T("Nav.Share");
        NavSecurityText.Text = Loc.T("Nav.Security");
        NavAdvancedText.Text = Loc.T("Nav.Advanced");
        NavLogText.Text = Loc.T("Nav.Log");
        NavProgramsText.Text = Loc.T("Nav.Programs");
        NavToolsText.Text = Loc.T("Nav.Tools");
        ToolsTitleText.Text = Loc.T("Tool.Title");
        ToolsHintText.Text = Loc.T("Tool.Hint");
        if (ToolsImportBtn != null)
            ToolsImportBtn.Content = Loc.T("Tool.Import");
        SearchPlaceholderText.Text = Loc.T("Search.Placeholder");
        RefreshToolsPage();
        LogTitleText.Text = Loc.T("Log.Title");
        RefreshLogHint();
        LogDownloadBtnText.Text = Loc.T("Log.Download");
        LogCopyBtnText.Text = Loc.T("Log.Copy");
        LogClearBtnText.Text = Loc.T("Log.Clear");
        LogPrevBtnText.Text = Loc.T("Log.Prev");
        LogNextBtnText.Text = Loc.T("Log.Next");
        LogLatestBtnText.Text = Loc.T("Log.Latest");
        LogDateBtnText.Text = Loc.T("Log.Date");
        LogDownloadBtn.ToolTip = LogDownloadBtnText.Text;
        LogCopyBtn.ToolTip = Loc.T("Log.SelectTip");
        LogClearBtn.ToolTip = LogClearBtnText.Text;
        LogPrevBtn.ToolTip = LogPrevBtnText.Text;
        LogNextBtn.ToolTip = LogNextBtnText.Text;
        LogLatestBtn.ToolTip = LogLatestBtnText.Text;
        LogDateBtn.ToolTip = LogDateBtnText.Text;
        EnsureLogLevelFilterItems();
        Dispatcher.BeginInvoke(UpdateLogToolMode, System.Windows.Threading.DispatcherPriority.Loaded);
        ProgTitleText.Text = Loc.T("Prog.Title");
        ProgHintText.Text = Loc.T("Prog.Hint");
        ProgAddBtnText.Text = Loc.T("Prog.Add");
        ProgDeleteBtnText.Text = Loc.T("Prog.Delete");
        ProgStartBtnText.Text = Loc.T("Prog.Start");
        ProgStopBtnText.Text = Loc.T("Prog.Stop");
        ProgAddBtn.ToolTip = ProgAddBtnText.Text;
        ProgDeleteBtn.ToolTip = ProgDeleteBtnText.Text;
        ProgStartBtn.ToolTip = ProgStartBtnText.Text;
        ProgStopBtn.ToolTip = ProgStopBtnText.Text;
        SetStr("Str.ProgActStart", Loc.T("Prog.ActStart"));
        SetStr("Str.ProgActStop", Loc.T("Prog.ActStop"));
        SetStr("Str.ProgActEdit", Loc.T("Prog.ActEdit"));
        SetStr("Str.ProgActDelete", Loc.T("Prog.ActDelete"));
        SetStr("Str.ProgActLog", Loc.T("Prog.ActLog"));
        SetStr("Str.ProgActShare", Loc.T("Prog.ActShare"));
        SetStr("Str.ProgActSettings", Loc.T("Prog.ActSettings"));
        ProgColName.Header = Loc.T("Prog.ColName");
        ProgColLang.Header = Loc.T("Prog.ColLang");
        ProgColSize.Header = Loc.T("Prog.ColSize");
        ProgColStatus.Header = Loc.T("Prog.ColStatus");
        ProgColActions.Header = Loc.T("Prog.ColActions");

        SecurityTitleText.Text = Loc.T("Sec.Title");
        ShowKeyCheck.Content = Loc.T("Sec.Show");
        NewKeyBtn.Content = Loc.T("Sec.NewKey");
        if (SyncKeyBtn != null) SyncKeyBtn.Content = Loc.T("Sec.SyncKey");
        if (SecuritySyncHintText != null) SecuritySyncHintText.Text = Loc.T("Sec.SyncHint");
        SecurityHintText.Text = Loc.T("Sec.Hint");
        if (SecuritySyncHintText != null) SecuritySyncHintText.Text = Loc.T("Sec.SyncHint");
        LayoutTitleText.Text = Loc.T("Layout.Title");
        LayoutHintText.Text = Loc.T("Layout.Hint");
        LayoutDragTipText.Text = Loc.T("Layout.DragTip");
        AddMachineBtn.Content = Loc.T("Layout.Add");
        RemoveMachineBtn.Content = Loc.T("Layout.Remove");
        ReconnectBtn.Content = Loc.T("Layout.Reconnect");

        NicTitleText.Text = Loc.T("Net.NicTitle");
        if (NicHintText != null) NicHintText.Text = Loc.T("Net.NicHint");
        RefreshNicsBtn.ToolTip = Loc.T("Net.RefreshNics");
        TcpPortLabel.Text = Loc.T("Net.TcpPortShort");
        UdpPortLabel.Text = Loc.T("Net.UdpPortShort");
        if (ConnConfigTitleText != null) ConnConfigTitleText.Text = Loc.T("Net.ConnConfigTitle");
        if (ConnConfigHintText != null) ConnConfigHintText.Text = Loc.T("Net.ConnConfigHint");
        if (ConnColType != null) ConnColType.Text = Loc.T("Net.Col.Type");
        if (ConnColAddr != null) ConnColAddr.Text = Loc.T("Net.Col.Address");
        if (ConnColOp != null) ConnColOp.Text = Loc.T("Net.Col.Action");
        if (AddEndpointBtn != null) AddEndpointBtn.ToolTip = Loc.T("Net.AddEndpoint");
        DiscoveryTitleText.Text = Loc.T("Net.DeviceMgmtTitle");
        DiscoveryHintText.Text = Loc.T("Net.DeviceMgmtHint");
        RefreshDiscoveryBtn.ToolTip = Loc.T("Net.RefreshDiscovery");
        if (DevFilterAll != null) DevFilterAll.Content = Loc.T("Net.Filter.All");
        if (DevFilterLocal != null) DevFilterLocal.Content = Loc.T("Net.Filter.Local");
        if (DevFilterRemote != null) DevFilterRemote.Content = Loc.T("Net.Filter.Remote");
        if (DevFilterService != null) DevFilterService.Content = Loc.T("Net.Filter.Service");
        if (DevColName != null) DevColName.Text = Loc.T("Net.Col.DeviceName");
        if (DevColKind != null) DevColKind.Text = Loc.T("Net.Col.Kind");
        if (DevColAddr != null) DevColAddr.Text = Loc.T("Net.Col.Address");
        if (DevColStatus != null) DevColStatus.Text = Loc.T("Net.Col.Status");
        if (DevColOp != null) DevColOp.Text = Loc.T("Net.Col.Action");
        RefreshNetworkBreadcrumb();

        XferTitleText.Text = Loc.T("Xfer.Title");
        XferHintText.Text = Loc.T("Xfer.Hint");
        DirectTransferBtn.Content = Loc.T("Xfer.Direct");
        DirectTransferBtn.ToolTip = Loc.T("Tip.DirectTransfer");
        OpenInboxBtn.Content = Loc.T("Xfer.OpenInbox");
        ReceiveClipboardBtn.Content = Loc.T("Xfer.ReceiveClipboard");
        ClearTransfersBtn.Content = Loc.T("Xfer.ClearDone");
        ClearTransfersBtn.ToolTip = Loc.T("Tip.ClearTransfers");
        ColDir.Header = Loc.T("Xfer.Col.Dir");
        ColName.Header = Loc.T("Xfer.Col.Name");
        ColPeer.Header = Loc.T("Xfer.Col.Peer");
        ColSize.Header = Loc.T("Xfer.Col.Size");
        ColStatus.Header = Loc.T("Xfer.Col.Status");

        FeatTitleText.Text = Loc.T("Feat.Title");
        BehTitleText.Text = Loc.T("Beh.Title");
        HotTitleText.Text = Loc.T("Hot.Title");
        HotSwitchText.Text = Loc.T("Hot.Switch");
        HotF.Content = Loc.T("Hot.F14");
        HotNum.Content = Loc.T("Hot.Num");
        HotOff.Content = Loc.T("Hot.Off");
        HotSettingsLabel.Text = Loc.T("Hot.Settings");
        HotReconnectLabel.Text = Loc.T("Hot.Reconnect");
        HotExitLabel.Text = Loc.T("Hot.Exit");
        HotLockLabel.Text = Loc.T("Hot.Lock");

        SysTitleText.Text = Loc.T("Sys.Title");
        SysStyleLabel.Text = Loc.T("Sys.Style");
        SysLangLabel.Text = Loc.T("Sys.Language");
        ThemeLight.Content = Loc.T("Sys.StyleLight");
        ThemeDark.Content = Loc.T("Sys.StyleDark");
        SysStyleLabel.ToolTip = Loc.T("Sys.StyleTip");
        SysLangLabel.ToolTip = Loc.T("Sys.LangTip");
        ThemeLight.ToolTip = Loc.T("Sys.StyleTip");
        ThemeDark.ToolTip = Loc.T("Sys.StyleTip");
        LanguageCombo.ToolTip = Loc.T("Sys.LangTip");

        SetOpt(WrapMouseCheck, "Opt.WrapMouse", "Tip.WrapMouse");
        SetOpt(ShareClipboardCheck, "Opt.ShareClipboard", "Tip.ShareClipboard");
        SetOpt(TransferFileCheck, "Opt.TransferFile", "Tip.TransferFile");
        SetOpt(HideMouseCheck, "Opt.HideMouse", "Tip.HideMouse");
        SetOpt(DrawCursorCheck, "Opt.DrawCursor", "Tip.DrawCursor");
        SetOpt(SameSubnetCheck, "Opt.SameSubnet", "Tip.SameSubnet");
        SetOpt(ShowStatusCheck, "Opt.ShowStatus", "Tip.ShowStatus");
        SetOpt(AutoStartCheck, "Opt.AutoStart", "Tip.AutoStart");
        SetOpt(BlockSaverCheck, "Opt.BlockSaver", "Tip.BlockSaver");
        SetOpt(MoveRelCheck, "Opt.MoveRel", "Tip.MoveRel");
        SetOpt(BlockCornerCheck, "Opt.BlockCorner", "Tip.BlockCorner");
        SetOpt(EasyMouseCheck, "Opt.EasyMouse", "Tip.EasyMouse");

        EdgeCrossTitleText.Text = Loc.T("Opt.EdgeCrossMode");
        EdgeCrossBarrierRadio.Content = Loc.T("Opt.EdgeCross.BarrierFree");
        EdgeCrossDoubleRadio.Content = Loc.T("Opt.EdgeCross.DoubleTap");
        EdgeCrossSpeedRadio.Content = Loc.T("Opt.EdgeCross.SpeedBurst");
        EdgeCrossHotkeyRadio.Content = Loc.T("Opt.EdgeCross.HotkeyOnly");
        RefreshEdgeCrossTip();
        foreach (var item in _host.Transfers.Items.ToList())
            item.RefreshLocalized();

        foreach (var slot in _slots.ToList())
            slot.RefreshStatus();

        // 强制刷新列表主题（避免系统默认白底）
        ThemeService.Apply(ThemeService.Current, this);
        TransferList.Background = (System.Windows.Media.Brush)Resources["ListBg"];
        TransferList.Foreground = (System.Windows.Media.Brush)Resources["Text"];
        DiscoveryList.Background = (System.Windows.Media.Brush)Resources["ListBg"];
        DiscoveryList.Foreground = (System.Windows.Media.Brush)Resources["Text"];
        UpdateProgToolMode();
    }

    private void SetStr(string key, string value)
    {
        if (Resources[key] is StringBox box)
            box.Value = value;
    }

    private static void SetOpt(System.Windows.Controls.CheckBox box, string contentKey, string tipKey)
    {
        box.Content = Loc.T(contentKey);
        box.ToolTip = Loc.T(tipKey);
    }

    private void LoadEdgeCrossModeUi(int mode)
    {
        EdgeCrossBarrierRadio.Checked -= EdgeCrossMode_Changed;
        EdgeCrossDoubleRadio.Checked -= EdgeCrossMode_Changed;
        EdgeCrossSpeedRadio.Checked -= EdgeCrossMode_Changed;
        EdgeCrossHotkeyRadio.Checked -= EdgeCrossMode_Changed;
        EdgeCrossBarrierRadio.IsChecked = mode is < 1 or > 3;
        EdgeCrossDoubleRadio.IsChecked = mode == 1;
        EdgeCrossSpeedRadio.IsChecked = mode == 2;
        EdgeCrossHotkeyRadio.IsChecked = mode == 3;
        EdgeCrossBarrierRadio.Checked += EdgeCrossMode_Changed;
        EdgeCrossDoubleRadio.Checked += EdgeCrossMode_Changed;
        EdgeCrossSpeedRadio.Checked += EdgeCrossMode_Changed;
        EdgeCrossHotkeyRadio.Checked += EdgeCrossMode_Changed;
        RefreshEdgeCrossTip();
    }

    private int ReadEdgeCrossModeUi()
    {
        if (EdgeCrossDoubleRadio.IsChecked == true) return 1;
        if (EdgeCrossSpeedRadio.IsChecked == true) return 2;
        if (EdgeCrossHotkeyRadio.IsChecked == true) return 3;
        return 0;
    }

    private void EdgeCrossMode_Changed(object sender, RoutedEventArgs e) => RefreshEdgeCrossTip();

    private void RefreshEdgeCrossTip()
    {
        var key = ReadEdgeCrossModeUi() switch
        {
            1 => "Tip.EdgeCross.DoubleTap",
            2 => "Tip.EdgeCross.SpeedBurst",
            3 => "Tip.EdgeCross.HotkeyOnly",
            _ => "Tip.EdgeCross.BarrierFree"
        };
        EdgeCrossTipText.Text = Loc.T(key);
    }

    /// <summary>高级顶栏：宽屏功能/行为/穿边三列，窄屏纵向堆叠。</summary>
    private void OptionsFeatGrid_SizeChanged(object sender, SizeChangedEventArgs e)
        => ApplyOptionsFeatPlacement(OptionsFeatGrid.ActualWidth);

    private void ApplyOptionsFeatPlacement(double width)
    {
        if (!IsLoaded || width <= 1 || FeatColumn == null || BehColumn == null || EdgeCrossColumn == null)
            return;
        var stacked = width < 640;
        if (stacked == _optionsFeatStacked
            && OptionsFeatGrid.ColumnDefinitions.Count == (stacked ? 1 : 3)
            && OptionsFeatGrid.RowDefinitions.Count == (stacked ? 3 : 1))
            return;
        _optionsFeatStacked = stacked;

        OptionsFeatGrid.ColumnDefinitions.Clear();
        OptionsFeatGrid.RowDefinitions.Clear();
        if (stacked)
        {
            OptionsFeatGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            OptionsFeatGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            OptionsFeatGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            OptionsFeatGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(FeatColumn, 0);
            Grid.SetColumn(FeatColumn, 0);
            Grid.SetColumnSpan(FeatColumn, 1);
            Grid.SetRow(BehColumn, 1);
            Grid.SetColumn(BehColumn, 0);
            Grid.SetColumnSpan(BehColumn, 1);
            Grid.SetRow(EdgeCrossColumn, 2);
            Grid.SetColumn(EdgeCrossColumn, 0);
            Grid.SetColumnSpan(EdgeCrossColumn, 1);
            FeatColumn.Margin = new Thickness(0, 0, 0, 12);
            BehColumn.Margin = new Thickness(0, 0, 0, 12);
            EdgeCrossColumn.Margin = new Thickness(0, 0, 0, 8);
        }
        else
        {
            OptionsFeatGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            OptionsFeatGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            OptionsFeatGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            OptionsFeatGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(FeatColumn, 0);
            Grid.SetColumn(FeatColumn, 0);
            Grid.SetColumnSpan(FeatColumn, 1);
            Grid.SetRow(BehColumn, 0);
            Grid.SetColumn(BehColumn, 1);
            Grid.SetColumnSpan(BehColumn, 1);
            Grid.SetRow(EdgeCrossColumn, 0);
            Grid.SetColumn(EdgeCrossColumn, 2);
            Grid.SetColumnSpan(EdgeCrossColumn, 1);
            FeatColumn.Margin = new Thickness(0, 0, 10, 0);
            BehColumn.Margin = new Thickness(5, 0, 5, 0);
            EdgeCrossColumn.Margin = new Thickness(10, 0, 0, 0);
        }
    }

    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        var s = _host.Settings;
        var prevTcp = s.TcpPort;
        var prevUdp = s.UdpPort;
        var prevNic = s.PreferredNicId ?? "";
        var prevIp = s.PreferredLocalIp ?? "";
        var prevKey = s.SecurityKey ?? "";

        ApplySettingsFromUi();
        await _host.BroadcastLayoutSyncAsync();
        await _host.BroadcastBehaviorSyncAsync();
        // 默认保持连接；仅端口/网卡/密钥变化才整网重启
        await _host.ApplyNetworkingAsync(prevTcp, prevUdp, prevNic, prevIp, prevKey);
        StatusText.Text = Loc.T("Sys.Applied");
        SyncSlotLocalFlags();
    }

    private void OnLayoutSyncedFromPeer(string from)
    {
        Dispatcher.Invoke(() =>
        {
            ApplyLayoutFromSettings();
            StatusText.Text = $"已同步布局 ← {from}";
        });
    }

    private void OnBehaviorSyncedFromPeer(string from)
    {
        Dispatcher.Invoke(() =>
        {
            ApplyBehaviorFromSettings();
            StatusText.Text = $"已同步功能与行为 ← {from}";
        });
    }

    private void OnRemoteBindingSyncedFromPeer(string from)
    {
        Dispatcher.Invoke(() =>
        {
            RefreshSlotBoundRemotes();
            RefreshDiscovery();
            StatusText.Text = $"已同步远程投影关联 ← {from}";
        });
    }

    private void SyncRemoteBindingsToPeers(string? hint = null)
        => _ = _host.BroadcastRemoteBindingSyncAsync(hint);

    private void OnSecurityKeySyncedFromPeer(string from)
    {
        Dispatcher.Invoke(() =>
        {
            SetSecurityKey(_host.Settings.SecurityKey ?? "");
            StatusText.Text = $"已同步安全密钥 ← {from}";
        });
    }

    /// <summary>用设置中的功能/行为选项刷新高级页 UI（对端同步时调用）。</summary>
    private void ApplyBehaviorFromSettings()
    {
        var s = _host.Settings;
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
        LoadEdgeCrossModeUi(s.EdgeCrossMode);
        _host.Input.EasyMouseEnabled = s.EasyMouse;
        _host.Input.RefreshEdgeCrossFromSettings();
    }

    /// <summary>用设置中的 MachineSlots / TwoRow 刷新矩阵 UI（对端同步时调用）。</summary>
    private void ApplyLayoutFromSettings()
    {
        var s = _host.Settings;
        s.EnsureLocalMachineSlot();

        if (s.TwoRow) Layout2x2.IsChecked = true;
        else Layout1x4.IsChecked = true;

        for (var i = 0; i < 4; i++)
        {
            var name = i < s.MachineSlots.Count ? s.MachineSlots[i] : "";
            name = string.IsNullOrWhiteSpace(name) ? "" : name.Trim();
            var slot = _slots[i];
            var isLocal = string.Equals(name, LocalPeerIdentity.MachineName, StringComparison.OrdinalIgnoreCase);
            slot.Name = name;
            slot.Enabled = !string.IsNullOrWhiteSpace(name);
            slot.IsLocal = isLocal;
            slot.ResolvedIp = "";
            if (isLocal)
            {
                slot.OutboundOk = true;
                slot.InboundOk = true;
                slot.StatusText = Loc.T("Slot.Local");
            }
            else
            {
                if (!string.IsNullOrEmpty(name))
                {
                    var st = _host.Connections.GetPeerStatus(name);
                    slot.OutboundOk = st.Outbound;
                    slot.InboundOk = st.Inbound;
                    if (!string.IsNullOrEmpty(st.DiscoveredIp))
                        slot.ResolvedIp = st.DiscoveredIp;
                    else
                    {
                        var ip = _host.Connections.Discovery.FindIp(name);
                        if (ip != null) slot.ResolvedIp = ip;
                    }
                }
                else
                {
                    slot.OutboundOk = false;
                    slot.InboundOk = false;
                    slot.ResolvedIp = "";
                }

                slot.RefreshStatus();
            }
        }

        ApplyLayoutGrid();
        RefreshPositionLabels();
        RefreshSlotBoundRemotes();
        RedrawArrows();
        SyncSlotLocalFlags();
        RefreshDiscovery();
    }

    private void SyncSlotLocalFlags()
    {
        foreach (var slot in _slots)
        {
            slot.IsLocal = string.Equals(slot.Name, LocalPeerIdentity.MachineName, StringComparison.OrdinalIgnoreCase);
            if (slot.IsLocal) slot.RefreshStatus();
        }
    }

    private void Hide_Click(object sender, RoutedEventArgs e) => Hide();

    /// <summary>二次启动时激活已有窗口（含托盘隐藏状态）。</summary>
    public void BringToFront()
    {
        ShowInTaskbar = true;
        Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;

        Activate();
        Topmost = true;
        Topmost = false;
        Focus();

        try
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero)
            {
                if (Native.NativeMethods.IsIconic(hwnd))
                    Native.NativeMethods.ShowWindow(hwnd, Native.NativeMethods.SW_RESTORE);
                Native.NativeMethods.SetForegroundWindow(hwnd);
            }
        }
        catch
        {
            // ignore
        }
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => ExitApp();

    private async void ExitApp()
    {
        DisposeTray();
        // DisposeAsync 内会 StopProjection 并等待 /internal，避免扩展拓扑残留
        try { await _host.DisposeAsync(); }
        catch { /* ignore */ }
        System.Windows.Application.Current.Shutdown();
    }

    /// <summary>自动更新：释放托盘后退出，供外部脚本覆盖单文件 exe 并重启。</summary>
    public void ExitForUpdate()
    {
        try { DisposeTray(); } catch { /* ignore */ }
        // 同步等拓扑收拢，避免更新重启后仍卡在扩展屏
        try { _host.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { /* ignore */ }
        Environment.Exit(0);
    }

    private void DisposeTray()
    {
        if (_tray != null)
        {
            _tray.Visible = false;
            _tray.Dispose();
            _tray = null;
        }

        _trayIcon?.Dispose();
        _trayIcon = null;
    }

    private static System.Drawing.Icon? LoadAppIcon()
    {
        try
        {
            var info = System.Windows.Application.GetResourceStream(AppIconUri);
            if (info?.Stream == null) return null;
            // 托盘优先取 16/32 清晰尺寸；NotifyIcon 需要独立副本
            using var stream = info.Stream;
            var size = System.Windows.Forms.SystemInformation.SmallIconSize;
            var wh = Math.Clamp(Math.Max(size.Width, size.Height), 16, 32);
            using var temp = new System.Drawing.Icon(stream, wh, wh);
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

    private async void SyncKey_Click(object sender, RoutedEventArgs e)
    {
        var s = _host.Settings;
        var prevKey = s.SecurityKey ?? "";
        ApplySettingsFromUi();
        var (ok, msg) = await _host.SyncSecurityKeyAsync();
        if (ok)
            await _host.ApplyNetworkingAsync(s.TcpPort, s.UdpPort, s.PreferredNicId ?? "", s.PreferredLocalIp ?? "", prevKey);
        StatusText.Text = msg;
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
        var s = _host.Settings;
        var prevTcp = s.TcpPort;
        var prevUdp = s.UdpPort;
        var prevNic = s.PreferredNicId ?? "";
        var prevIp = s.PreferredLocalIp ?? "";
        var prevKey = s.SecurityKey ?? "";
        ApplySettingsFromUi();
        await _host.ApplyNetworkingAsync(prevTcp, prevUdp, prevNic, prevIp, prevKey);
        StatusText.Text = "正在重新连接…";
    }

    private void RefreshNics_Click(object sender, RoutedEventArgs e) => RefreshNics();

    private void RefreshNics()
    {
        var nics = NicService.ListSelectableIpv4Nics();
        NicCombo.ItemsSource = nics;
        var preferred = NicService.ResolvePreferred(_host.Settings.PreferredNicId, _host.Settings.PreferredLocalIp);
        if (preferred != null)
            NicCombo.SelectedItem = nics.FirstOrDefault(n => n.Id == preferred.Id && n.IPv4 == preferred.IPv4)
                                    ?? nics.FirstOrDefault();
        else if (nics.Count > 0)
            NicCombo.SelectedIndex = 0;
    }

    private async void RefreshDiscovery_Click(object sender, RoutedEventArgs e)
    {
        StatusText.Text = Loc.T("Net.RefreshDiscovery");
        try
        {
            // 先测连通状态；再突发广播 + 握手学名（写入发现表，不进「远程」行）
            await RevalidateEndpointsAsync().ConfigureAwait(true);
            await _host.Connections.RefreshDiscoveryProbeAsync().ConfigureAwait(true);
        }
        catch
        {
            // 探测失败仍刷新清单
        }

        RefreshDiscovery();
        StatusText.Text = Loc.T("Net.DeviceMgmtTitle") + " · " + Loc.T("Net.RefreshDiscovery");
    }

    private void QueueDiscoveryRefresh()
    {
        if (Interlocked.Exchange(ref _discoveryRefreshQueued, 1) != 0)
            return;
        _ = Dispatcher.BeginInvoke(() =>
        {
            Interlocked.Exchange(ref _discoveryRefreshQueued, 0);
            RefreshDiscovery();
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    private void RefreshDiscovery()
    {
        if (_refreshDiscoveryRunning)
        {
            _refreshDiscoveryPending = true;
            return;
        }

        _refreshDiscoveryRunning = true;
        try
        {
            do
            {
                _refreshDiscoveryPending = false;
                RefreshDiscoveryCore();
            } while (_refreshDiscoveryPending);
        }
        finally
        {
            _refreshDiscoveryRunning = false;
        }
    }

    private void RefreshDiscoveryCore()
    {
        foreach (var slot in _slots.Where(s => s.Enabled && !s.IsLocal).ToList())
        {
            var ip = _host.Connections.Discovery.FindIp(slot.Name);
            if (ip != null) slot.ResolvedIp = ip;
        }

        var items = DeviceInventoryBuilder.Build(
            _host.Connections.Discovery.GetPeers(),
            _slots.ToList(),
            _endpoints.ToList(),
            _host.Settings.RemoteBindings,
            LocalPeerIdentity.MachineName,
            CurrentDeviceProjectingPeer(),
            _host.Connections.LocalAnnounceIp);

        var filter = DevFilterRemote?.IsChecked == true ? DeviceInventoryItem.KindRemote
            : DevFilterService?.IsChecked == true ? DeviceInventoryItem.KindService
            : DevFilterLocal?.IsChecked == true ? DeviceInventoryItem.KindLocal
            : (int?)null;

        var filtered = filter is int kind
            ? items.Where(i => i.Kind == kind).ToList()
            : items;

        DeviceInventoryItem.ReplaceIfChanged(_deviceInventory, filtered);

        // 兼容：旧 DiscoveryList 仍可被其它路径引用（已折叠）
        if (DiscoveryList != null)
            DiscoveryList.ItemsSource = UnusedDiscoveredPeers();
    }

    /// <summary>设备管理发起的投影源（含投到布局内其它电脑时本机未必在会话中）。</summary>
    private string? _deviceMgmtProjectRemote;

    /// <summary>设备管理直接投影：本机作 Sink，或本机发起投到其它电脑时记下的远程源。</summary>
    private string? CurrentDeviceProjectingPeer()
    {
        if (!string.IsNullOrWhiteSpace(_deviceMgmtProjectRemote))
            return _deviceMgmtProjectRemote;
        var se = _host.ScreenExtend;
        if (!se.IsActive || se.Role != ScreenExtendRole.Sink)
            return null;
        return string.IsNullOrWhiteSpace(se.Peer) ? null : se.Peer;
    }

    private void OnDeviceProjectSessionEnded()
    {
        foreach (var item in _deviceInventory.Where(i => i.IsProjecting).ToList())
            _host.Connections.RemoveAdHocPeer(item.Name);
        if (!string.IsNullOrWhiteSpace(_deviceMgmtProjectRemote))
            _host.Connections.RemoveAdHocPeer(_deviceMgmtProjectRemote);
        _deviceMgmtProjectRemote = null;
        RefreshDiscovery();
    }

    private List<DiscoveredPeer> UnusedDiscoveredPeers()
    {
        var used = new HashSet<string>(
            _slots.Where(s => !string.IsNullOrWhiteSpace(s.Name)).Select(s => s.Name),
            StringComparer.OrdinalIgnoreCase);
        used.Add(LocalPeerIdentity.MachineName);
        return _host.Connections.Discovery.GetPeers()
            .Where(p => !string.IsNullOrWhiteSpace(p.MachineName) && !used.Contains(p.MachineName))
            .ToList();
    }

    private void RefreshNetworkBreadcrumb()
    {
        if (_networkBreadcrumb == null) return;
        var onDevices = _networkSubPage == "devices";
        _networkBreadcrumb.SetItems(
        [
            new BreadcrumbSegment
            {
                Id = "network",
                Title = Loc.T("Nav.Network"),
                IsActive = false,
                IsClickable = true
            },
            new BreadcrumbSegment
            {
                Id = "conn",
                Title = Loc.T("Net.ConnConfigTitle"),
                IsActive = !onDevices,
                IsClickable = true
            },
            new BreadcrumbSegment
            {
                Id = "devices",
                Title = Loc.T("Net.DeviceMgmtTitle"),
                IsActive = onDevices,
                IsClickable = true
            }
        ]);
    }

    private void OnNetworkBreadcrumbClick(BreadcrumbSegment seg)
    {
        if (seg.Id is "network" or "conn")
            ShowNetworkSubPage("conn");
        else if (seg.Id == "devices")
            ShowNetworkSubPage("devices");
    }

    private void ShowNetworkSubPage(string page)
    {
        _networkSubPage = page == "devices" ? "devices" : "conn";
        if (PanelConnConfig != null)
            PanelConnConfig.Visibility = _networkSubPage == "conn" ? Visibility.Visible : Visibility.Collapsed;
        if (PanelDeviceMgmt != null)
            PanelDeviceMgmt.Visibility = _networkSubPage == "devices" ? Visibility.Visible : Visibility.Collapsed;
        RefreshNetworkBreadcrumb();
        if (_networkSubPage == "devices")
            RefreshDiscovery();
    }

    private void DevFilter_Changed(object sender, RoutedEventArgs e)
    {
        if (IsLoaded) RefreshDiscovery();
    }

    private void ReloadEndpointListFromSettings()
    {
        var s = _host.Settings;
        s.EnsureCollections();
        var before = s.ConnectionEndpoints.Count;
        RemoteBindingSyncHelper.DeduplicateEndpoints(s);
        if (s.ConnectionEndpoints.Count != before)
            s.Save();
        _endpoints.Clear();
        foreach (var ep in s.ConnectionEndpoints)
            _endpoints.Add(ep);
        if (EndpointList != null)
            EndpointList.ItemsSource = _endpoints;
    }

    private void PersistEndpoints(bool saveFile = true)
    {
        var s = _host.Settings;
        s.EnsureCollections();
        s.ConnectionEndpoints = _endpoints.ToList();
        RemoteBindingSyncHelper.DeduplicateEndpoints(s);
        _endpoints.Clear();
        foreach (var ep in s.ConnectionEndpoints)
            _endpoints.Add(ep);
        if (saveFile)
            s.Save();
        try { _host.Connections.RefreshDiscoveryProbeHosts(); } catch { /* ignore */ }
    }

    private void AddEndpoint_Click(object sender, RoutedEventArgs e)
    {
        var ep = new ConnectionEndpoint
        {
            Kind = ConnectionEndpoint.KindRemote,
            Address = "",
            State = ConnectionEndpoint.StateUnknown
        };
        _endpoints.Add(ep);
        PersistEndpoints();
        // 强制刷新容器：部分主题下嵌套模板首次加项不生成可视行
        EndpointList.ItemsSource = null;
        EndpointList.ItemsSource = _endpoints;
        StatusText.Text = Loc.T("Net.EndpointAdded");
    }

    private void EndpointKind_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ConnectionEndpoint ep }) return;
        ep.ToggleKind();
        PersistEndpoints();
    }

    private async void TestEndpoint_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ConnectionEndpoint ep }) return;
        await TestEndpointAsync(ep).ConfigureAwait(true);
    }

    private async Task RevalidateEndpointsAsync()
    {
        foreach (var ep in _endpoints.ToList())
        {
            if (string.IsNullOrWhiteSpace(ep.Address)) continue;
            await TestEndpointAsync(ep, updateStatusBar: false).ConfigureAwait(true);
        }
    }

    private int ReadUiTcpPort()
        => int.TryParse(TcpPortBox?.Text, out var p) && p is > 0 and <= 65535
            ? p
            : (_host.Settings.TcpPort > 0 ? _host.Settings.TcpPort : 15155);

    private async Task TestEndpointAsync(ConnectionEndpoint ep, bool updateStatusBar = true)
    {
        if (updateStatusBar)
            StatusText.Text = Loc.T("Net.TestingEndpoint");

        bool ok;
        string message;
        if (ep.Kind == ConnectionEndpoint.KindRelay)
        {
            var relay = await _host.Connections.ConnectRelayEndpointAsync(ep).ConfigureAwait(true);
            ok = relay.Ok;
            message = relay.Message;
        }
        else
        {
            var result = await ConnectionEndpointProbe.TestAsync(ep, ReadUiTcpPort()).ConfigureAwait(true);
            ok = result.Ok;
            message = result.Message;
            if (result.Ok)
            {
                ep.LastError = "";
                ep.LastRttMs = result.RttMs;
                ep.DiscoveredDevices = [];
                ep.VerifiedPeerName = result.VerifiedPeerName?.Trim() ?? "";
                ep.State = ConnectionEndpoint.StateConnected;
            }
            else
            {
                ep.ClearVerification();
                ep.LastError = result.Message;
                ep.State = ConnectionEndpoint.StateError;
            }
        }

        PersistEndpoints();
        if (updateStatusBar)
            StatusText.Text = ok ? $"{Loc.T("Net.TestOk")}: {message}" : $"{Loc.T("Net.TestFail")}: {message}";
        RefreshDiscovery();
    }

    private void DeleteEndpoint_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ConnectionEndpoint ep }) return;
        var bound = (_host.Settings.RemoteBindings ?? [])
            .Any(b => string.Equals(b.EndpointId, ep.Id, StringComparison.OrdinalIgnoreCase)
                      || string.Equals(b.Endpoint, ep.Address, StringComparison.OrdinalIgnoreCase));
        if (bound)
        {
            var confirm = MessageBox.Show(this,
                Loc.T("Net.DeleteEndpointBoundConfirm"),
                Loc.T("App.Name"),
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.OK) return;
            _host.Settings.EnsureCollections();
            _host.Settings.RemoteBindings!.RemoveAll(b =>
                string.Equals(b.EndpointId, ep.Id, StringComparison.OrdinalIgnoreCase)
                || string.Equals(b.Endpoint, ep.Address, StringComparison.OrdinalIgnoreCase));
            _host.Settings.Save();
        }
        _endpoints.Remove(ep);
        PersistEndpoints();
        EndpointList.ItemsSource = null;
        EndpointList.ItemsSource = _endpoints;
        RefreshDiscovery();
    }

    private void DevicePrimaryAction_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: DeviceInventoryItem item }) return;
        switch (item.Kind)
        {
            case DeviceInventoryItem.KindLocal:
                if (item.LayoutSlotIndex >= 0)
                    LocateLayoutSlot(item.LayoutSlotIndex);
                else if (item.Peer != null)
                    TryAddPeer(item.Peer);
                else
                    StatusText.Text = Loc.T("Net.NoPeerToAdd");
                break;
            case DeviceInventoryItem.KindRemote:
            case DeviceInventoryItem.KindService:
                _ = ToggleDeviceProjectAsync(item);
                break;
        }
    }

    private void DeviceSecondaryAction_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: DeviceInventoryItem item }) return;
        switch (item.Kind)
        {
            case DeviceInventoryItem.KindLocal when item.LayoutSlotIndex >= 0 && !item.IsLocalMachine:
            {
                var slot = _slots.ElementAtOrDefault(item.LayoutSlotIndex);
                if (slot != null) TryRemoveSlot(slot);
                break;
            }
            case DeviceInventoryItem.KindRemote:
            case DeviceInventoryItem.KindService:
                PromptUnbindDevice(item);
                break;
        }
    }

    /// <summary>设备管理：选目标电脑投影 / 停止投影（不绑布局）。</summary>
    private async Task ToggleDeviceProjectAsync(DeviceInventoryItem item)
    {
        var remote = (item.Name ?? "").Trim();
        if (string.IsNullOrWhiteSpace(remote)) return;

        if (item.IsProjecting
            || string.Equals(CurrentDeviceProjectingPeer(), remote, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                await _host.ScreenExtend.RequestPeerEndHostAsync(remote).ConfigureAwait(true);
                await _host.ScreenExtend.StopAsync("device-mgmt-stop").ConfigureAwait(true);
            }
            catch { /* ignore */ }
            _host.Connections.RemoveAdHocPeer(remote);
            _deviceMgmtProjectRemote = null;
            RefreshDiscovery();
            StatusText.Text = string.Format(Loc.T("Remote.ProjectStopped"), remote);
            return;
        }

        var targets = RemoteBindingHelper.ListLayoutTargets(_slots);
        if (targets.Count == 0)
        {
            StatusText.Text = Loc.T("Net.BindNoLayout");
            return;
        }

        var choice = RemoteBindDialog.ShowChoice(
            this, remote, targets, LocalPeerIdentity.MachineName, _host.Settings);
        if (choice == null || string.IsNullOrWhiteSpace(choice.DeviceId)) return;

        choice.WriteRemembered(_host.Settings);
        _host.Settings.Save();

        var sink = choice.DeviceId.Trim();
        if (string.Equals(sink, LocalPeerIdentity.MachineName, StringComparison.OrdinalIgnoreCase)
            || _slots.FirstOrDefault(s =>
                   string.Equals(s.Name, sink, StringComparison.OrdinalIgnoreCase))?.IsLocal == true)
            sink = LocalPeerIdentity.MachineName;

        try
        {
            StatusText.Text = Loc.T("Remote.Projecting") + " · " + remote;
            await _host.Connections.EnsureAdHocPeerAsync(remote).ConfigureAwait(true);
            var st = _host.Connections.GetPeerStatus(remote);
            if (!st.AnyAlive && !st.Bidirectional)
            {
                _host.Connections.RemoveAdHocPeer(remote);
                StatusText.Text = string.Format(Loc.T("Remote.ProjectNeedPeer"), remote);
                return;
            }

            var (ok, err) = await _host.ScreenExtend.RequestPeerBecomeHostAsync(
                remote,
                sink,
                preferredWidth: choice.Width,
                preferredHeight: choice.Height,
                fps: choice.Fps,
                remoteControl: true,
                jpegQuality: choice.JpegQuality).ConfigureAwait(true);
            if (!ok)
            {
                _host.Connections.RemoveAdHocPeer(remote);
                _deviceMgmtProjectRemote = null;
                StatusText.Text = string.Format(Loc.T("Remote.ProjectFail"), err ?? "");
                RefreshDiscovery();
                return;
            }

            _deviceMgmtProjectRemote = remote;
            RefreshDiscovery();
            StatusText.Text = string.Format(Loc.T("Remote.ProjectStarted"), remote, sink);
        }
        catch (Exception ex) when (ex is OperationCanceledException or TaskCanceledException)
        {
            _host.Connections.RemoveAdHocPeer(remote);
            _deviceMgmtProjectRemote = null;
            StatusText.Text = string.Format(Loc.T("Remote.ProjectFail"), "操作超时或已取消");
            RefreshDiscovery();
        }
        catch (Exception ex)
        {
            _host.Connections.RemoveAdHocPeer(remote);
            _deviceMgmtProjectRemote = null;
            StatusText.Text = string.Format(Loc.T("Remote.ProjectFail"), ex.Message);
            RefreshDiscovery();
        }
    }

    private void PromptUnbindDevice(DeviceInventoryItem item)
    {
        var confirm = ConfirmDialog.Show(this,
            Loc.T("Net.BindTitle"),
            string.Format(Loc.T("Net.UnbindConfirm"), item.Name),
            Loc.T("Dlg.Ok"),
            secondaryText: null);
        if (confirm != UiConfirmChoice.Primary) return;

        RemoteBindingHelper.Unbind(_host.Settings, item);
        _host.Settings.Save();
        RefreshSlotBoundRemotes();
        RefreshDiscovery();
        _ = _host.Connections.RefreshConfiguredPeersAsync();
        SyncRemoteBindingsToPeers("远程投影关联已同步到布局内电脑");
        StatusText.Text = string.Format(Loc.T("Net.UnbindDone"), item.Name);
    }

    private void LocateLayoutSlot(int index)
    {
        ShowPage(0);
        if (index < 0 || index >= _slots.Count) return;
        SelectSlot(_slots[index]);
        StatusText.Text = string.Format(Loc.T("Net.LocatedSlot"), _slots[index].PositionLabel);
    }

    private void AddMachine_Click(object sender, RoutedEventArgs e)
    {
        var unused = UnusedDiscoveredPeers();
        if (unused.Count == 0)
        {
            ShowPage(2);
            ShowNetworkSubPage("devices");
            RefreshDiscovery();
            StatusText.Text = "暂无新发现的电脑，请先刷新发现或确认对端已启动";
            return;
        }

        var menu = new System.Windows.Controls.ContextMenu();
        foreach (var peer in unused)
        {
            var item = new System.Windows.Controls.MenuItem
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

    private void SlotCard_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var slot = FindSlotFromElement(sender as DependencyObject);
        if (slot == null) return;
        SelectSlot(slot);
        if (e.ClickCount >= 2)
        {
            e.Handled = true;
            TryRemoveSlot(slot);
            return;
        }
        // 单击不 Handled，以便继续支持拖拽
    }

    private void RemoveSelectedMachine_Click(object sender, RoutedEventArgs e)
    {
        var selected = _slots.FirstOrDefault(s => s.IsSelected);
        if (selected == null)
        {
            StatusText.Text = "请先单击选中一张电脑卡片";
            return;
        }

        TryRemoveSlot(selected);
    }

    private void SelectSlot(MachineSlot slot)
    {
        foreach (var s in _slots)
            s.IsSelected = ReferenceEquals(s, slot);
    }

    private void TryRemoveSlot(MachineSlot slot)
    {
        if (slot.IsLocal)
        {
            StatusText.Text = "本机不能从布局中移除";
            return;
        }

        if (string.IsNullOrWhiteSpace(slot.Name))
        {
            StatusText.Text = "该位置已经是空位";
            return;
        }

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
        StatusText.Text = $"已移除 {name}，将从所有已连接电脑布局中踢出";
        _ = RemovePeerAndSyncAsync(name);
    }

    private void TryAddPeer(DiscoveredPeer peer)
    {
        if (string.Equals(peer.MachineName, LocalPeerIdentity.MachineName, StringComparison.OrdinalIgnoreCase))
        {
            StatusText.Text = "不能添加本机";
            return;
        }

        if (_host.Connections.IsLayoutIneligiblePeer(peer.MachineName)
            || peer.Origin is DiscoveryOrigin.Remote or DiscoveryOrigin.Service)
        {
            StatusText.Text = $"{peer.MachineName} 是远程设备，不能加入电脑布局；请在设备管理中投影";
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
            System.Windows.MessageBox.Show("没有空的机器槽位（最多 4 台），请先移除一台电脑。", "玄鸟");
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
        _host.Connections.ArmLayoutJoin(peer.MachineName);
        RefreshDiscovery();
        RedrawArrows();
        StatusText.Text = $"已添加 {peer.MachineName}，正在与对端合并布局";
        _ = RestartAfterSlotChangeAsync(merge: true);
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

    private async Task RestartAfterSlotChangeAsync(bool merge)
    {
        try
        {
            if (merge)
                await _host.BroadcastLayoutMergeAsync().ConfigureAwait(true);
            else
                await _host.BroadcastLayoutSyncAsync().ConfigureAwait(true);
            // 布局变更：剪枝+补连，不断开仍在布局中的链路
            await _host.Connections.RefreshConfiguredPeersAsync().ConfigureAwait(true);
        }
        catch
        {
            // ignore reconnect errors
        }
    }

    private async Task RemovePeerAndSyncAsync(string removedPeer)
    {
        try
        {
            await _host.Connections.DropPeerAsync(removedPeer).ConfigureAwait(true);
            await _host.BroadcastLayoutSyncAsync().ConfigureAwait(true);
            await _host.Connections.PruneUnconfiguredLinksAsync().ConfigureAwait(true);
            _host.RestoreCursorIfLayoutSolo();
        }
        catch
        {
            // ignore
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

    private void DirectTransfer_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new DirectTransferWindow(_host) { Owner = this };
        dlg.ShowDialog();
    }

    private void OpenInbox_Click(object sender, RoutedEventArgs e)
    {
        var dir = _host.Transfers.InboxDirectory;
        Directory.CreateDirectory(dir);
        Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
    }

    private async void ReceiveClipboardFiles_Click(object sender, RoutedEventArgs e)
    {
        if (!_host.Transfers.HasRemotePendingFiles)
        {
            StatusText.Text = Loc.T("Xfer.NoPendingClipboard");
            return;
        }

        StatusText.Text = Loc.T("Xfer.ReceivingClipboard");
        try
        {
            var files = await _host.Transfers.PullRemoteFilesForPasteAsync();
            StatusText.Text = files is { Count: > 0 }
                ? string.Format(Loc.T("Xfer.ReceivedClipboard"), files.Count)
                : Loc.T("Xfer.ReceiveClipboardFail");
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
        }
    }

    private void ClearTransfers_Click(object sender, RoutedEventArgs e)
    {
        var hasReceive = _host.Transfers.Items.Any(i =>
            i.Direction == TransferDirection.Receive
            && i.State is TransferState.Completed or TransferState.Failed or TransferState.Cancelled);
        if (!hasReceive)
        {
            _host.Transfers.ClearCompleted(deleteReceiveFiles: false);
            return;
        }

        var choice = ConfirmDialog.ShowTransferDelete(
            this,
            Loc.T("Xfer.ConfirmClearTitle"),
            Loc.T("Xfer.ConfirmClearReceive"));
        if (choice == UiConfirmChoice.Cancel) return;
        _host.Transfers.ClearCompleted(deleteReceiveFiles: choice == UiConfirmChoice.Primary);
    }

    private void DeleteTransfer_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not FrameworkElement { Tag: TransferItem item }) return;

        // 传输中：未完成文件无意义，直接删记录+文件
        if (item.State is TransferState.Queued or TransferState.Transferring)
        {
            _host.Transfers.RemoveItem(item, deleteCache: true);
            return;
        }

        // 发送项：不删本机源文件
        if (item.Direction != TransferDirection.Receive)
        {
            _host.Transfers.RemoveItem(item, deleteCache: false);
            return;
        }

        var choice = ConfirmDialog.ShowTransferDelete(
            this,
            Loc.T("Xfer.ConfirmDeleteTitle"),
            Loc.T("Xfer.ConfirmDeleteReceive"));
        if (choice == UiConfirmChoice.Cancel) return;
        _host.Transfers.RemoveItem(item, deleteCache: choice == UiConfirmChoice.Primary);
    }

    private void TransferFileName_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TransferItem item })
            RevealTransferPath(item);
        e.Handled = true;
    }

    private void TransferList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (TransferList.SelectedItem is TransferItem item)
            RevealTransferPath(item);
    }

    private void RevealTransferPath(TransferItem item)
    {
        if (!PathReveal.TryReveal(item.FullPath, out var error))
            StatusText.Text = string.IsNullOrWhiteSpace(error) ? "无法打开所在文件夹" : error;
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

        try
        {
            DragDrop.DoDragDrop(MachineMatrix, _dragItem, System.Windows.DragDropEffects.Move);
        }
        finally
        {
            ClearDropTarget();
            _dragItem = null;
        }
    }

    private void Matrix_DragOver(object sender, System.Windows.DragEventArgs e)
    {
        e.Effects = System.Windows.DragDropEffects.Move;
        e.Handled = true;
        if (_dragItem is not MachineSlot src) return;
        var under = FindSlotFromElement(e.OriginalSource as DependencyObject);
        SetDropTarget(under != null && !ReferenceEquals(under, src) ? under : null);
    }

    private void Matrix_DragLeave(object sender, System.Windows.DragEventArgs e)
    {
        // 离开矩阵或子项时清高亮；进入另一子项会再走 DragOver
        var pos = e.GetPosition(MachineMatrix);
        if (pos.X < 0 || pos.Y < 0 || pos.X > MachineMatrix.ActualWidth || pos.Y > MachineMatrix.ActualHeight)
            ClearDropTarget();
    }

    private void Matrix_Drop(object sender, System.Windows.DragEventArgs e)
    {
        try
        {
            if (_dragItem is not MachineSlot src) return;
            var target = FindSlotFromElement(e.OriginalSource as DependencyObject);
            if (target == null || ReferenceEquals(src, target)) return;

            // 互换两个格子内容，保持矩阵位置语义；远程池跟机器名走，刷新影子子卡
            SwapSlotData(src, target);
            SyncSlotLocalFlags();
            RefreshPositionLabels();
            RefreshSlotBoundRemotes();
            RedrawArrows();
            e.Handled = true;
        }
        finally
        {
            ClearDropTarget();
            _dragItem = null;
        }
    }

    private void SetDropTarget(MachineSlot? target)
    {
        foreach (var s in _slots)
            s.IsDropTarget = target != null && ReferenceEquals(s, target);
    }

    private void ClearDropTarget() => SetDropTarget(null);

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
        else if (NavSecurity.IsChecked == true) ShowPage(4);
        else if (NavAdvanced.IsChecked == true) ShowPage(1);
        else if (NavPrograms.IsChecked == true) ShowPage(8);
        else if (NavTools.IsChecked == true) ShowPage(9);
        else if (NavLog.IsChecked == true) ShowPage(7);
        else if (NavAbout.IsChecked == true) ShowPage(5);
        else if (NavHelp.IsChecked == true) ShowPage(6);
        else ShowPage(0);
    }

    private void ShowPage(int index)
    {
        PageBasic.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
        PageOptions.Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed;
        PageNetwork.Visibility = index == 2 ? Visibility.Visible : Visibility.Collapsed;
        PageTransfer.Visibility = index == 3 ? Visibility.Visible : Visibility.Collapsed;
        PageSecurity.Visibility = index == 4 ? Visibility.Visible : Visibility.Collapsed;
        PageAbout.Visibility = index == 5 ? Visibility.Visible : Visibility.Collapsed;
        PageHelp.Visibility = index == 6 ? Visibility.Visible : Visibility.Collapsed;
        PageLog.Visibility = index == 7 ? Visibility.Visible : Visibility.Collapsed;
        PagePrograms.Visibility = index == 8 ? Visibility.Visible : Visibility.Collapsed;
        PageTools.Visibility = index == 9 ? Visibility.Visible : Visibility.Collapsed;

        _syncingNav = true;
        try
        {
            switch (index)
            {
                case 0:
                    NavBasic.IsChecked = true;
                    break;
                case 1:
                    NavAdvanced.IsChecked = true;
                    break;
                case 2:
                    NavNetwork.IsChecked = true;
                    break;
                case 3:
                    NavShare.IsChecked = true;
                    break;
                case 4:
                    NavSecurity.IsChecked = true;
                    break;
                case 5:
                    NavAbout.IsChecked = true;
                    break;
                case 6:
                    NavHelp.IsChecked = true;
                    break;
                case 7:
                    NavLog.IsChecked = true;
                    break;
                case 8:
                    NavPrograms.IsChecked = true;
                    break;
                case 9:
                    NavTools.IsChecked = true;
                    break;
            }
        }
        finally { _syncingNav = false; }

        if (index == 0)
            Dispatcher.BeginInvoke(RedrawArrows, System.Windows.Threading.DispatcherPriority.Loaded);
        else if (index == 3)
            Dispatcher.BeginInvoke(FitTransferListColumns, System.Windows.Threading.DispatcherPriority.Loaded);
        else if (index == 5)
            RefreshAboutPage();
        else if (index == 6)
            ShowHelpDocument();
        else if (index == 9)
            RefreshToolsPage();
        else if (index == 7)
        {
            if (!_openProgramLog)
            {
                _logProgramId = null;
                _logProgramName = null;
            }
            _openProgramLog = false;
            RefreshLogHint();
            RefreshLogPage();
        }
        else if (index == 8)
        {
            RefreshProgramsPage();
            Dispatcher.BeginInvoke(UpdateProgToolMode, System.Windows.Threading.DispatcherPriority.Loaded);
        }
    }

    private void ProgToolBarHost_SizeChanged(object sender, SizeChangedEventArgs e)
        => UpdateProgToolMode();

    private void ProgList_SizeChanged(object sender, SizeChangedEventArgs e)
        => FitProgListColumns();

    private void TransferList_SizeChanged(object sender, SizeChangedEventArgs e)
        => FitTransferListColumns();

    /// <summary>传输列表：固定列定宽，文件名列吃满剩余空间，避免操作列后空白。</summary>
    private void FitTransferListColumns()
    {
        if (TransferList == null || ColName == null) return;
        var avail = TransferList.ActualWidth;
        if (avail <= 1) return;
        // 预留纵向滚动条与边距，避免出现横向滚动条抖动
        var scroll = SystemParameters.VerticalScrollBarWidth + 8;
        const double fixedCols = 56 + 120 + 100 + 88 + 40; // 方向/对端/大小/状态/操作
        var nameW = Math.Max(120, avail - scroll - fixedCols);
        if (Math.Abs(ColName.Width - nameW) > 0.5)
            ColName.Width = nameW;
    }

    /// <summary>程序列表：固定列按内容宽度，名称列吃满剩余空间，避免右侧留白。</summary>
    private void FitProgListColumns()
    {
        if (ProgList == null || ProgColName == null) return;
        var avail = ProgList.ActualWidth;
        if (avail <= 1) return;
        // 预留纵向滚动条与边距，避免出现横向滚动条抖动
        var scroll = SystemParameters.VerticalScrollBarWidth + 8;
        const double fixedCols = 36 + 110 + 70 + 70 + 176; // 勾选/语言/大小/状态/操作
        var nameW = Math.Max(120, avail - scroll - fixedCols);
        if (Math.Abs(ProgColName.Width - nameW) > 0.5)
            ProgColName.Width = nameW;
    }

    /// <summary>程序页顶部工具按钮：放得下时图标+文字，放不下时只留图标。</summary>
    private void UpdateProgToolMode()
    {
        if (!IsLoaded || _progToolModeUpdating || ProgToolBarHost.ActualWidth <= 1) return;
        _progToolModeUpdating = true;
        try
        {
            var available = ProgToolBarHost.ActualWidth;
            // 先按「图标+文字」测量。左对齐时 ActualWidth 不会超过父级，必须用无限宽 Measure。
            ProgToolsCompact = false;
            ProgToolBar.Measure(new System.Windows.Size(double.PositiveInfinity, 48));
            var toolbarNeed = ProgToolBar.DesiredSize.Width;
            ProgToolsCompact = toolbarNeed > available + 0.5;
        }
        finally
        {
            _progToolModeUpdating = false;
        }
    }

    private void RefreshProgramsPage()
    {
        var svc = ProgramService.Instance;
        foreach (var p in svc.Catalog.Items)
        {
            svc.Catalog.RefreshSize(p);
            svc.Runner.SyncStatus(p);
        }
        ProgList.ItemsSource = null;
        ProgList.ItemsSource = svc.Catalog.Items;
        Dispatcher.BeginInvoke(FitProgListColumns, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private IEnumerable<UserProgram> SelectedPrograms()
        => ProgramService.Instance.Catalog.Items.Where(p => p.IsSelected);

    private void ProgSelectAll_Click(object sender, RoutedEventArgs e)
    {
        var on = ProgSelectAll.IsChecked == true;
        foreach (var p in ProgramService.Instance.Catalog.Items)
            p.IsSelected = on;
    }

    private void ProgAdd_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new ProgramAddDialog { Owner = this };
        if (dlg.ShowDialog() != true) return;
        try
        {
            ProgramService.Instance.Catalog.Create(
                dlg.ProgramName, dlg.SelectedLanguage, dlg.NamespaceValue, dlg.FileNameValue);
            RefreshProgramsPage();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Loc.T("Prog.Add"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void ProgDelete_Click(object sender, RoutedEventArgs e)
    {
        var list = SelectedPrograms().ToList();
        if (list.Count == 0)
        {
            MessageBox.Show(this, Loc.T("Prog.SelectFirst"), Loc.T("Prog.Delete"), MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (MessageBox.Show(this, Loc.T("Prog.ConfirmDelete"), Loc.T("Prog.Delete"),
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        await ProgramService.Instance.Runner.DeleteManyAsync(list);
        RefreshProgramsPage();
    }

    private async void ProgStart_Click(object sender, RoutedEventArgs e)
    {
        var list = SelectedPrograms().ToList();
        if (list.Count == 0)
        {
            MessageBox.Show(this, Loc.T("Prog.SelectFirst"), Loc.T("Prog.Start"), MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        await ProgramService.Instance.Runner.StartManyAsync(list);
        RefreshProgramsPage();
    }

    private async void ProgStop_Click(object sender, RoutedEventArgs e)
    {
        var list = SelectedPrograms().ToList();
        if (list.Count == 0)
        {
            MessageBox.Show(this, Loc.T("Prog.SelectFirst"), Loc.T("Prog.Stop"), MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        await ProgramService.Instance.Runner.StopManyAsync(list);
        RefreshProgramsPage();
    }

    private async void ProgRowStart_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not UserProgram prog) return;
        var runner = ProgramService.Instance.Runner;
        var stop = runner.IsRunning(prog.Id)
                   || prog.RuntimeStatus is ProgramStatus.Running or ProgramStatus.Stopping;
        try
        {
            if (stop) await runner.StopAsync(prog);
            else await runner.StartAsync(prog);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message,
                stop ? Loc.T("Prog.ActStop") : Loc.T("Prog.ActStart"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        RefreshProgramsPage();
    }

    private void ProgRowEdit_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not UserProgram prog) return;
        var win = new ProgramEditorWindow(prog) { Owner = this };
        win.ShowDialog();
        RefreshProgramsPage();
    }

    private void ProgRowSettings_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not UserProgram prog) return;
        var win = new ProgramSettingsWindow(prog) { Owner = this };
        win.ShowDialog();
    }

    private async void ProgRowDelete_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not UserProgram prog) return;
        if (prog.IsBuiltin
            || string.Equals(prog.Id, BuiltinPrograms.GenericSwipeId, StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(this, Loc.T("Prog.BuiltinNoDelete"), Loc.T("Prog.ActDelete"),
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (MessageBox.Show(this, Loc.T("Prog.ConfirmDelete"), Loc.T("Prog.ActDelete"),
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        try { await ProgramService.Instance.Runner.DeleteAsync(prog); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, Loc.T("Prog.ActDelete"), MessageBoxButton.OK, MessageBoxImage.Warning); }
        RefreshProgramsPage();
    }

    private void ProgRowLog_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not UserProgram prog) return;
        _logProgramId = prog.Id;
        _logProgramName = prog.Name;
        _logPageFromLatest = 0;
        _openProgramLog = true;
        if (NavLog.IsChecked == true)
            ShowPage(7);
        else
            NavLog.IsChecked = true;
    }

    private async void ProgRowShare_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not UserProgram prog) return;
        var share = ProgramShareService.Current;
        if (share == null)
        {
            MessageBox.Show(this, Loc.T("Prog.ShareNone"), Loc.T("Prog.ActShare"), MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        ProgramShareResult result;
        try
        {
            result = await share.ShareAsync(prog);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Loc.T("Prog.ActShare"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var text = result.Reachable == 0
            ? Loc.T("Prog.ShareNone")
            : string.Format(Loc.T("Prog.ShareSent"), result.Sent);
        MessageBox.Show(this, text, Loc.T("Prog.ActShare"), MessageBoxButton.OK,
            result.Reachable == 0 ? MessageBoxImage.Information : MessageBoxImage.Information);
    }

    private void RefreshLogHint()
    {
        var baseHint = string.IsNullOrEmpty(_logProgramName)
            ? Loc.T("Log.Hint")
            : string.Format(Loc.T("Prog.LogFilter"), _logProgramName);
        LogHintText.Text = baseHint + " " + Loc.T("Log.SelectTip");
    }

    private sealed class LogLevelOption
    {
        public string Name { get; init; } = "";
        public AppLogLevelFilter Filter { get; init; }
        public override string ToString() => Name;
    }

    private AppLogLevelFilter CurrentLogLevelFilter()
    {
        if (LogLevelFilter.SelectedItem is LogLevelOption opt)
            return opt.Filter;
        return AppLogLevelFilter.All;
    }

    private void EnsureLogLevelFilterItems()
    {
        // 勿 new ComboBoxItem：未入视觉树时主题 Style 会 RelativeSource 找 ItemsControl，刷一串 Data Error 4
        var selected = CurrentLogLevelFilter();
        _logFilterReady = false;
        var options = new LogLevelOption[]
        {
            new() { Filter = AppLogLevelFilter.All, Name = Loc.T("Log.LevelAll") },
            new() { Filter = AppLogLevelFilter.Debug, Name = Loc.T("Log.LevelDebug") },
            new() { Filter = AppLogLevelFilter.Info, Name = Loc.T("Log.LevelInfo") },
            new() { Filter = AppLogLevelFilter.Warn, Name = Loc.T("Log.LevelWarn") },
            new() { Filter = AppLogLevelFilter.Error, Name = Loc.T("Log.LevelError") },
        };
        LogLevelFilter.DisplayMemberPath = nameof(LogLevelOption.Name);
        LogLevelFilter.ItemsSource = options;
        LogLevelFilter.SelectedItem = options.FirstOrDefault(o => o.Filter == selected) ?? options[0];
        _logFilterReady = true;
    }

    private void ApplyLogPageChrome(AppLogPage page)
    {
        _logPageFromLatest = page.PageFromLatest;
        _logTotalPages = page.TotalPages;
        _logKnownTotal = page.TotalCount;
        LogPageText.Text = FormatLogPageText(page.TotalCount, page.PageFromLatest, page.TotalPages);
        LogPrevBtn.IsEnabled = page.HasOlder;
        LogNextBtn.IsEnabled = page.HasNewer;
        LogLatestBtn.IsEnabled = page.HasNewer || _logFrom != null || _logTo != null;
    }

    private string FormatLogPageText(int totalCount, int pageFromLatest, int totalPages)
    {
        if (totalCount == 0)
            return Loc.T("Log.Empty");
        if (_logFrom != null || _logTo != null)
        {
            var from = _logFrom ?? DateTime.MinValue;
            var to = _logTo ?? DateTime.MaxValue;
            return pageFromLatest == 0
                ? string.Format(Loc.T("Log.PageDateLatest"), from, to, totalCount)
                : string.Format(Loc.T("Log.PageDate"), from, to, pageFromLatest + 1, totalPages, totalCount);
        }
        return pageFromLatest == 0
            ? string.Format(Loc.T("Log.PageLatest"), totalCount)
            : string.Format(Loc.T("Log.Page"), pageFromLatest + 1, totalPages, totalCount);
    }

    private bool IsLogLiveMode()
        => _logPageFromLatest == 0 && _logFrom == null && _logTo == null;

    private void ReplaceLogItems(IReadOnlyList<AppLogEntry> items)
    {
        _logItems.Clear();
        foreach (var e in items)
            _logItems.Add(e);
    }

    private void LogToolBarHost_SizeChanged(object sender, SizeChangedEventArgs e)
        => UpdateLogToolMode();

    /// <summary>日志工具栏：一行排布；放不下时只留图标。</summary>
    private void UpdateLogToolMode()
    {
        if (!IsLoaded || _logToolModeUpdating || LogToolBarHost.ActualWidth <= 1) return;
        _logToolModeUpdating = true;
        try
        {
            var available = LogToolBarHost.ActualWidth;
            LogToolsCompact = false;
            LogLevelFilter.Width = 96;
            LogToolBar.Measure(new System.Windows.Size(double.PositiveInfinity, 48));
            var need = LogToolBar.DesiredSize.Width;
            LogToolsCompact = need > available + 0.5;
            // 极窄：图标按钮 + 缩短筛选框（仍保证两字级别完整可见）
            LogLevelFilter.Width = LogToolsCompact
                ? (available < 240 ? 72 : 84)
                : 96;
        }
        finally
        {
            _logToolModeUpdating = false;
        }
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject? root)
    {
        if (root == null) return null;
        if (root is ScrollViewer sv) return sv;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var found = FindScrollViewer(VisualTreeHelper.GetChild(root, i));
            if (found != null) return found;
        }
        return null;
    }

    private void ScrollLogToEndQuiet()
    {
        var sv = FindScrollViewer(LogList);
        if (sv == null) return;
        sv.ScrollToVerticalOffset(sv.ExtentHeight);
    }

    /// <summary>最新页：水印增量追加；失配时整页替换尾部（禁止按时间重放整表）。</summary>
    private void RefreshLogPageLive()
    {
        if (!IsLogLiveMode()) return;
        var level = CurrentLogLevelFilter();
        var stick = _logStickToBottom;
        var delta = AppLog.DeltaSince(
            _logWatermark, level, _logProgramId, _logProgramName, AppLog.DefaultPageSize,
            _logFrom, _logTo);
        var uiChanged = false;

        if (delta.Reset)
        {
            uiChanged = !LogItemsSameTail(delta.Items);
            if (uiChanged)
                ReplaceLogItems(delta.Items);
            _logWatermark = _logItems.Count > 0 ? _logItems[^1] : null;
            if (uiChanged)
                _logKnownTotal = Math.Max(_logKnownTotal, _logItems.Count);
        }
        else if (delta.Items.Count > 0)
        {
            foreach (var e in delta.Items)
                _logItems.Add(e);
            _logWatermark = _logItems[^1];
            _logKnownTotal = Math.Max(_logKnownTotal + delta.Items.Count, _logItems.Count);
            uiChanged = true;
        }
        else
            return;

        if (!uiChanged)
            return;

        LogPageText.Text = FormatLogPageText(_logKnownTotal, 0, _logTotalPages);
        LogPrevBtn.IsEnabled = _logKnownTotal > AppLog.DefaultPageSize || _logTotalPages > 1;
        LogNextBtn.IsEnabled = false;
        LogLatestBtn.IsEnabled = false;

        _logStickToBottom = stick;
        if (stick)
            Dispatcher.BeginInvoke(ScrollLogToEndQuiet, System.Windows.Threading.DispatcherPriority.Background);
    }

    private bool LogItemsSameTail(IReadOnlyList<AppLogEntry> items)
    {
        if (_logItems.Count != items.Count) return false;
        if (items.Count == 0) return true;
        return ReferenceEquals(_logItems[0], items[0])
               && ReferenceEquals(_logItems[^1], items[^1]);
    }

    private void RefreshLogPage()
    {
        var level = CurrentLogLevelFilter();
        var page = AppLog.GetPage(
            _logPageFromLatest,
            AppLog.DefaultPageSize,
            level,
            _logProgramId,
            _logProgramName,
            _logFrom,
            _logTo);
        ApplyLogPageChrome(page);
        if (page.PageFromLatest == 0 && IsLogLiveMode())
        {
            // 实时页用水印增量源（内存）；内存空时才回退磁盘页
            var delta = AppLog.DeltaSince(
                null, level, _logProgramId, _logProgramName, AppLog.DefaultPageSize, _logFrom, _logTo);
            if (delta.Items.Count > 0)
                ReplaceLogItems(delta.Items);
            else
                ReplaceLogItems(page.Items);
            _logWatermark = _logItems.Count > 0 && delta.Items.Count > 0 ? _logItems[^1] : null;
            _logStickToBottom = true;
            Dispatcher.BeginInvoke(ScrollLogToEndQuiet, System.Windows.Threading.DispatcherPriority.Loaded);
        }
        else
        {
            ReplaceLogItems(page.Items);
            _logWatermark = null;
            _logStickToBottom = page.PageFromLatest == 0;
            if (_logStickToBottom)
                Dispatcher.BeginInvoke(ScrollLogToEndQuiet, System.Windows.Threading.DispatcherPriority.Loaded);
        }
    }

    private void LogList_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.ViewportHeight <= 0) return;
        // 程序化贴底滚动时忽略，避免 Add 导致 Extent 变化把 stick 打成 false
        if (e.ExtentHeightChange != 0 && e.VerticalChange == 0)
            return;
        _logStickToBottom = e.VerticalOffset + e.ViewportHeight >= e.ExtentHeight - 24;
    }

    private void LogFilter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_logFilterReady) return;
        _logPageFromLatest = 0;
        if (IsLoaded && PageLog.Visibility == Visibility.Visible)
            RefreshLogPage();
    }

    private void LogPrev_Click(object sender, RoutedEventArgs e)
    {
        _logPageFromLatest++;
        RefreshLogPage();
    }

    private void LogNext_Click(object sender, RoutedEventArgs e)
    {
        if (_logPageFromLatest > 0)
            _logPageFromLatest--;
        RefreshLogPage();
    }

    private void LogLatest_Click(object sender, RoutedEventArgs e)
    {
        _logFrom = null;
        _logTo = null;
        _logPageFromLatest = 0;
        RefreshLogPage();
    }

    private void LogDate_Click(object sender, RoutedEventArgs e)
    {
        var (kind, from, to) = LogDateRangeDialog.Show(this, _logFrom, _logTo);
        if (kind == LogDateRangeDialog.ResultKind.Cancel) return;
        if (kind == LogDateRangeDialog.ResultKind.Clear)
        {
            _logFrom = null;
            _logTo = null;
        }
        else
        {
            _logFrom = from;
            _logTo = to;
        }
        _logPageFromLatest = 0;
        RefreshLogPage();
    }

    private void LogClear_Click(object sender, RoutedEventArgs e)
    {
        AppLog.Clear();
        _logPageFromLatest = 0;
        _logTotalPages = 0;
        _logKnownTotal = 0;
        _logFrom = null;
        _logTo = null;
        _logWatermark = null;
        _logItems.Clear();
        LogPageText.Text = Loc.T("Log.Empty");
        LogPrevBtn.IsEnabled = false;
        LogNextBtn.IsEnabled = false;
        LogLatestBtn.IsEnabled = false;
    }

    private void LogDownload_Click(object sender, RoutedEventArgs e)
    {
        var text = AppLog.FormatExport(
            CurrentLogLevelFilter(),
            _logProgramId,
            _logProgramName,
            _logFrom,
            _logTo);
        if (string.IsNullOrEmpty(text))
        {
            StatusText.Text = Loc.T("Log.DownloadEmpty");
            return;
        }

        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = Loc.T("Log.Download"),
            Filter = "Text (*.txt)|*.txt|All files (*.*)|*.*",
            FileName = AppLog.SuggestExportFileName(),
            DefaultExt = ".txt",
            AddExtension = true
        };
        if (dlg.ShowDialog(this) != true) return;

        try
        {
            File.WriteAllText(dlg.FileName, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            StatusText.Text = string.Format(Loc.T("Log.DownloadOk"), dlg.FileName);
        }
        catch (Exception ex)
        {
            StatusText.Text = string.Format(Loc.T("Log.DownloadFail"), ex.Message);
        }
    }

    private void LogCopy_Click(object sender, RoutedEventArgs e)
        => CopySelectedLogEntries();

    private void LogCopy_Executed(object sender, ExecutedRoutedEventArgs e)
        => CopySelectedLogEntries();

    private void CopySelectedLogEntries()
    {
        var selected = LogList.SelectedItems.OfType<AppLogEntry>().ToList();
        if (selected.Count == 0)
        {
            StatusText.Text = Loc.T("Log.CopyEmpty");
            return;
        }

        // 按列表顺序（选中集合无序）
        var ordered = _logItems.Where(selected.Contains).Select(e => e.Display);
        var text = string.Join(Environment.NewLine, ordered);
        try
        {
            System.Windows.Clipboard.SetText(text);
            StatusText.Text = string.Format(Loc.T("Log.CopyOk"), selected.Count);
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
        }
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
        else if (q.Contains("密钥") || q.Contains("安全"))
            ShowPage(4);
        else if (q.Contains("程序") || q.Contains("脚本") || q.Contains("python"))
            ShowPage(8);
        else if (q.Contains("工具") || q.Contains("共享设备") || q.Contains("摄像头") || q.Contains("麦克风") || q.Contains("扬声器"))
            ShowPage(9);
        else if (q.Contains("日志") || q.Contains("错误"))
            ShowPage(7);
        else if (q.Contains("关于") || q.Contains("版本"))
            ShowPage(5);
        else if (q.Contains("帮助") || q.Contains("更新") || q.Contains("诊断") || q.Contains("修复"))
            ShowPage(6);
        else if (q.Contains("布局") || q.Contains("基础"))
            ShowPage(0);
    }

    private void RefreshAboutPage()
    {
        AboutTitleText.Text = Loc.T("About.Title");
        AboutBodyText.Inlines.Clear();
        var docs = AboutDocument.Build(AppVersionDisplay.Current);
        for (var i = 0; i < docs.Count; i++)
        {
            var para = docs[i];
            if (para.Spans.Count == 0)
            {
                AboutBodyText.Inlines.Add(new LineBreak());
                continue;
            }

            foreach (var span in para.Spans)
            {
                if (!string.IsNullOrEmpty(span.Url))
                {
                    var link = new Hyperlink(new Run(span.Text))
                    {
                        NavigateUri = new Uri(span.Url),
                        Foreground = TryFindResource("Blue") as Media.Brush ?? Media.Brushes.DodgerBlue
                    };
                    link.RequestNavigate += (_, e) =>
                    {
                        try
                        {
                            Process.Start(new ProcessStartInfo
                            {
                                FileName = e.Uri.AbsoluteUri,
                                UseShellExecute = true
                            });
                        }
                        catch { /* ignore */ }
                        e.Handled = true;
                    };
                    AboutBodyText.Inlines.Add(link);
                }
                else
                {
                    var run = new Run(span.Text);
                    if (para.IsHeading)
                        run.FontWeight = FontWeights.SemiBold;
                    AboutBodyText.Inlines.Add(run);
                }
            }

            if (i < docs.Count - 1)
                AboutBodyText.Inlines.Add(new LineBreak());
        }
    }

    private void RefreshHelpToolbarLabels()
    {
        HelpUpdateBtn.Content = _helpUpdateReady ? Loc.T("Help.ApplyUpdate") : Loc.T("Help.CheckUpdate");
        HelpDiagBtn.Content = _helpFixReady ? Loc.T("Help.DiagFix") : Loc.T("Help.DiagCheck");
        HelpUpdateBtn.Style = (Style)FindResource(_helpUpdateReady ? "PrimaryBtn" : "SoftBlueBtn");
        HelpDiagBtn.Style = (Style)FindResource(_helpFixReady ? "PrimaryBtn" : "SoftWarnBtn");
    }

    private void HelpDoc_Click(object sender, RoutedEventArgs e) => ShowHelpDocument();

    private async void HelpUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_helpUpdateReady)
            await ApplyHelpUpdateAsync();
        else
            await CheckHelpUpdateAsync();
    }

    private async void HelpDiag_Click(object sender, RoutedEventArgs e)
    {
        if (_helpFixReady)
            await FixHelpDiagAsync();
        else
            await CheckHelpDiagAsync();
    }

    private async Task CheckHelpUpdateAsync()
    {
        SetHelpBusy(true);
        try
        {
            ShowHelpLog(Loc.T("Help.CheckingUpdate"));
            var result = await _helpUpdater.CheckAsync().ConfigureAwait(true);
            _helpLastCheck = result;
            _helpUpdateReady = result is { Success: true, UpdateAvailable: true };
            RefreshHelpToolbarLabels();

            if (result.Success && result.UpdateAvailable)
            {
                var notes = string.IsNullOrWhiteSpace(result.ReleaseNotes)
                    ? result.Message
                    : $"版本 {result.RemoteVersion}\n\n{result.ReleaseNotes}";
                ShowHelpLog(notes);
            }
            else if (result.Success)
            {
                ShowHelpLog(string.IsNullOrWhiteSpace(result.Message) ? Loc.T("Help.NoUpdate") : result.Message);
            }
            else
            {
                ShowHelpLog(result.Message);
            }
        }
        finally
        {
            SetHelpBusy(false);
        }
    }

    private async Task ApplyHelpUpdateAsync()
    {
        if (_helpLastCheck is not { Success: true, UpdateAvailable: true })
        {
            _helpUpdateReady = false;
            RefreshHelpToolbarLabels();
            ShowHelpLog(Loc.T("Help.NeedCheckFirst"));
            return;
        }

        var confirm = MessageBox.Show(
            this,
            string.Format(
                Loc.T("Help.ConfirmUpdate"),
                _helpLastCheck.RemoteVersion,
                _helpLastCheck.AssetName ?? Loc.T("Help.AssetUnnamed")),
            Loc.T("Help.ConfirmUpdateTitle"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        SetHelpBusy(true);
        ShowHelpUpdateProgress(0, indeterminate: false);
        try
        {
            var progress = new Progress<UpdateDownloadProgress>(OnHelpUpdateProgress);
            var apply = await _helpUpdater.DownloadAndApplyAsync(_helpLastCheck, progress).ConfigureAwait(true);
            ShowHelpLog(apply.Message);
            if (apply.ShouldExit)
            {
                ShowHelpLog(Loc.T("Help.Restarting"));
                HelpUpdateProgressBar.IsIndeterminate = true;
                HelpUpdateProgressBar.Visibility = Visibility.Visible;
                await Task.Delay(300).ConfigureAwait(true);
                ExitForUpdate();
                return;
            }

            HideHelpUpdateProgress();
        }
        finally
        {
            if (IsLoaded)
                SetHelpBusy(false);
        }
    }

    private void OnHelpUpdateProgress(UpdateDownloadProgress p)
    {
        var msg = string.IsNullOrWhiteSpace(p.Message)
            ? string.Format(Loc.T("Help.Downloading"), Math.Max(0, p.Percent))
            : p.Message;
        ShowHelpLog(msg);
        ShowHelpUpdateProgress(p.Percent, p.IsIndeterminate);
    }

    private async Task CheckHelpDiagAsync()
    {
        SetHelpBusy(true);
        try
        {
            ShowHelpLog(Loc.T("Help.CheckingConn"));
            var diag = new ConnectionDiagnostics(_host);
            var report = await diag.CheckAsync().ConfigureAwait(true);
            _helpFixReady = report.Items.Any(i =>
                i.Level is DiagnosticLevel.Error or DiagnosticLevel.Warn);
            RefreshHelpToolbarLabels();
            var text = report.FormatText();
            if (!_helpFixReady)
                text += "\n\n" + Loc.T("Help.NoProblems");
            ShowHelpLog(text);
        }
        finally
        {
            SetHelpBusy(false);
        }
    }

    private async Task FixHelpDiagAsync()
    {
        SetHelpBusy(true);
        try
        {
            var diag = new ConnectionDiagnostics(_host);
            var progress = new Progress<string>(s => ShowHelpLog(s));
            var report = await diag.FixAsync(progress).ConfigureAwait(true);
            _helpFixReady = report.Items.Any(i =>
                i.Level is DiagnosticLevel.Error or DiagnosticLevel.Warn);
            RefreshHelpToolbarLabels();
            ShowHelpLog(report.FormatText());
            ReloadAfterDiagnostics();
        }
        finally
        {
            SetHelpBusy(false);
        }
    }

    private void SetHelpBusy(bool busy)
    {
        HelpDocBtn.IsEnabled = !busy;
        HelpUpdateBtn.IsEnabled = !busy;
        HelpDiagBtn.IsEnabled = !busy;
        Cursor = busy ? System.Windows.Input.Cursors.Wait : System.Windows.Input.Cursors.Arrow;
    }

    private void ShowHelpUpdateProgress(double percent, bool indeterminate)
    {
        HelpUpdateProgressBar.Visibility = Visibility.Visible;
        HelpUpdateProgressBar.IsIndeterminate = indeterminate;
        if (!indeterminate)
            HelpUpdateProgressBar.Value = Math.Clamp(percent, 0, 100);
    }

    private void HideHelpUpdateProgress()
    {
        HelpUpdateProgressBar.IsIndeterminate = false;
        HelpUpdateProgressBar.Value = 0;
        HelpUpdateProgressBar.Visibility = Visibility.Collapsed;
    }

    private void ShowHelpDocument()
    {
        HideHelpUpdateProgress();
        HelpRich.Document = BuildHelpDocument();
        HelpRich.Foreground = TryFindResource("Text") as Media.Brush ?? Media.Brushes.Black;
        HelpRich.Background = Media.Brushes.Transparent;
    }

    private void ShowHelpLog(string text)
    {
        var doc = new FlowDocument
        {
            PagePadding = new Thickness(0),
            FontFamily = new Media.FontFamily("Consolas, Microsoft YaHei UI, Segoe UI"),
            FontSize = 13,
            LineHeight = 20,
            Foreground = TryFindResource("Text") as Media.Brush ?? Media.Brushes.Black
        };
        foreach (var line in (text ?? "").Replace("\r\n", "\n").Split('\n'))
        {
            doc.Blocks.Add(new Paragraph(new Run(line))
            {
                Margin = new Thickness(0, 0, 0, 2)
            });
        }

        HelpRich.Document = doc;
    }

    private FlowDocument BuildHelpDocument()
    {
        var muted = TryFindResource("Muted") as Media.Brush ?? Media.Brushes.Gray;
        var text = TryFindResource("Text") as Media.Brush ?? Media.Brushes.Black;
        var doc = new FlowDocument
        {
            PagePadding = new Thickness(0),
            FontFamily = new Media.FontFamily("Microsoft YaHei UI, Segoe UI"),
            FontSize = 13,
            LineHeight = 22,
            Foreground = text
        };

        Paragraph DocTitle(string t) => new(new Run(t)
        {
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Foreground = text
        })
        { Margin = new Thickness(0, 0, 0, 10) };

        Paragraph Heading(string t) => new(new Run(t)
        {
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = text
        })
        { Margin = new Thickness(0, 12, 0, 6) };

        Paragraph Para(string t) => new(new Run(t) { Foreground = muted })
        { Margin = new Thickness(0, 0, 0, 4) };

        List Numbered(params string[] items)
        {
            var list = new List { MarkerStyle = TextMarkerStyle.Decimal, Margin = new Thickness(8, 0, 0, 4) };
            foreach (var item in items)
                list.ListItems.Add(new ListItem(Para(item)));
            return list;
        }

        List Bullets(params string[] items)
        {
            var list = new List { MarkerStyle = TextMarkerStyle.Disc, Margin = new Thickness(8, 0, 0, 4) };
            foreach (var item in items)
                list.ListItems.Add(new ListItem(Para(item)));
            return list;
        }

        doc.Blocks.Add(DocTitle(Loc.T("Help.Doc.Title")));
        doc.Blocks.Add(Para(Loc.T("Help.Doc.Intro")));
        doc.Blocks.Add(Heading(Loc.T("Help.Doc.Quick")));
        doc.Blocks.Add(Numbered(
            Loc.T("Help.Doc.Q1"), Loc.T("Help.Doc.Q2"), Loc.T("Help.Doc.Q3"),
            Loc.T("Help.Doc.Q4"), Loc.T("Help.Doc.Q5")));
        doc.Blocks.Add(Heading(Loc.T("Help.Doc.Clip")));
        doc.Blocks.Add(Bullets(
            Loc.T("Help.Doc.C1"), Loc.T("Help.Doc.C2"),
            Loc.T("Help.Doc.C3"), Loc.T("Help.Doc.C4"), Loc.T("Help.Doc.C5")));
        doc.Blocks.Add(Heading(Loc.T("Help.Doc.Opts")));
        doc.Blocks.Add(Bullets(
            Loc.T("Help.Doc.O1"), Loc.T("Help.Doc.O2"), Loc.T("Help.Doc.O3"),
            Loc.T("Help.Doc.O4"), Loc.T("Help.Doc.O5"), Loc.T("Help.Doc.O6"), Loc.T("Help.Doc.O7")));
        doc.Blocks.Add(Heading(Loc.T("Help.Doc.Hot")));
        doc.Blocks.Add(Para(Loc.T("Help.Doc.H1")));
        doc.Blocks.Add(Para(Loc.T("Help.Doc.H2")));
        doc.Blocks.Add(Heading(Loc.T("Help.Doc.Net")));
        doc.Blocks.Add(Para(Loc.T("Help.Doc.N1")));
        doc.Blocks.Add(Para(Loc.T("Help.Doc.N2")));
        doc.Blocks.Add(Heading(Loc.T("Help.Doc.Update")));
        doc.Blocks.Add(Para(Loc.T("Help.Doc.U1")));
        doc.Blocks.Add(Para(GitHubUpdateService.ReleasesUrl));
        doc.Blocks.Add(Para(GitHubUpdateService.GiteeReleasesUrl));
        doc.Blocks.Add(Heading(Loc.T("Help.Doc.Diag")));
        doc.Blocks.Add(Para(Loc.T("Help.Doc.D1")));
        return doc;
    }

    /// <summary>诊断修复后刷新布局槽位与连接状态显示。</summary>
    public void ReloadAfterDiagnostics()
    {
        ApplyLayoutFromSettings();
        foreach (var st in _host.Connections.GetConfiguredPeerStatuses())
        {
            var slot = _slots.FirstOrDefault(s =>
                string.Equals(s.Name, st.Name, StringComparison.OrdinalIgnoreCase));
            if (slot == null) continue;
            slot.OutboundOk = st.Outbound;
            slot.InboundOk = st.Inbound;
            if (!string.IsNullOrEmpty(st.DiscoveredIp))
                slot.ResolvedIp = st.DiscoveredIp;
            slot.RefreshStatus();
        }
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

    private void RegisterToolUiBridge()
        => FlyScreen.Tools.Ui.ToolUiBootstrap.RegisterAll(this);

    private void RefreshToolsPage()
    {
        if (ToolsList == null) return;
        ToolIconRegistry.EnsureFromLoadedAssemblies();
        ToolIconImageFactory.ClearCache();
        ToolsList.ItemsSource = ToolCardRows.Build();
        Dispatcher.BeginInvoke(ApplyToolsWrapColumns, System.Windows.Threading.DispatcherPriority.Loaded);
        RefreshToolsShortcuts();
        RefreshToolsWorkspace();
    }

    private void RefreshToolsShortcuts()
    {
        if (ToolsShortcutBar == null) return;
        ToolsShortcutBar.Children.Clear();
        var rows = ToolCardRows.Build();
        var occupied = ToolService.Instance.Shortcuts.Occupied;
        var muted = TryFindResource("Muted") as System.Windows.Media.Brush
                    ?? new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x6B, 0x72, 0x80));
        var textBrush = TryFindResource("Text") as System.Windows.Media.Brush
                        ?? new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x11, 0x18, 0x27));
        var line = TryFindResource("Line") as System.Windows.Media.Brush
                   ?? new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE5, 0xE7, 0xEB));
        var subtle = TryFindResource("SubtleBg") as System.Windows.Media.Brush
                     ?? new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF3, 0xF4, 0xF6));

        for (var i = 0; i < occupied.Count; i++)
        {
            var index = i;
            var id = occupied[i];
            var row = rows.FirstOrDefault(r => string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase));
            if (row == null) continue;

            var slot = CreateToolsShortcutChrome(width: 64, allowDrop: true, tag: index);
            var iconBox = new Border
            {
                Width = 40,
                Height = 40,
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(1),
                BorderBrush = line,
                Background = subtle,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                Child = CreateToolShortcutIcon(row.IconKey, size: 28)
            };
            var stack = new StackPanel { HorizontalAlignment = System.Windows.HorizontalAlignment.Center };
            stack.Children.Add(iconBox);
            stack.Children.Add(new TextBlock
            {
                Text = row.Name,
                FontSize = 11,
                Foreground = textBrush,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.NoWrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 64,
                Margin = new Thickness(0, 4, 0, 0)
            });
            slot.Child = stack;
            slot.MouseLeftButtonUp += async (_, _) =>
            {
                if (!row.IsInstalled) return;
                await HandleToolActionAsync(row, primary: true);
            };
            slot.MouseRightButtonUp += (_, e) =>
            {
                ToolService.Instance.Shortcuts.SetSlot(index, null);
                RefreshToolsShortcuts();
                e.Handled = true;
            };
            WireToolsShortcutDrop(slot, rows, replaceIndex: index);
            ToolsShortcutBar.Children.Add(slot);
        }

        if (!ToolService.Instance.Shortcuts.IsFull)
        {
            var placeholder = CreateToolsShortcutChrome(width: 64, allowDrop: true, tag: -1);
            var dash = new System.Windows.Shapes.Rectangle
            {
                Width = 40,
                Height = 40,
                RadiusX = 8,
                RadiusY = 8,
                Stroke = muted,
                StrokeThickness = 1.5,
                StrokeDashArray = [3, 2],
                Fill = System.Windows.Media.Brushes.Transparent,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center
            };
            var stack = new StackPanel { HorizontalAlignment = System.Windows.HorizontalAlignment.Center };
            stack.Children.Add(dash);
            stack.Children.Add(new TextBlock
            {
                Text = Loc.T("Tool.Shortcut.DragHere"),
                FontSize = 11,
                Foreground = muted,
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(0, 4, 0, 0)
            });
            placeholder.Child = stack;
            WireToolsShortcutDrop(placeholder, rows, replaceIndex: null);
            ToolsShortcutBar.Children.Add(placeholder);
        }
    }

    private static Border CreateToolsShortcutChrome(double width, bool allowDrop, object? tag)
        => new()
        {
            Width = width,
            Margin = new Thickness(0, 0, 10, 0),
            Background = System.Windows.Media.Brushes.Transparent,
            AllowDrop = allowDrop,
            Cursor = System.Windows.Input.Cursors.Hand,
            Tag = tag,
            VerticalAlignment = VerticalAlignment.Top
        };

    /// <summary>与工具卡片同一套 IconKey → 工具程序集贡献的位图。</summary>
    private static System.Windows.UIElement CreateToolShortcutIcon(string? iconKey, double size)
    {
        var src = ToolIconImageFactory.TryGet(iconKey);
        if (src != null)
        {
            return new System.Windows.Controls.Image
            {
                Source = src,
                Width = size,
                Height = size,
                Stretch = System.Windows.Media.Stretch.Uniform,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
        }

        return new TextBlock
        {
            Text = "◆",
            FontSize = size * 0.6,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
    }

    private void WireToolsShortcutDrop(Border slot, IReadOnlyList<ToolCardRow> rows, int? replaceIndex)
    {
        slot.DragOver += (_, e) =>
        {
            if (e.Data.GetDataPresent(System.Windows.DataFormats.StringFormat) || e.Data.GetDataPresent(typeof(string)))
                e.Effects = DragDropEffects.Copy;
            else
                e.Effects = DragDropEffects.None;
            e.Handled = true;
        };
        slot.Drop += (_, e) =>
        {
            var toolId = e.Data.GetData(System.Windows.DataFormats.StringFormat) as string
                         ?? e.Data.GetData(typeof(string)) as string;
            if (string.IsNullOrWhiteSpace(toolId)) return;
            var hit = rows.FirstOrDefault(r =>
                string.Equals(r.Id, toolId, StringComparison.OrdinalIgnoreCase));
            if (hit == null || !hit.IsInstalled) return;
            if (replaceIndex is int idx)
                ToolService.Instance.Shortcuts.SetSlot(idx, toolId);
            else
                ToolService.Instance.Shortcuts.TryAppend(toolId);
            RefreshToolsShortcuts();
            e.Handled = true;
        };
    }

    private async void ToolsImport_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = Loc.T("Tool.ImportTitle"),
            Filter = Loc.T("Tool.ImportFilter")
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            var plan = ToolService.Instance.AnalyzeImport(dlg.FileName);
            if (!plan.Ok)
            {
                MessageBox.Show(this, plan.Message, Loc.T("Tool.ImportTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var overwrite = true;
            if (plan.HasConflict)
            {
                var ask = ConfirmDialog.Show(
                    this,
                    Loc.T("Tool.ImportTitle"),
                    string.Format(Loc.T("Tool.ImportOverwrite"), plan.DisplayName),
                    primaryText: Loc.T("Tool.Overwrite"),
                    cancelText: Loc.T("Tool.Discard"));
                if (ask != UiConfirmChoice.Primary)
                {
                    StatusText.Text = Loc.T("Tool.ImportDiscarded");
                    return;
                }
            }

            var r = await ToolService.Instance.CommitImportAsync(plan, overwrite);
            StatusText.Text = r.Message;
            if (!r.Success)
                MessageBox.Show(this, r.Message, Loc.T("Tool.ImportTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
            RefreshToolsPage();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Loc.T("Tool.ImportTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ToolsCatalogHost_SizeChanged(object sender, SizeChangedEventArgs e)
        => ApplyToolsWrapColumns();

    /// <summary>对齐 Maui-Win：≥400 至少两列，更宽按 240 槽加列；列表置顶不垂直居中。</summary>
    private void ApplyToolsWrapColumns()
    {
        if (ToolsCatalogHost == null || ToolsList == null) return;
        var host = ToolsCatalogHost.ActualWidth;
        if (host < 80) return;
        const double minCardSlot = 240;
        var byWidth = Math.Max(1, (int)Math.Floor(host / minCardSlot));
        var cols = host >= 400 ? Math.Max(2, byWidth) : byWidth;
        ToolsList.VerticalAlignment = System.Windows.VerticalAlignment.Top;
        ToolsList.ApplyTemplate();
        ToolsList.UpdateLayout();
        var grid = FindVisualChild<UniformGrid>(ToolsList);
        if (grid == null) return;
        if (grid.Columns != cols)
            grid.Columns = cols;
        var count = ToolsList.Items?.Count ?? 0;
        var rows = Math.Max(1, (int)Math.Ceiling(count / (double)Math.Max(1, cols)));
        if (grid.Rows != rows)
            grid.Rows = rows;
        // 与 Maui 卡片间距一致：行列间距由卡片 Margin=6 承担；置顶避免单元格被拉高后卡片悬空
        grid.HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch;
        grid.VerticalAlignment = System.Windows.VerticalAlignment.Top;
    }

    private void RefreshToolsWorkspace()
    {
        if (ToolsWorkspaceNavPanel == null || ToolsCatalogHost == null || ToolsWorkspaceHost == null)
            return;

        var ws = ToolService.Instance.Workspace;
        var hasTabs = ws.HasOpenTabs;
        ToolsHintText.Visibility = hasTabs && !ws.IsShowingCatalog ? Visibility.Collapsed : Visibility.Visible;
        ToolsTitleText.Visibility = hasTabs ? Visibility.Collapsed : Visibility.Visible;

        if (!_toolsBreadcrumbWired)
        {
            _toolsBreadcrumb.SegmentClicked += seg =>
            {
                if (string.IsNullOrEmpty(seg.Id))
                    ToolService.Instance.Workspace.ShowCatalog();
                else
                    ToolService.Instance.Workspace.Activate(seg.Id);
            };
            _toolsBreadcrumb.SegmentCloseClicked += async seg =>
            {
                if (string.IsNullOrEmpty(seg.Id)) return;
                var module = ToolService.Instance.Host.Get(seg.Id);
                if (module != null)
                    await module.StopAsync();
                else
                    ToolService.Instance.Workspace.Close(seg.Id);
                RefreshToolsPage();
            };
            _toolsBreadcrumbWired = true;
        }

        ToolsWorkspaceNavPanel.Children.Clear();
        if (hasTabs)
        {
            var crumbs = new List<BreadcrumbSegment>
            {
                new()
                {
                    Id = "",
                    Title = Loc.T("Tool.Workspace.Catalog"),
                    IsActive = ws.IsShowingCatalog,
                    IsClickable = true
                }
            };
            foreach (var tab in ws.OpenTabs)
            {
                var active = !ws.IsShowingCatalog
                    && string.Equals(ws.ActiveToolId, tab.ToolId, StringComparison.OrdinalIgnoreCase);
                crumbs.Add(new BreadcrumbSegment
                {
                    Id = tab.ToolId,
                    Title = tab.Title,
                    IsActive = active,
                    IsClickable = true,
                    ShowClose = true
                });
            }
            _toolsBreadcrumb.SetItems(crumbs);
            ToolsWorkspaceNavPanel.Children.Add(_toolsBreadcrumb);
        }

        var showCatalog = ws.IsShowingCatalog || !hasTabs;
        if (ToolsShortcutHost != null)
            ToolsShortcutHost.Visibility = showCatalog ? Visibility.Visible : Visibility.Collapsed;

        if (showCatalog)
        {
            ToolsCatalogHost.Visibility = Visibility.Visible;
            ToolsWorkspaceHost.Visibility = Visibility.Collapsed;
            ToolsWorkspaceHost.Content = null;
        }
        else
        {
            ToolsCatalogHost.Visibility = Visibility.Collapsed;
            ToolsWorkspaceHost.Visibility = Visibility.Visible;
            var activeId = ws.ActiveToolId;
            if (!string.IsNullOrEmpty(activeId))
            {
                var content = ToolService.Instance.CreateWorkspaceContent(activeId);
                if (content != null
                    && (ToolsWorkspaceHost.Content == null
                        || ToolsWorkspaceHost.Content.GetType() != content.GetType()))
                    ToolsWorkspaceHost.Content = content;
            }
        }
    }

    private System.Windows.Point _toolDragStart;
    private ToolCardRow? _toolDragRow;
    private bool _toolDragStarted;

    private void ToolCard_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ToolCardRow row }) return;
        if (IsDescendantOfButton(e.OriginalSource as DependencyObject, sender as DependencyObject))
            return;
        _toolDragStart = e.GetPosition(this);
        _toolDragRow = row;
        _toolDragStarted = false;
    }

    private async void ToolCard_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ToolCardRow row }) return;
        if (IsDescendantOfButton(e.OriginalSource as DependencyObject, sender as DependencyObject))
            return;
        if (_toolDragStarted) return;
        if (e.ClickCount != 1) return;
        await HandleToolActionAsync(row, primary: true);
        _toolDragRow = null;
    }

    private void ToolCard_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _toolDragRow == null) return;
        if (sender is not FrameworkElement fe) return;
        var pos = e.GetPosition(this);
        if (Math.Abs(pos.X - _toolDragStart.X) < 8 && Math.Abs(pos.Y - _toolDragStart.Y) < 8)
            return;
        if (!_toolDragRow.IsInstalled) return;
        _toolDragStarted = true;
        var data = new System.Windows.DataObject(System.Windows.DataFormats.StringFormat, _toolDragRow.Id);
        DragDrop.DoDragDrop(fe, data, DragDropEffects.Copy);
        _toolDragRow = null;
    }

    private void ToolCard_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (sender is not Border card) return;
        if (TryFindResource("Blue") is System.Windows.Media.Brush blue)
            card.BorderBrush = blue;
        SetToolCardHoverWash(card, visible: true);
    }

    private void ToolCard_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (sender is not Border card) return;
        if (TryFindResource("Line") is System.Windows.Media.Brush line)
            card.BorderBrush = line;
        SetToolCardHoverWash(card, visible: false);
    }

    private static void SetToolCardHoverWash(Border card, bool visible)
    {
        if (card.Child is not Grid root) return;
        foreach (var child in root.Children)
        {
            if (child is Border { Tag: "ToolCardHoverWash" } wash)
            {
                wash.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
                break;
            }
        }
    }

    private static bool IsDescendantOfButton(DependencyObject? source, DependencyObject? stop)
    {
        for (var p = source; p != null && p != stop; p = VisualTreeHelper.GetParent(p))
        {
            if (p is System.Windows.Controls.Button)
                return true;
        }

        return false;
    }

    private ToolCardRow? ResolveToolCardRow(FrameworkElement fe)
    {
        if (fe.DataContext is ToolCardRow row)
            return row;
        if (fe.Tag is ToolCardRow tagged)
            return tagged;
        for (DependencyObject? p = fe; p != null; p = VisualTreeHelper.GetParent(p))
        {
            if (p is FrameworkElement { Tag: ToolCardRow r })
                return r;
        }

        return null;
    }

    private void ToolCardMore_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // 在 Preview 阶段直接弹菜单：Handled 会阻断 Button.Click，故不能只靠 Click
        e.Handled = true;
        if (sender is not FrameworkElement fe) return;
        var row = ResolveToolCardRow(fe);
        if (row == null) return;
        OpenToolCardMenu(fe, row);
    }

    private void ToolCardMore_Click(object sender, RoutedEventArgs e)
    {
        // Preview 已处理时通常不会到此；保留作兜底
        if (sender is not FrameworkElement fe) return;
        var row = ResolveToolCardRow(fe);
        if (row == null) return;
        OpenToolCardMenu(fe, row);
        e.Handled = true;
    }

    private void OpenToolCardMenu(FrameworkElement? placementTarget, ToolCardRow row)
    {
        var menu = new System.Windows.Controls.ContextMenu();
        if (TryFindResource("ToolCardContextMenu") is Style menuStyle)
            menu.Style = menuStyle;
        if (TryFindResource("ToolCardMenuItem") is Style itemStyle)
            menu.ItemContainerStyle = itemStyle;

        if (placementTarget != null)
        {
            menu.PlacementTarget = placementTarget;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.VerticalOffset = 2;
            // 挂到按钮上，避免游离 ContextMenu 在部分主题下不显示
            placementTarget.ContextMenu = menu;
        }

        void AddItem(string header, RoutedEventHandler onClick)
        {
            var item = new System.Windows.Controls.MenuItem { Header = header };
            item.Click += onClick;
            menu.Items.Add(item);
        }

        if (row.IsLocalProgram)
        {
            if (row.CanStart)
                AddItem(Loc.T("Tool.Start"), async (_, _) => await HandleToolActionAsync(row, primary: true));
            AddItem(Loc.T("Tool.Shortcut.DragHere"), (_, _) =>
            {
                ToolService.Instance.Shortcuts.TryAppend(row.Id);
                RefreshToolsShortcuts();
            });
            AddItem(Loc.T("Tool.Remove"), async (_, _) => await HandleToolActionAsync(row, primary: false));
        }
        else if (!row.IsInstalled)
        {
            AddItem(Loc.T("Tool.Install"), async (_, _) => await HandleToolActionAsync(row, primary: true));
        }
        else
        {
            if (row.CanStart)
                AddItem(Loc.T("Tool.Start"), async (_, _) => await HandleToolActionAsync(row, primary: true));

            foreach (var contrib in ToolService.Instance.GetMenuContributions(row))
            {
                var actionId = contrib.ActionId;
                AddItem(contrib.Label, async (_, _) =>
                {
                    await ToolService.Instance.InvokeMenuAsync(row.Id, actionId);
                    RefreshToolsPage();
                });
            }

            if (row.SecondaryEnabled || row.IsInstalled)
                AddItem(Loc.T("Tool.Uninstall"), async (_, _) => await HandleToolActionAsync(row, primary: false));
        }

        if (menu.Items.Count == 0) return;
        menu.IsOpen = true;
    }

    private async Task HandleToolActionAsync(ToolCardRow row, bool primary, bool fromMenu = false)
    {
        try
        {
            if (row.IsLocalProgram)
            {
                if (primary)
                {
                    var (ok, message) = ToolService.Instance.LaunchLocalProgram(row.Id);
                    if (!string.IsNullOrWhiteSpace(message))
                        StatusText.Text = message;
                    if (!ok && !string.IsNullOrWhiteSpace(message))
                        MessageBox.Show(this, message, Loc.T("Tool.Title"), MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                else
                {
                    var r = ToolService.Instance.RemoveLocalProgram(row.Id);
                    StatusText.Text = r.Message;
                    RefreshToolsPage();
                }
                return;
            }

            if (!row.IsInstalled && primary)
            {
                if (row.IsInstalling) return;
                var r = await ToolService.Instance.InstallAsync(row.Id);
                StatusText.Text = r.Message;
                if (!r.Success)
                    MessageBox.Show(this, r.Message, Loc.T("Tool.Install"), MessageBoxButton.OK, MessageBoxImage.Warning);
                RefreshToolsPage();
                return;
            }

            if (!primary && row.IsInstalled)
            {
                var r = await ToolService.Instance.UninstallAsync(row.Id);
                StatusText.Text = r.Message;
                RefreshToolsPage();
                return;
            }

            if (primary)
            {
                var (ok, message) = await ToolService.Instance.PrimaryAsync(row, fromMenu);
                if (!string.IsNullOrWhiteSpace(message))
                    StatusText.Text = message;
                if (!ok && !string.IsNullOrWhiteSpace(message))
                    MessageBox.Show(this, message, Loc.T("Tool.Title"), MessageBoxButton.OK, MessageBoxImage.Warning);
                RefreshToolsPage();
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Loc.T("Tool.Title"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
