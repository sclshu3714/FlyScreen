using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia.Platform.Storage;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FlyScreen.DesktopUi.Helpers;
using FlyScreen.DesktopUi.Services;
using FlyScreen.DesktopUi.Shell;
using FlyScreen.Helpers.Localization;
using FlyScreen.Models;
using FlyScreen.Services;
using FlyScreen.Services.Network;
using FlyScreen.Services.Programs;
using FlyScreen.Services.Update;

namespace FlyScreen.DesktopUi;

public partial class MainWindow : Window
{
    private readonly IDesktopShellHost _host;
    private readonly ObservableCollection<MachineSlot> _slots = [];
    private bool _syncingNav;
    private bool _syncingLayout;
    private bool _twoRowLayout = true;
    private DispatcherTimer? _refreshTimer;
    private Point _dragStart;
    private MachineSlot? _dragSlot;
    private Control? _dragSourceVisual;
    private bool _dragStarted;
    private MachineSlot? _selectedSlot;
    private MachineSlot? _lastClickSlot;
    private long _lastClickTicks;
    private Border? _dragGhost;
    private Control? _dragHighlight;
    private Cursor? _savedCursor;
    private readonly GitHubReleaseChecker _helpUpdater = new();
    private UpdateCheckResult? _helpLastCheck;
    private bool _helpUpdateReady;
    private bool _helpFixReady;
    private bool _progToolModeUpdating;
    private bool _progToolsCompact;
    private bool _optionsFeatStacked;
    private string? _logProgramId;
    private string? _logProgramName;
    private int _logPageFromLatest;
    private int _logTotalPages;
    private readonly ObservableCollection<AppLogEntry> _logItems = new();
    private bool _logStickToBottom = true;
    private bool _logFilterReady;
    private int _logKnownTotal;
    private AppLogEntry? _logWatermark;
    private bool _logToolModeUpdating;
    private bool _logToolsCompact;
    private bool _openProgramLog;

    // 标题栏拖动：Wayland→BeginMoveDrag；X11→屏幕绝对坐标 - 抓取偏移
    private bool _titleMoving;
    private int _titleGrabOffsetX;
    private int _titleGrabOffsetY;

    /// <summary>满足 AVLN3001（XAML 运行时加载器/预览器）；正式启动请用带 host 的构造函数。</summary>
    public MainWindow()
    {
        if (!Design.IsDesignMode)
            throw new InvalidOperationException("MainWindow requires IDesktopShellHost.");
        _host = null!;
        InitializeComponent();
    }

