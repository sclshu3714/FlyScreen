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
using System.Windows.Shapes;
using FlyScreen.Helpers;
using FlyScreen.Helpers.Localization;
using FlyScreen.Models;
using FlyScreen.Services;
using FlyScreen.Services.Network;
using FlyScreen.Services.Programs;
using FlyScreen.Services.Update;
using WinForms = System.Windows.Forms;
using Media = System.Windows.Media;

namespace FlyScreen;

public partial class MainWindow : Window
{
    private static readonly Uri AppIconUri = new("pack://application:,,,/Assets/wlogo_tu.ico");

    private readonly AppHost _host;
    private readonly ObservableCollection<MachineSlot> _slots = [];
    private WinForms.NotifyIcon? _tray;
    private WinForms.ToolStripMenuItem? _trayOpen;
    private WinForms.ToolStripMenuItem? _trayReconnect;
    private WinForms.ToolStripMenuItem? _trayAbout;
    private WinForms.ToolStripMenuItem? _trayExit;
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
    private readonly ObservableCollection<AppLogEntry> _logItems = new();
    private bool _logStickToBottom = true;
    private bool _logFilterReady;
    private int _logKnownTotal;
    private AppLogEntry? _logWatermark;

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
        AppLog.Changed += () => Dispatcher.Invoke(() =>
        {
            // 仅在「最新」页增量追加，避免整表重绑闪烁回顶
            if (PageLog.Visibility == Visibility.Visible && _logPageFromLatest == 0)
                RefreshLogPageLive();
        });
        LogList.ItemsSource = _logItems;
        ProgramService.Instance.Catalog.Changed += () => Dispatcher.BeginInvoke(RefreshProgramsPage);
        _host.Connections.PeerLinkChanged += OnPeerLinkChanged;
        _host.Connections.Discovery.PeersUpdated += _ => Dispatcher.Invoke(RefreshDiscovery);
        _host.LayoutSyncedFromPeer += OnLayoutSyncedFromPeer;
        _host.BehaviorSyncedFromPeer += OnBehaviorSyncedFromPeer;

        _host.Clipboard.Start(this);
        await _host.StartAsync();
        StatusText.Text = $"监听 {_host.Connections.BindIp}:{_host.Settings.TcpPort}";

        // 开机自启：隐藏到托盘，后台已自动连接
        if (AutoStartHelper.IsAutostartLaunch(Environment.GetCommandLineArgs()))
        {
            StatusText.Text = $"已自启并连接 · {_host.Connections.BindIp}:{_host.Settings.TcpPort}";
            Hide();
        }

