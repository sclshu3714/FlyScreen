using System.Collections.ObjectModel;
using FlyScreen.AppMaui.Helpers;
using FlyScreen.AppMaui.Services;
using FlyScreen.Helpers.Localization;
using FlyScreen.Models;
using FlyScreen.Services;
using FlyScreen.Services.Network;
using FlyScreen.Services.Programs;
using FlyScreen.Services.Update;
using Microsoft.Maui.Controls.Shapes;

namespace FlyScreen.AppMaui;

public partial class MainPage : ContentPage
{
    private readonly MauiAppHost _host;
    private ObservableCollection<DiscoveredPeer> _discovery = [];
    private readonly ObservableCollection<MachineSlot> _slots = [];
    private int _pageIndex;
    private bool _initialPageChosen;
    private bool _twoRow = true;
    private MachineSlot? _selected;
    private IDispatcherTimer? _statusTimer;
    private const string DragSlotKey = "FlyScreen.MachineSlot";
    private readonly MauiUpdateChecker _helpUpdater = new();
    private UpdateCheckResult? _helpLastCheck;
    private bool _helpUpdateReady;
    private bool _helpFixReady;
    private bool _compactLayout;
    private bool _navOpen;
    private const double CompactWidthBreakpoint = 720;
    private string? _logProgramId;
    private string? _logProgramName;
    private bool _openProgramLog;
    private bool _progSelectAllUpdating;
    private UserProgram? _progMoreTarget;

    // 矩阵级拖拽：手势挂在 MachineMatrix 上，避免手指移出卡片后 Pan 被 Android 取消
    private bool _matrixInputWired;
    private MachineSlot? _matrixTouchSlot;
    private Border? _matrixTouchBorder;
    private Point _matrixTouchOrigin;
    private bool _matrixDragging;
    private bool _matrixMoved;
    private const double PanDragThreshold = 12;

    public MainPage()
    {
        InitializeComponent();
        _host = MauiAppHost.EnsureCreated();
        DiscoveryList.ItemsSource = _discovery;
        TransferList.ItemsSource = _host.Transfers.Items;
        TrayBtn.IsVisible = true;
        AutoStartCheck.IsEnabled = _host.Platform.SupportsAutoStart;
        Loaded += OnLoaded;
        SizeChanged += OnPageSizeChanged;
        Unloaded += (_, _) => _statusTimer?.Stop();
    }

    private async void OnLoaded(object? sender, EventArgs e)
    {
        Loc.SetCulture(_host.Settings.UiLanguage);
        MauiThemeService.Apply(_host.Settings.UiTheme, this);
        LoadUi();
        ApplyLocalizedUi();
        RebuildMatrix();
        PlatformLabel.Text = _host.PlatformBanner;
        _host.StatusMessage += msg => MainThread.BeginInvokeOnMainThread(() => StatusLabel.Text = msg);
        AppLog.Changed += () => MainThread.BeginInvokeOnMainThread(() =>
        {
            if (PageLog.IsVisible)
                RefreshLogPage();
        });
        _host.LayoutSyncedFromPeer += _ => MainThread.BeginInvokeOnMainThread(() =>
        {
            LoadUi();
            ApplyLocalizedUi();
            RebuildMatrix();
        });
        _host.BehaviorSyncedFromPeer += _ => MainThread.BeginInvokeOnMainThread(() =>
        {
            LoadUi();
            ApplyLocalizedUi();
        });
        _host.Connections.Discovery.PeersUpdated += _ => MainThread.BeginInvokeOnMainThread(RefreshDiscovery);
        _host.Connections.PeerLinkChanged += (_, _, _) => MainThread.BeginInvokeOnMainThread(RefreshSlotStatuses);

        try
        {
            await _host.StartAsync();
            StatusLabel.Text = $"监听 {_host.Connections.BindIp}:{_host.Settings.TcpPort}";
        }
        catch (System.Net.Sockets.SocketException ex)
        {
            StatusLabel.Text =
                $"网络启动失败（{ex.SocketErrorCode}）。端口可能被占用：请退出同机其它玄鸟，或更换 TCP/发现端口后点「应用」。";
        }
        catch (Exception ex)
        {
            StatusLabel.Text = $"网络启动失败：{ex.Message}";
        }

        RefreshNics();
        RefreshDiscovery();
        RefreshSlotStatuses();
        // 弹窗关闭可能再次触发 Loaded，不要强制回到「基础」
        if (!_initialPageChosen)
        {
            _initialPageChosen = true;
            ShowPage(0);
        }
        else
            ShowPage(_pageIndex);
        ApplyResponsiveLayout(Width);

        _statusTimer = Dispatcher.CreateTimer();
        _statusTimer.Interval = TimeSpan.FromSeconds(2);
        _statusTimer.Tick += (_, _) => RefreshSlotStatuses();
        _statusTimer.Start();
    }

    private void OnPageSizeChanged(object? sender, EventArgs e)
        => ApplyResponsiveLayout(Width);

    private void ApplyResponsiveLayout(double width)
    {
        if (width <= 0) return;

        // 手机 / 窄屏用抽屉；平板横屏与桌面用固定侧栏
        var compact = width < CompactWidthBreakpoint
                      || DeviceInfo.Idiom == DeviceIdiom.Phone;
        _compactLayout = compact;

        PhoneTopBar.IsVisible = compact;
        if (!compact)
            _navOpen = false;

        SidePane.WidthRequest = compact ? 260 : 168;
        SidePane.IsVisible = !compact || _navOpen;
        NavScrim.IsVisible = compact && _navOpen;

        // 宽屏：主区左侧让出固定侧栏；窄屏主区全宽
        MainPane.Margin = compact ? new Thickness(0) : new Thickness(168, 0, 0, 0);
        MainPane.Padding = compact ? new Thickness(12) : new Thickness(16);

        ApplyLayoutActionsPlacement(compact);
        ApplyOptionsPlacement(compact);
        ApplyProgToolbarLabels(iconOnly: compact || width < 560);
        ApplyProgListColumns();
    }

    // 勾选 / 语言 / 大小 / 状态 / 操作 + 间距；名称列吃剩余（按列表容器实际宽度）
    private const double ProgWCheck = 36;
    private const double ProgWLang = 100;
    private const double ProgWSize = 70;
    private const double ProgWStatus = 72;
    private const double ProgWActions = 72;
    private const double ProgWSpacing = 20;
    private const double ProgWNameMin = 80;

    private void ProgListHost_SizeChanged(object? sender, EventArgs e)
        => ApplyProgListColumns();

    /// <summary>严格按 ProgListHost.Width 分配 6 列绝对宽度，状态/操作始终可见。</summary>
    private void ApplyProgListColumns()
    {
        if (ProgListHost == null || ProgListHeader == null) return;
        var hostW = ProgListHost.Width;
        if (hostW < 40)
        {
            // 首次布局尚未就绪时用主区估算，仍扣除侧栏
            var pageW = Width > 0 ? Width : 800;
            hostW = Math.Max(280, pageW - (_compactLayout ? 48 : 168 + 48));
        }

        var fixedOther = ProgWCheck + ProgWLang + ProgWSize + ProgWStatus + ProgWActions + ProgWSpacing;
        var nameW = Math.Max(ProgWNameMin, hostW - fixedOther);
        // 极窄时压缩语言列，优先保住操作列
        var langW = ProgWLang;
        if (nameW + fixedOther > hostW + 0.5)
        {
            langW = 72;
            fixedOther = ProgWCheck + langW + ProgWSize + ProgWStatus + ProgWActions + ProgWSpacing;
            nameW = Math.Max(64, hostW - fixedOther);
        }

        var widths = new[] { ProgWCheck, nameW, langW, ProgWSize, ProgWStatus, ProgWActions };
        ProgListRowView.SharedColWidths = widths;
        ProgListHeader.ColumnDefinitions = new ColumnDefinitionCollection
        {
            new(new GridLength(widths[0])),
            new(new GridLength(widths[1])),
            new(new GridLength(widths[2])),
            new(new GridLength(widths[3])),
            new(new GridLength(widths[4])),
            new(new GridLength(widths[5])),
        };

        // 已生成的行同步列宽
        foreach (var row in FindProgRows(ProgList))
            row.ApplySharedWidths();
    }

    private static IEnumerable<ProgListRowView> FindProgRows(IView? root)
    {
        if (root is ProgListRowView row)
        {
            yield return row;
            yield break;
        }
        if (root is not IVisualTreeElement vte) yield break;
        foreach (var child in vte.GetVisualChildren())
        {
            if (child is not IView childView) continue;
            foreach (var r in FindProgRows(childView))
                yield return r;
        }
    }

    internal async Task ProgRowStartAsync(UserProgram prog)
        => await ProgRowToggleAsync(prog);