    public MainWindow(IDesktopShellHost host)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        InitializeComponent();
        Title = "玄鸟";
        TryApplyWindowIcon();
        ConfigureLinuxClientTitleBar();
        Opened += OnOpened;
        Closing += OnClosing;
    }

    /// <summary>
    /// WSL/X11 系统标题栏常因字体/编码只画出「玄」。关闭系统标题文字，改用客户端标题栏显示完整「玄鸟」。
    /// </summary>
    private void ConfigureLinuxClientTitleBar()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return;

        // None：自绘标题栏 + 圆角；Wayland 仍走 BeginMoveDrag，X11 用手势跟指针
        SystemDecorations = SystemDecorations.None;
        ClientTitleBar.IsVisible = true;
        Title = "玄鸟";
        // 不使用整窗透明：X11 上拖动时透明合成会严重闪烁；圆角由 WindowChrome 裁剪呈现
        Background = TryBrush("WinBg") ?? Brushes.White;
        if (WindowChrome != null)
        {
            WindowChrome.BorderThickness = new Thickness(1);
            UpdateWindowCornerRadius();
        }

        PropertyChanged += (_, e) =>
        {
            if (e.Property == WindowStateProperty)
                UpdateWindowCornerRadius();
        };
    }

    private void UpdateWindowCornerRadius()
    {
        if (WindowChrome == null) return;
        // Win11 DWMWCP_ROUND ≈ 8px；最大化时取消圆角
        WindowChrome.CornerRadius = WindowState == WindowState.Maximized
            ? default
            : new CornerRadius(8);
    }

    private void ClientTitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        // 点在标题按钮上时不要拖窗
        if (e.Source is Control src && IsTitleBarButton(src))
            return;

        if (e.ClickCount >= 2)
        {
            EndTitleMove(e.Pointer);
            TitleMax_Click(sender, e);
            e.Handled = true;
            return;
        }

        // Wayland 只能走平台拖动；X11/WSL 上 BeginMoveDrag 常“假成功”却不动窗，必须用手势
        var wayland = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));
        if (wayland)
        {
            try
            {
                BeginMoveDrag(e);
                e.Handled = true;
                return;
            }
            catch
            {
                // fall through
            }
        }

        StartManualTitleMove(e);
    }

    private void StartManualTitleMove(PointerPressedEventArgs e)
    {
        // 屏幕绝对坐标 - 窗口左上角 = 抓取偏移（与客户区坐标无关，不会因 Position 异步更新而抖动）
        if (!LinuxScreenPointer.TryGetPosition(out var px, out var py))
        {
            // 无 X11 时退回 PointToScreen（仍可能略抖，但优于客户区增量）
            var screen = this.PointToScreen(e.GetPosition(this));
            px = screen.X;
            py = screen.Y;
        }

        _titleGrabOffsetX = px - Position.X;
        _titleGrabOffsetY = py - Position.Y;
        _titleMoving = true;
        e.Pointer.Capture(ClientTitleBar);
        e.Handled = true;
    }

    private void ClientTitleBar_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_titleMoving) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            EndTitleMove(e.Pointer);
            return;
        }

        int px, py;
        if (!LinuxScreenPointer.TryGetPosition(out px, out py))
        {
            var screen = this.PointToScreen(e.GetPosition(this));
            px = screen.X;
            py = screen.Y;
        }

        var next = new PixelPoint(px - _titleGrabOffsetX, py - _titleGrabOffsetY);
        if (next != Position)
            Position = next;

        e.Handled = true;
    }

    private void ClientTitleBar_PointerReleased(object? sender, PointerReleasedEventArgs e)
        => EndTitleMove(e.Pointer);

    private void ClientTitleBar_PointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
        => EndTitleMove(null);

    private void EndTitleMove(IPointer? pointer)
    {
        if (!_titleMoving && pointer == null) return;
        _titleMoving = false;
        try { pointer?.Capture(null); } catch { /* ignore */ }
    }

    private static bool IsTitleBarButton(Control? c)
    {
        while (c != null)
        {
            if (c is Button) return true;
            c = c.GetVisualParent() as Control;
        }

        return false;
    }

    private void TitleMin_Click(object? sender, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void TitleMax_Click(object? sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void TitleClose_Click(object? sender, RoutedEventArgs e)
        => Close();

    private void TryApplyWindowIcon()
    {
        try
        {
            var uri = new Uri("avares://FlyScreen.App.Desktop/Assets/app_icon.png");
            Icon = new WindowIcon(AssetLoader.Open(uri));
        }
        catch
        {
            try
            {
                var uri = new Uri("avares://FlyScreen.App.Desktop/Assets/wlogo_tu.ico");
                Icon = new WindowIcon(AssetLoader.Open(uri));
            }
            catch
            {
                // 保持默认
            }
        }
    }

    private async void OnOpened(object? sender, EventArgs e)
    {
        // 应用层可能已写入含 CJK 的 UiFont
        if (Application.Current?.Resources.TryGetResource("UiFont", null, out var font) == true
            && font is FontFamily ff)
            FontFamily = ff;

        LoadUiFromSettings();
        MachineMatrix.ItemsSource = _slots;
        TransferList.ItemsSource = _host.TransferItems;
        _host.TransferItems.CollectionChanged += (_, _) =>
            TransferPlaceholder.IsVisible = _host.TransferItems.Count == 0;
        TransferPlaceholder.IsVisible = _host.TransferItems.Count == 0;
        BuildPlatformActions();
        RefreshNics();
        RefreshDiscovery();
        ApplyLayoutGrid();

        _host.StatusMessage += msg => Dispatcher.UIThread.Post(() => StatusText.Text = msg);
        AppLog.Changed += () => Dispatcher.UIThread.Post(() =>
        {
            if (PageLog.IsVisible && _logPageFromLatest == 0)
                RefreshLogPageLive();
        });
        LogList.ItemsSource = _logItems;
        LogList.AddHandler(InputElement.PointerWheelChangedEvent, (_, _) => _logStickToBottom = false, RoutingStrategies.Tunnel);
        _host.LayoutSyncedFromPeer += from => Dispatcher.UIThread.Post(() =>
        {
            ApplyLayoutFromSettings();
            StatusText.Text = $"已同步布局 ← {from}";
        });
        _host.BehaviorSyncedFromPeer += from => Dispatcher.UIThread.Post(() =>
        {
            ApplyBehaviorFromSettings();
            StatusText.Text = $"已同步功能与行为 ← {from}";
        });
        _host.Connections.PeerLinkChanged += OnPeerLinkChanged;
        _host.Connections.Discovery.PeersUpdated += _ => Dispatcher.UIThread.Post(RefreshDiscovery);

        await _host.StartAsync();
        StatusText.Text = $"监听 {_host.Connections.BindIp}:{_host.Settings.TcpPort}";
        PlatformStatusText.Text = _host.PlatformStatusLine;

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _refreshTimer.Tick += (_, _) => PlatformStatusText.Text = _host.PlatformStatusLine;
        _refreshTimer.Start();

        HideTrayBtn.IsVisible = _host.SupportsTray;
        AutoStartCheck.IsEnabled = _host.SupportsAutoStart;
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        _refreshTimer?.Stop();
    }

    private void BuildPlatformActions()
    {
        PlatformActionsPanel.Children.Clear();
        foreach (var (label, invoke) in _host.PlatformActions)
        {
            var btn = new Button
            {
                Content = label,
                Classes = { "normal" },
                Margin = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            btn.Click += (_, _) => invoke();
            PlatformActionsPanel.Children.Add(btn);
        }
    }

    private void LoadUiFromSettings()
    {
        var s = _host.Settings;
        s.EnsureLocalMachineSlot();
        SetSecurityKey(s.SecurityKey);
        _twoRowLayout = s.TwoRow;
        Layout2x2.IsChecked = _twoRowLayout;
        Layout1x4.IsChecked = !_twoRowLayout;

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

        var autoStart = _host.SupportsAutoStart && (_host.GetAutoStartEnabled() || s.AutoStart);
        AutoStartCheck.IsChecked = autoStart;
        if (_host.SupportsAutoStart)
        {
            s.AutoStart = autoStart;
            _host.SetAutoStartEnabled(autoStart);
        }

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

        if (string.Equals(s.UiTheme, "Dark", StringComparison.OrdinalIgnoreCase))
            ThemeDark.IsChecked = true;
        else
            ThemeLight.IsChecked = true;
        DesktopThemeService.Apply(s.UiTheme, this);

        Title = Loc.T("App.Name");
        ApplyLocalizedChrome();

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
        ShowKey_Changed(null, null);
        ApplyLayoutGrid();
        RefreshPositionLabels();
        var local = _slots.FirstOrDefault(x => x.IsLocal) ?? _slots.FirstOrDefault();
        if (local != null) SelectSlot(local, announce: false);
        ShowPage(0);
    }

    private void ApplyLocalizedChrome()
    {
        NavBasicText.Text = Loc.T("Nav.Basic");
        NavNetworkText.Text = Loc.T("Nav.Network");
        NavShareText.Text = Loc.T("Nav.Share");
        NavSecurityText.Text = Loc.T("Nav.Security");
        NavAdvancedText.Text = Loc.T("Nav.Advanced");
        NavProgramsText.Text = Loc.T("Nav.Programs");
        NavLogText.Text = Loc.T("Nav.Log");
        AboutBtnText.Text = Loc.T("Btn.About");
        LogTitleText.Text = Loc.T("Log.Title");
        LogHintText.Text = Loc.T("Log.Hint");
        LogDownloadBtnText.Text = Loc.T("Log.Download");
        LogClearBtnText.Text = Loc.T("Log.Clear");
        LogPrevBtnText.Text = Loc.T("Log.Prev");
        LogNextBtnText.Text = Loc.T("Log.Next");
        LogLatestBtnText.Text = Loc.T("Log.Latest");
        ToolTip.SetTip(LogDownloadBtn, Loc.T("Log.Download"));
        ToolTip.SetTip(LogClearBtn, Loc.T("Log.Clear"));
        ToolTip.SetTip(LogPrevBtn, Loc.T("Log.Prev"));
        ToolTip.SetTip(LogNextBtn, Loc.T("Log.Next"));
        ToolTip.SetTip(LogLatestBtn, Loc.T("Log.Latest"));
        EnsureLogLevelFilterItems();
        Dispatcher.UIThread.Post(UpdateLogToolMode, DispatcherPriority.Loaded);
        ProgTitleText.Text = Loc.T("Prog.Title");
        ProgHintText.Text = Loc.T("Prog.Hint");
        ProgAddBtnText.Text = Loc.T("Prog.Add");
        ProgDeleteBtnText.Text = Loc.T("Prog.Delete");
        ProgStartBtnText.Text = Loc.T("Prog.Start");
        ProgStopBtnText.Text = Loc.T("Prog.Stop");
        ToolTip.SetTip(ProgAddBtn, Loc.T("Prog.Add"));
        ToolTip.SetTip(ProgDeleteBtn, Loc.T("Prog.Delete"));
        ToolTip.SetTip(ProgStartBtn, Loc.T("Prog.Start"));
        ToolTip.SetTip(ProgStopBtn, Loc.T("Prog.Stop"));
        ProgColName.Text = Loc.T("Prog.ColName");
        ProgColLang.Text = Loc.T("Prog.ColLang");
        ProgColSize.Text = Loc.T("Prog.ColSize");
        ProgColStatus.Text = Loc.T("Prog.ColStatus");
        ProgColActions.Text = Loc.T("Prog.ColActions");
        ToolTip.SetTip(ProgAddBtn, ProgAddBtnText.Text);
        ToolTip.SetTip(ProgDeleteBtn, ProgDeleteBtnText.Text);
        ToolTip.SetTip(ProgStartBtn, ProgStartBtnText.Text);
        ToolTip.SetTip(ProgStopBtn, ProgStopBtnText.Text);
        UpdateProgToolMode();
        HelpBtnText.Text = Loc.T("Btn.Help").Replace("？ ", "").Replace("? ", "");
        HelpDocBtn.Content = Loc.T("Help.ShowDoc");
        RefreshHelpToolbarLabels();
        RefreshAboutPage();
        if (PageHelp.IsVisible)
            ShowHelpDocument();
        ApplyBtn.Content = Loc.T("Btn.Apply");
        HideTrayBtn.Content = Loc.T("Btn.HideTray");
        ExitBtn.Content = Loc.T("Btn.Exit");
        if (XferTitleText != null) XferTitleText.Text = Loc.T("Xfer.Title");
        if (XferHintText != null) XferHintText.Text = Loc.T("Xfer.Hint");
        if (DirectTransferBtn != null) DirectTransferBtn.Content = Loc.T("Xfer.Direct");
        if (OpenInboxBtn != null) OpenInboxBtn.Content = Loc.T("Xfer.OpenInbox");
        if (ClearTransfersBtn != null) ClearTransfersBtn.Content = Loc.T("Xfer.ClearDone");
        if (XferColDir != null) XferColDir.Text = Loc.T("Xfer.Col.Dir");
        if (XferColName != null) XferColName.Text = Loc.T("Xfer.Col.Name");
        if (XferColPeer != null) XferColPeer.Text = Loc.T("Xfer.Col.Peer");
        if (XferColSize != null) XferColSize.Text = Loc.T("Xfer.Col.Size");
        if (XferColStatus != null) XferColStatus.Text = Loc.T("Xfer.Col.Status");
        if (TransferPlaceholder != null) TransferPlaceholder.Text = Loc.T("Xfer.Empty");
        Title = Loc.T("App.Name");
        if (SecurityTitleText != null) SecurityTitleText.Text = Loc.T("Sec.Title");
        if (SecurityHintText != null) SecurityHintText.Text = Loc.T("Sec.Hint");
        if (ShowKeyCheck != null) ShowKeyCheck.Content = Loc.T("Sec.Show");
        if (NewKeyBtn != null) NewKeyBtn.Content = Loc.T("Sec.NewKey");

        if (FeatTitleText != null) FeatTitleText.Text = Loc.T("Feat.Title");
        if (BehTitleText != null) BehTitleText.Text = Loc.T("Beh.Title");

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
    }

    private static void SetOpt(CheckBox box, string contentKey, string tipKey)
    {
        box.Content = Loc.T(contentKey);
        ToolTip.SetTip(box, Loc.T(tipKey));
    }

    private void LoadEdgeCrossModeUi(int mode)
    {
        EdgeCrossBarrierRadio.IsChecked = mode is < 1 or > 2;
        EdgeCrossDoubleRadio.IsChecked = mode == 1;
        EdgeCrossSpeedRadio.IsChecked = mode == 2;
        RefreshEdgeCrossTip();
    }

    private int ReadEdgeCrossModeUi()
    {
        if (EdgeCrossDoubleRadio.IsChecked == true) return 1;
        if (EdgeCrossSpeedRadio.IsChecked == true) return 2;
        return 0;
    }

    private void EdgeCrossMode_Changed(object? sender, RoutedEventArgs e)
        => RefreshEdgeCrossTip();

    private void RefreshEdgeCrossTip()
    {
        if (EdgeCrossTipText == null) return;
        var key = ReadEdgeCrossModeUi() switch
        {
            1 => "Tip.EdgeCross.DoubleTap",
            2 => "Tip.EdgeCross.SpeedBurst",
            _ => "Tip.EdgeCross.BarrierFree"
        };
        EdgeCrossTipText.Text = Loc.T(key);
    }

    /// <summary>高级顶栏：宽屏功能/行为/穿边三列，窄屏纵向堆叠。</summary>
    private void OptionsFeatGrid_SizeChanged(object? sender, SizeChangedEventArgs e)
        => ApplyOptionsFeatPlacement(OptionsFeatGrid.Bounds.Width);

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
            OptionsFeatGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            OptionsFeatGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            OptionsFeatGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            OptionsFeatGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
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
            OptionsFeatGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            OptionsFeatGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            OptionsFeatGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            OptionsFeatGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            Grid.SetRow(FeatColumn, 0);
            Grid.SetColumn(FeatColumn, 0);
            Grid.SetRow(BehColumn, 0);
            Grid.SetColumn(BehColumn, 1);
            Grid.SetRow(EdgeCrossColumn, 0);
            Grid.SetColumn(EdgeCrossColumn, 2);
            FeatColumn.Margin = new Thickness(0, 0, 8, 0);
            BehColumn.Margin = new Thickness(4, 0);
            EdgeCrossColumn.Margin = new Thickness(8, 0, 0, 0);
        }
    }

    private void ApplySettingsFromUi()
    {
        var s = _host.Settings;
        s.SecurityKey = GetSecurityKey().Trim();
        s.TwoRow = _twoRowLayout;
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

        if (_host.SupportsAutoStart)
        {
            s.AutoStart = AutoStartCheck.IsChecked == true;
            _host.SetAutoStartEnabled(s.AutoStart);
        }

        s.SwitchHotkeyMode = HotNum.IsChecked == true ? 1 : HotOff.IsChecked == true ? 2 : 0;
        s.HotkeySettings = HkSettings.Text?.Trim() ?? "";
        s.HotkeyReconnect = HkReconnect.Text?.Trim() ?? "";
        s.HotkeyExit = HkExit.Text?.Trim() ?? "";
        s.HotkeyLock = HkLock.Text?.Trim() ?? "";
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

        s.UiTheme = ThemeDark.IsChecked == true ? "Dark" : "Light";
        DesktopThemeService.Apply(s.UiTheme, this);
        Loc.SetCulture(s.UiLanguage);
        ApplyLocalizedChrome();

        s.MachineSlots = _slots.Select(x => x.Enabled ? x.Name.Trim() : "").ToList();
        while (s.MachineSlots.Count < 4) s.MachineSlots.Add("");
        s.EnsureLocalMachineSlot();
        s.Save();
    }

    private async void Apply_Click(object? sender, RoutedEventArgs e)
    {
        ApplySettingsFromUi();
        await _host.BroadcastLayoutSyncAsync();
        await _host.BroadcastBehaviorSyncAsync();
        await _host.RestartNetworkingAsync();
        SyncSlotLocalFlags();
        StatusText.Text = Loc.T("Sys.Applied");
    }

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
    }

    private void Hide_Click(object? sender, RoutedEventArgs e)
    {
        if (_host.SupportsTray)
            Hide();
        else
            WindowState = WindowState.Minimized;
    }

    private void Exit_Click(object? sender, RoutedEventArgs e)
    {
        if (Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desk)
            desk.Shutdown();
        else
            Close();
    }

    /// <summary>诊断修复后刷新布局槽位与连接状态显示。</summary>
    public void ReloadAfterDiagnostics()
    {
        Dispatcher.UIThread.Post(() =>
        {
            ApplyLayoutFromSettings();
            RefreshDiscovery();
            RefreshPositionLabels();
        });
    }

    private void Nav_Changed(object? sender, RoutedEventArgs e)
    {
        if (_syncingNav) return;
        // IsCheckedChanged 在勾选/取消时都会触发，只处理变为选中
        if (sender is RadioButton { IsChecked: not true }) return;
        if (NavBasic.IsChecked == true) ShowPage(0);
        else if (NavNetwork.IsChecked == true) ShowPage(2);
        else if (NavShare.IsChecked == true) ShowPage(3);
        else if (NavSecurity.IsChecked == true) ShowPage(4);
        else if (NavAdvanced.IsChecked == true) ShowPage(1);
        else if (NavPrograms.IsChecked == true) ShowPage(8);
        else if (NavLog.IsChecked == true) ShowPage(7);
        else if (NavAbout.IsChecked == true) ShowPage(5);
        else if (NavHelp.IsChecked == true) ShowPage(6);
    }

    private void ShowPage(int index)
    {
        _syncingNav = true;
        try
        {
            PageBasic.IsVisible = index == 0;
            PageOptions.IsVisible = index == 1;
            PageNetwork.IsVisible = index == 2;
            PageTransfer.IsVisible = index == 3;
            PageSecurity.IsVisible = index == 4;
            PageAbout.IsVisible = index == 5;
            PageHelp.IsVisible = index == 6;
            PageLog.IsVisible = index == 7;
            PagePrograms.IsVisible = index == 8;

            NavBasic.IsChecked = index == 0;
            NavAdvanced.IsChecked = index == 1;
            NavNetwork.IsChecked = index == 2;
            NavShare.IsChecked = index == 3;
            NavSecurity.IsChecked = index == 4;
            NavAbout.IsChecked = index == 5;
            NavHelp.IsChecked = index == 6;
            NavLog.IsChecked = index == 7;
            NavPrograms.IsChecked = index == 8;
        }
        finally
        {
            _syncingNav = false;
        }

        if (index == 5)
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
            Dispatcher.UIThread.Post(UpdateProgToolMode, DispatcherPriority.Loaded);
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
    }

    private IEnumerable<UserProgram> SelectedPrograms()
        => ProgramService.Instance.Catalog.Items.Where(p => p.IsSelected);

    private void ProgSelectAll_Changed(object? sender, RoutedEventArgs e)
    {
        var on = ProgSelectAll.IsChecked == true;
        foreach (var p in ProgramService.Instance.Catalog.Items)
            p.IsSelected = on;
    }

    private void ProgToolBarHost_SizeChanged(object? sender, SizeChangedEventArgs e)
        => UpdateProgToolMode();

    /// <summary>程序页工具栏：放得下时图标+文字，放不下时只留图标。</summary>
    private void UpdateProgToolMode()
    {
        if (!IsLoaded || _progToolModeUpdating || ProgToolBarHost.Bounds.Width <= 1)
            return;
        _progToolModeUpdating = true;
        try
        {
            var available = ProgToolBarHost.Bounds.Width;
            // 先显示文字再测；否则 DesiredSize 偏小，永远不切回宽屏
            SetProgToolCompact(false);
            ProgToolBar.Measure(new Size(double.PositiveInfinity, 48));
            var need = ProgToolBar.DesiredSize.Width;
            SetProgToolCompact(need > available + 0.5);
        }
        finally
        {
            _progToolModeUpdating = false;
        }
    }

    private void SetProgToolCompact(bool compact)
    {
        if (_progToolsCompact == compact && ProgAddBtnText.IsVisible == !compact)
            return;
        _progToolsCompact = compact;
        ProgAddBtnText.IsVisible = !compact;
        ProgDeleteBtnText.IsVisible = !compact;
        ProgStartBtnText.IsVisible = !compact;
        ProgStopBtnText.IsVisible = !compact;
        var pad = compact ? new Thickness(8, 0) : new Thickness(10, 0);
        ProgAddBtn.Padding = pad;
        ProgDeleteBtn.Padding = pad;
        ProgStartBtn.Padding = pad;
        ProgStopBtn.Padding = pad;
    }

    private async void ProgAdd_Click(object? sender, RoutedEventArgs e)
    {
        var dlg = new ProgramAddDialog();
        await dlg.ShowDialog(this);
        if (!dlg.Confirmed) return;
        try
        {
            ProgramService.Instance.Catalog.Create(
                dlg.ProgramName, dlg.SelectedLanguage, dlg.NamespaceValue, dlg.FileNameValue);
            RefreshProgramsPage();
            StatusText.Text = Loc.T("Prog.Add") + " OK";
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
            AppLog.Error(ex, "添加程序失败");
        }
    }

    private async void ProgDelete_Click(object? sender, RoutedEventArgs e)
    {
        var list = SelectedPrograms().ToList();
        if (list.Count == 0)
        {
            StatusText.Text = Loc.T("Prog.SelectFirst");
            return;
        }
        await ProgramService.Instance.Runner.DeleteManyAsync(list);
        RefreshProgramsPage();
    }

    private async void ProgStart_Click(object? sender, RoutedEventArgs e)
    {
        var list = SelectedPrograms().ToList();
        if (list.Count == 0)
        {
            StatusText.Text = Loc.T("Prog.SelectFirst");
            return;
        }
        await ProgramService.Instance.Runner.StartManyAsync(list);
        RefreshProgramsPage();
    }

    private async void ProgStop_Click(object? sender, RoutedEventArgs e)
    {
        var list = SelectedPrograms().ToList();
        if (list.Count == 0)
        {
            StatusText.Text = Loc.T("Prog.SelectFirst");
            return;
        }
        await ProgramService.Instance.Runner.StopManyAsync(list);
        RefreshProgramsPage();
    }

    private async void ProgRowStart_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is not UserProgram prog) return;
        var runner = ProgramService.Instance.Runner;
        var stop = runner.IsRunning(prog.Id)
                   || prog.RuntimeStatus is ProgramStatus.Running or ProgramStatus.Stopping;
        try
        {
            if (stop) await runner.StopAsync(prog);
            else await runner.StartAsync(prog);
        }
        catch (Exception ex) { StatusText.Text = ex.Message; }
        RefreshProgramsPage();
    }

    private async void ProgRowEdit_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is not UserProgram prog) return;
        var win = new ProgramEditorWindow(prog);
        await win.ShowDialog(this);
        RefreshProgramsPage();
    }

    private async void ProgRowDelete_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is not UserProgram prog) return;
        try { await ProgramService.Instance.Runner.DeleteAsync(prog); }
        catch (Exception ex) { StatusText.Text = ex.Message; }
        RefreshProgramsPage();
    }

    private void ProgRowLog_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is not UserProgram prog) return;
        _logProgramId = prog.Id;
        _logProgramName = prog.Name;
        _logPageFromLatest = 0;
        _openProgramLog = true;
        if (NavLog.IsChecked == true)
            ShowPage(7);
        else
            NavLog.IsChecked = true;
    }

    private async void ProgRowShare_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is not UserProgram prog) return;
        var share = ProgramShareService.Current;
        if (share == null)
        {
            StatusText.Text = Loc.T("Prog.ShareNone");
            return;
        }

        try
        {
            var result = await share.ShareAsync(prog);
            StatusText.Text = result.Reachable == 0
                ? Loc.T("Prog.ShareNone")
                : string.Format(Loc.T("Prog.ShareSent"), result.Sent);
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
        }
    }

    private void RefreshLogHint()
    {
        LogHintText.Text = string.IsNullOrEmpty(_logProgramName)
            ? Loc.T("Log.Hint")
            : string.Format(Loc.T("Prog.LogFilter"), _logProgramName);
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
        var idx = 0;
        for (var i = 0; i < LogLevelFilter.Items.Count; i++)
        {
            if (LogLevelFilter.Items[i] is ComboBoxItem { Tag: AppLogLevelFilter f } && f == selected)
            {
                idx = i;
                break;
            }
        }
        LogLevelFilter.SelectedIndex = idx;
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

    private void LogToolBarHost_SizeChanged(object? sender, SizeChangedEventArgs e)
        => UpdateLogToolMode();

    private void UpdateLogToolMode()
    {
        if (!IsLoaded || _logToolModeUpdating || LogToolBarHost.Bounds.Width <= 1)
            return;
        _logToolModeUpdating = true;
        try
        {
            var available = LogToolBarHost.Bounds.Width;
            SetLogToolCompact(false);
            LogToolBar.Measure(new Size(double.PositiveInfinity, 48));
            SetLogToolCompact(LogToolBar.DesiredSize.Width > available + 0.5);
        }
        finally
        {
            _logToolModeUpdating = false;
        }
    }

    private void SetLogToolCompact(bool compact)
    {
        if (_logToolsCompact == compact && LogPrevBtnText.IsVisible == !compact)
            return;
        _logToolsCompact = compact;
        LogPrevBtnText.IsVisible = !compact;
        LogNextBtnText.IsVisible = !compact;
        LogLatestBtnText.IsVisible = !compact;
        LogDownloadBtnText.IsVisible = !compact;
        LogClearBtnText.IsVisible = !compact;
        var pad = compact ? new Thickness(0) : new Thickness(8, 0);
        var w = compact ? 30.0 : double.NaN;
        foreach (var b in new[] { LogPrevBtn, LogNextBtn, LogLatestBtn, LogDownloadBtn, LogClearBtn })
        {
            b.Padding = pad;
            b.MinWidth = 30;
            b.Width = w;
        }
        var avail = LogToolBarHost.Bounds.Width;
        LogLevelFilter.Width = compact ? (avail < 240 ? 52 : 64) : 72;
    }

    private void ScrollLogToEndQuiet()
    {
        var sv = LogList.FindDescendantOfType<ScrollViewer>();
        sv?.ScrollToEnd();
    }

    private bool LogItemsSameTail(IReadOnlyList<AppLogEntry> items)
    {
        if (_logItems.Count != items.Count) return false;
        if (items.Count == 0) return true;
        return ReferenceEquals(_logItems[0], items[0])
               && ReferenceEquals(_logItems[^1], items[^1]);
    }

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
            Dispatcher.UIThread.Post(ScrollLogToEndQuiet, DispatcherPriority.Background);
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
            var delta = AppLog.DeltaSince(null, level, _logProgramId, _logProgramName, AppLog.DefaultPageSize);
            if (delta.Items.Count > 0)
                ReplaceLogItems(delta.Items);
            else
                ReplaceLogItems(page.Items);
            _logWatermark = _logItems.Count > 0 && delta.Items.Count > 0 ? _logItems[^1] : null;
            _logStickToBottom = true;
            Dispatcher.UIThread.Post(ScrollLogToEndQuiet, DispatcherPriority.Loaded);
        }
        else
        {
            ReplaceLogItems(page.Items);
            _logWatermark = null;
            _logStickToBottom = false;
        }
    }

    private void LogFilter_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (!_logFilterReady) return;
        _logPageFromLatest = 0;
        if (PageLog.IsVisible)
            RefreshLogPage();
    }

    private void LogPrev_Click(object? sender, RoutedEventArgs e)
    {
        _logPageFromLatest++;
        RefreshLogPage();
    }

    private void LogNext_Click(object? sender, RoutedEventArgs e)
    {
        if (_logPageFromLatest > 0)
            _logPageFromLatest--;
        RefreshLogPage();
    }

    private void LogLatest_Click(object? sender, RoutedEventArgs e)
    {
        _logPageFromLatest = 0;
        RefreshLogPage();
    }

    private void LogClear_Click(object? sender, RoutedEventArgs e)
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

    private async void LogDownload_Click(object? sender, RoutedEventArgs e)
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

        try
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = Loc.T("Log.Download"),
                SuggestedFileName = AppLog.SuggestExportFileName(),
                DefaultExtension = "txt",
                FileTypeChoices =
                [
                    new FilePickerFileType("Text") { Patterns = ["*.txt"] },
                    new FilePickerFileType("All") { Patterns = ["*.*"] }
                ]
            }).ConfigureAwait(true);

            if (file == null) return;

            await using (var stream = await file.OpenWriteAsync().ConfigureAwait(true))
            await using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
                await writer.WriteAsync(text).ConfigureAwait(true);

            var path = file.TryGetLocalPath() ?? file.Name;
            StatusText.Text = string.Format(Loc.T("Log.DownloadOk"), path);
        }
        catch (Exception ex)
        {
            StatusText.Text = string.Format(Loc.T("Log.DownloadFail"), ex.Message);
        }
    }

    private void RefreshAboutPage()
    {
        var ver = typeof(MainWindow).Assembly.GetName().Version;
        var verText = ver == null ? "1.0" : $"{ver.Major}.{ver.Minor}.{Math.Max(ver.Build, 0)}";
        var body = string.Format(Loc.T("About.Body"), verText);
        if (!string.IsNullOrWhiteSpace(_host.PlatformStatusLine))
            body += "\n\n" + _host.PlatformStatusLine;
        AboutTitleText.Text = Loc.T("About.Title");
        AboutBodyText.Text = body;
    }

    private void RefreshHelpToolbarLabels()
    {
        HelpUpdateBtn.Content = _helpUpdateReady ? Loc.T("Help.ApplyUpdate") : Loc.T("Help.CheckUpdate");
        HelpDiagBtn.Content = _helpFixReady ? Loc.T("Help.DiagFix") : Loc.T("Help.DiagCheck");
        HelpUpdateBtn.Classes.Set("primary", _helpUpdateReady);
        HelpUpdateBtn.Classes.Set("softBlue", !_helpUpdateReady);
        HelpDiagBtn.Classes.Set("primary", _helpFixReady);
        HelpDiagBtn.Classes.Set("softWarn", !_helpFixReady);
    }

    private void HelpDoc_Click(object? sender, RoutedEventArgs e) => ShowHelpDocument();

    private async void HelpUpdate_Click(object? sender, RoutedEventArgs e)
    {
        if (_helpUpdateReady)
            await ApplyHelpUpdateAsync();
        else
            await CheckHelpUpdateAsync();
    }

    private async void HelpDiag_Click(object? sender, RoutedEventArgs e)
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
                ShowHelpLog(string.IsNullOrWhiteSpace(result.Message) ? Loc.T("Help.NoUpdate") : result.Message);
            else
                ShowHelpLog(result.Message);
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

        if (string.IsNullOrWhiteSpace(_helpLastCheck.AssetDownloadUrl))
        {
            var url = string.IsNullOrWhiteSpace(_helpLastCheck.ReleaseUrl)
                ? GitHubReleaseChecker.ReleasesUrl
                : _helpLastCheck.ReleaseUrl;
            ShowHelpLog(_helpLastCheck.Message + "\n\n" + url);
            TryOpenUrl(url);
            return;
        }

        SetHelpBusy(true);
        try
        {
            ShowHelpLog("正在下载并安装更新…");
            var progress = new Progress<UpdateDownloadProgress>(p =>
            {
                if (!string.IsNullOrWhiteSpace(p.Message))
                    Dispatcher.UIThread.Post(() => ShowHelpLog(p.Message));
            });
            var apply = await _helpUpdater.DownloadAndApplyAsync(_helpLastCheck, progress)
                .ConfigureAwait(true);
            ShowHelpLog(apply.Message);
            if (apply.ShouldExit)
            {
                ShowHelpLog(Loc.T("Help.Restarting"));
                await Task.Delay(400).ConfigureAwait(true);
                if (Application.Current?.ApplicationLifetime is IControlledApplicationLifetime lifetime)
                    lifetime.Shutdown();
                else
                    Environment.Exit(0);
            }
        }
        catch (Exception ex)
        {
            ShowHelpLog(ex.Message);
        }
        finally
        {
            SetHelpBusy(false);
        }
    }

    private async Task CheckHelpDiagAsync()
    {
        SetHelpBusy(true);
        try
        {
            ShowHelpLog(Loc.T("Help.CheckingConn"));
            var diag = new DesktopConnectionDiagnostics(_host);
            var report = await diag.CheckAsync().ConfigureAwait(true);
            _helpFixReady = report.Items.Any(i =>
                i.Level is DesktopDiagnosticLevel.Error or DesktopDiagnosticLevel.Warn);
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
            var diag = new DesktopConnectionDiagnostics(_host);
            var progress = new Progress<string>(s =>
                Dispatcher.UIThread.Post(() => ShowHelpLog(s)));
            var report = await diag.FixAsync(progress).ConfigureAwait(true);
            _helpFixReady = report.Items.Any(i =>
                i.Level is DesktopDiagnosticLevel.Error or DesktopDiagnosticLevel.Warn);
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
        Cursor = busy
            ? new Cursor(StandardCursorType.Wait)
            : Cursor.Default;
    }

    private void ShowHelpDocument()
    {
        HelpUpdateProgressBar.IsVisible = false;
        HelpContent.Children.Clear();
        HelpContent.Children.Add(HelpDocTitle(Loc.T("Help.Doc.Title")));
        HelpContent.Children.Add(HelpPara(Loc.T("Help.Doc.Intro")));
        HelpContent.Children.Add(HelpHeading(Loc.T("Help.Doc.Quick")));
        AddHelpNumbered(
            Loc.T("Help.Doc.Q1"), Loc.T("Help.Doc.Q2"), Loc.T("Help.Doc.Q3"),
            Loc.T("Help.Doc.Q4"), Loc.T("Help.Doc.Q5"));
        HelpContent.Children.Add(HelpHeading(Loc.T("Help.Doc.Clip")));
        AddHelpBullets(
            Loc.T("Help.Doc.C1"), Loc.T("Help.Doc.C2"),
            Loc.T("Help.Doc.C3"), Loc.T("Help.Doc.C4"), Loc.T("Help.Doc.C5"));
        HelpContent.Children.Add(HelpHeading(Loc.T("Help.Doc.Opts")));
        AddHelpBullets(
            Loc.T("Help.Doc.O1"), Loc.T("Help.Doc.O2"), Loc.T("Help.Doc.O3"),
            Loc.T("Help.Doc.O4"), Loc.T("Help.Doc.O5"), Loc.T("Help.Doc.O6"), Loc.T("Help.Doc.O7"));
        HelpContent.Children.Add(HelpHeading(Loc.T("Help.Doc.Hot")));
        HelpContent.Children.Add(HelpPara(Loc.T("Help.Doc.H1")));
        HelpContent.Children.Add(HelpPara(Loc.T("Help.Doc.H2")));
        HelpContent.Children.Add(HelpHeading(Loc.T("Help.Doc.Net")));
        HelpContent.Children.Add(HelpPara(Loc.T("Help.Doc.N1")));
        HelpContent.Children.Add(HelpPara(HelpConfigDirLine()));
        HelpContent.Children.Add(HelpHeading(Loc.T("Help.Doc.Update")));
        HelpContent.Children.Add(HelpPara(Loc.T("Help.Doc.U1")));
        HelpContent.Children.Add(HelpPara(GitHubReleaseChecker.ReleasesUrl));
        HelpContent.Children.Add(HelpHeading(Loc.T("Help.Doc.Diag")));
        HelpContent.Children.Add(HelpPara(Loc.T("Help.Doc.D1")));
    }

    private void ShowHelpLog(string text)
    {
        HelpContent.Children.Clear();
        foreach (var line in (text ?? "").Replace("\r\n", "\n").Split('\n'))
            HelpContent.Children.Add(HelpPara(line, muted: false));
    }

    private static string HelpConfigDirLine()
    {
        if (OperatingSystem.IsWindows())
            return Loc.T("Help.Doc.N2");
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "FlyScreen");
        return $"配置目录：{dir}";
    }

    private IBrush? HelpTextBrush()
        => TryGetHelpBrush("Text") ?? Brushes.Black;

    private IBrush? HelpMutedBrush()
        => TryGetHelpBrush("Muted") ?? Brushes.Gray;

    private IBrush? TryGetHelpBrush(string key)
    {
        if (Application.Current?.Resources.TryGetResource(key, null, out var r) == true && r is IBrush b)
            return b;
        if (Resources.TryGetResource(key, null, out var local) && local is IBrush lb)
            return lb;
        return null;
    }

    private TextBlock HelpDocTitle(string text) => new()
    {
        Text = text,
        FontSize = 16,
        FontWeight = FontWeight.SemiBold,
        Foreground = HelpTextBrush(),
        Margin = new Thickness(0, 0, 0, 10),
        TextWrapping = TextWrapping.Wrap
    };

    private TextBlock HelpHeading(string text) => new()
    {
        Text = text,
        FontSize = 14,
        FontWeight = FontWeight.SemiBold,
        Foreground = HelpTextBrush(),
        Margin = new Thickness(0, 12, 0, 6),
        TextWrapping = TextWrapping.Wrap
    };

    private TextBlock HelpPara(string text, bool muted = true) => new()
    {
        Text = text,
        Foreground = muted ? HelpMutedBrush() : HelpTextBrush(),
        Margin = new Thickness(0, 0, 0, 4),
        TextWrapping = TextWrapping.Wrap,
        FontFamily = muted
            ? FontFamily
            : new FontFamily("Consolas, DejaVu Sans Mono, monospace")
    };

    private void AddHelpNumbered(params string[] items)
    {
        for (var i = 0; i < items.Length; i++)
            HelpContent.Children.Add(HelpPara($"{i + 1}. {items[i]}"));
    }

    private void AddHelpBullets(params string[] items)
    {
        foreach (var item in items)
            HelpContent.Children.Add(HelpPara("• " + item));
    }

    private static void TryOpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch
        {
            // ignore
        }
    }

    private void LayoutMode_Changed(object? sender, RoutedEventArgs e)
    {
        if (_syncingLayout) return;
        // 只在「选中」时应用，避免 Unchecked 瞬间两边都为 false 导致状态错乱
        if (sender is not RadioButton { IsChecked: true } rb) return;
        _twoRowLayout = ReferenceEquals(rb, Layout2x2);
        ApplyLayoutGrid();
    }

    private void ApplyLayoutGrid()
    {
        if (Layout1x4 == null || Layout2x2 == null || MachineMatrix == null) return;

        _syncingLayout = true;
        try
        {
            Layout1x4.IsChecked = !_twoRowLayout;
            Layout2x2.IsChecked = _twoRowLayout;
        }
        finally
        {
            _syncingLayout = false;
        }

        ApplyUniformGridDimensions(_twoRowLayout);
        RefreshPositionLabels(_twoRowLayout);
    }

    private void ApplyUniformGridDimensions(bool twoRow)
    {
        // 重建 ItemsPanel，避免仅改 Rows/Columns 时 Avalonia UniformGrid 不刷新
        MachineMatrix.ItemsPanel = new FuncTemplate<Panel?>(() => new UniformGrid
        {
            Rows = twoRow ? 2 : 1,
            Columns = twoRow ? 2 : 4
        });
        MachineMatrix.InvalidateMeasure();
        MachineMatrix.InvalidateArrange();
        MachineMatrix.UpdateLayout();
    }

    private void RefreshPositionLabels(bool? twoRowOverride = null)
    {
        var twoRow = twoRowOverride ?? _twoRowLayout;
        for (var i = 0; i < _slots.Count; i++)
        {
            _slots[i].Index = i;
            _slots[i].SetLayoutHint(twoRow);
        }
    }

    private void ApplyLayoutFromSettings()
    {
        var s = _host.Settings;
        s.EnsureLocalMachineSlot();
        _twoRowLayout = s.TwoRow;
        Layout2x2.IsChecked = _twoRowLayout;
        Layout1x4.IsChecked = !_twoRowLayout;

        for (var i = 0; i < 4; i++)
        {
            var name = i < s.MachineSlots.Count ? s.MachineSlots[i] : "";
            name = string.IsNullOrWhiteSpace(name) ? "" : name.Trim();
            var slot = _slots[i];
            slot.Name = name;
            slot.Enabled = !string.IsNullOrWhiteSpace(name);
            slot.IsLocal = string.Equals(name, LocalPeerIdentity.MachineName, StringComparison.OrdinalIgnoreCase);
            if (slot.IsLocal)
            {
                slot.OutboundOk = true;
                slot.InboundOk = true;
            }
            else if (!string.IsNullOrEmpty(name))
            {
                var st = _host.Connections.GetPeerStatus(name);
                slot.OutboundOk = st.Outbound;
                slot.InboundOk = st.Inbound;
                if (!string.IsNullOrEmpty(st.DiscoveredIp))
                    slot.ResolvedIp = st.DiscoveredIp;
            }
            else
            {
                slot.OutboundOk = false;
                slot.InboundOk = false;
                slot.ResolvedIp = "";
            }

            slot.RefreshStatus();
        }

        ApplyLayoutGrid();
        RefreshDiscovery();
    }

    private void ShowKey_Changed(object? sender, RoutedEventArgs? e)
    {
        if (ShowKeyCheck.IsChecked == true)
        {
            SecurityKeyBox.Text = SecurityKeyPassword.Text;
            SecurityKeyBox.IsVisible = true;
            SecurityKeyPassword.IsVisible = false;
        }
        else
        {
            SecurityKeyPassword.Text = SecurityKeyBox.Text;
            SecurityKeyPassword.IsVisible = true;
            SecurityKeyBox.IsVisible = false;
        }
    }

    private string GetSecurityKey()
        => ShowKeyCheck.IsChecked == true ? (SecurityKeyBox.Text ?? "") : (SecurityKeyPassword.Text ?? "");

    private void SetSecurityKey(string key)
    {
        SecurityKeyBox.Text = key;
        SecurityKeyPassword.Text = key;
        ShowKey_Changed(null, null);
    }

    private void NewKey_Click(object? sender, RoutedEventArgs e)
        => SetSecurityKey(AppSettings.GenerateKey());

    private async void Reconnect_Click(object? sender, RoutedEventArgs e)
    {
        ApplySettingsFromUi();
        await _host.Connections.ReconnectNowAsync();
        StatusText.Text = "正在重新连接…";
    }

    private void RefreshNics_Click(object? sender, RoutedEventArgs e) => RefreshNics();

    private void RefreshNics()
    {
        var nics = NicService.ListUsableIpv4Nics();
        NicCombo.ItemsSource = nics;
        NicCombo.SelectedItem = null;
        var preferred = NicService.ResolvePreferred(_host.Settings.PreferredNicId, _host.Settings.PreferredLocalIp);
        if (preferred != null)
            NicCombo.SelectedItem = nics.FirstOrDefault(n => n.Id == preferred.Id && n.IPv4 == preferred.IPv4)
                                    ?? nics.FirstOrDefault();
        else if (nics.Count > 0)
            NicCombo.SelectedIndex = 0;
    }

    private void RefreshDiscovery_Click(object? sender, RoutedEventArgs e) => RefreshDiscovery();

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

    private void DiscoveryList_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DiscoveryList.SelectedItem is DiscoveredPeer peer)
            TryAddPeer(peer);
    }

    private void AddDiscovered_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { Tag: DiscoveredPeer peer })
            TryAddPeer(peer);
        else if (DiscoveryList.SelectedItem is DiscoveredPeer selected)
            TryAddPeer(selected);
    }

    private async void AddMachine_Click(object? sender, RoutedEventArgs e)
    {
        var unused = UnusedDiscoveredPeers();
        if (unused.Count == 0)
        {
            ShowPage(2);
            RefreshDiscovery();
            StatusText.Text = "暂无新发现的电脑，请先刷新发现或确认对端已启动";
            return;
        }

        if (unused.Count == 1)
        {
            TryAddPeer(unused[0]);
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

        if (sender is Control c)
        {
            menu.Open(c);
        }
    }

    private static MachineSlot? SlotFromControl(Control? c)
    {
        while (c != null)
        {
            if (c.DataContext is MachineSlot dc) return dc;
            if (c.Tag is MachineSlot tag) return tag;
            c = c.GetVisualParent() as Control;
        }

        return null;
    }

    private void SlotCard_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control c) return;
        var slot = SlotFromControl(c);
        if (slot == null) return;

        // 按下只改选中态，不刷状态栏（避免拖动时反复弹「已选中」）
        SelectSlot(slot, announce: false);

        var now = Environment.TickCount64;
        var isDouble = e.ClickCount >= 2
                       || (ReferenceEquals(slot, _lastClickSlot) && now - _lastClickTicks < 400);
        _lastClickSlot = slot;
        _lastClickTicks = now;

        if (isDouble)
        {
            e.Handled = true;
            ClearDragState();
            TryRemoveSlot(slot);
            return;
        }

        if (!e.GetCurrentPoint(c).Properties.IsLeftButtonPressed) return;
        _dragSlot = slot;
        _dragSourceVisual = c;
        _dragStarted = false;
        _dragStart = e.GetPosition(this);
        e.Pointer.Capture(c);
        e.Handled = true;
    }

    private void SlotCard_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragSlot == null) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            FinishSlotDrag(e, commit: false);
            return;
        }

        var pos = e.GetPosition(this);
        if (!_dragStarted)
        {
            if (Math.Abs(pos.X - _dragStart.X) < 6 && Math.Abs(pos.Y - _dragStart.Y) < 6)
                return;

            // 自管拖拽：不走系统 DnD（X11 上慢且无拖影）
            _dragStarted = true;
            SuppressSlotTooltips();
            _dragSourceVisual?.Classes.Add("slot-dragging");
            _savedCursor = Cursor;
            Cursor = new Cursor(StandardCursorType.DragMove);
            ShowDragGhost(_dragSlot, _dragSourceVisual, e.GetPosition(DragGhostLayer));
        }
        else
        {
            MoveDragGhost(e.GetPosition(DragGhostLayer));
            UpdateDropHighlight(pos);
        }

        e.Handled = true;
    }

    private void SlotCard_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        FinishSlotDrag(e, commit: true);
    }

    private void FinishSlotDrag(PointerEventArgs e, bool commit)
    {
        var wasDragging = _dragStarted;
        var src = _dragSlot;
        var pos = e.GetPosition(this);

        if (wasDragging && commit && src != null)
        {
            var targetBorder = HitTestSlotBorder(pos);
            var target = SlotFromControl(targetBorder);
            if (target != null && !ReferenceEquals(src, target))
            {
                SwapSlotData(src, target);
                SyncSlotLocalFlags();
                RefreshPositionLabels();
                PersistSlots();
                StatusText.Text = "已互换布局位置（点「应用」同步到其他电脑）";
                SelectSlot(target, announce: false);
            }
        }
        else if (!wasDragging && commit && src != null)
        {
            // 单击才提示选中
            SelectSlot(src, announce: true);
        }

        ClearDragState();
        try { e.Pointer.Capture(null); } catch { /* ignore */ }
        e.Handled = true;
    }

    private void ClearDragState()
    {
        _dragSlot = null;
        _dragStarted = false;
        HideDragGhost();
        ClearDragHighlights();
        if (_dragSourceVisual != null)
        {
            _dragSourceVisual.Classes.Remove("slot-dragging");
            _dragSourceVisual = null;
        }

        if (_savedCursor != null)
        {
            Cursor = _savedCursor;
            _savedCursor = null;
        }
    }

    /// <summary>拖动中关闭可能残留的 tip，避免跟指针闪烁。</summary>
    private void SuppressSlotTooltips()
    {
        if (MachineMatrix == null) return;
        foreach (var c in MachineMatrix.GetVisualDescendants().OfType<Control>())
        {
            ToolTip.SetIsOpen(c, false);
            ToolTip.SetTip(c, null);
        }
    }

    private void ClearDragHighlights()
    {
        if (_dragHighlight != null)
        {
            _dragHighlight.Classes.Remove("slot-drop-target");
            _dragHighlight = null;
        }
    }

    private void UpdateDropHighlight(Point windowPos)
    {
        var border = HitTestSlotBorder(windowPos);
        if (border != null && (ReferenceEquals(border, _dragSourceVisual) || border.Classes.Contains("slot-dragging")))
            border = null;

        if (ReferenceEquals(border, _dragHighlight)) return;
        ClearDragHighlights();
        if (border == null) return;
        border.Classes.Add("slot-drop-target");
        _dragHighlight = border;
    }

    private Border? HitTestSlotBorder(Point windowPos)
    {
        // 幽灵层不参与命中；从窗口命中结果向上找 slot
        var hit = this.InputHitTest(windowPos);
        return FindSlotBorder(hit as Visual);
    }

    private void ShowDragGhost(MachineSlot slot, Control? source, Point posInLayer)
    {
        HideDragGhost();
        if (DragGhostLayer == null) return;

        var w = source is { Bounds.Width: > 8 } ? source.Bounds.Width : 120;
        var h = source is { Bounds.Height: > 8 } ? source.Bounds.Height : 88;
        var blue = TryBrush("Blue") ?? Brushes.DodgerBlue;
        var bg = TryBrush("BlueSoft") ?? TryBrush("CardBg") ?? Brushes.White;
        var text = TryBrush("Text") ?? Brushes.Black;

        // 轻量幽灵：无阴影，减少每帧绘制开销
        _dragGhost = new Border
        {
            Width = w,
            Height = h,
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(2),
            BorderBrush = blue,
            Background = bg,
            Opacity = 0.88,
            Padding = new Thickness(8),
            IsHitTestVisible = false,
            Child = new TextBlock
            {
                Text = slot.DisplayName,
                FontSize = 12,
                FontWeight = FontWeight.SemiBold,
                Foreground = text,
                TextAlignment = TextAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            }
        };

        Canvas.SetLeft(_dragGhost, posInLayer.X - w / 2);
        Canvas.SetTop(_dragGhost, posInLayer.Y - h / 2);
        DragGhostLayer.Children.Add(_dragGhost);
    }

    private void MoveDragGhost(Point posInLayer)
    {
        if (_dragGhost == null) return;
        Canvas.SetLeft(_dragGhost, posInLayer.X - _dragGhost.Width / 2);
        Canvas.SetTop(_dragGhost, posInLayer.Y - _dragGhost.Height / 2);
    }

    private void HideDragGhost()
    {
        if (_dragGhost == null) return;
        DragGhostLayer?.Children.Remove(_dragGhost);
        _dragGhost = null;
    }

    private static IBrush? TryBrush(string key)
    {
        if (Application.Current?.TryGetResource(key, Application.Current.ActualThemeVariant, out var r) == true
            && r is IBrush b)
            return b;
        return null;
    }

    private static Border? FindSlotBorder(Visual? v)
    {
        while (v != null)
        {
            if (v is Border b && (b.DataContext is MachineSlot || b.Classes.Contains("slot")))
                return b;
            v = v.GetVisualParent();
        }

        return null;
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

    private void RemoveSelectedMachine_Click(object? sender, RoutedEventArgs e)
    {
        var selected = _selectedSlot ?? _slots.FirstOrDefault(s => s.IsSelected);
        if (selected == null)
        {
            StatusText.Text = "请先单击选中一张电脑卡片";
            return;
        }

        TryRemoveSlot(selected);
    }

    private void SelectSlot(MachineSlot slot, bool announce = true)
    {
        _selectedSlot = slot;
        foreach (var s in _slots)
            s.IsSelected = ReferenceEquals(s, slot);
        if (!announce) return;
        StatusText.Text = string.IsNullOrWhiteSpace(slot.Name)
            ? $"已选中空位（{slot.PositionLabel}）"
            : $"已选中 {slot.DisplayName}（{slot.PositionLabel}）";
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
            StatusText.Text = "没有空的机器槽位（最多 4 台），请先移除一台电脑。";
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

    private void SyncSlotLocalFlags()
    {
        foreach (var slot in _slots)
        {
            slot.IsLocal = string.Equals(slot.Name, LocalPeerIdentity.MachineName, StringComparison.OrdinalIgnoreCase);
            if (slot.IsLocal) slot.RefreshStatus();
        }
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
            // ignore
        }
    }

    private async Task RemovePeerAndSyncAsync(string removedPeer)
    {
        try
        {
            await _host.Connections.DropPeerAsync(removedPeer).ConfigureAwait(true);
            await _host.BroadcastLayoutSyncAsync().ConfigureAwait(true);
            await _host.Connections.PruneUnconfiguredLinksAsync().ConfigureAwait(true);
            // Desktop 壳无 InputRouter；被踢/仅本机时由各平台 Host 的 LayoutSync 回调恢复光标
        }
        catch
        {
            // ignore
        }
    }

    private void OnPeerLinkChanged(string name, bool outbound, bool inbound)
    {
        Dispatcher.UIThread.Post(() =>
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

    private void DirectTransfer_Click(object? sender, RoutedEventArgs e)
    {
        var win = new DirectTransferWindow(_host);
        win.Show(this);
    }

    private async void ClearTransfers_Click(object? sender, RoutedEventArgs e)
    {
        var hasReceive = _host.Transfers.Items.Any(i =>
            i.Direction == TransferDirection.Receive
            && i.State is TransferState.Completed or TransferState.Failed or TransferState.Cancelled);
        if (hasReceive)
        {
            var choice = await AskYesNoCancelAsync(
                Loc.T("Xfer.ConfirmClearTitle"),
                Loc.T("Xfer.ConfirmClearReceive")).ConfigureAwait(true);
            if (choice is null) return;
            _host.Transfers.ClearCompleted(deleteReceiveFiles: choice == true);
        }
        else
        {
            _host.Transfers.ClearCompleted(deleteReceiveFiles: false);
        }

        TransferPlaceholder.IsVisible = _host.TransferItems.Count == 0;
    }

    private async void DeleteTransfer_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { Tag: TransferItem item }) return;

        if (item.State is TransferState.Queued or TransferState.Transferring)
        {
            _host.Transfers.RemoveItem(item, deleteCache: true);
            TransferPlaceholder.IsVisible = _host.TransferItems.Count == 0;
            return;
        }

        if (item.Direction != TransferDirection.Receive)
        {
            _host.Transfers.RemoveItem(item, deleteCache: false);
            TransferPlaceholder.IsVisible = _host.TransferItems.Count == 0;
            return;
        }

        var choice = await AskYesNoCancelAsync(
            Loc.T("Xfer.ConfirmDeleteTitle"),
            Loc.T("Xfer.ConfirmDeleteReceive")).ConfigureAwait(true);
        if (choice is null) return;
        _host.Transfers.RemoveItem(item, deleteCache: choice == true);
        TransferPlaceholder.IsVisible = _host.TransferItems.Count == 0;
    }

    /// <summary>true=删除文件，false=仅记录，null=取消。</summary>
    private async Task<bool?> AskYesNoCancelAsync(string title, string message)
    {
        bool? result = null;
        var dlg = new Window
        {
            Title = title,
            Width = 440,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Background = Background
        };

        var yes = new Button { Content = Loc.T("Xfer.DeleteFiles"), MinWidth = 96, Classes = { "primary" } };
        var no = new Button { Content = Loc.T("Xfer.KeepFiles"), MinWidth = 96, Classes = { "normal" } };
        var cancel = new Button { Content = Loc.T("DT.Cancel"), MinWidth = 88, Classes = { "normal" } };
        yes.Click += (_, _) => { result = true; dlg.Close(); };
        no.Click += (_, _) => { result = false; dlg.Close(); };
        cancel.Click += (_, _) => { result = null; dlg.Close(); };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0)
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(no);
        buttons.Children.Add(yes);

        var root = new StackPanel { Margin = new Thickness(16), Spacing = 8 };
        root.Children.Add(new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap
        });
        root.Children.Add(buttons);
        dlg.Content = root;

        await dlg.ShowDialog(this).ConfigureAwait(true);
        return result;
    }

    private async void OpenInbox_Click(object? sender, RoutedEventArgs e)
    {
        if (_host.Transfers.HasRemotePendingFiles)
        {
            StatusText.Text = "正在接收对端文件…";
            try { await _host.Transfers.PullRemoteFilesForPasteAsync(); }
            catch { /* ignore */ }
        }

        var inbox = _host.Transfers.InboxDirectory;
        if (DesktopFolderOpen.TryOpen(inbox, out var error))
        {
            StatusText.Text = Loc.T("Xfer.OpenInbox") + ": " + inbox;
            return;
        }

        StatusText.Text = string.IsNullOrWhiteSpace(error)
            ? $"无法打开收件箱: {inbox}"
            : $"无法打开收件箱: {error}（{inbox}）";
    }

    private void TransferFileName_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.ClickCount < 1) return;
        if (sender is Control { DataContext: TransferItem item })
            RevealTransferPath(item);
    }

    private void TransferFileName_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control { DataContext: TransferItem item })
            RevealTransferPath(item);
    }

    private void RevealTransferPath(TransferItem item)
    {
        if (!FlyScreen.Helpers.PathReveal.TryReveal(item.FullPath, out var error))
            StatusText.Text = string.IsNullOrWhiteSpace(error) ? "无法打开所在文件夹" : error;
    }
}