        if (_host.Settings.CheckUpdatesOnStartup)
            _ = CheckUpdatesOnStartupQuietAsync();
    }

    private async Task CheckUpdatesOnStartupQuietAsync()
    {
        try
        {
            var updater = new Services.Update.GitHubUpdateService();
            var result = await updater.CheckAsync().ConfigureAwait(true);
            if (result is { Success: true, UpdateAvailable: true })
            {
                StatusText.Text = $"发现新版本 {result.RemoteVersion}，可在「帮助」中自动更新";
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
        _tray.DoubleClick += (_, _) =>
        {
            Show();
            WindowState = WindowState.Normal;
            Activate();
        };
        var menu = new WinForms.ContextMenuStrip();
        _trayOpen = new WinForms.ToolStripMenuItem(Loc.T("Tray.Open"), null, (_, _) => { Show(); Activate(); });
        _trayReconnect = new WinForms.ToolStripMenuItem(Loc.T("Tray.Reconnect"), null,
            async (_, _) => await _host.Connections.ReconnectNowAsync());
        _trayAbout = new WinForms.ToolStripMenuItem(Loc.T("Tray.About"), null, (_, _) =>
        {
            Show();
            WindowState = WindowState.Normal;
            Activate();
            ShowPage(5);
        });
        _trayExit = new WinForms.ToolStripMenuItem(Loc.T("Tray.Exit"), null, (_, _) => ExitApp());
        menu.Items.AddRange([_trayOpen, _trayReconnect, _trayAbout, _trayExit]);
        _tray.ContextMenuStrip = menu;
    }

    private void ApplyTrayLocalized()
    {
        if (_tray != null)
            _tray.Text = Loc.T("Tray.Tip");
        if (_trayOpen != null) _trayOpen.Text = Loc.T("Tray.Open");
        if (_trayReconnect != null) _trayReconnect.Text = Loc.T("Tray.Reconnect");
        if (_trayAbout != null) _trayAbout.Text = Loc.T("Tray.About");
        if (_trayExit != null) _trayExit.Text = Loc.T("Tray.Exit");
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
        var local = _slots.FirstOrDefault(s => s.IsLocal) ?? _slots.FirstOrDefault();
        if (local != null) SelectSlot(local);
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
        s.EdgeCrossMode = ReadEdgeCrossModeUi();
        s.AutoStart = AutoStartCheck.IsChecked == true;
        if (!AutoStartHelper.SetEnabled(s.AutoStart))
            StatusText.Text = s.AutoStart
                ? "开机自启设置失败（需管理员权限创建计划任务）"
                : "关闭开机自启失败";
        else if (s.AutoStart)
            StatusText.Text = "已启用开机自启（登录后约 15 秒以管理员启动）";
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

        _host.Input.EasyMouseEnabled = s.EasyMouse;
        _host.Input.RefreshEdgeCrossFromSettings();
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
        SearchPlaceholderText.Text = Loc.T("Search.Placeholder");
        LogTitleText.Text = Loc.T("Log.Title");
        RefreshLogHint();
        LogDownloadBtnText.Text = Loc.T("Log.Download");
        LogCopyBtnText.Text = Loc.T("Log.Copy");
        LogClearBtnText.Text = Loc.T("Log.Clear");
        LogPrevBtnText.Text = Loc.T("Log.Prev");
        LogNextBtnText.Text = Loc.T("Log.Next");
        LogLatestBtnText.Text = Loc.T("Log.Latest");
        LogDownloadBtn.ToolTip = LogDownloadBtnText.Text;
        LogCopyBtn.ToolTip = Loc.T("Log.SelectTip");
        LogClearBtn.ToolTip = LogClearBtnText.Text;
        LogPrevBtn.ToolTip = LogPrevBtnText.Text;
        LogNextBtn.ToolTip = LogNextBtnText.Text;
        LogLatestBtn.ToolTip = LogLatestBtnText.Text;
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
        ProgColName.Header = Loc.T("Prog.ColName");
        ProgColLang.Header = Loc.T("Prog.ColLang");
        ProgColSize.Header = Loc.T("Prog.ColSize");
        ProgColStatus.Header = Loc.T("Prog.ColStatus");
        ProgColActions.Header = Loc.T("Prog.ColActions");

        SecurityTitleText.Text = Loc.T("Sec.Title");
        ShowKeyCheck.Content = Loc.T("Sec.Show");
        NewKeyBtn.Content = Loc.T("Sec.NewKey");
        SecurityHintText.Text = Loc.T("Sec.Hint");
        LayoutTitleText.Text = Loc.T("Layout.Title");
        LayoutHintText.Text = Loc.T("Layout.Hint");
        LayoutDragTipText.Text = Loc.T("Layout.DragTip");
        AddMachineBtn.Content = Loc.T("Layout.Add");
        RemoveMachineBtn.Content = Loc.T("Layout.Remove");
        ReconnectBtn.Content = Loc.T("Layout.Reconnect");

        NicTitleText.Text = Loc.T("Net.NicTitle");
        NicHintText.Text = Loc.T("Net.NicHint");
        RefreshNicsBtn.Content = Loc.T("Net.RefreshNics");
        TcpPortLabel.Text = Loc.T("Net.TcpPort");
        UdpPortLabel.Text = Loc.T("Net.UdpPort");
        DiscoveryTitleText.Text = Loc.T("Net.DiscoveryTitle");
        DiscoveryHintText.Text = Loc.T("Net.DiscoveryHint");
        RefreshDiscoveryBtn.Content = Loc.T("Net.RefreshDiscovery");

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
        RefreshEdgeCrossTip();
        foreach (var item in _host.Transfers.Items)
            item.RefreshLocalized();

        foreach (var slot in _slots)
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
        EdgeCrossBarrierRadio.IsChecked = mode is < 1 or > 2;
        EdgeCrossDoubleRadio.IsChecked = mode == 1;
        EdgeCrossSpeedRadio.IsChecked = mode == 2;
        EdgeCrossBarrierRadio.Checked += EdgeCrossMode_Changed;
        EdgeCrossDoubleRadio.Checked += EdgeCrossMode_Changed;
        EdgeCrossSpeedRadio.Checked += EdgeCrossMode_Changed;
        RefreshEdgeCrossTip();
    }

    private int ReadEdgeCrossModeUi()
    {
        if (EdgeCrossDoubleRadio.IsChecked == true) return 1;
        if (EdgeCrossSpeedRadio.IsChecked == true) return 2;
        return 0;
    }

    private void EdgeCrossMode_Changed(object sender, RoutedEventArgs e) => RefreshEdgeCrossTip();

    private void RefreshEdgeCrossTip()
    {
        var key = ReadEdgeCrossModeUi() switch
        {
            1 => "Tip.EdgeCross.DoubleTap",
            2 => "Tip.EdgeCross.SpeedBurst",
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
        if (stacked == _optionsFeatStacked && OptionsFeatGrid.ColumnDefinitions.Count == (stacked ? 1 : 3))
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
            Grid.SetRow(BehColumn, 1);
            Grid.SetColumn(BehColumn, 0);
            Grid.SetRow(EdgeCrossColumn, 2);
            Grid.SetColumn(EdgeCrossColumn, 0);
            FeatColumn.Margin = new Thickness(0, 0, 0, 12);
            BehColumn.Margin = new Thickness(0, 0, 0, 12);
            EdgeCrossColumn.Margin = new Thickness(0);
        }
        else
        {
            OptionsFeatGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            OptionsFeatGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            OptionsFeatGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            OptionsFeatGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(FeatColumn, 0);
            Grid.SetColumn(FeatColumn, 0);
            Grid.SetRow(BehColumn, 0);
            Grid.SetColumn(BehColumn, 1);
            Grid.SetRow(EdgeCrossColumn, 0);
            Grid.SetColumn(EdgeCrossColumn, 2);
            FeatColumn.Margin = new Thickness(0, 0, 10, 0);
            BehColumn.Margin = new Thickness(5, 0, 5, 0);
            EdgeCrossColumn.Margin = new Thickness(10, 0, 0, 0);
        }
    }

    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        ApplySettingsFromUi();
        await _host.BroadcastLayoutSyncAsync();
        await _host.BroadcastBehaviorSyncAsync();
        await _host.RestartNetworkingAsync();
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
        await _host.DisposeAsync();
        System.Windows.Application.Current.Shutdown();
    }

    /// <summary>自动更新：释放托盘后立即退出，供外部脚本覆盖单文件 exe 并重启。</summary>
    public void ExitForUpdate()
    {
        try { DisposeTray(); } catch { /* ignore */ }
        try { _ = _host.DisposeAsync().AsTask(); } catch { /* ignore */ }
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
        used.Add(LocalPeerIdentity.MachineName);
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
            await _host.Connections.PruneUnconfiguredLinksAsync().ConfigureAwait(true);
            await _host.RestartNetworkingAsync().ConfigureAwait(true);
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
        else if (NavSecurity.IsChecked == true) ShowPage(4);
        else if (NavAdvanced.IsChecked == true) ShowPage(1);
        else if (NavPrograms.IsChecked == true) ShowPage(8);
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

    private async void ProgRowDelete_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not UserProgram prog) return;
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

    private AppLogLevelFilter CurrentLogLevelFilter()
    {
        if (LogLevelFilter.SelectedItem is ComboBoxItem { Tag: AppLogLevelFilter f })
            return f;
        return AppLogLevelFilter.All;
    }

    private void EnsureLogLevelFilterItems()
    {
        var selected = CurrentLogLevelFilter();
        _logFilterReady = false;
        LogLevelFilter.Items.Clear();
        void Add(AppLogLevelFilter f, string key)
            => LogLevelFilter.Items.Add(new ComboBoxItem { Content = Loc.T(key), Tag = f });
        Add(AppLogLevelFilter.All, "Log.LevelAll");
        Add(AppLogLevelFilter.Info, "Log.LevelInfo");
        Add(AppLogLevelFilter.Warn, "Log.LevelWarn");
        Add(AppLogLevelFilter.Error, "Log.LevelError");
        foreach (ComboBoxItem it in LogLevelFilter.Items)
        {
            if (it.Tag is AppLogLevelFilter f && f == selected)
            {
                LogLevelFilter.SelectedItem = it;
                break;
            }
        }
        if (LogLevelFilter.SelectedIndex < 0)
            LogLevelFilter.SelectedIndex = 0;
        _logFilterReady = true;
    }

    private void ApplyLogPageChrome(AppLogPage page)
    {
        _logPageFromLatest = page.PageFromLatest;
        _logTotalPages = page.TotalPages;
        _logKnownTotal = page.TotalCount;
        LogPageText.Text = page.TotalCount == 0
            ? Loc.T("Log.Empty")
            : page.PageFromLatest == 0
                ? string.Format(Loc.T("Log.PageLatest"), page.TotalCount)
                : string.Format(Loc.T("Log.Page"), page.PageFromLatest + 1, page.TotalPages, page.TotalCount);
        LogPrevBtn.IsEnabled = page.HasOlder;
        LogNextBtn.IsEnabled = page.HasNewer;
        LogLatestBtn.IsEnabled = page.HasNewer;
    }

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
            LogLevelFilter.Width = 72;
            LogToolBar.Measure(new System.Windows.Size(double.PositiveInfinity, 48));
            var need = LogToolBar.DesiredSize.Width;
            LogToolsCompact = need > available + 0.5;
            // 极窄：图标按钮 + 缩短筛选框
            LogLevelFilter.Width = LogToolsCompact
                ? (available < 240 ? 52 : 64)
                : 72;
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
        var level = CurrentLogLevelFilter();
        var stick = _logStickToBottom;
        var delta = AppLog.DeltaSince(
            _logWatermark, level, _logProgramId, _logProgramName, AppLog.DefaultPageSize);
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

        LogPageText.Text = _logKnownTotal == 0
            ? Loc.T("Log.Empty")
            : string.Format(Loc.T("Log.PageLatest"), _logKnownTotal);
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
            _logProgramName);
        ApplyLogPageChrome(page);
        if (page.PageFromLatest == 0)
        {
            // 最新页始终用水印增量源（内存）；内存空时才回退磁盘页（无水印，后续 live 会 Reset 到内存）
            var delta = AppLog.DeltaSince(null, level, _logProgramId, _logProgramName, AppLog.DefaultPageSize);
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
            _logStickToBottom = false;
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
        _logPageFromLatest = 0;
        RefreshLogPage();
    }

    private void LogClear_Click(object sender, RoutedEventArgs e)
    {
        AppLog.Clear();
        _logPageFromLatest = 0;
        _logTotalPages = 0;
        _logKnownTotal = 0;
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
            _logProgramName);
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
        var ver = typeof(MainWindow).Assembly.GetName().Version;
        var verText = ver == null ? "1.0" : $"{ver.Major}.{ver.Minor}.{ver.Build}";
        AboutTitleText.Text = Loc.T("About.Title");
        AboutBodyText.Text = string.Format(Loc.T("About.Body"), verText);
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
}