    /// <summary>行内启停切换：未运行→启动，运行中→停止；图标随状态更新。</summary>
    internal async Task ProgRowToggleAsync(UserProgram prog)
    {
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
            await DisplayAlertAsync(stop ? Loc.T("Prog.ActStop") : Loc.T("Prog.ActStart"), ex.Message, "OK");
        }
        RefreshProgramsPage();
    }

    /// <summary>在 ⋮ 按钮旁弹出锚定菜单（非全屏 Modal）。</summary>
    internal void ProgRowMoreAsync(UserProgram prog, View anchor)
    {
        _progMoreTarget = prog;
        ProgMoreEditBtn.Text = $"✎  {Loc.T("Prog.ActEdit")}";
        ProgMoreLogBtn.Text = $"☰  {Loc.T("Prog.ActLog")}";
        ProgMoreShareBtn.Text = $"⇪  {Loc.T("Prog.ActShare")}";
        ProgMoreDeleteBtn.Text = $"✕  {Loc.T("Prog.ActDelete")}";

        // 先显示以便测量，再按锚点定位
        ProgMoreOverlay.IsVisible = true;
        ProgMoreFlyout.TranslationX = 0;
        ProgMoreFlyout.TranslationY = 0;
        Dispatcher.Dispatch(() => PositionProgMoreFlyout(anchor));
    }

    private void PositionProgMoreFlyout(View anchor)
    {
        try
        {
            var pagePos = GetOffsetToPage(anchor);
            var menuW = ProgMoreFlyout.Width > 1 ? ProgMoreFlyout.Width : 168;
            var menuH = ProgMoreFlyout.Height > 1 ? ProgMoreFlyout.Height : 168;
            var pageW = Width > 1 ? Width : 800;
            var pageH = Height > 1 ? Height : 600;

            // 默认：菜单右对齐锚点右缘，出现在按钮下方
            var x = pagePos.X + anchor.Width - menuW;
            var y = pagePos.Y + anchor.Height + 4;
            if (x < 8) x = 8;
            if (x + menuW > pageW - 8) x = Math.Max(8, pageW - menuW - 8);
            if (y + menuH > pageH - 8)
                y = Math.Max(8, pagePos.Y - menuH - 4); // 下方空间不够则翻到上方

            ProgMoreFlyout.TranslationX = x;
            ProgMoreFlyout.TranslationY = y;
        }
        catch
        {
            ProgMoreFlyout.TranslationX = 24;
            ProgMoreFlyout.TranslationY = 120;
        }
    }

    private Point GetOffsetToPage(VisualElement element)
    {
#if WINDOWS
        try
        {
            // 相对 ProgMoreOverlay 定位，Translation 才与菜单父容器对齐
            if (element.Handler?.PlatformView is Microsoft.UI.Xaml.FrameworkElement fe
                && ProgMoreOverlay.Handler?.PlatformView is Microsoft.UI.Xaml.FrameworkElement overlay)
            {
                var t = fe.TransformToVisual(overlay);
                var p = t.TransformPoint(new Windows.Foundation.Point(0, 0));
                return new Point(p.X, p.Y);
            }
        }
        catch { /* fall through */ }
#endif
        double x = 0, y = 0;
        VisualElement? cur = element;
        while (cur != null && !ReferenceEquals(cur, ProgMoreOverlay) && cur is not Page)
        {
            x += cur.X;
            y += cur.Y;
            if (cur.Parent is ScrollView sv)
            {
                x -= sv.ScrollX;
                y -= sv.ScrollY;
            }
            cur = cur.Parent as VisualElement;
        }
        return new Point(x, y);
    }

    private void HideProgMoreFlyout()
    {
        ProgMoreOverlay.IsVisible = false;
        _progMoreTarget = null;
        ProgMoreFlyout.TranslationX = 0;
        ProgMoreFlyout.TranslationY = 0;
    }

    private void ProgMoreOverlay_Tapped(object? sender, TappedEventArgs e)
        => HideProgMoreFlyout();

    private async void ProgMoreEdit_Clicked(object? sender, EventArgs e)
    {
        var prog = _progMoreTarget;
        HideProgMoreFlyout();
        if (prog != null) await ProgRowEditAsync(prog);
    }

    private void ProgMoreLog_Clicked(object? sender, EventArgs e)
    {
        var prog = _progMoreTarget;
        HideProgMoreFlyout();
        if (prog != null) ProgRowLog(prog);
    }

    private async void ProgMoreShare_Clicked(object? sender, EventArgs e)
    {
        var prog = _progMoreTarget;
        HideProgMoreFlyout();
        if (prog != null) await ProgRowShareAsync(prog);
    }

    private async void ProgMoreDelete_Clicked(object? sender, EventArgs e)
    {
        var prog = _progMoreTarget;
        HideProgMoreFlyout();
        if (prog != null) await ProgRowDeleteAsync(prog);
    }

    private void ApplyOptionsPlacement(bool compact)
    {
        if (OptionsBottomGrid == null || HotCard == null || SysCard == null || HotKeysGrid == null)
            return;

        if (compact)
        {
            // 快捷键 / 系统设置纵向堆叠，避免并排挤压
            OptionsBottomGrid.ColumnDefinitions = new ColumnDefinitionCollection
            {
                new(GridLength.Star)
            };
            Grid.SetRow(HotCard, 0);
            Grid.SetColumn(HotCard, 0);
            Grid.SetColumnSpan(HotCard, 1);
            Grid.SetRow(SysCard, 1);
            Grid.SetColumn(SysCard, 0);
            Grid.SetColumnSpan(SysCard, 1);

            // 快捷键输入改为单列：标签 + 输入
            HotKeysGrid.ColumnDefinitions = new ColumnDefinitionCollection
            {
                new(GridLength.Star),
                new(68)
            };
            Grid.SetRow(HotSettingsLabel, 0);
            Grid.SetColumn(HotSettingsLabel, 0);
            Grid.SetRow(HotkeySettingsEntry, 0);
            Grid.SetColumn(HotkeySettingsEntry, 1);

            Grid.SetRow(HotReconnectLabel, 1);
            Grid.SetColumn(HotReconnectLabel, 0);
            Grid.SetRow(HotkeyReconnectEntry, 1);
            Grid.SetColumn(HotkeyReconnectEntry, 1);

            Grid.SetRow(HotExitLabel, 2);
            Grid.SetColumn(HotExitLabel, 0);
            Grid.SetRow(HotkeyExitEntry, 2);
            Grid.SetColumn(HotkeyExitEntry, 1);

            Grid.SetRow(HotLockLabel, 3);
            Grid.SetColumn(HotLockLabel, 0);
            Grid.SetRow(HotkeyLockEntry, 3);
            Grid.SetColumn(HotkeyLockEntry, 1);

            if (OptionsFeatGrid != null && FeatColumn != null && BehColumn != null && EdgeCrossColumn != null)
            {
                OptionsFeatGrid.ColumnDefinitions = new ColumnDefinitionCollection
                {
                    new(GridLength.Star)
                };
                OptionsFeatGrid.RowDefinitions = new RowDefinitionCollection
                {
                    new(GridLength.Auto),
                    new(GridLength.Auto),
                    new(GridLength.Auto)
                };
                OptionsFeatGrid.RowSpacing = 12;
                OptionsFeatGrid.ColumnSpacing = 0;
                Grid.SetRow(FeatColumn, 0);
                Grid.SetColumn(FeatColumn, 0);
                Grid.SetRow(BehColumn, 1);
                Grid.SetColumn(BehColumn, 0);
                Grid.SetRow(EdgeCrossColumn, 2);
                Grid.SetColumn(EdgeCrossColumn, 0);
            }
        }
        else
        {
            OptionsBottomGrid.ColumnDefinitions = new ColumnDefinitionCollection
            {
                new(GridLength.Star),
                new(new GridLength(12)),
                new(240)
            };
            Grid.SetRow(HotCard, 0);
            Grid.SetColumn(HotCard, 0);
            Grid.SetColumnSpan(HotCard, 1);
            Grid.SetRow(SysCard, 0);
            Grid.SetColumn(SysCard, 2);
            Grid.SetColumnSpan(SysCard, 1);

            HotKeysGrid.ColumnDefinitions = new ColumnDefinitionCollection
            {
                new(GridLength.Auto),
                new(68),
                new(new GridLength(12)),
                new(GridLength.Auto),
                new(68)
            };
            Grid.SetRow(HotSettingsLabel, 0);
            Grid.SetColumn(HotSettingsLabel, 0);
            Grid.SetRow(HotkeySettingsEntry, 0);
            Grid.SetColumn(HotkeySettingsEntry, 1);
            Grid.SetRow(HotReconnectLabel, 0);
            Grid.SetColumn(HotReconnectLabel, 3);
            Grid.SetRow(HotkeyReconnectEntry, 0);
            Grid.SetColumn(HotkeyReconnectEntry, 4);
            Grid.SetRow(HotExitLabel, 1);
            Grid.SetColumn(HotExitLabel, 0);
            Grid.SetRow(HotkeyExitEntry, 1);
            Grid.SetColumn(HotkeyExitEntry, 1);
            Grid.SetRow(HotLockLabel, 1);
            Grid.SetColumn(HotLockLabel, 3);
            Grid.SetRow(HotkeyLockEntry, 1);
            Grid.SetColumn(HotkeyLockEntry, 4);

            if (OptionsFeatGrid != null && FeatColumn != null && BehColumn != null && EdgeCrossColumn != null)
            {
                OptionsFeatGrid.ColumnDefinitions = new ColumnDefinitionCollection
                {
                    new(GridLength.Star),
                    new(GridLength.Star),
                    new(GridLength.Star)
                };
                OptionsFeatGrid.RowDefinitions = new RowDefinitionCollection
                {
                    new(GridLength.Auto)
                };
                OptionsFeatGrid.RowSpacing = 0;
                OptionsFeatGrid.ColumnSpacing = 12;
                Grid.SetRow(FeatColumn, 0);
                Grid.SetColumn(FeatColumn, 0);
                Grid.SetRow(BehColumn, 0);
                Grid.SetColumn(BehColumn, 1);
                Grid.SetRow(EdgeCrossColumn, 0);
                Grid.SetColumn(EdgeCrossColumn, 2);
            }
        }
    }

    /// <summary>
    /// 工具栏固定在卡片上方；窄屏只显示图标，宽屏显示图标+文字。
    /// </summary>
    private void ApplyLayoutActionsPlacement(bool compact)
    {
        if (LayoutActions == null || MatrixBorder == null) return;

        Grid.SetRow(LayoutActions, 2);
        Grid.SetRow(MatrixBorder, 3);
        LayoutActions.Margin = new Thickness(0, 0, 0, 10);
        LayoutActions.HorizontalOptions = LayoutOptions.Fill;
        LayoutActions.ClearValue(WidthRequestProperty);

        // 窄宽或横屏矮屏：隐藏提示文字、按钮只留符号
        var narrow = compact || Width < 520 || (Height > 0 && Height < 420);
        LayoutDragTipText.IsVisible = !narrow;
        ApplyLayoutToolbarLabels(iconOnly: narrow);
    }

    private void ApplyLayoutToolbarLabels(bool iconOnly)
    {
        if (AddMachineBtn == null) return;
        AddMachineBtn.Text = Loc.T(iconOnly ? "Layout.AddShort" : "Layout.AddTool");
        RemoveMachineBtn.Text = Loc.T(iconOnly ? "Layout.RemoveShort" : "Layout.RemoveTool");
        ReconnectBtn.Text = Loc.T(iconOnly ? "Layout.ReconnectShort" : "Layout.ReconnectTool");
        AddMachineBtn.Padding = iconOnly ? new Thickness(8, 0) : new Thickness(10, 0);
        RemoveMachineBtn.Padding = iconOnly ? new Thickness(8, 0) : new Thickness(10, 0);
        ReconnectBtn.Padding = iconOnly ? new Thickness(8, 0) : new Thickness(10, 0);
    }

    // 与文字同行的符号，避免 Path 叠字导致垂直错位
    private const string ProgIcAdd = "＋";
    private const string ProgIcDelete = "✕";
    private const string ProgIcStart = "▶";
    private const string ProgIcStop = "■";

    /// <summary>程序页工具栏：宽屏符号+文字，窄屏只留符号（垂直天然居中）。</summary>
    private void ApplyProgToolbarLabels(bool iconOnly)
    {
        if (ProgAddBtn == null) return;
        ProgAddBtn.Text = iconOnly ? ProgIcAdd : $"{ProgIcAdd}  {Loc.T("Prog.Add")}";
        ProgDeleteBtn.Text = iconOnly ? ProgIcDelete : $"{ProgIcDelete}  {Loc.T("Prog.Delete")}";
        ProgStartBtn.Text = iconOnly ? ProgIcStart : $"{ProgIcStart}  {Loc.T("Prog.Start")}";
        ProgStopBtn.Text = iconOnly ? ProgIcStop : $"{ProgIcStop}  {Loc.T("Prog.Stop")}";
        var pad = iconOnly ? new Thickness(8, 0) : new Thickness(10, 0);
        ProgAddBtn.Padding = pad;
        ProgDeleteBtn.Padding = pad;
        ProgStartBtn.Padding = pad;
        ProgStopBtn.Padding = pad;
    }

    private void MenuBtn_Clicked(object? sender, EventArgs e)
    {
        _navOpen = !_navOpen;
        ApplyResponsiveLayout(Width);
    }

    private void NavScrim_Tapped(object? sender, TappedEventArgs e)
    {
        _navOpen = false;
        ApplyResponsiveLayout(Width);
    }

    private void ApplyNavButtonTexts()
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
        LogErrorsOnlyLabel.Text = Loc.T("Log.ErrorsOnly");
        LogClearBtn.Text = Loc.T("Log.Clear");
        ProgTitleText.Text = Loc.T("Prog.Title");
        ProgHintText.Text = Loc.T("Prog.Hint");
        ToolTipProperties.SetText(ProgAddBtn, Loc.T("Prog.Add"));
        ToolTipProperties.SetText(ProgDeleteBtn, Loc.T("Prog.Delete"));
        ToolTipProperties.SetText(ProgStartBtn, Loc.T("Prog.Start"));
        ToolTipProperties.SetText(ProgStopBtn, Loc.T("Prog.Stop"));
        ProgColName.Text = Loc.T("Prog.ColName");
        ProgColLang.Text = Loc.T("Prog.ColLang");
        ProgColSize.Text = Loc.T("Prog.ColSize");
        ProgColStatus.Text = Loc.T("Prog.ColStatus");
        ProgColActions.Text = Loc.T("Prog.ColActions");
        ApplyProgToolbarLabels(_compactLayout || Width > 0 && Width < 560);
        HelpBtnText.Text = Loc.T("Btn.Help").Replace("？ ", "").Replace("? ", "");
    }

    private void LoadUi()
    {
        var s = _host.Settings;
        s.EnsureLocalMachineSlot();
        KeyEntry.Text = s.SecurityKey;
        KeyEntry.IsPassword = true;
        ShowKeyCheck.IsChecked = false;
        _twoRow = s.TwoRow;
        ApplyLayoutModeButtons();

        WrapMouseCheck.IsChecked = s.WrapMouse;
        ShareClipboardCheck.IsChecked = s.ShareClipboard;
        TransferFileCheck.IsChecked = s.TransferFile;
        HideMouseCheck.IsChecked = s.HideMouseAtEdge;
        DrawCursorCheck.IsChecked = s.DrawMouseCursor;
        EasyMouseCheck.IsChecked = s.EasyMouse;
        LoadEdgeCrossModeUi(s.EdgeCrossMode);
        SameSubnetCheck.IsChecked = s.SameSubnetOnly;
        ShowStatusCheck.IsChecked = s.ShowStatusMessages;
        AutoStartCheck.IsChecked = _host.Platform.SupportsAutoStart
            ? _host.Platform.GetAutoStartEnabled()
            : s.AutoStart;
        BlockSaverCheck.IsChecked = s.BlockScreenSaver;
        RelativeMouseCheck.IsChecked = s.MoveMouseRelatively;
        BlockCornersCheck.IsChecked = s.BlockMouseAtCorners;
        HotkeySettingsEntry.Text = s.HotkeySettings;
        HotkeyReconnectEntry.Text = s.HotkeyReconnect;
        HotkeyExitEntry.Text = s.HotkeyExit;
        HotkeyLockEntry.Text = s.HotkeyLock;
        HotF.IsChecked = s.SwitchHotkeyMode == 0;
        HotNum.IsChecked = s.SwitchHotkeyMode == 1;
        HotOff.IsChecked = s.SwitchHotkeyMode == 2;
        ThemeDark.IsChecked = string.Equals(s.UiTheme, "Dark", StringComparison.OrdinalIgnoreCase);
        ThemeLight.IsChecked = !ThemeDark.IsChecked;

        LanguagePicker.ItemsSource = Loc.Languages.ToList();
        LanguagePicker.SelectedItem = Loc.Languages.FirstOrDefault(l =>
            string.Equals(l.Code, s.UiLanguage, StringComparison.OrdinalIgnoreCase)) ?? Loc.Languages[0];

        TcpPortEntry.Text = s.TcpPort.ToString();
        UdpPortEntry.Text = s.UdpPort.ToString();

        SyncSlotsFromSettings();
    }

    private void ApplyLocalizedUi()
    {
        if (Application.Current?.Resources is { } rd)
        {
            rd["Str.Add"] = Loc.T("Btn.Add");
            rd["Str.Delete"] = Loc.T("Btn.Delete");
            rd["Str.DeleteTip"] = Loc.T("Tip.DeleteTransfer");
        }


        Title = Loc.T("App.Name");
        PhoneTitleLabel.Text = Loc.T("App.Name");
        SearchEntry.Placeholder = Loc.T("Search.Placeholder") + "…";

        ApplyNavButtonTexts();
        HelpDocBtn.Text = Loc.T("Help.ShowDoc");
        RefreshHelpToolbarLabels();
        RefreshAboutPage();
        if (PageHelp.IsVisible)
            ShowHelpDocument();
        ApplyBtn.Text = Loc.T("Btn.Apply");
        ExitBtn.Text = Loc.T("Btn.Exit");
        TrayBtn.Text = _host.Platform.SupportsTray ? Loc.T("Btn.HideTray") : "后台运行";

        SecurityTitleText.Text = Loc.T("Sec.Title");
        ShowKeyLabel.Text = Loc.T("Sec.Show");
        NewKeyBtn.Text = Loc.T("Sec.NewKey");
        SecurityHintText.Text = Loc.T("Sec.Hint");
        LayoutTitleText.Text = Loc.T("Layout.Title");
        LayoutHintText.Text = Loc.T("Layout.Hint");
        LayoutDragTipText.Text = Loc.T("Layout.DragTip");
        ApplyLayoutActionsPlacement(_compactLayout);

        FeatTitleText.Text = Loc.T("Feat.Title");
        BehTitleText.Text = Loc.T("Beh.Title");
        WrapMouseLabel.Text = Loc.T("Opt.WrapMouse");
        ShareClipboardLabel.Text = Loc.T("Opt.ShareClipboard");
        TransferFileLabel.Text = Loc.T("Opt.TransferFile");
        HideMouseLabel.Text = Loc.T("Opt.HideMouse");
        DrawCursorLabel.Text = Loc.T("Opt.DrawCursor");
        SameSubnetLabel.Text = Loc.T("Opt.SameSubnet");
        ShowStatusLabel.Text = Loc.T("Opt.ShowStatus");
        AutoStartLabel.Text = Loc.T("Opt.AutoStart");
        BlockSaverLabel.Text = Loc.T("Opt.BlockSaver");
        RelativeMouseLabel.Text = Loc.T("Opt.MoveRel");
        BlockCornersLabel.Text = Loc.T("Opt.BlockCorner");
        EasyMouseLabel.Text = Loc.T("Opt.EasyMouse");
        EdgeCrossTitleText.Text = Loc.T("Opt.EdgeCrossMode");
        EdgeCrossBarrierRadio.Content = Loc.T("Opt.EdgeCross.BarrierFree");
        EdgeCrossDoubleRadio.Content = Loc.T("Opt.EdgeCross.DoubleTap");
        EdgeCrossSpeedRadio.Content = Loc.T("Opt.EdgeCross.SpeedBurst");
        RefreshEdgeCrossTip();

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

        NicTitleText.Text = Loc.T("Net.NicTitle");
        NicHintText.Text = Loc.T("Net.NicHint");
        RefreshNicsBtn.Text = Loc.T("Net.RefreshNics");
        TcpPortLabel.Text = Loc.T("Net.TcpPort");
        UdpPortLabel.Text = Loc.T("Net.UdpPort");
        DiscoveryTitleText.Text = Loc.T("Net.DiscoveryTitle");
        DiscoveryHintText.Text = Loc.T("Net.DiscoveryHint");
        RefreshDiscoveryBtn.Text = Loc.T("Net.RefreshDiscovery");

        XferTitleText.Text = Loc.T("Xfer.Title");
        XferHintText.Text = Loc.T("Xfer.Hint");
        DirectTransferBtn.Text = Loc.T("Xfer.Direct");
        OpenInboxBtn.Text = Loc.T("Xfer.OpenInbox");
        ClearTransfersBtn.Text = Loc.T("Xfer.ClearDone");
        ColDir.Text = Loc.T("Xfer.Col.Dir");
        ColName.Text = Loc.T("Xfer.Col.Name");
        ColPeer.Text = Loc.T("Xfer.Col.Peer");
        ColSize.Text = Loc.T("Xfer.Col.Size");
        ColStatus.Text = Loc.T("Xfer.Col.Status");
        XferEmptyText.Text = Loc.Culture.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
            ? "暂无传输任务。"
            : "No transfers yet.";

        foreach (var item in _host.Transfers.Items)
            item.RefreshLocalized();

        foreach (var slot in _slots)
            slot.RefreshStatus();

        if (Window != null)
            Window.Title = Loc.T("App.Name");
    }

    private string ReadSelectedLanguage()
    {
        if (LanguagePicker.SelectedItem is LanguageOption opt)
            return opt.Code;
        if (LanguagePicker.ItemsSource is System.Collections.IList list
            && LanguagePicker.SelectedIndex >= 0
            && LanguagePicker.SelectedIndex < list.Count
            && list[LanguagePicker.SelectedIndex] is LanguageOption byIndex)
            return byIndex.Code;
        return string.IsNullOrWhiteSpace(_host.Settings.UiLanguage)
            ? Loc.DefaultCulture
            : _host.Settings.UiLanguage;
    }

    private void SyncSlotsFromSettings()
    {
        var s = _host.Settings;
        s.EnsureLocalMachineSlot();
        var local = LocalPeerIdentity.MachineName;
        while (s.MachineSlots.Count < 4) s.MachineSlots.Add("");

        _slots.Clear();
        for (var i = 0; i < 4; i++)
        {
            var name = s.MachineSlots[i]?.Trim() ?? "";
            var env = Environment.MachineName;
            var envOk = !LocalPeerIdentity.IsBogusMachineName(env);
            var slot = new MachineSlot
            {
                Index = i,
                Name = name,
                IsLocal = string.Equals(name, local, StringComparison.OrdinalIgnoreCase)
                          || (envOk && string.Equals(name, env, StringComparison.OrdinalIgnoreCase)),
                Enabled = !string.IsNullOrWhiteSpace(name)
            };
            slot.SetLayoutHint(_twoRow);
            slot.RefreshStatus();
            _slots.Add(slot);
        }

        _selected = null;
    }

    private void RebuildMatrix()
    {
        // 与 Win UniformGrid 一致：1×4 或 2×2，卡片拉伸填满
        MachineMatrix.IsClippedToBounds = false;
        // 透明底也要能命中触摸（子卡片 InputTransparent 时事件落到矩阵）
        MachineMatrix.BackgroundColor = Colors.Transparent;
        MachineMatrix.Children.Clear();
        MachineMatrix.RowDefinitions.Clear();
        MachineMatrix.ColumnDefinitions.Clear();

        var rows = _twoRow ? 2 : 1;
        var cols = _twoRow ? 2 : 4;
        for (var r = 0; r < rows; r++)
            MachineMatrix.RowDefinitions.Add(new RowDefinition(GridLength.Star));
        for (var c = 0; c < cols; c++)
            MachineMatrix.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));

        for (var i = 0; i < _slots.Count; i++)
        {
            var slot = _slots[i];
            var border = CreateSlotCard(slot);
            // 非 Win 原生拖放：卡片透明给矩阵，由矩阵级触摸完成拖拽（否则移出卡片边界就断）
            if (!SupportsNativeDragDrop)
                border.InputTransparent = true;
            Grid.SetRow(border, i / cols);
            Grid.SetColumn(border, i % cols);
            MachineMatrix.Children.Add(border);
        }

        EnsureMatrixDragInput();
    }

    private Border CreateSlotCard(MachineSlot slot)
    {
        var selected = slot.IsLocal || slot.IsSelected;
        // 与 Win 一致：默认 Margin=4；2×2 时 Margin=3；内边距由内容模板自身 Margin 承担
        var margin = _twoRow ? new Thickness(3) : new Thickness(4);
        var border = new Border
        {
            Margin = margin,
            Padding = 0,
            StrokeThickness = selected ? 2 : 1,
            Stroke = selected
                ? (Color)Application.Current!.Resources["Blue"]
                : (Color)Application.Current!.Resources["Line"],
            BackgroundColor = selected
                ? (Color)Application.Current!.Resources["BlueHover"]
                : slot.Enabled
                    ? (Color)Application.Current!.Resources["CardBg"]
                    : (Color)Application.Current!.Resources["MatrixBg"],
            StrokeShape = new RoundRectangle { CornerRadius = 8 },
            HorizontalOptions = LayoutOptions.Fill,
            VerticalOptions = LayoutOptions.Fill,
            Opacity = slot.Enabled || selected ? 1 : 0.9,
            BindingContext = slot
        };

        var statusColor = slot.IsLocal || slot.OutboundOk || slot.InboundOk
            ? (Color)Application.Current!.Resources["Success"]
            : (Color)Application.Current!.Resources["Muted"];

        View content;
        if (_twoRow)
        {
            // 对齐 Win MachineCard2x2：图标左 + 文字右
            var grid = new Grid
            {
                Margin = new Thickness(6, 4),
                ColumnDefinitions = new ColumnDefinitionCollection
                {
                    new(GridLength.Auto),
                    new(GridLength.Star)
                },
                ColumnSpacing = 10,
                VerticalOptions = LayoutOptions.Center
            };
            grid.Add(CreatePcIcon(), 0, 0);
            var text = new VerticalStackLayout { VerticalOptions = LayoutOptions.Center, Spacing = 3 };
            text.Children.Add(new Label
            {
                Text = slot.PositionLabel,
                FontSize = 11,
                TextColor = (Color)Application.Current!.Resources["Muted"],
                Margin = new Thickness(0, 0, 0, 1)
            });
            text.Children.Add(new Label
            {
                Text = slot.DisplayName,
                FontSize = 13,
                FontAttributes = FontAttributes.Bold,
                LineBreakMode = LineBreakMode.TailTruncation,
                HorizontalTextAlignment = TextAlignment.Start
            });
            text.Children.Add(new Label
            {
                Text = slot.DisplaySubtitle,
                FontSize = 11,
                FontAttributes = FontAttributes.Bold,
                TextColor = statusColor,
                HorizontalTextAlignment = TextAlignment.Start,
                Margin = new Thickness(0, 3, 0, 0)
            });
            grid.Add(text, 1, 0);
            content = grid;
        }
        else
        {
            // 对齐 Win MachineCard1x4：位置 / 图标 / 名称 / 状态 纵向居中
            var stack = new VerticalStackLayout
            {
                Margin = new Thickness(4),
                VerticalOptions = LayoutOptions.Center,
                HorizontalOptions = LayoutOptions.Center,
                Spacing = 0
            };
            stack.Children.Add(new Label
            {
                Text = slot.PositionLabel,
                FontSize = 11,
                TextColor = (Color)Application.Current!.Resources["Muted"],
                HorizontalTextAlignment = TextAlignment.Center,
                Margin = new Thickness(0, 0, 0, 4)
            });
            var icon = CreatePcIcon();
            icon.Margin = new Thickness(0, 0, 0, 6);
            stack.Children.Add(icon);
            stack.Children.Add(new Label
            {
                Text = slot.DisplayName,
                FontSize = 12,
                FontAttributes = FontAttributes.Bold,
                LineBreakMode = LineBreakMode.TailTruncation,
                HorizontalTextAlignment = TextAlignment.Center
            });
            stack.Children.Add(new Label
            {
                Text = slot.DisplaySubtitle,
                FontSize = 11,
                FontAttributes = FontAttributes.Bold,
                TextColor = statusColor,
                HorizontalTextAlignment = TextAlignment.Center,
                Margin = new Thickness(0, 4, 0, 0)
            });
            content = stack;
        }

        border.Content = content;
        AttachSlotGestures(border, slot);
        return border;
    }

    /// <summary>WinUI/MacCatalyst：原生拖放。Android/iOS：矩阵级触摸拖拽。</summary>
    private static bool SupportsNativeDragDrop =>
        DeviceInfo.Platform == DevicePlatform.WinUI
        || DeviceInfo.Platform == DevicePlatform.MacCatalyst;

    private void AttachSlotGestures(Border border, MachineSlot slot)
    {
        if (!SupportsNativeDragDrop)
            return; // 触摸端由 EnsureMatrixDragInput 统一处理

        var tap = new TapGestureRecognizer();
        tap.Tapped += (_, _) => OnSlotTapped(slot);
        border.GestureRecognizers.Add(tap);

        var drag = new DragGestureRecognizer();
        drag.DragStarting += (_, e) =>
        {
            e.Data.Properties[DragSlotKey] = slot;
            SelectSlotOnly(slot);
        };
        border.GestureRecognizers.Add(drag);

        var drop = new DropGestureRecognizer { AllowDrop = true };
        drop.DragOver += (_, e) => e.AcceptedOperation = DataPackageOperation.Copy;
        drop.Drop += (_, e) =>
        {
            e.Handled = true;
            if (!e.Data.Properties.TryGetValue(DragSlotKey, out var obj) || obj is not MachineSlot src)
                return;
            if (ReferenceEquals(src, slot)) return;

            SwapSlotData(src, slot);
            SyncSlotLocalFlags();
            SelectSlotOnly(slot);
            RebuildMatrix();
            StatusLabel.Text = "已互换位置（请点「应用」同步）";
        };
        border.GestureRecognizers.Add(drop);
    }

    private void EnsureMatrixDragInput()
    {
        if (SupportsNativeDragDrop || _matrixInputWired || MachineMatrix == null)
            return;
        _matrixInputWired = true;

#if ANDROID
        MachineMatrix.HandlerChanged += (_, _) => WireAndroidMatrixTouch();
        WireAndroidMatrixTouch();
#else
        var pointer = new PointerGestureRecognizer();
        pointer.PointerPressed += (_, e) =>
        {
            var pos = e.GetPosition(MachineMatrix);
            if (pos.HasValue) MatrixTouchBegan(pos.Value);
        };
        pointer.PointerMoved += (_, e) =>
        {
            var pos = e.GetPosition(MachineMatrix);
            if (pos.HasValue) MatrixTouchMoved(pos.Value);
        };
        pointer.PointerReleased += (_, e) =>
        {
            var pos = e.GetPosition(MachineMatrix);
            MatrixTouchEnded(pos ?? _matrixTouchOrigin, cancelled: false);
        };
        MachineMatrix.GestureRecognizers.Add(pointer);
#endif
    }

