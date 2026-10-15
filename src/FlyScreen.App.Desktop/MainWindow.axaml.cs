using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
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
using FlyScreen.DesktopUi.Shell;
using FlyScreen.Helpers.Localization;
using FlyScreen.Models;
using FlyScreen.Services.Network;

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
        _host.LayoutSyncedFromPeer += from => Dispatcher.UIThread.Post(() =>
        {
            ApplyLayoutFromSettings();
            StatusText.Text = $"已同步布局 ← {from}";
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
        TabBasic.Content = Loc.T("Tab.Basic");
        TabOptions.Content = Loc.T("Tab.Options");
        TabNetwork.Content = Loc.T("Tab.Network");
        TabTransfer.Content = Loc.T("Tab.Transfer");
        AboutBtn.Content = Loc.T("Btn.About");
        HelpBtn.Content = Loc.T("Btn.Help");
        ApplyBtn.Content = Loc.T("Btn.Apply");
        HideTrayBtn.Content = Loc.T("Btn.HideTray");
        ExitBtn.Content = Loc.T("Btn.Exit");
        Title = Loc.T("App.Name");

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
    }

    private static void SetOpt(CheckBox box, string contentKey, string tipKey)
    {
        box.Content = Loc.T(contentKey);
        ToolTip.SetTip(box, Loc.T(tipKey));
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
        await _host.RestartNetworkingAsync();
        SyncSlotLocalFlags();
        StatusText.Text = Loc.T("Sys.Applied");
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

    private async void About_Click(object? sender, RoutedEventArgs e) => await ShowAboutAsync();

    private async Task ShowAboutAsync()
    {
        var ver = typeof(MainWindow).Assembly.GetName().Version;
        var verText = ver == null ? "1.0" : $"{ver.Major}.{ver.Minor}.{Math.Max(ver.Build, 0)}";
        var body = string.Format(Loc.T("About.Body"), verText);
        if (!string.IsNullOrWhiteSpace(_host.PlatformStatusLine))
            body += "\n\n" + _host.PlatformStatusLine;

        var closeBtn = new Button
        {
            Content = Loc.T("Help.Close"),
            Classes = { "primary" },
            MinWidth = 88,
            Height = 34,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0)
        };
        DockPanel.SetDock(closeBtn, Dock.Bottom);

        var dlg = new Window
        {
            Title = Loc.T("About.Title"),
            Width = 440,
            Height = 420,
            MinWidth = 360,
            MinHeight = 280,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            Background = Background,
            FontFamily = FontFamily,
            FontSize = 13,
            Content = new DockPanel
            {
                Margin = new Thickness(20),
                LastChildFill = true,
                Children =
                {
                    closeBtn,
                    new ScrollViewer
                    {
                        HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                        Content = new TextBlock
                        {
                            Text = body,
                            TextWrapping = TextWrapping.Wrap,
                            LineHeight = 22
                        }
                    }
                }
            }
        };
        closeBtn.Click += (_, _) => dlg.Close();
        await dlg.ShowDialog(this);
    }

    private async void Help_Click(object? sender, RoutedEventArgs e)
    {
        var win = new HelpWindow(_host, ReloadAfterDiagnostics)
        {
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        await win.ShowDialog(this);
    }

    /// <summary>诊断修复后刷新布局槽位与连接状态显示（对齐 Win HelpWindow）。</summary>
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
        if (NavBasic.IsChecked == true) ShowPage(0);
        else if (NavNetwork.IsChecked == true) ShowPage(2);
        else if (NavShare.IsChecked == true) ShowPage(3);
        else if (NavSecurity.IsChecked == true) ShowPage(0);
        else if (NavAdvanced.IsChecked == true) ShowPage(1);
    }

    private void TopTab_Changed(object? sender, RoutedEventArgs e)
    {
        if (_syncingNav) return;
        if (TabBasic.IsChecked == true) ShowPage(0);
        else if (TabOptions.IsChecked == true) ShowPage(1);
        else if (TabNetwork.IsChecked == true) ShowPage(2);
        else if (TabTransfer.IsChecked == true) ShowPage(3);
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

            TabBasic.IsChecked = index == 0;
            TabOptions.IsChecked = index == 1;
            TabNetwork.IsChecked = index == 2;
            TabTransfer.IsChecked = index == 3;

            NavBasic.IsChecked = index is 0;
            NavAdvanced.IsChecked = index == 1;
            NavNetwork.IsChecked = index == 2;
            NavShare.IsChecked = index == 3;
            if (index == 0) NavSecurity.IsChecked = false;
        }
        finally
        {
            _syncingNav = false;
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
            var slot = _slots[i];
            slot.Name = string.IsNullOrWhiteSpace(name) ? "" : name;
            slot.Enabled = !string.IsNullOrWhiteSpace(name);
            slot.IsLocal = string.Equals(slot.Name, LocalPeerIdentity.MachineName, StringComparison.OrdinalIgnoreCase);
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
        StatusText.Text = $"已移除 {name}，可在局域网自动发现中再添加";
        _ = RestartAfterSlotChangeAsync();
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
            StatusText.Text = "没有空的机器槽位，请先移除一台电脑。";
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

    private void SyncSlotLocalFlags()
    {
        foreach (var slot in _slots)
        {
            slot.IsLocal = string.Equals(slot.Name, LocalPeerIdentity.MachineName, StringComparison.OrdinalIgnoreCase);
            if (slot.IsLocal) slot.RefreshStatus();
        }
    }

    private async Task RestartAfterSlotChangeAsync()
    {
        try
        {
            await _host.BroadcastLayoutSyncAsync().ConfigureAwait(true);
            await _host.RestartNetworkingAsync().ConfigureAwait(true);
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

    private void ClearTransfers_Click(object? sender, RoutedEventArgs e)
    {
        _host.Transfers.ClearCompleted();
        TransferPlaceholder.IsVisible = _host.TransferItems.Count == 0;
    }

    private void OpenInbox_Click(object? sender, RoutedEventArgs e)
    {
        var inbox = _host.Transfers.InboxDirectory;
        Directory.CreateDirectory(inbox);
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = inbox,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            StatusText.Text = $"无法打开收件箱: {ex.Message}";
        }
    }
}