#if ANDROID
    private void WireAndroidMatrixTouch()
    {
        if (MachineMatrix.Handler?.PlatformView is not Android.Views.View native)
            return;
        native.Touch -= OnAndroidMatrixTouch;
        native.Touch += OnAndroidMatrixTouch;
    }

    private void OnAndroidMatrixTouch(object? sender, Android.Views.View.TouchEventArgs e)
    {
        var ev = e.Event;
        if (ev == null || sender is not Android.Views.View native)
            return;

        // MotionEvent 为像素；MAUI Bounds 为 dp
        var density = DeviceDisplay.MainDisplayInfo.Density;
        if (density <= 0) density = 1;
        var pos = new Point(ev.GetX() / density, ev.GetY() / density);

        switch (ev.ActionMasked)
        {
            case Android.Views.MotionEventActions.Down:
                native.Parent?.RequestDisallowInterceptTouchEvent(true);
                MatrixTouchBegan(pos);
                e.Handled = true;
                break;
            case Android.Views.MotionEventActions.Move:
                MatrixTouchMoved(pos);
                e.Handled = true;
                break;
            case Android.Views.MotionEventActions.Up:
                native.Parent?.RequestDisallowInterceptTouchEvent(false);
                MatrixTouchEnded(pos, cancelled: false);
                e.Handled = true;
                break;
            case Android.Views.MotionEventActions.Cancel:
                native.Parent?.RequestDisallowInterceptTouchEvent(false);
                MatrixTouchEnded(pos, cancelled: true);
                e.Handled = true;
                break;
        }
    }
#endif

    private void MatrixTouchBegan(Point pos)
    {
        var border = HitTestBorderAt(pos);
        _matrixTouchBorder = border;
        _matrixTouchSlot = border?.BindingContext as MachineSlot;
        _matrixTouchOrigin = pos;
        _matrixDragging = false;
        _matrixMoved = false;
    }

    private void MatrixTouchMoved(Point pos)
    {
        if (_matrixTouchSlot == null || _matrixTouchBorder == null)
            return;

        var dx = pos.X - _matrixTouchOrigin.X;
        var dy = pos.Y - _matrixTouchOrigin.Y;
        if (!_matrixDragging && (Math.Abs(dx) >= PanDragThreshold || Math.Abs(dy) >= PanDragThreshold))
        {
            _matrixDragging = true;
            _matrixMoved = true;
            SelectSlotOnly(_matrixTouchSlot);
            _matrixTouchBorder.Opacity = 0.88;
            _matrixTouchBorder.ZIndex = 100;
            StatusLabel.Text = $"拖动 {_matrixTouchSlot.DisplayName}，放到目标格松开";
            try
            {
                if (Vibration.Default.IsSupported)
                    Vibration.Default.Vibrate(TimeSpan.FromMilliseconds(18));
            }
            catch { /* ignore */ }
        }

        if (_matrixDragging)
        {
            _matrixTouchBorder.TranslationX = dx;
            _matrixTouchBorder.TranslationY = dy;
        }
    }

    private void MatrixTouchEnded(Point pos, bool cancelled)
    {
        var border = _matrixTouchBorder;
        var slot = _matrixTouchSlot;
        var wasDragging = _matrixDragging;

        if (border != null)
        {
            border.TranslationX = 0;
            border.TranslationY = 0;
            border.Opacity = 1;
            border.ZIndex = 0;
        }

        _matrixTouchBorder = null;
        _matrixTouchSlot = null;
        _matrixDragging = false;

        if (slot == null)
        {
            _matrixMoved = false;
            return;
        }

        if (wasDragging && !cancelled)
        {
            var target = HitTestSlotAt(pos);
            _matrixMoved = false;
            if (target == null || ReferenceEquals(target, slot))
            {
                StatusLabel.Text = "未放到其它格子，已取消互换";
                SelectSlotOnly(slot);
                RebuildMatrix();
                return;
            }

            SwapSlotData(slot, target);
            SyncSlotLocalFlags();
            SelectSlotOnly(target);
            RebuildMatrix();
            StatusLabel.Text = "已互换位置（请点「应用」同步）";
            return;
        }

        // 未拖拽：视为点击 / 双击
        if (!_matrixMoved && !cancelled)
            OnSlotTapped(slot);
        _matrixMoved = false;
    }

    private Border? HitTestBorderAt(Point pos)
    {
        foreach (var child in MachineMatrix.Children.OfType<Border>())
        {
            // 布局 Bounds（不含 Translation），用于起点与落点命中
            var b = child.Bounds;
            if (pos.X >= b.Left && pos.X <= b.Right && pos.Y >= b.Top && pos.Y <= b.Bottom)
                return child;
        }

        return null;
    }

    private MachineSlot? HitTestSlotAt(Point pos)
        => HitTestBorderAt(pos)?.BindingContext as MachineSlot;

    private static void SwapSlotData(MachineSlot a, MachineSlot b)
    {
        (a.Name, b.Name) = (b.Name, a.Name);
        (a.Enabled, b.Enabled) = (b.Enabled, a.Enabled);
        (a.IsLocal, b.IsLocal) = (b.IsLocal, a.IsLocal);
        (a.ResolvedIp, b.ResolvedIp) = (b.ResolvedIp, a.ResolvedIp);
        (a.OutboundOk, b.OutboundOk) = (b.OutboundOk, a.OutboundOk);
        (a.InboundOk, b.InboundOk) = (b.InboundOk, a.InboundOk);
        a.RefreshStatus();
        b.RefreshStatus();
    }

    private void SyncSlotLocalFlags()
    {
        var local = LocalPeerIdentity.MachineName;
        var env = Environment.MachineName;
        var envOk = !LocalPeerIdentity.IsBogusMachineName(env);
        foreach (var s in _slots)
        {
            s.IsLocal = string.Equals(s.Name, local, StringComparison.OrdinalIgnoreCase)
                        || (envOk && string.Equals(s.Name, env, StringComparison.OrdinalIgnoreCase));
            s.Enabled = !s.IsEmpty;
            s.RefreshStatus();
        }
    }

    private void SelectSlotOnly(MachineSlot slot)
    {
        foreach (var s in _slots)
            s.IsSelected = ReferenceEquals(s, slot);
        _selected = slot;
    }

    /// <summary>简易 PC 图标，尺寸对齐 Win PcIconTemplate 40×34。</summary>
    private static View CreatePcIcon()
    {
        var screen = new Border
        {
            WidthRequest = 36,
            HeightRequest = 24,
            BackgroundColor = Color.FromArgb("#1F2937"),
            Stroke = Color.FromArgb("#111827"),
            StrokeThickness = 2,
            StrokeShape = new RoundRectangle { CornerRadius = 3 },
            HorizontalOptions = LayoutOptions.Center,
            Content = new Border
            {
                Margin = 3,
                BackgroundColor = Color.FromArgb("#60A5FA"),
                Opacity = 0.4,
                StrokeThickness = 0,
                StrokeShape = new RoundRectangle { CornerRadius = 2 }
            }
        };
        var stand = new BoxView
        {
            WidthRequest = 12,
            HeightRequest = 3,
            Color = Color.FromArgb("#6B7280"),
            HorizontalOptions = LayoutOptions.Center,
            CornerRadius = 1
        };
        return new VerticalStackLayout
        {
            WidthRequest = 40,
            HeightRequest = 34,
            Spacing = 0,
            HorizontalOptions = LayoutOptions.Center,
            Children = { screen, stand }
        };
    }

    private void OnSlotTapped(MachineSlot slot)
    {
        // 双击移除（对齐 Win）
        if (_selected == slot && slot.CanRemove
            && Environment.TickCount64 - _lastTapTicks < 400)
        {
            RemoveSlot(slot);
            _lastTapTicks = 0;
            return;
        }

        _lastTapTicks = Environment.TickCount64;
        SelectSlotOnly(slot);
        RebuildMatrix();
        StatusLabel.Text = slot.IsEmpty
            ? "已选空位，可添加电脑"
            : $"已选 {slot.DisplayName}（按住拖到其他格子互换）";
    }

    private long _lastTapTicks;

    private void RefreshSlotStatuses()
    {
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

        RebuildMatrix();
    }

    private void ApplyLayoutModeButtons()
    {
        try
        {
            var active = (Style)Application.Current!.Resources["SegBtnActive"];
            var normal = (Style)Application.Current!.Resources["SegBtn"];
            Layout1x4Btn.Style = _twoRow ? normal : active;
            Layout2x2Btn.Style = _twoRow ? active : normal;
        }
        catch { /* ignore */ }
    }

    private void ShowPage(int index)
    {
        _pageIndex = index;
        PageBasic.IsVisible = index == 0;
        PageOptions.IsVisible = index == 1;
        PageNetwork.IsVisible = index == 2;
        PageTransfer.IsVisible = index == 3;
        PageSecurity.IsVisible = index == 4;
        PageAbout.IsVisible = index == 5;
        PageHelp.IsVisible = index == 6;
        PageLog.IsVisible = index == 7;
        PagePrograms.IsVisible = index == 8;

        try
        {
            var normal = (Style)Application.Current!.Resources["SideNavBtn"];
            var active = (Style)Application.Current!.Resources["SideNavBtnActive"];
            var labelNormal = (Style)Application.Current!.Resources["SideNavLabel"];
            var labelActive = (Style)Application.Current!.Resources["SideNavLabelActive"];

            void SetNav(Button btn, Label label, bool on)
            {
                btn.Style = on ? active : normal;
                label.Style = on ? labelActive : labelNormal;
            }

            SetNav(NavBasic, NavBasicText, index == 0);
            SetNav(NavAdvanced, NavAdvancedText, index == 1);
            SetNav(NavNetwork, NavNetworkText, index == 2);
            SetNav(NavShare, NavShareText, index == 3);
            SetNav(NavSecurity, NavSecurityText, index == 4);
            SetNav(NavAbout, AboutBtnText, index == 5);
            SetNav(NavHelp, HelpBtnText, index == 6);
            SetNav(NavLog, NavLogText, index == 7);
            SetNav(NavPrograms, NavProgramsText, index == 8);
        }
        catch { /* ignore */ }

        // 首次显示高级页时 Android RadioButton 可能仍是 Material 深色底，显式刷一遍
        if (index == 1)
            RefreshOptionsRadioChrome();

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
            RefreshProgramsPage();

        // 窄屏选中菜单后自动收起抽屉
        if (_compactLayout && _navOpen)
        {
            _navOpen = false;
            ApplyResponsiveLayout(Width);
        }
    }

    private void Search_Changed(object? sender, TextChangedEventArgs e)
    {
        var q = (e.NewTextValue ?? "").Trim();
        if (string.IsNullOrEmpty(q)) return;
        if (q.Contains("传输") || q.Contains("文件") || q.Contains("共享")) ShowPage(3);
        else if (q.Contains("网络") || q.Contains("发现") || q.Contains("网卡") || q.Contains("端口")) ShowPage(2);
        else if (q.Contains("高级") || q.Contains("热键") || q.Contains("选项") || q.Contains("主题")) ShowPage(1);
        else if (q.Contains("密钥") || q.Contains("安全")) ShowPage(4);
        else if (q.Contains("程序") || q.Contains("脚本") || q.Contains("python")) ShowPage(8);
        else if (q.Contains("日志") || q.Contains("错误")) ShowPage(7);
        else if (q.Contains("关于") || q.Contains("版本")) ShowPage(5);
        else if (q.Contains("帮助") || q.Contains("更新") || q.Contains("诊断") || q.Contains("修复")) ShowPage(6);
        else if (q.Contains("布局") || q.Contains("基础")) ShowPage(0);
    }

    private void Nav_Clicked(object? sender, EventArgs e)
    {
        var key = (sender as Button)?.CommandParameter as string ?? "";
        switch (key)
        {
            case "basic": ShowPage(0); break;
            case "advanced": ShowPage(1); break;
            case "network": ShowPage(2); break;
            case "share": ShowPage(3); break;
            case "security": ShowPage(4); break;
            case "about": ShowPage(5); break;
            case "help": ShowPage(6); break;
            case "log": ShowPage(7); break;
            case "programs": ShowPage(8); break;
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
        Dispatcher.Dispatch(() => ApplyProgListColumns());
    }

    private IEnumerable<UserProgram> SelectedPrograms()
        => ProgramService.Instance.Catalog.Items.Where(p => p.IsSelected);

    private void ProgSelectAll_Changed(object? sender, CheckedChangedEventArgs e)
    {
        if (_progSelectAllUpdating) return;
        foreach (var p in ProgramService.Instance.Catalog.Items)
            p.IsSelected = e.Value;
    }

    private Task PushModalAsync(Page page)
        => Navigation.PushModalAsync(new NavigationPage(page));

    private async void ProgAdd_Clicked(object? sender, EventArgs e)
    {
        var page = new ProgramAddPage();
        await PushModalAsync(page);
        var ok = await page.Result;
        ShowPage(8);
        if (!ok) return;
        try
        {
            ProgramService.Instance.Catalog.Create(
                page.ProgramName, page.SelectedLanguage, page.NamespaceValue, page.FileNameValue);
            RefreshProgramsPage();
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync(Loc.T("Prog.Add"), ex.Message, "OK");
        }
    }

    private async void ProgDelete_Clicked(object? sender, EventArgs e)
    {
        var list = SelectedPrograms().ToList();
        if (list.Count == 0)
        {
            await DisplayAlertAsync(Loc.T("Prog.Delete"), Loc.T("Prog.SelectFirst"), "OK");
            return;
        }
        if (!await DisplayAlertAsync(Loc.T("Prog.Delete"), Loc.T("Prog.ConfirmDelete"), "OK", "Cancel"))
            return;
        await ProgramService.Instance.Runner.DeleteManyAsync(list);
        RefreshProgramsPage();
    }

    private async void ProgStart_Clicked(object? sender, EventArgs e)
    {
        var list = SelectedPrograms().ToList();
        if (list.Count == 0)
        {
            await DisplayAlertAsync(Loc.T("Prog.Start"), Loc.T("Prog.SelectFirst"), "OK");
            return;
        }
        await ProgramService.Instance.Runner.StartManyAsync(list);
        RefreshProgramsPage();
    }

    private async void ProgStop_Clicked(object? sender, EventArgs e)
    {
        var list = SelectedPrograms().ToList();
        if (list.Count == 0)
        {
            await DisplayAlertAsync(Loc.T("Prog.Stop"), Loc.T("Prog.SelectFirst"), "OK");
            return;
        }
        await ProgramService.Instance.Runner.StopManyAsync(list);
        RefreshProgramsPage();
    }

    private async void ProgRowStart_Clicked(object? sender, EventArgs e)
    {
        if ((sender as Button)?.CommandParameter is not UserProgram prog) return;
        await ProgRowToggleAsync(prog);
    }

    private void ProgRowMore_Clicked(object? sender, EventArgs e)
    {
        if (sender is not Button btn || btn.CommandParameter is not UserProgram prog) return;
        ProgRowMoreAsync(prog, btn);
    }

    private async void ProgRowEdit_Clicked(object? sender, EventArgs e)
    {
        if ((sender as Button)?.CommandParameter is not UserProgram prog) return;
        await ProgRowEditAsync(prog);
    }

    private async Task ProgRowEditAsync(UserProgram prog)
    {
        var page = new ProgramEditorPage(prog);
        await PushModalAsync(page);
        await page.WhenClosed;
        ShowPage(8);
        RefreshProgramsPage();
    }

    private async void ProgRowDelete_Clicked(object? sender, EventArgs e)
    {
        if ((sender as Button)?.CommandParameter is not UserProgram prog) return;
        await ProgRowDeleteAsync(prog);
    }

    private async Task ProgRowDeleteAsync(UserProgram prog)
    {
        if (!await DisplayAlertAsync(Loc.T("Prog.ActDelete"), Loc.T("Prog.ConfirmDelete"), "OK", "Cancel"))
            return;
        try { await ProgramService.Instance.Runner.DeleteAsync(prog); }
        catch (Exception ex) { await DisplayAlertAsync(Loc.T("Prog.ActDelete"), ex.Message, "OK"); }
        RefreshProgramsPage();
    }

    private void ProgRowLog_Clicked(object? sender, EventArgs e)
    {
        if ((sender as Button)?.CommandParameter is not UserProgram prog) return;
        ProgRowLog(prog);
    }

    private void ProgRowLog(UserProgram prog)
    {
        _logProgramId = prog.Id;
        _logProgramName = prog.Name;
        _openProgramLog = true;
        ShowPage(7);
    }

    private async void ProgRowShare_Clicked(object? sender, EventArgs e)
    {
        if ((sender as Button)?.CommandParameter is not UserProgram prog) return;
        await ProgRowShareAsync(prog);
    }

    private async Task ProgRowShareAsync(UserProgram prog)
    {
        var share = ProgramShareService.Current;
        if (share == null)
        {
            await DisplayAlertAsync(Loc.T("Prog.ActShare"), Loc.T("Prog.ShareNone"), "OK");
            return;
        }

        try
        {
            var result = await share.ShareAsync(prog);
            var text = result.Reachable == 0
                ? Loc.T("Prog.ShareNone")
                : string.Format(Loc.T("Prog.ShareSent"), result.Sent);
            await DisplayAlertAsync(Loc.T("Prog.ActShare"), text, "OK");
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync(Loc.T("Prog.ActShare"), ex.Message, "OK");
        }
    }

    private void RefreshLogHint()
    {
        LogHintText.Text = string.IsNullOrEmpty(_logProgramName)
            ? Loc.T("Log.Hint")
            : string.Format(Loc.T("Prog.LogFilter"), _logProgramName);
    }

    private void RefreshLogPage()
    {
        LogList.ItemsSource = AppLog.Snapshot(
            LogErrorsOnlyCheck.IsChecked,
            _logProgramId,
            _logProgramName);
    }

    private void LogFilter_Changed(object? sender, CheckedChangedEventArgs e)
    {
        if (PageLog.IsVisible)
            RefreshLogPage();
    }

    private void LogClear_Clicked(object? sender, EventArgs e) => AppLog.Clear();

    private void LayoutMode_Clicked(object? sender, EventArgs e)
    {
        _twoRow = ReferenceEquals(sender, Layout2x2Btn);
        foreach (var s in _slots) s.SetLayoutHint(_twoRow);
        ApplyLayoutModeButtons();
        RebuildMatrix();
    }

    private void ShowKey_Changed(object? sender, CheckedChangedEventArgs e)
        => KeyEntry.IsPassword = !e.Value;

    private void NewKey_Clicked(object? sender, EventArgs e)
    {
        KeyEntry.Text = AppSettings.GenerateKey();
        KeyEntry.IsPassword = ShowKeyCheck.IsChecked != true;
    }

    private async void Apply_Clicked(object? sender, EventArgs e)
    {
        _ = int.TryParse(TcpPortEntry.Text, out var tcp);
        _ = int.TryParse(UdpPortEntry.Text, out var udp);
        if (tcp <= 0) tcp = 15155;
        if (udp <= 0) udp = 15156;

        if (NicPicker.SelectedItem is NicInfo nic)
        {
            _host.Settings.PreferredNicId = nic.Id;
            _host.Settings.PreferredLocalIp = nic.IPv4;
        }

        var switchMode = HotF.IsChecked == true ? 0 : HotNum.IsChecked == true ? 1 : 2;
        var theme = ThemeDark.IsChecked == true ? MauiThemeService.Dark : MauiThemeService.Light;
        var lang = ReadSelectedLanguage();

        var prevSlots = _host.Settings.MachineSlots.ToList();
        var newSlots = _slots.Select(s => s.Name).ToList();
        var prevSet = new HashSet<string>(
            LayoutSlotMerge.NonEmptyNames(prevSlots), StringComparer.OrdinalIgnoreCase);
        var newSet = new HashSet<string>(
            LayoutSlotMerge.NonEmptyNames(newSlots), StringComparer.OrdinalIgnoreCase);
        var removedAny = prevSet.Any(n => !newSet.Contains(n));
        var layoutMerge = !removedAny && newSet.Count >= prevSet.Count;

        _host.ApplyFromUi(
            KeyEntry.Text ?? "",
            newSlots,
            _twoRow,
            tcp,
            udp,
            WrapMouseCheck.IsChecked == true,
            ShareClipboardCheck.IsChecked == true,
            TransferFileCheck.IsChecked == true,
            EasyMouseCheck.IsChecked == true,
            SameSubnetCheck.IsChecked == true,
            HideMouseCheck.IsChecked == true,
            DrawCursorCheck.IsChecked == true,
            ShowStatusCheck.IsChecked == true,
            AutoStartCheck.IsChecked == true,
            BlockSaverCheck.IsChecked == true,
            RelativeMouseCheck.IsChecked == true,
            BlockCornersCheck.IsChecked == true,
            switchMode,
            HotkeySettingsEntry.Text,
            HotkeyLockEntry.Text,
            HotkeyReconnectEntry.Text,
            HotkeyExitEntry.Text,
            theme,
            lang,
            ReadEdgeCrossModeUi());

        Loc.SetCulture(lang);
        MauiThemeService.Apply(theme, this);
        LoadUi();
        ApplyLocalizedUi();
        RebuildMatrix();
        if (layoutMerge)
            await _host.BroadcastLayoutMergeAsync();
        else
            await _host.BroadcastLayoutSyncAsync();
        await _host.BroadcastBehaviorSyncAsync();
        await _host.RestartNetworkingAsync();
        StatusLabel.Text = Loc.T("Sys.Applied");
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

    private void EdgeCrossMode_Changed(object? sender, CheckedChangedEventArgs e)
    {
        if (sender is RadioButton { IsChecked: not true }) return;
        RefreshEdgeCrossTip();
    }

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

    /// <summary>
    /// Android 上 RadioButton 若在父级首次可见前未落地主题色，会短暂/首屏显示 Material 深色底。
    /// 进入高级页时强制写回背景与文字色，并轻触 Content 触发原生重绘。
    /// </summary>
    private void RefreshOptionsRadioChrome()
    {
        var text = Application.Current?.Resources.TryGetValue("Text", out var tv) == true && tv is Color c
            ? c
            : Color.FromArgb("#111827");
        var bg = Color.FromArgb("#01000000");
        foreach (var rb in new RadioButton?[]
                 {
                     EdgeCrossBarrierRadio, EdgeCrossDoubleRadio, EdgeCrossSpeedRadio,
                     HotF, HotNum, HotOff, ThemeLight, ThemeDark
                 })
        {
            if (rb == null) continue;
            rb.BackgroundColor = bg;
            rb.TextColor = text;
            // 重新赋值 Content 可迫使 Android 重建标签层，去掉残留深色底
            if (rb.Content is string s)
                rb.Content = s;
        }
    }

    private async void Reconnect_Clicked(object? sender, EventArgs e)
    {
        await _host.Connections.ReconnectNowAsync();
        StatusLabel.Text = "正在重新连接…";
    }

    private void RefreshNics_Clicked(object? sender, EventArgs e) => RefreshNics();

    private void RefreshNics()
    {
        var nics = NicService.ListUsableIpv4Nics().ToList();
        NicPicker.ItemsSource = nics;
        NicPicker.ItemDisplayBinding = new Binding(nameof(NicInfo.ShortDisplay));
        var preferred = NicService.ResolvePreferred(_host.Settings.PreferredNicId, _host.Settings.PreferredLocalIp);
        NicPicker.SelectedItem = preferred != null
            ? nics.FirstOrDefault(n => n.Id == preferred.Id && n.IPv4 == preferred.IPv4) ?? nics.FirstOrDefault()
            : nics.FirstOrDefault();
    }

    private void RefreshDiscovery_Clicked(object? sender, EventArgs e) => RefreshDiscovery();

    private void RefreshDiscovery()
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            LocalPeerIdentity.MachineName,
            Environment.MachineName
        };
        foreach (var s in _slots)
            if (!string.IsNullOrWhiteSpace(s.Name)) used.Add(s.Name);

        var peers = _host.Connections.Discovery.GetPeers()
            .Where(p => !string.IsNullOrWhiteSpace(p.MachineName) && !used.Contains(p.MachineName))
            .ToList();

        // WinUI：对已绑定 CollectionView 的 ObservableCollection 调用 Clear() 会发 Reset，
        // 进而在 NotifyCollectionChanged 封送时 COM 崩溃；改为整体替换 ItemsSource。
        _discovery = new ObservableCollection<DiscoveredPeer>(peers);
        DiscoveryList.ItemsSource = _discovery;
    }

    private void AddDiscovered_Clicked(object? sender, EventArgs e)
    {
        if (sender is not Button { CommandParameter: DiscoveredPeer peer }) return;
        TryAddPeer(peer.MachineName);
    }

    private void DiscoveryItem_DoubleTapped(object? sender, TappedEventArgs e)
    {
        var peer = e.Parameter as DiscoveredPeer
                   ?? (sender as Element)?.BindingContext as DiscoveredPeer;
        if (peer == null) return;
        TryAddPeer(peer.MachineName);
    }

    private void AddPeer_Clicked(object? sender, EventArgs e)
    {
        RefreshDiscovery();
        if (_discovery.Count == 0)
        {
            ShowPage(2);
            StatusLabel.Text = "暂无新发现的电脑，请先刷新发现";
            return;
        }

        TryAddPeer(_discovery[0].MachineName);
    }

    private void RemovePeer_Clicked(object? sender, EventArgs e)
    {
        if (_selected == null || !_selected.CanRemove)
        {
            StatusLabel.Text = "请先选中一台可移除的对端电脑";
            return;
        }

        RemoveSlot(_selected);
    }

    private void RemoveSlot(MachineSlot slot)
    {
        var name = slot.Name;
        slot.Name = "";
        slot.IsLocal = false;
        slot.Enabled = false;
        slot.OutboundOk = false;
        slot.InboundOk = false;
        slot.RefreshStatus();
        _selected = null;
        PersistSlotsFromUi();
        if (!string.IsNullOrWhiteSpace(name))
            _host.Connections.ClearLayoutJoin(name);
        RebuildMatrix();
        RefreshDiscovery();
        StatusLabel.Text = string.IsNullOrWhiteSpace(name) ? "已移除" : $"已移除 {name}";
        if (!string.IsNullOrWhiteSpace(name))
            _ = RemovePeerAndSyncAsync(name);
    }

    private async Task RemovePeerAndSyncAsync(string name)
    {
        try
        {
            await _host.Connections.DropPeerAsync(name);
            await _host.BroadcastLayoutSyncAsync();
            await _host.Connections.PruneUnconfiguredLinksAsync();
        }
        catch
        {
            // ignore
        }
    }

    private void TryAddPeer(string name)
    {
        if (_slots.Any(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            StatusLabel.Text = $"{name} 已在布局中";
            return;
        }

        var empty = _slots.FirstOrDefault(s => s.IsEmpty);
        if (empty == null)
        {
            StatusLabel.Text = "没有空的机器槽位，请先移除一台电脑。";
            return;
        }

        empty.Name = name;
        empty.Enabled = true;
        empty.IsLocal = false;
        empty.RefreshStatus();
        PersistSlotsFromUi();
        _host.Connections.ArmLayoutJoin(name);
        RebuildMatrix();
        StatusLabel.Text = $"已添加 {name}，正在连接";
        RefreshDiscovery();
        ShowPage(0);
        _ = ConnectAddedPeerAsync();
    }

    private void PersistSlotsFromUi()
    {
        var slots = _slots.Select(s => s.Name?.Trim() ?? "").ToList();
        while (slots.Count < 4) slots.Add("");
        _host.Settings.MachineSlots = slots;
        _host.Settings.EnsureLocalMachineSlot();
        _host.Settings.Save();
    }

    private async Task ConnectAddedPeerAsync()
    {
        try
        {
            await _host.BroadcastLayoutMergeAsync();
            await _host.Connections.ReconnectNowAsync();
        }
        catch
        {
            // 密钥不一致时由连接层提示
        }
    }

    private async void DirectTransfer_Clicked(object? sender, EventArgs e)
    {
        var peer = _host.Connections.GetConfiguredPeerStatuses().FirstOrDefault(s => s.AnyAlive)?.Name;
        if (string.IsNullOrEmpty(peer))
        {
            StatusLabel.Text = "没有已连接的对端";
            return;
        }

        var picked = await FilePicker.Default.PickMultipleAsync();
        var files = picked?.ToList() ?? [];
        if (files.Count == 0) return;
        var paths = files.Select(f => f?.FullPath).Where(p => !string.IsNullOrEmpty(p) && File.Exists(p!)).Cast<string>().ToList();
        if (paths.Count == 0) return;

        await _host.Transfers.StartDirectTransferAsync(peer, _host.Transfers.InboxDirectory, paths);
        StatusLabel.Text = $"正在传输 {paths.Count} 个文件 → {peer}";
        ShowPage(3);
    }

    private async void OpenInbox_Clicked(object? sender, EventArgs e)
    {
        var dir = _host.Transfers.InboxDirectory;
        try
        {
            Directory.CreateDirectory(dir);
#if WINDOWS
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = dir,
                UseShellExecute = true
            });
#else
            await Launcher.Default.OpenAsync(new OpenFileRequest { File = new ReadOnlyFile(dir) });
#endif
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync("收件箱", $"{dir}\n{ex.Message}", "OK");
        }
    }

    private void ClearTransfers_Clicked(object? sender, EventArgs e)
        => _host.Transfers.ClearCompleted();

    private void DeleteTransfer_Clicked(object? sender, EventArgs e)
    {
        if (sender is Button { CommandParameter: TransferItem item })
            _host.Transfers.RemoveItem(item);
    }

    private void Tray_Clicked(object? sender, EventArgs e)
    {
        _host.Platform.HideToBackground(
            showMain: () => { },
            reconnect: () => _ = _host.Connections.ReconnectNowAsync(),
            about: () => MainThread.BeginInvokeOnMainThread(() => ShowPage(5)),
            exit: () => Application.Current?.Quit());
    }

    private void RefreshAboutPage()
    {
        var ver = typeof(MainPage).Assembly.GetName().Version;
        var verText = ver == null ? "1.0" : $"{ver.Major}.{ver.Minor}.{Math.Max(ver.Build, 0)}";
        AboutTitleText.Text = Loc.T("About.Title");
        AboutBodyText.Text = string.Format(Loc.T("About.Body"), verText);
    }

    private void RefreshHelpToolbarLabels()
    {
        HelpUpdateBtn.Text = _helpUpdateReady ? Loc.T("Help.ApplyUpdate") : Loc.T("Help.CheckUpdate");
        HelpDiagBtn.Text = _helpFixReady ? Loc.T("Help.DiagFix") : Loc.T("Help.DiagCheck");
        HelpUpdateBtn.Style = (Style)Application.Current!.Resources[
            _helpUpdateReady ? "PrimaryBtn" : "SoftBlueBtn"];
        HelpDiagBtn.Style = (Style)Application.Current!.Resources[
            _helpFixReady ? "PrimaryBtn" : "SoftWarnBtn"];
    }

    private void HelpDoc_Clicked(object? sender, EventArgs e) => ShowHelpDocument();

    private async void HelpUpdate_Clicked(object? sender, EventArgs e)
    {
        if (_helpUpdateReady)
            await ApplyHelpUpdateAsync();
        else
            await CheckHelpUpdateAsync();
    }

    private async void HelpDiag_Clicked(object? sender, EventArgs e)
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
                ? MauiUpdateChecker.ReleasesUrl
                : _helpLastCheck.ReleaseUrl;
            ShowHelpLog(_helpLastCheck.Message + "\n\n" + url);
            try { await Launcher.Default.OpenAsync(url); } catch { /* ignore */ }
            return;
        }

        var ok = await DisplayAlertAsync(
            Loc.T("Help.ConfirmUpdateTitle"),
            string.Format(Loc.T("Help.ConfirmUpdate"),
                _helpLastCheck.RemoteVersion,
                _helpLastCheck.AssetName ?? Loc.T("Help.AssetUnnamed")),
            "是", "否");
        if (!ok) return;

        SetHelpBusy(true);
        HelpUpdateProgressBar.IsVisible = true;
        HelpUpdateProgressBar.Progress = 0;
        try
        {
            var progress = new Progress<UpdateDownloadProgress>(p =>
            {
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    if (p.Percent >= 0)
                        HelpUpdateProgressBar.Progress = Math.Clamp(p.Percent / 100.0, 0, 1);
                    if (!string.IsNullOrWhiteSpace(p.Message))
                        ShowHelpLog(p.Message);
                });
            });
            var apply = await MauiUpdateInstaller.DownloadAndApplyAsync(_helpLastCheck, progress)
                .ConfigureAwait(true);
            ShowHelpLog(apply.Message);
            if (apply.ShouldExit)
            {
                ShowHelpLog(Loc.T("Help.Restarting"));
                await Task.Delay(400).ConfigureAwait(true);
                Application.Current?.Quit();
                Environment.Exit(0);
                return;
            }
        }
        catch (Exception ex)
        {
            ShowHelpLog(ex.Message);
        }
        finally
        {
            HelpUpdateProgressBar.IsVisible = false;
            HelpUpdateProgressBar.Progress = 0;
            SetHelpBusy(false);
        }
    }

    private async Task CheckHelpDiagAsync()
    {
        SetHelpBusy(true);
        try
        {
            ShowHelpLog(Loc.T("Help.CheckingConn"));
            var diag = new MauiConnectionDiagnostics(_host);
            var report = await diag.CheckAsync().ConfigureAwait(true);
            _helpFixReady = report.Items.Any(i =>
                i.Level is MauiDiagnosticLevel.Error or MauiDiagnosticLevel.Warn);
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
            var diag = new MauiConnectionDiagnostics(_host);
            var progress = new Progress<string>(s =>
                MainThread.BeginInvokeOnMainThread(() => ShowHelpLog(s)));
            var report = await diag.FixAsync(progress).ConfigureAwait(true);
            _helpFixReady = report.Items.Any(i =>
                i.Level is MauiDiagnosticLevel.Error or MauiDiagnosticLevel.Warn);
            RefreshHelpToolbarLabels();
            ShowHelpLog(report.FormatText());
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
    }

    private void ShowHelpDocument()
    {
        HelpUpdateProgressBar.IsVisible = false;
        HelpContent.Children.Clear();
        HelpContent.Children.Add(HelpDocTitle(Loc.T("Help.Doc.Title")));
        HelpContent.Children.Add(HelpPara(Loc.T("Help.Doc.Intro")));
        HelpContent.Children.Add(HelpHeading(Loc.T("Help.Doc.Quick")));
        AddHelpNumbered(Loc.T("Help.Doc.Q1"), Loc.T("Help.Doc.Q2"), Loc.T("Help.Doc.Q3"),
            Loc.T("Help.Doc.Q4"), Loc.T("Help.Doc.Q5"));
        HelpContent.Children.Add(HelpHeading(Loc.T("Help.Doc.Clip")));
        AddHelpBullets(Loc.T("Help.Doc.C1"), Loc.T("Help.Doc.C2"), Loc.T("Help.Doc.C3"),
            Loc.T("Help.Doc.C4"), Loc.T("Help.Doc.C5"));
        HelpContent.Children.Add(HelpHeading(Loc.T("Help.Doc.Opts")));
        AddHelpBullets(Loc.T("Help.Doc.O1"), Loc.T("Help.Doc.O2"), Loc.T("Help.Doc.O3"),
            Loc.T("Help.Doc.O4"), Loc.T("Help.Doc.O5"), Loc.T("Help.Doc.O6"), Loc.T("Help.Doc.O7"));
        HelpContent.Children.Add(HelpHeading(Loc.T("Help.Doc.Hot")));
        HelpContent.Children.Add(HelpPara(Loc.T("Help.Doc.H1")));
        HelpContent.Children.Add(HelpPara(Loc.T("Help.Doc.H2")));
        HelpContent.Children.Add(HelpHeading(Loc.T("Help.Doc.Net")));
        HelpContent.Children.Add(HelpPara(Loc.T("Help.Doc.N1")));
        HelpContent.Children.Add(HelpPara(Loc.T("Help.Doc.N2")));
        HelpContent.Children.Add(HelpHeading(Loc.T("Help.Doc.Update")));
        HelpContent.Children.Add(HelpPara(Loc.T("Help.Doc.U1")));
        HelpContent.Children.Add(HelpPara(MauiUpdateChecker.ReleasesUrl));
        HelpContent.Children.Add(HelpHeading(Loc.T("Help.Doc.Diag")));
        HelpContent.Children.Add(HelpPara(Loc.T("Help.Doc.D1")));
    }

    private void ShowHelpLog(string text)
    {
        HelpContent.Children.Clear();
        foreach (var line in (text ?? "").Replace("\r\n", "\n").Split('\n'))
            HelpContent.Children.Add(HelpLogLine(line));
    }

    private static Label HelpDocTitle(string t) => new()
    {
        Text = t,
        FontSize = 18,
        FontAttributes = FontAttributes.Bold,
        Margin = new Thickness(0, 0, 0, 6)
    };

    private static Label HelpHeading(string t) => new()
    {
        Text = t,
        FontSize = 15,
        FontAttributes = FontAttributes.Bold,
        Margin = new Thickness(0, 12, 0, 4)
    };

    private static Label HelpPara(string t) => new()
    {
        Text = t,
        FontSize = 13,
        TextColor = (Color)Application.Current!.Resources["Muted"],
        LineBreakMode = LineBreakMode.WordWrap
    };

    private static Label HelpLogLine(string t) => new()
    {
        Text = t,
        FontSize = 13,
        FontFamily = "Consolas",
        LineBreakMode = LineBreakMode.WordWrap
    };

    private void AddHelpNumbered(params string[] lines)
    {
        for (var i = 0; i < lines.Length; i++)
            HelpContent.Children.Add(HelpPara($"{i + 1}. {lines[i]}"));
    }

    private void AddHelpBullets(params string[] lines)
    {
        foreach (var line in lines)
            HelpContent.Children.Add(HelpPara("· " + line));
    }

    private void Exit_Clicked(object? sender, EventArgs e)
        => Application.Current?.Quit();
}
