using System.Collections.ObjectModel;
using System.Text;
using FlyScreen.AppMaui.Helpers;
using FlyScreen.AppMaui.Services;
using FlyScreen.Assets;
using FlyScreen.Helpers;
using FlyScreen.Helpers.Localization;
using FlyScreen.Models;
using FlyScreen.Services;
using FlyScreen.Services.Network;
using FlyScreen.Services.Programs;
using FlyScreen.Services.Tools;
using FlyScreen.Services.Tools.SharedDevices;
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
    private MachineSlot? _nativeDragSource;
    private readonly MauiUpdateChecker _helpUpdater = new();
    private UpdateCheckResult? _helpLastCheck;
    private bool _helpUpdateReady;
    private bool _helpFixReady;
    private bool _compactLayout;
    private bool _navOpen;
    private const double CompactWidthBreakpoint = 720;
    private string? _logProgramId;
    private string? _logProgramName;
    private int _logPageFromLatest;
    private int _logTotalPages;
    private DateTime? _logFrom;
    private DateTime? _logTo;
    private readonly ObservableCollection<LogListItem> _logItems = new();
    private static readonly Color LogRowSelectedBg = Color.FromArgb("#DBEAFE");
    private static readonly Color LogRowNormalBg = Colors.Transparent;
    private bool _logStickToBottom = true;
    private bool _logFilterReady;
    private int _logKnownTotal;
    private AppLogEntry? _logWatermark;
    private bool _openProgramLog;
    private UserProgram? _progMoreTarget;
    private bool _hostWired;
    private bool _uiPrepared;
    private bool _a11yInjectUpdating;
#if ANDROID
    private bool _a11yInjectWired;
#endif
    private bool _notifyInjectUpdating;
    private int _a11yRefreshGen;
    private bool _pageAlive = true;
    private bool _optionsScrollHooked;
    private bool _edgeCrossTipWidthHooked;

    // 具名委托，便于 Unloaded 时反订阅，避免关闭窗体后断线回调再上主线程崩溃
    private Action<string>? _onStatusMessage;
    private Action<string>? _onProjectionNotice;
    private Action? _onAppLogChanged;
    private Action<string>? _onLayoutSynced;
    private Action<string>? _onBehaviorSynced;
    private Action<string>? _onSecurityKeySynced;
    private Action<IReadOnlyList<DiscoveredPeer>>? _onPeersUpdated;
    private Action<string, bool, bool>? _onPeerLinkChanged;

    /// <summary>触控板可在空闲时调用，把重 UI 初始化提前做完。</summary>
    public bool IsUiPrepared => _uiPrepared;

    // 矩阵级拖拽：手势挂在 MachineMatrix 上，避免手指移出卡片后 Pan 被 Android 取消
    private bool _matrixInputWired;
    private MachineSlot? _matrixTouchSlot;
    private Border? _matrixTouchBorder;
    private Point _matrixTouchOrigin;
    private bool _matrixDragging;
#if WINDOWS
    /// <summary>主动结束指针捕获时忽略 CaptureLost，否则会把松手误判为取消。</summary>
    private bool _matrixPointerEnding;
    private Point _matrixLastPos;
#endif
    private bool _matrixMoved;
    private const double PanDragThreshold = 12;

    public MainPage()
    {
        InitializeComponent();
        _host = MauiAppHost.EnsureCreated();
        DiscoveryList.ItemsSource = _discovery;
        TransferList.ItemsSource = _host.Transfers.Items;
        TrayBtn.IsVisible = _host.Platform.SupportsTray;
        // 触控板入口：手机（及 Android 平板）按需打开，默认不进触控板页
        TouchpadBtn.IsVisible =
            DeviceInfo.Platform == DevicePlatform.Android
            && (DeviceInfo.Idiom == DeviceIdiom.Phone || DeviceInfo.Idiom == DeviceIdiom.Tablet);
        AutoStartCheck.IsEnabled = _host.Platform.SupportsAutoStart;
        Loaded += OnLoaded;
        SizeChanged += OnPageSizeChanged;
        Unloaded += OnUnloaded;
    }

    private void OnUnloaded(object? sender, EventArgs e)
    {
        _pageAlive = false;
        _statusTimer?.Stop();
        UnwireHost();
    }

    /// <summary>
    /// 预热重活（LoadUi/矩阵/文案）。触控板空闲时调用后，点「设置」只需 Push，不再卡 3–4 秒。
    /// </summary>
    public void PrepareHeavyUi()
    {
        if (_uiPrepared) return;
        Loc.SetCulture(_host.Settings.UiLanguage);
        MauiThemeService.Apply(_host.Settings.UiTheme, this);
        LoadUi();
        ApplyLocalizedUi();
        RebuildMatrix();
        PlatformLabel.Text = _host.PlatformBanner;
        WireHostOnce();
        RefreshNics();
        RefreshDiscovery();
        RefreshSlotStatuses();
        if (!_initialPageChosen)
        {
            _initialPageChosen = true;
            ShowPage(0);
        }
        else
            ShowPage(_pageIndex);
        var w = Width > 1 ? Width : 400;
        ApplyResponsiveLayout(w);
        _uiPrepared = true;
    }

    private void WireHostOnce()
    {
        if (_hostWired) return;
        _hostWired = true;
        _pageAlive = true;

        _onStatusMessage = msg => RunOnUi(() => StatusLabel.Text = msg);
        _onProjectionNotice = msg => RunOnUi(async () =>
        {
            try { await DisplayAlertAsync("投影", msg, "确定"); }
            catch { /* ignore */ }
        });
        _onAppLogChanged = () => RunOnUi(() =>
        {
            if (PageLog.IsVisible && IsLogLiveMode())
                RefreshLogPageLive();
        });
        _onLayoutSynced = _ => RunOnUi(() =>
        {
            // LoadUi 会重建槽位对象，必须先丢掉进行中的拖拽引用
            AbortMatrixDragVisual();
            LoadUi();
            ApplyLocalizedUi();
            RebuildMatrix();
        });
        _onBehaviorSynced = _ => RunOnUi(() =>
        {
            LoadUi();
            ApplyLocalizedUi();
        });
        _onSecurityKeySynced = from => RunOnUi(() =>
        {
            KeyEntry.Text = _host.Settings.SecurityKey ?? "";
            StatusLabel.Text = $"已同步安全密钥 ← {from}";
        });
        _onPeersUpdated = _ => RunOnUi(RefreshDiscovery);
        _onPeerLinkChanged = (_, _, _) => RunOnUi(RefreshSlotStatuses);

        _host.StatusMessage += _onStatusMessage;
        if (_onProjectionNotice != null)
            _host.ProjectionUserNotice += _onProjectionNotice;
        AppLog.Changed += _onAppLogChanged;
        _host.LayoutSyncedFromPeer += _onLayoutSynced;
        _host.BehaviorSyncedFromPeer += _onBehaviorSynced;
        _host.SecurityKeySyncedFromPeer += _onSecurityKeySynced;
        _host.Connections.Discovery.PeersUpdated += _onPeersUpdated;
        _host.Connections.PeerLinkChanged += _onPeerLinkChanged;
        _host.ScreenExtend.HostSessionReady += OnProjectionSessionChanged;
        _host.ScreenExtend.SessionEnded += OnProjectionSessionChanged;
        ToolService.Instance.Catalog.Changed += OnToolsCatalogChanged;
        ToolService.Instance.InstallProgress.Changed += OnToolsCatalogChanged;
        ToolService.Instance.Workspace.Changed += OnToolsWorkspaceChanged;
        ToolService.Instance.ColorPicker.SessionChanged += OnToolsCatalogChanged;
        DeviceDisplay.MainDisplayInfoChanged += OnToolsDisplayInfoChanged;
        var shared = ToolService.Instance.SharedDevices;
        if (shared != null)
        {
            shared.SessionChanged += OnToolsCatalogChanged;
            shared.IncomingRequest += OnSharedDevicesIncomingRequest;
        }

        _statusTimer = Dispatcher.CreateTimer();
        _statusTimer.Interval = TimeSpan.FromSeconds(2);
        _statusTimer.Tick += (_, _) =>
        {
            if (_pageAlive && Handler != null)
                RefreshSlotStatuses();
        };
        RefreshPhoneProjectButton();
    }

    private void OnProjectionSessionChanged()
        => RunOnUi(RefreshPhoneProjectButton);

    private void OnToolsCatalogChanged()
        => RunOnUi(RefreshToolsPage);

    private void UnwireHost()
    {
        if (!_hostWired) return;
        _hostWired = false;

        if (_onStatusMessage != null)
            _host.StatusMessage -= _onStatusMessage;
        if (_onProjectionNotice != null)
            _host.ProjectionUserNotice -= _onProjectionNotice;
        if (_onAppLogChanged != null)
            AppLog.Changed -= _onAppLogChanged;
        if (_onLayoutSynced != null)
            _host.LayoutSyncedFromPeer -= _onLayoutSynced;
        if (_onBehaviorSynced != null)
            _host.BehaviorSyncedFromPeer -= _onBehaviorSynced;
        if (_onSecurityKeySynced != null)
            _host.SecurityKeySyncedFromPeer -= _onSecurityKeySynced;
        if (_onPeersUpdated != null)
            _host.Connections.Discovery.PeersUpdated -= _onPeersUpdated;
        if (_onPeerLinkChanged != null)
            _host.Connections.PeerLinkChanged -= _onPeerLinkChanged;
        _host.ScreenExtend.HostSessionReady -= OnProjectionSessionChanged;
        _host.ScreenExtend.SessionEnded -= OnProjectionSessionChanged;
        ToolService.Instance.Catalog.Changed -= OnToolsCatalogChanged;
        ToolService.Instance.InstallProgress.Changed -= OnToolsCatalogChanged;
        ToolService.Instance.Workspace.Changed -= OnToolsWorkspaceChanged;
        ToolService.Instance.ColorPicker.SessionChanged -= OnToolsCatalogChanged;
        DeviceDisplay.MainDisplayInfoChanged -= OnToolsDisplayInfoChanged;
        var shared = ToolService.Instance.SharedDevices;
        if (shared != null)
        {
            shared.SessionChanged -= OnToolsCatalogChanged;
            shared.IncomingRequest -= OnSharedDevicesIncomingRequest;
        }

        _onStatusMessage = null;
        _onProjectionNotice = null;
        _onAppLogChanged = null;
        _onLayoutSynced = null;
        _onBehaviorSynced = null;
        _onSecurityKeySynced = null;
        _onPeersUpdated = null;
        _onPeerLinkChanged = null;
    }

    /// <summary>关闭窗体后平台主线程可能不可用；先判断存活再调度，并吞掉关闭期异常。</summary>
    private void RunOnUi(Action action)
    {
        if (!_pageAlive || Handler is null) return;
        try
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                if (!_pageAlive || Handler is null) return;
                try { action(); }
                catch (ObjectDisposedException) { /* ignore */ }
                catch (InvalidOperationException) { /* ignore */ }
            });
        }
        catch (ObjectDisposedException) { /* ignore */ }
        catch (InvalidOperationException) { /* ignore */ }
    }

    private async void OnLoaded(object? sender, EventArgs e)
    {
        // 未预热则在此补齐；已预热则跳过重活，只刷新状态
        if (!_uiPrepared)
            PrepareHeavyUi();
        else
        {
            RefreshSlotStatuses();
            RefreshDiscovery();
        }

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

        // 对齐 Win：启动即挂托盘；--autostart 时隐藏主窗（后台已连接）
        if (_host.Platform.SupportsTray)
        {
            EnsureDesktopTray();
#if WINDOWS
            if (FlyScreen.Helpers.AutoStartHelper.IsAutostartLaunch(Environment.GetCommandLineArgs()))
            {
                StatusLabel.Text =
                    $"已自启并连接 · {_host.Connections.BindIp}:{_host.Settings.TcpPort}";
                _host.Platform.HideToBackground(
                    showMain: () => { },
                    reconnect: () => _ = _host.Connections.ReconnectNowAsync(),
                    project: () => MainThread.BeginInvokeOnMainThread(() => _ = OpenProjectionFromTrayAsync()),
                    about: () => MainThread.BeginInvokeOnMainThread(() => ShowPage(5)),
                    exit: RequestAppExit,
                    projectPhase: () => ProjectionUi.Resolve(_host.ScreenExtend));
            }
#endif
        }

        _statusTimer?.Start();

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
                StatusLabel.Text = $"发现新版本 {result.RemoteVersion}，可在「帮助」中自动更新";
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

    private void EnsureDesktopTray()
    {
        if (!_host.Platform.SupportsTray) return;
        _host.Platform.EnsureTray(
            showMain: () => { },
            reconnect: () => _ = _host.Connections.ReconnectNowAsync(),
            project: () => MainThread.BeginInvokeOnMainThread(() => _ = OpenProjectionFromTrayAsync()),
            about: () => MainThread.BeginInvokeOnMainThread(() => ShowPage(5)),
            exit: RequestAppExit,
            projectPhase: () => ProjectionUi.Resolve(_host.ScreenExtend));
    }

    /// <summary>托盘「关于」：显示关于页。</summary>
    public void ShowAboutFromTray() => ShowPage(5);

    /// <summary>托盘「投影」：与主界面投影按钮同一流程。</summary>
    public Task OpenProjectionFromTrayAsync() => RunProjectionEntryAsync();

    private static void RequestAppExit()
    {
#if WINDOWS
        Platforms.Windows.MauiCloseToTray.RequestExit();
#else
        try { ProgramService.Instance.Runner.StopAllForShutdown(); }
        catch { /* ignore */ }
        Application.Current?.Quit();
#endif
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _pageAlive = true;
        // Unloaded 会反订阅；再次显示时补回
        if (!_hostWired && _uiPrepared)
            WireHostOnce();
        if (!_hostWired) return;
        _statusTimer?.Start();
        RefreshSlotStatuses();
        RefreshDiscovery();
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
        EnsureOptionsScrollPolicy();
        ApplyProgToolbarLabels(iconOnly: compact || width < 560);
        ApplyLogToolbarLabels(iconOnly: compact || width < 720);
        ApplyXferToolbarLabels(iconOnly: compact || width < 640);
        ApplyProgListColumns();
        ApplyXferListColumns();
        if (PageTools.IsVisible)
            RefreshToolsPage();
    }

    // 方向 / 文件名 / 状态 / 操作必显；窄屏先藏大小，再藏对端；状态与操作固定窄宽
    private const double XferWDir = 40;
    private const double XferWStatus = 52;
    private const double XferWAction = 36;
    private const double XferWPeerPref = 72;
    private const double XferWSizePref = 56;
    private const double XferWNameMin = 64;
    private const double XferColSpacing = 4;

    private const string XferIcDirect = "⇧";
    private const string XferIcInbox = "📁";
    private const string XferIcReceive = "⇩";
    private const string XferIcClear = "✕";

    private void TransferListHost_SizeChanged(object? sender, EventArgs e)
        => ApplyXferListColumns();

    private void XferToolBarHost_SizeChanged(object? sender, EventArgs e)
    {
        if (XferToolBarHost == null || Width <= 1) return;
        var forceIcon = _compactLayout || Width < 640
                        || (XferToolBarHost.Width > 1 && XferToolBarHost.Width < 420);
        ApplyXferToolbarLabels(forceIcon);
    }

    /// <summary>传输工具栏：窄屏只留符号，避免按钮被裁切。</summary>
    private void ApplyXferToolbarLabels(bool iconOnly)
    {
        if (DirectTransferBtn == null) return;
        DirectTransferBtn.Text = iconOnly ? XferIcDirect : $"{XferIcDirect}  {Loc.T("Xfer.Direct")}";
        OpenInboxBtn.Text = iconOnly ? XferIcInbox : $"{XferIcInbox}  {Loc.T("Xfer.OpenInbox")}";
        ReceiveClipboardBtn.Text = iconOnly ? XferIcReceive : $"{XferIcReceive}  {Loc.T("Xfer.ReceiveClipboard")}";
        ClearTransfersBtn.Text = iconOnly ? XferIcClear : $"{XferIcClear}  {Loc.T("Xfer.ClearDone")}";

        var minW = iconOnly ? 34.0 : 0.0;
        var maxW = iconOnly ? 34.0 : 160.0;
        var pad = iconOnly ? new Thickness(0) : new Thickness(10, 0);
        foreach (var b in new[] { DirectTransferBtn, OpenInboxBtn, ReceiveClipboardBtn, ClearTransfersBtn })
        {
            b.Padding = pad;
            b.MinimumWidthRequest = iconOnly ? minW : 36;
            b.MaximumWidthRequest = maxW;
            b.WidthRequest = iconOnly ? 34 : -1;
        }

        if (XferToolSep != null)
            XferToolSep.IsVisible = !iconOnly;

        ToolTipProperties.SetText(DirectTransferBtn, Loc.T("Xfer.Direct"));
        ToolTipProperties.SetText(OpenInboxBtn, Loc.T("Xfer.OpenInbox"));
        ToolTipProperties.SetText(ReceiveClipboardBtn, Loc.T("Xfer.ReceiveClipboard"));
        ToolTipProperties.SetText(ClearTransfersBtn, Loc.T("Xfer.ClearDone"));
    }

    /// <summary>列宽之和≈容器宽；文件名优先，不够时藏大小再藏对端；状态/操作固定。</summary>
    private void ApplyXferListColumns()
    {
        if (TransferListHost == null || TransferListHeader == null) return;

        var hostW = TransferListHost.Width;
        if (hostW < 40)
        {
            var pageW = Width > 0 ? Width : 360;
            hostW = Math.Max(240, pageW - (_compactLayout ? 48 : 168 + 48));
        }

        // 必显：方向 + 状态 + 操作 + 5 段间距；余量给文件名（+可选对端/大小）
        var must = XferWDir + XferWStatus + XferWAction + XferColSpacing * 5;
        var rest = hostW - must;

        double peerW;
        double sizeW;
        double nameW;
        if (rest < XferWNameMin)
        {
            peerW = 0;
            sizeW = 0;
            nameW = Math.Max(48, rest);
        }
        else if (rest < XferWNameMin + XferWPeerPref)
        {
            // 仅文件名（+尽量一点对端也不给，保证名称）
            peerW = 0;
            sizeW = 0;
            nameW = rest;
        }
        else if (rest < XferWNameMin + XferWPeerPref + XferWSizePref)
        {
            // 有对端，藏大小
            peerW = XferWPeerPref;
            sizeW = 0;
            nameW = rest - peerW;
        }
        else
        {
            peerW = XferWPeerPref;
            sizeW = XferWSizePref;
            nameW = rest - peerW - sizeW;
        }

        var used = XferWDir + nameW + peerW + sizeW + XferWStatus + XferWAction + XferColSpacing * 5;
        if (used < hostW)
            nameW += hostW - used;
        else if (used > hostW + 0.5)
            nameW = Math.Max(48, nameW - (used - hostW));

        var widths = new[] { XferWDir, nameW, peerW, sizeW, XferWStatus, XferWAction };
        TransferListRowView.SharedColWidths = widths;

        TransferListHeader.ColumnDefinitions = new ColumnDefinitionCollection
        {
            new(new GridLength(widths[0])),
            new(new GridLength(widths[1])),
            new(new GridLength(widths[2])),
            new(new GridLength(widths[3])),
            new(new GridLength(widths[4])),
            new(new GridLength(widths[5])),
        };
        ColPeer.IsVisible = peerW > 1;
        ColSize.IsVisible = sizeW > 1;
        ColStatus.IsVisible = true;
        ColOption.IsVisible = true;

        foreach (var row in FindXferRows(TransferList))
            row.ApplySharedWidths();
    }

    private static IEnumerable<TransferListRowView> FindXferRows(IView? root)
    {
        if (root is TransferListRowView row)
        {
            yield return row;
            yield break;
        }
        if (root is not IVisualTreeElement vte) yield break;
        foreach (var child in vte.GetVisualChildren())
        {
            if (child is not IView v) continue;
            foreach (var r in FindXferRows(v))
                yield return r;
        }
    }

    // 勾选 / 语言 / 状态 / 操作 必显；窄屏只可隐藏大小
    private const double ProgWCheck = 32;
    private const double ProgWStatus = 56;
    private const double ProgWActions = 64; // ▶ + ⋮
    private const double ProgWLangMin = 72;
    private const double ProgWLangPref = 88;
    private const double ProgWSizePref = 56;
    private const double ProgWNameMin = 56;
    private const double ProgColSpacing = 4;

    private void ProgListHost_SizeChanged(object? sender, EventArgs e)
        => ApplyProgListColumns();

    /// <summary>列宽之和严格等于容器宽；语言不隐藏，不够宽时只藏大小。</summary>
    private void ApplyProgListColumns()
    {
        if (ProgListHost == null || ProgListHeader == null) return;

        var hostW = ProgListHost.Width;
        if (hostW < 40)
        {
            var pageW = Width > 0 ? Width : 360;
            hostW = Math.Max(260, pageW - (_compactLayout ? 48 : 168 + 48));
        }

        // 必显：勾选 + 语言 + 状态 + 操作 + 间距
        var must = ProgWCheck + ProgWLangMin + ProgWStatus + ProgWActions + ProgColSpacing * 5;
        var rest = hostW - must; // 留给名称（+可选大小）及语言加宽

        double langW = ProgWLangMin;
        double sizeW;
        double nameW;
        if (rest < ProgWNameMin)
        {
            // 极窄：压缩名称，仍保留语言
            sizeW = 0;
            nameW = Math.Max(40, rest);
        }
        else if (rest < ProgWNameMin + ProgWSizePref + (ProgWLangPref - ProgWLangMin))
        {
            // 中等：隐藏大小；余量给名称，语言尽量加到 pref
            sizeW = 0;
            var extra = rest - ProgWNameMin;
            var langBoost = Math.Min(ProgWLangPref - ProgWLangMin, Math.Max(0, extra));
            langW = ProgWLangMin + langBoost;
            nameW = rest - langBoost;
        }
        else
        {
            langW = ProgWLangPref;
            sizeW = ProgWSizePref;
            nameW = rest - sizeW - (ProgWLangPref - ProgWLangMin);
        }

        var used = ProgWCheck + nameW + langW + sizeW + ProgWStatus + ProgWActions + ProgColSpacing * 5;
        if (used < hostW)
            nameW += hostW - used;
        else if (used > hostW + 0.5)
            nameW = Math.Max(40, nameW - (used - hostW));

        var widths = new[] { ProgWCheck, nameW, langW, sizeW, ProgWStatus, ProgWActions };
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
        ProgColLang.IsVisible = true;
        ProgColSize.IsVisible = sizeW > 1;
        ProgColStatus.IsVisible = true;
        ProgColActions.IsVisible = true;

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
        ProgMoreSettingsBtn.Text = $"⚙  {Loc.T("Prog.ActSettings")}";
        ProgMoreDeleteBtn.Text = $"✕  {Loc.T("Prog.ActDelete")}";
        ProgMoreDeleteBtn.IsVisible = prog is not { IsBuiltin: true };

        // 先 Invisible 定位，避免 (0,0)→锚点 闪现
        ProgMoreFlyout.Opacity = 0;
        ProgMoreFlyout.TranslationX = 0;
        ProgMoreFlyout.TranslationY = 0;
        ProgMoreOverlay.IsVisible = true;
        Dispatcher.Dispatch(() =>
        {
            PositionProgMoreFlyout(anchor);
            // 再排一帧确保 Width/坐标就绪后再显示
            Dispatcher.Dispatch(() =>
            {
                PositionProgMoreFlyout(anchor);
                ProgMoreFlyout.Opacity = 1;
            });
        });
    }

    private void PositionProgMoreFlyout(View anchor)
    {
        try
        {
            var menuW = ProgMoreFlyout.Width > 1 ? ProgMoreFlyout.Width : 112;
            var menuH = ProgMoreFlyout.Height > 1 ? ProgMoreFlyout.Height : 160;
            var pageW = Width > 1 ? Width : 800;
            var pageH = Height > 1 ? Height : 600;

            var pagePos = GetAnchorInOverlay(anchor);
            // 菜单右对齐 ⋮ 按钮右缘，出现在按钮正下方
            var x = pagePos.X + Math.Max(anchor.Width, 1) - menuW;
            var y = pagePos.Y + Math.Max(anchor.Height, 1) + 4;
            if (x < 8) x = 8;
            if (x + menuW > pageW - 8) x = Math.Max(8, pageW - menuW - 8);
            if (y + menuH > pageH - 8)
                y = Math.Max(8, pagePos.Y - menuH - 4);

            ProgMoreFlyout.TranslationX = x;
            ProgMoreFlyout.TranslationY = y;
        }
        catch
        {
            ProgMoreFlyout.TranslationX = 24;
            ProgMoreFlyout.TranslationY = 120;
        }
    }

    /// <summary>锚点相对 ProgMoreOverlay 的 MAUI 坐标（DIP）。Android 用屏幕像素换算，避免 CollectionView 累加偏移错误。</summary>
    private Point GetAnchorInOverlay(View anchor)
    {
#if ANDROID
        try
        {
            if (anchor.Handler?.PlatformView is global::Android.Views.View av
                && ProgMoreOverlay.Handler?.PlatformView is global::Android.Views.View ov)
            {
                var aLoc = new int[2];
                var oLoc = new int[2];
                av.GetLocationOnScreen(aLoc);
                ov.GetLocationOnScreen(oLoc);
                var density = DeviceDisplay.MainDisplayInfo.Density;
                if (density <= 0) density = 1;
                return new Point((aLoc[0] - oLoc[0]) / density, (aLoc[1] - oLoc[1]) / density);
            }
        }
        catch { /* fall through */ }
#endif
#if WINDOWS
        try
        {
            if (anchor.Handler?.PlatformView is Microsoft.UI.Xaml.FrameworkElement fe
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
        VisualElement? cur = anchor;
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
        ProgMoreFlyout.Opacity = 0;
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

    private async void ProgMoreSettings_Clicked(object? sender, EventArgs e)
    {
        var prog = _progMoreTarget;
        HideProgMoreFlyout();
        if (prog != null) await ProgRowSettingsAsync(prog);
    }

    private async void ProgMoreDelete_Clicked(object? sender, EventArgs e)
    {
        var prog = _progMoreTarget;
        HideProgMoreFlyout();
        if (prog != null) await ProgRowDeleteAsync(prog);
    }

    private async Task ProgRowSettingsAsync(UserProgram prog)
    {
        try
        {
            await Navigation.PushAsync(new ProgramSettingsPage(prog));
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync(Loc.T("Prog.ActSettings"), ex.Message, "OK");
        }
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
                Grid.SetColumnSpan(FeatColumn, 1);
                Grid.SetRow(BehColumn, 1);
                Grid.SetColumn(BehColumn, 0);
                Grid.SetColumnSpan(BehColumn, 1);
                Grid.SetRow(EdgeCrossColumn, 2);
                Grid.SetColumn(EdgeCrossColumn, 0);
                Grid.SetColumnSpan(EdgeCrossColumn, 1);
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
                OptionsFeatGrid.RowSpacing = 10;
                OptionsFeatGrid.ColumnSpacing = 12;
                Grid.SetRow(FeatColumn, 0);
                Grid.SetColumn(FeatColumn, 0);
                Grid.SetColumnSpan(FeatColumn, 1);
                Grid.SetRow(BehColumn, 0);
                Grid.SetColumn(BehColumn, 1);
                Grid.SetColumnSpan(BehColumn, 1);
                Grid.SetRow(EdgeCrossColumn, 0);
                Grid.SetColumn(EdgeCrossColumn, 2);
                Grid.SetColumnSpan(EdgeCrossColumn, 1);
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
        if (TouchpadBtn != null && TouchpadBtn.IsVisible)
            TouchpadBtn.Text = iconOnly ? "⌨" : "⌨ 触控板";
        AddMachineBtn.Padding = iconOnly ? new Thickness(8, 0) : new Thickness(10, 0);
        RemoveMachineBtn.Padding = iconOnly ? new Thickness(8, 0) : new Thickness(10, 0);
        ReconnectBtn.Padding = iconOnly ? new Thickness(8, 0) : new Thickness(10, 0);
        if (TouchpadBtn != null)
            TouchpadBtn.Padding = iconOnly ? new Thickness(8, 0) : new Thickness(10, 0);
    }

    // 与文字同行的符号，避免 Path 叠字导致垂直错位
    private const string ProgIcAdd = "＋";
    private const string ProgIcDelete = "✕";
    private const string ProgIcStart = "▶";
    private const string ProgIcStop = "■";
    private const string LogIcPrev = "◀";
    private const string LogIcNext = "▶";
    private const string LogIcLatest = "↻";
    private const string LogIcDate = "▦";
    private const string LogIcDownload = "⇩";
    private const string LogIcCopy = "⎘";
    private const string LogIcClear = "✕";
    private bool _logToolsIconOnly;

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
        NavToolsText.Text = Loc.T("Nav.Tools");
        ToolsTitleText.Text = Loc.T("Tool.Title");
        ToolsHintText.Text = Loc.T("Tool.Hint");
        NavLogText.Text = Loc.T("Nav.Log");
        AboutBtnText.Text = Loc.T("Btn.About");
        LogTitleText.Text = Loc.T("Log.Title");
        RefreshToolsPage();
        EnsureLogLevelFilterItems();
        ApplyLogToolbarLabels(_logToolsIconOnly);
        RefreshLogHint();
        ProgTitleText.Text = Loc.T("Prog.Title");
        ProgHintText.Text = Loc.T(
            InProcessProgramExecutor.IsMobileHost ? "Prog.HintMobile" : "Prog.Hint");
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
        SyncNotifyInjectCheck();
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
        if (SyncKeyBtn != null) SyncKeyBtn.Text = Loc.T("Sec.SyncKey");
        SecurityHintText.Text = Loc.T("Sec.Hint");
        if (SecuritySyncHintText != null) SecuritySyncHintText.Text = Loc.T("Sec.SyncHint");
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
        NotifyInjectLabel.Text = Loc.T("Opt.NotifyInject");
        NotifyInjectTipLabel.Text = Loc.T("Opt.NotifyInjectTip");
        A11yInjectLabel.Text = Loc.T("Opt.A11yInject");
        A11yInjectTipLabel.Text = Loc.T("Opt.A11yInjectTip");
        EdgeCrossTitleText.Text = Loc.T("Opt.EdgeCrossMode");
        MauiRadioTheme.SetContent(EdgeCrossBarrierRadio, Loc.T("Opt.EdgeCross.BarrierFree"));
        MauiRadioTheme.SetContent(EdgeCrossDoubleRadio, Loc.T("Opt.EdgeCross.DoubleTap"));
        MauiRadioTheme.SetContent(EdgeCrossSpeedRadio, Loc.T("Opt.EdgeCross.SpeedBurst"));
        MauiRadioTheme.SetContent(EdgeCrossHotkeyRadio, Loc.T("Opt.EdgeCross.HotkeyOnly"));
        RefreshEdgeCrossTip();
        WireNotifyInjectUi();
        SyncNotifyInjectCheck();
        WireA11yInjectUi();
        SyncA11yInjectCheck();

        HotTitleText.Text = Loc.T("Hot.Title");
        HotSwitchText.Text = Loc.T("Hot.Switch");
        MauiRadioTheme.SetContent(HotF, Loc.T("Hot.F14"));
        MauiRadioTheme.SetContent(HotNum, Loc.T("Hot.Num"));
        MauiRadioTheme.SetContent(HotOff, Loc.T("Hot.Off"));
        HotSettingsLabel.Text = Loc.T("Hot.Settings");
        HotReconnectLabel.Text = Loc.T("Hot.Reconnect");
        HotExitLabel.Text = Loc.T("Hot.Exit");
        HotLockLabel.Text = Loc.T("Hot.Lock");

        SysTitleText.Text = Loc.T("Sys.Title");
        SysStyleLabel.Text = Loc.T("Sys.Style");
        SysLangLabel.Text = Loc.T("Sys.Language");
        MauiRadioTheme.SetContent(ThemeLight, Loc.T("Sys.StyleLight"));
        MauiRadioTheme.SetContent(ThemeDark, Loc.T("Sys.StyleDark"));

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
        ApplyXferToolbarLabels(_compactLayout || Width > 0 && Width < 640);
        ColDir.Text = Loc.T("Xfer.Col.Dir");
        ColName.Text = Loc.T("Xfer.Col.Name");
        ColPeer.Text = Loc.T("Xfer.Col.Peer");
        ColSize.Text = Loc.T("Xfer.Col.Size");
        ColStatus.Text = Loc.T("Xfer.Col.Status");
        ColOption.Text = Loc.T("Xfer.Col.Option");
        XferEmptyText.Text = Loc.Culture.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
            ? "暂无传输任务。"
            : "No transfers yet.";
        ApplyXferListColumns();

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
        var dragSlot = _matrixTouchSlot;
        var dragging = _matrixDragging;
#if WINDOWS
        var dragPos = _matrixLastPos;
#else
        var dragPos = _matrixTouchOrigin;
#endif

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
            // 非系统 DnD：卡片透明给矩阵，由矩阵级指针/触摸完成拖拽（否则移出卡片边界就断）
            if (!SupportsNativeDragDrop)
                border.InputTransparent = true;
            Grid.SetRow(border, i / cols);
            Grid.SetColumn(border, i % cols);
            MachineMatrix.Children.Add(border);
        }

        EnsureMatrixDragInput();

        // 状态刷新等路径重建矩阵时，把拖拽视觉挂回同一槽位的新卡片
        if (dragSlot != null)
            RestoreMatrixDragVisual(dragSlot, dragging, dragPos);
    }

    private void AbortMatrixDragVisual()
    {
        if (_matrixTouchBorder != null)
        {
            _matrixTouchBorder.TranslationX = 0;
            _matrixTouchBorder.TranslationY = 0;
            _matrixTouchBorder.Opacity = 1;
            _matrixTouchBorder.ZIndex = 0;
        }

        ClearMatrixDropTargets();
        _matrixTouchBorder = null;
        _matrixTouchSlot = null;
        _matrixDragging = false;
        _matrixMoved = false;
    }

    private void ClearMatrixDropTargets()
    {
        foreach (var s in _slots)
            s.IsDropTarget = false;
        RefreshMatrixCardChrome();
    }

    private void UpdateMatrixDropHighlight(Point pos)
    {
        if (!_matrixDragging || _matrixTouchSlot == null)
            return;

        var target = HitTestSlotAt(pos);
        if (target != null && ReferenceEquals(target, _matrixTouchSlot))
            target = null;

        var changed = false;
        foreach (var s in _slots)
        {
            var on = target != null && ReferenceEquals(s, target);
            if (s.IsDropTarget == on) continue;
            s.IsDropTarget = on;
            changed = true;
        }

        if (changed)
            RefreshMatrixCardChrome(preserveDragVisual: true);
    }

    private void RefreshMatrixCardChrome(bool preserveDragVisual = false)
    {
        foreach (var child in MachineMatrix.Children.OfType<Border>())
        {
            if (child.BindingContext is not MachineSlot slot)
                continue;
            ApplySlotCardChrome(child, slot);
        }

        if (preserveDragVisual && _matrixDragging && _matrixTouchBorder != null)
        {
            _matrixTouchBorder.Opacity = 0.88;
            _matrixTouchBorder.ZIndex = 100;
        }
    }

    private static void ApplySlotCardChrome(Border border, MachineSlot slot)
    {
        var resources = Application.Current!.Resources;
        if (slot.IsDropTarget)
        {
            border.StrokeThickness = 3;
            border.Stroke = (Color)resources["Blue"];
            border.BackgroundColor = (Color)resources["BlueSoft"];
            border.Opacity = 1;
            return;
        }

        if (slot.IsSelected)
        {
            border.StrokeThickness = 2;
            border.Stroke = (Color)resources["Blue"];
            border.BackgroundColor = (Color)resources["BlueHover"];
            border.Opacity = 1;
            return;
        }

        if (slot.IsLocal)
        {
            border.StrokeThickness = 2;
            border.Stroke = (Color)resources["Success"];
            border.BackgroundColor = resources.ContainsKey("SuccessSoft")
                ? (Color)resources["SuccessSoft"]
                : Color.FromArgb("#ECFDF5");
            border.Opacity = slot.Enabled ? 1 : 0.9;
            return;
        }

        border.StrokeThickness = 1;
        border.Stroke = (Color)resources["Line"];
        border.BackgroundColor = slot.Enabled
            ? (Color)resources["CardBg"]
            : (Color)resources["MatrixBg"];
        border.Opacity = slot.Enabled ? 1 : 0.9;
    }

    private void RestoreMatrixDragVisual(MachineSlot slot, bool dragging, Point pos)
    {
        _matrixTouchSlot = slot;
        _matrixTouchBorder = MachineMatrix.Children.OfType<Border>()
            .FirstOrDefault(b => ReferenceEquals(b.BindingContext, slot));
        _matrixDragging = dragging && _matrixTouchBorder != null;
        if (_matrixTouchBorder == null || !_matrixDragging)
            return;

        var dx = pos.X - _matrixTouchOrigin.X;
        var dy = pos.Y - _matrixTouchOrigin.Y;
        _matrixTouchBorder.Opacity = 0.88;
        _matrixTouchBorder.ZIndex = 100;
        _matrixTouchBorder.TranslationX = dx;
        _matrixTouchBorder.TranslationY = dy;
        StatusLabel.Text = $"拖动 {slot.DisplayName}，放到目标格松开";
    }

    private Border CreateSlotCard(MachineSlot slot)
    {
        // 与 Win 一致：默认 Margin=4；2×2 时 Margin=3；内边距由内容模板自身 Margin 承担
        var margin = _twoRow ? new Thickness(3) : new Thickness(4);
        var border = new Border
        {
            Margin = margin,
            Padding = 0,
            StrokeShape = new RoundRectangle { CornerRadius = 8 },
            HorizontalOptions = LayoutOptions.Fill,
            VerticalOptions = LayoutOptions.Fill,
            BindingContext = slot
        };
        ApplySlotCardChrome(border, slot);

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

    /// <summary>
    /// 仅 MacCatalyst 用系统拖放。
    /// WinUI 清单为 requireAdministrator（穿边钩子），提权进程下 OLE/WinUI DnD 会
    /// 「Drag start failed… elevated process」灾难性故障，故与 Android/iOS 一样走矩阵指针拖拽。
    /// </summary>
    private static bool SupportsNativeDragDrop =>
        DeviceInfo.Platform == DevicePlatform.MacCatalyst;

    private void AttachSlotGestures(Border border, MachineSlot slot)
    {
        if (!SupportsNativeDragDrop)
            return; // Win / 触摸端由 EnsureMatrixDragInput 统一处理

        var tap = new TapGestureRecognizer();
        tap.Tapped += (_, _) => OnSlotTapped(slot);
        border.GestureRecognizers.Add(tap);

        var drag = new DragGestureRecognizer();
        drag.DragStarting += (_, e) =>
        {
            var idx = _slots.IndexOf(slot);
            if (idx < 0) return;
            e.Data.Properties[DragSlotKey] = idx;
            _nativeDragSource = slot;
            SelectSlotOnly(slot);
        };
        border.GestureRecognizers.Add(drag);

        var drop = new DropGestureRecognizer { AllowDrop = true };
        drop.DragOver += (_, e) =>
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            var src = _nativeDragSource;
            var on = src != null && !ReferenceEquals(src, slot);
            if (slot.IsDropTarget == on) return;
            foreach (var s in _slots)
                s.IsDropTarget = ReferenceEquals(s, slot) && on;
            RefreshMatrixCardChrome();
        };
        drop.DragLeave += (_, _) =>
        {
            if (!slot.IsDropTarget) return;
            slot.IsDropTarget = false;
            ApplySlotCardChrome(border, slot);
        };
        drop.Drop += (_, e) =>
        {
            e.Handled = true;
            try
            {
                if (!TryResolveDragSourceSlot(e.Data, out var src) || src == null)
                    src = _nativeDragSource;
                if (src == null || ReferenceEquals(src, slot)) return;

                SwapSlotData(src, slot);
                SyncSlotLocalFlags();
                SelectSlotOnly(slot);
                ClearMatrixDropTargets();
                _nativeDragSource = null;
                RebuildMatrix();
                StatusLabel.Text = "已互换位置（请点「应用」同步）";
            }
            catch (Exception ex)
            {
                AppLog.Warn("布局拖拽互换失败: " + ex.Message);
            }
            finally
            {
                _nativeDragSource = null;
                ClearMatrixDropTargets();
            }
        };
        border.GestureRecognizers.Add(drop);
    }

    private bool TryResolveDragSourceSlot(DataPackageView data, out MachineSlot? src)
    {
        src = null;
        if (!data.Properties.TryGetValue(DragSlotKey, out var obj) || obj == null)
            return false;

        switch (obj)
        {
            case int idx when idx >= 0 && idx < _slots.Count:
                src = _slots[idx];
                return true;
            case long l when l >= 0 && l < _slots.Count:
                src = _slots[(int)l];
                return true;
            case MachineSlot slot:
                src = slot;
                return true;
            case string s when int.TryParse(s, out var parsed) && parsed >= 0 && parsed < _slots.Count:
                src = _slots[parsed];
                return true;
            default:
                return false;
        }
    }

    private void EnsureMatrixDragInput()
    {
        if (SupportsNativeDragDrop || _matrixInputWired || MachineMatrix == null)
            return;
        _matrixInputWired = true;

#if ANDROID
        MachineMatrix.HandlerChanged += (_, _) => WireAndroidMatrixTouch();
        WireAndroidMatrixTouch();
#elif WINDOWS
        // 提权进程不能用 WinUI DnD；原生指针 + Capture，鼠标拖出卡片也不丢
        MachineMatrix.HandlerChanged += (_, _) => WireWindowsMatrixPointer();
        WireWindowsMatrixPointer();
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

#if WINDOWS
    private void WireWindowsMatrixPointer()
    {
        if (MachineMatrix.Handler?.PlatformView is not Microsoft.UI.Xaml.FrameworkElement native)
            return;

        native.PointerPressed -= OnWindowsMatrixPointerPressed;
        native.PointerMoved -= OnWindowsMatrixPointerMoved;
        native.PointerReleased -= OnWindowsMatrixPointerReleased;
        native.PointerCanceled -= OnWindowsMatrixPointerCanceled;
        native.PointerCaptureLost -= OnWindowsMatrixPointerCaptureLost;

        native.PointerPressed += OnWindowsMatrixPointerPressed;
        native.PointerMoved += OnWindowsMatrixPointerMoved;
        native.PointerReleased += OnWindowsMatrixPointerReleased;
        native.PointerCanceled += OnWindowsMatrixPointerCanceled;
        native.PointerCaptureLost += OnWindowsMatrixPointerCaptureLost;
    }

    private static Point ToMatrixPoint(
        Microsoft.UI.Xaml.FrameworkElement native,
        Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        var p = e.GetCurrentPoint(native).Position;
        return new Point(p.X, p.Y);
    }

    /// <summary>以物理左键为准；WinUI 在捕获/钩子干扰下会冒虚假 Released。</summary>
    private static bool IsPhysicalLeftButtonDown()
        => (FlyScreen.Native.NativeMethods.GetAsyncKeyState(0x01) & 0x8000) != 0;

    private void FinishWindowsMatrixDrag(
        Microsoft.UI.Xaml.FrameworkElement native,
        Microsoft.UI.Xaml.Input.Pointer pointer,
        Point pos,
        bool cancelled)
    {
        if (_matrixPointerEnding || _matrixTouchSlot == null)
            return;

        _matrixPointerEnding = true;
        try
        {
            MatrixTouchEnded(pos, cancelled);
            try { native.ReleasePointerCapture(pointer); } catch { /* ignore */ }
        }
        finally
        {
            _matrixPointerEnding = false;
        }
    }

    private void OnWindowsMatrixPointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (sender is not Microsoft.UI.Xaml.FrameworkElement native)
            return;
        if (!e.GetCurrentPoint(native).Properties.IsLeftButtonPressed)
            return;

        native.CapturePointer(e.Pointer);
        var pos = ToMatrixPoint(native, e);
        _matrixLastPos = pos;
        MatrixTouchBegan(pos);
        e.Handled = true;
    }

    private void OnWindowsMatrixPointerMoved(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (sender is not Microsoft.UI.Xaml.FrameworkElement native)
            return;
        if (_matrixTouchSlot == null)
            return;

        var pos = ToMatrixPoint(native, e);
        _matrixLastPos = pos;

        // Released 被吞时，在移动中发现物理键已抬起再提交
        if (!IsPhysicalLeftButtonDown())
        {
            FinishWindowsMatrixDrag(native, e.Pointer, pos, cancelled: false);
            e.Handled = true;
            return;
        }

        MatrixTouchMoved(pos);
        e.Handled = true;
    }

    private void OnWindowsMatrixPointerReleased(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (sender is not Microsoft.UI.Xaml.FrameworkElement native)
            return;

        // 拖拽途中的虚假 Released：物理左键仍按下则绝不提交互换
        if (IsPhysicalLeftButtonDown())
        {
            e.Handled = true;
            return;
        }

        var pos = ToMatrixPoint(native, e);
        _matrixLastPos = pos;
        FinishWindowsMatrixDrag(native, e.Pointer, pos, cancelled: false);
        e.Handled = true;
    }

    private void OnWindowsMatrixPointerCanceled(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (sender is not Microsoft.UI.Xaml.FrameworkElement native)
            return;

        // 取消时若键仍按下，多半是捕获被抢，夺回后继续拖
        if (IsPhysicalLeftButtonDown())
        {
            try { native.CapturePointer(e.Pointer); } catch { /* ignore */ }
            e.Handled = true;
            return;
        }

        FinishWindowsMatrixDrag(native, e.Pointer, ToMatrixPoint(native, e), cancelled: true);
        e.Handled = true;
    }

    private void OnWindowsMatrixPointerCaptureLost(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (_matrixPointerEnding || _matrixTouchSlot == null)
            return;
        if (sender is not Microsoft.UI.Xaml.FrameworkElement native)
            return;

        if (IsPhysicalLeftButtonDown())
        {
            try { native.CapturePointer(e.Pointer); } catch { /* ignore */ }
            return;
        }

        // 键已松开但只收到 CaptureLost：按最后位置提交
        FinishWindowsMatrixDrag(native, e.Pointer, _matrixLastPos, cancelled: false);
    }
#endif

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
        if (_matrixTouchSlot == null)
            return;

        // 定时 RebuildMatrix 会换掉 Border；按槽位重新挂上，避免拖影突然消失
        if (_matrixTouchBorder?.Parent == null)
        {
            _matrixTouchBorder = MachineMatrix.Children.OfType<Border>()
                .FirstOrDefault(b => ReferenceEquals(b.BindingContext, _matrixTouchSlot));
            if (_matrixTouchBorder == null)
                return;
        }

        var dx = pos.X - _matrixTouchOrigin.X;
        var dy = pos.Y - _matrixTouchOrigin.Y;
        if (!_matrixDragging && (Math.Abs(dx) >= PanDragThreshold || Math.Abs(dy) >= PanDragThreshold))
        {
            _matrixDragging = true;
            _matrixMoved = true;
            SelectSlotOnly(_matrixTouchSlot);
            RefreshMatrixCardChrome(preserveDragVisual: true);
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
            UpdateMatrixDropHighlight(pos);
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

        ClearMatrixDropTargets();
        _matrixTouchBorder = null;
        _matrixTouchSlot = null;
        _matrixDragging = false;

        if (slot == null)
        {
            _matrixMoved = false;
            return;
        }

        if (wasDragging && cancelled)
        {
            _matrixMoved = false;
            StatusLabel.Text = "已取消互换";
            SelectSlotOnly(slot);
            return;
        }

        if (wasDragging)
        {
            var target = HitTestSlotAt(pos);
            _matrixMoved = false;
            if (target == null || ReferenceEquals(target, slot))
            {
                StatusLabel.Text = "未放到其它格子，已取消互换";
                SelectSlotOnly(slot);
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
    {
        if (HitTestBorderAt(pos)?.BindingContext is MachineSlot hit)
            return hit;

        // Bounds 偶发未就绪时，按矩阵几何兜底（与 1×4 / 2×2 一致）
        var w = MachineMatrix.Width;
        var h = MachineMatrix.Height;
        if (w <= 0 || h <= 0 || _slots.Count == 0)
            return null;

        var cols = _twoRow ? 2 : 4;
        var rows = _twoRow ? 2 : 1;
        var col = (int)Math.Clamp(Math.Floor(pos.X * cols / w), 0, cols - 1);
        var row = (int)Math.Clamp(Math.Floor(pos.Y * rows / h), 0, rows - 1);
        var idx = row * cols + col;
        return idx >= 0 && idx < _slots.Count ? _slots[idx] : null;
    }

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

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var v in values)
        {
            if (!string.IsNullOrWhiteSpace(v))
                return v.Trim();
        }

        return null;
    }

    private long _lastTapTicks;

    private void RefreshSlotStatuses()
    {
        if (!_pageAlive || Handler is null) return;
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

        // 拖拽中重建会清掉 Translation，表现为「按住一会儿拖影没了」
        if (_matrixTouchSlot != null)
            return;

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
        PageTools.IsVisible = index == 9;
        if (index == 3)
        {
            ApplyXferToolbarLabels(_compactLayout || Width > 0 && Width < 640);
            Dispatcher.Dispatch(ApplyXferListColumns);
        }

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
            SetNav(NavTools, NavToolsText, index == 9);
        }
        catch { /* ignore */ }

        // 首次显示高级页时 Android RadioButton 可能仍是 Material 深色底，显式刷一遍
        if (index == 1)
        {
            RefreshOptionsRadioChrome();
            SyncA11yInjectCheck();
            EnsureOptionsScrollPolicy();
            RelayoutOptionsPage();
            Dispatcher.Dispatch(() =>
            {
                RelayoutOptionsPage();
                UpdateOptionsScrollPolicy();
            });
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
            RefreshProgramsPage();
        else if (index == 9)
        {
            RefreshToolsPage();
            // 抽屉收起/首次显示后主区宽度会变，补一次布局后刷新
            QueueToolsRefreshAfterLayout();
        }

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
        else if (q.Contains("工具") || q.Contains("共享设备") || q.Contains("摄像头") || q.Contains("麦克风") || q.Contains("扬声器")) ShowPage(9);
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
            case "tools": ShowPage(9); break;
        }
    }

    // 共享设备：摄像头 + 麦克风 + 扬声器 + 分享
    private const string ToolIconSharedDevices =
        "M2,6.3H5.2L6,4.9H10L10.8,6.3H14V11.8H2ZM8,7.6A1.9,1.9 0 0,0 6.1,9.5A1.9,1.9 0 0,0 8,11.4A1.9,1.9 0 0,0 9.9,9.5A1.9,1.9 0 0,0 8,7.6M16.4,3.2A1.9,2.5 0 0,1 20.2,3.2V8A1.9,2.5 0 0,1 16.4,8ZM18.3,10.3V12M15.2,8.6C16.5,10.3 20.1,10.3 21.4,8.6M15.4,12H21.2M2.2,15.4H5.2L9.2,12.8V21.2L5.2,18.6H2.2ZM10.5,14.6C11.8,16 11.8,19.2 10.5,20.6M12.4,13.4C14.4,15.4 14.4,19.8 12.4,21.8M16.8,14.2L20.2,16.2M16.8,20.2L20.2,16.2";
    private const string ToolIconSharedDevicesAccent =
        "M16.5,13A1.15,1.15 0 0,0 15.35,14.15A1.15,1.15 0 0,0 16.5,15.3A1.15,1.15 0 0,0 17.65,14.15A1.15,1.15 0 0,0 16.5,13M21,15.05A1.15,1.15 0 0,0 19.85,16.2A1.15,1.15 0 0,0 21,17.35A1.15,1.15 0 0,0 22.15,16.2A1.15,1.15 0 0,0 21,15.05M16.5,19A1.15,1.15 0 0,0 15.35,20.15A1.15,1.15 0 0,0 16.5,21.3A1.15,1.15 0 0,0 17.65,20.15A1.15,1.15 0 0,0 16.5,19Z";

    private static ImageSource? _toolColorPickerIcon;
    private static bool _toolColorPickerIconTried;

    private static ImageSource? GetToolColorPickerIcon()
    {
        if (_toolColorPickerIconTried) return _toolColorPickerIcon;
        _toolColorPickerIconTried = true;
        try
        {
            using var stream = BrandAssets.Open(BrandAssets.ToolColorPickerJpg);
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            var bytes = ms.ToArray();
            _toolColorPickerIcon = ImageSource.FromStream(() => new MemoryStream(bytes));
        }
        catch
        {
            _toolColorPickerIcon = null;
        }

        return _toolColorPickerIcon;
    }

    private const double ToolCardMinSlot = 240;
    private bool _toolsStackHooked;
    private double _toolsHostWidthTracked;
    private bool _toolsRefreshQueued;
    private int _toolsColumnsTracked;

    private void RefreshToolsPage()
    {
        if (ToolsStack == null) return;
        EnsureToolsStackLayoutHook();
        try
        {
            var host = ResolveToolsHostWidth();
            var cols = ResolveToolColumns(host);
            _toolsColumnsTracked = cols;
            _toolsHostWidthTracked = host;

            ToolsStack.Children.Clear();
            ToolsStack.ColumnDefinitions.Clear();
            ToolsStack.RowDefinitions.Clear();
            for (var c = 0; c < cols; c++)
                ToolsStack.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));

            var running = ToolService.Instance.SharedDevices?.IsRunning == true;
            var colorRunning = ToolService.Instance.ColorPicker.IsRunning;
            var rows = ToolCardRows.Build(running, colorRunning);
            var cardW = ResolveToolCardWidth(host, cols);
            for (var i = 0; i < rows.Count; i++)
            {
                var r = i / cols;
                var c = i % cols;
                while (ToolsStack.RowDefinitions.Count <= r)
                    ToolsStack.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                var card = CreateToolCard(rows[i], cardW, fillWidth: true);
                Grid.SetRow(card, r);
                Grid.SetColumn(card, c);
                ToolsStack.Children.Add(card);
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("刷新工具页失败: " + ex.Message);
        }

        RefreshToolsWorkspace();

        // 首次进入时宿主宽度可能尚未测到：布局后再刷一次
        if (PageTools.IsVisible && _toolsHostWidthTracked < 8)
            QueueToolsRefreshAfterLayout();
    }

    private void OnToolsWorkspaceChanged()
        => MainThread.BeginInvokeOnMainThread(RefreshToolsWorkspace);

    private void OnToolsDisplayInfoChanged(object? sender, DisplayInfoChangedEventArgs e)
    {
        if (!PageTools.IsVisible) return;
        MainThread.BeginInvokeOnMainThread(RefreshToolsPage);
    }

    private void RefreshToolsWorkspace()
    {
        if (ToolsWorkspaceNavPanel == null || ToolsScroll == null || ToolsWorkspaceHost == null)
            return;

        var ws = ToolService.Instance.Workspace;
        var hasTabs = ws.HasOpenTabs;
        ToolsHintText.IsVisible = !hasTabs || ws.IsShowingCatalog;
        ToolsTitleText.IsVisible = !hasTabs;

        ToolsWorkspaceNavPanel.Children.Clear();
        if (hasTabs)
        {
            ToolsWorkspaceNavPanel.Children.Add(CreateWorkspaceCrumb(
                Loc.T("Tool.Workspace.Catalog"), ws.IsShowingCatalog, null, showSep: false));
            foreach (var tab in ws.OpenTabs)
            {
                var active = !ws.IsShowingCatalog
                    && string.Equals(ws.ActiveToolId, tab.ToolId, StringComparison.OrdinalIgnoreCase);
                ToolsWorkspaceNavPanel.Children.Add(CreateWorkspaceCrumb(tab.Title, active, tab.ToolId, showSep: true));
            }
        }

        if (ws.IsShowingCatalog || !hasTabs)
        {
            ToolsScroll.IsVisible = true;
            ToolsWorkspaceHost.IsVisible = false;
            ToolsWorkspaceHost.Content = null;
        }
        else
        {
            ToolsScroll.IsVisible = false;
            ToolsWorkspaceHost.IsVisible = true;
            if (string.Equals(ws.ActiveToolId, ToolIds.ColorPicker, StringComparison.OrdinalIgnoreCase)
                && ToolsWorkspaceHost.Content is not ColorPickerPanel)
                ToolsWorkspaceHost.Content = new ColorPickerPanel();
        }
    }

    private View CreateWorkspaceCrumb(string title, bool isActive, string? toolId, bool showSep)
    {
        var dark = MauiThemeService.IsDark(MauiThemeService.Current);
        var text = Color.FromArgb(dark ? "#E2E8F0" : "#111827");
        var muted = Color.FromArgb(dark ? "#94A3B8" : "#6B7280");
        var blue = Color.FromArgb("#2563EB");

        var row = new HorizontalStackLayout { Spacing = 0, VerticalOptions = LayoutOptions.Center };
        if (showSep)
        {
            row.Children.Add(new Label
            {
                Text = "»",
                FontSize = 15,
                FontAttributes = FontAttributes.Bold,
                TextColor = muted,
                VerticalOptions = LayoutOptions.Center,
                Margin = new Thickness(10, 0)
            });
        }

        var link = new Label
        {
            Text = title,
            FontSize = 15,
            FontAttributes = isActive ? FontAttributes.Bold : FontAttributes.None,
            TextColor = isActive ? blue : text,
            VerticalOptions = LayoutOptions.Center,
            Margin = new Thickness(0, 0, 4, 0)
        };
        var tap = new TapGestureRecognizer();
        tap.Tapped += (_, _) =>
        {
            if (string.IsNullOrEmpty(toolId))
                ToolService.Instance.Workspace.ShowCatalog();
            else
                ToolService.Instance.Workspace.Activate(toolId);
        };
        link.GestureRecognizers.Add(tap);
        row.Children.Add(link);

        if (toolId != null)
        {
            var line = Color.FromArgb(dark ? "#334155" : "#E5E7EB");
            var blueSoft = Color.FromArgb(dark ? "#1E3A5F" : "#DBEAFE");
            var close = new Button
            {
                Text = "×",
                WidthRequest = 28,
                HeightRequest = 28,
                Margin = new Thickness(8, 0, 0, 0),
                Padding = 0,
                FontSize = 13,
                BackgroundColor = Colors.Transparent,
                TextColor = muted,
                BorderColor = line,
                BorderWidth = 1,
                CornerRadius = 4
            };
            var ptr = new PointerGestureRecognizer();
            ptr.PointerEntered += (_, _) =>
            {
                close.BackgroundColor = blueSoft;
                close.TextColor = blue;
                close.BorderColor = blue;
            };
            ptr.PointerExited += (_, _) =>
            {
                close.BackgroundColor = Colors.Transparent;
                close.TextColor = muted;
                close.BorderColor = line;
            };
            close.GestureRecognizers.Add(ptr);
            close.Clicked += (_, _) =>
            {
                if (string.Equals(toolId, ToolIds.ColorPicker, StringComparison.OrdinalIgnoreCase))
                    ToolService.Instance.StopColorPickerWorkspace();
                else
                    ToolService.Instance.Workspace.Close(toolId);
                RefreshToolsPage();
            };
            row.Children.Add(close);
        }

        return row;
    }

    private void EnsureToolsStackLayoutHook()
    {
        if (ToolsStack == null || _toolsStackHooked) return;
        _toolsStackHooked = true;
        ToolsStack.SizeChanged += (_, _) => OnToolsHostSizeChanged();
        if (ToolsScroll != null)
            ToolsScroll.SizeChanged += (_, _) => OnToolsHostSizeChanged();
    }

    private void OnToolsHostSizeChanged()
    {
        if (!PageTools.IsVisible) return;
        var w = ResolveToolsHostWidth();
        var cols = ResolveToolColumns(w);
        if (w < 8) return;
        if (Math.Abs(w - _toolsHostWidthTracked) < 12 && cols == _toolsColumnsTracked)
            return;
        _toolsHostWidthTracked = w;
        _toolsColumnsTracked = cols;
        RefreshToolsPage();
    }

    private void QueueToolsRefreshAfterLayout()
    {
        if (_toolsRefreshQueued) return;
        _toolsRefreshQueued = true;
        Dispatcher.Dispatch(async () =>
        {
            try
            {
                await Task.Delay(32);
                if (!PageTools.IsVisible) return;
                RefreshToolsPage();
            }
            finally
            {
                _toolsRefreshQueued = false;
            }
        });
    }

    private double ResolveToolsHostWidth()
    {
        if (ToolsScroll?.Width > 8) return ToolsScroll.Width;
        if (ToolsStack?.Width > 8) return ToolsStack.Width;
        if (PageTools?.Width > 8) return Math.Max(0, PageTools.Width - 32);
        if (MainPane?.Width > 8) return Math.Max(0, MainPane.Width - (_compactLayout ? 40 : 64));
        return 0;
    }

    /// <summary>手机竖屏 1 列；电脑宽度够时至少 2 列；更宽/横屏继续加列。</summary>
    private int ResolveToolColumns(double hostWidth)
    {
        var phonePortrait = DeviceInfo.Idiom == DeviceIdiom.Phone
            && DeviceDisplay.MainDisplayInfo.Orientation == DisplayOrientation.Portrait;
        if (phonePortrait)
            return 1;

        if (hostWidth < 40)
            return DeviceInfo.Idiom == DeviceIdiom.Phone ? 1 : 2;

        var byWidth = Math.Max(1, (int)Math.Floor(hostWidth / ToolCardMinSlot));
        // 电脑 / 平板 / 横屏：≥400 时默认至少两列
        if (hostWidth >= 400)
            return Math.Max(2, byWidth);
        return byWidth;
    }

    private double ResolveToolCardWidth(double host, int cols)
    {
        if (host < 40)
            return cols <= 1 ? 360 : ToolCardMinSlot;
        return Math.Max(140, Math.Floor(host / cols) - 12);
    }

    /// <summary>桌面（Maui-Win 等）：单击主操作、右上角三点菜单、悬停；手机：单击 + 左滑卸载。</summary>
    private View CreateToolCard(ToolCardRow row, double cardWidth, bool fillWidth)
    {
        var dark = MauiThemeService.IsDark(MauiThemeService.Current);
        var line = ResolveThemeColor("Line") ?? Color.FromArgb(dark ? "#334155" : "#E5E7EB");
        // 卡片自带底色：深色主题用深底+浅字，避免浅底配浅字看不清
        var text = Color.FromArgb(dark ? "#E2E8F0" : "#111827");
        var muted = Color.FromArgb(dark ? "#94A3B8" : "#6B7280");
        var subtle = ResolveThemeColor("SubtleBg") ?? Color.FromArgb(dark ? "#0F172A" : "#F3F4F6");
        var blue = ResolveThemeColor("Blue") ?? Color.FromArgb("#2563EB");
        var blueHover = ResolveThemeColor("BlueHover") ?? Color.FromArgb(dark ? "#243447" : "#EFF6FF");
        var progressColor = ResolveThemeColor("BlueSoft") ?? Color.FromArgb(dark ? "#3B82F6" : "#93C5FD");
        var baseBg = Color.FromArgb(ResolveToolCardBgHex(row.VisualState, dark));

        var progress = new BoxView
        {
            Color = progressColor,
            Opacity = 0.55,
            InputTransparent = true,
            IsVisible = row.IsInstalling,
            HorizontalOptions = LayoutOptions.Start,
            VerticalOptions = LayoutOptions.Fill,
            WidthRequest = Math.Max(0, cardWidth * row.InstallProgressRatio)
        };

        var hoverWash = new BoxView
        {
            Color = blueHover,
            Opacity = 0.32,
            InputTransparent = true,
            IsVisible = false
        };

        View iconContent;
        try
        {
            var isColor = string.Equals(row.Id, ToolIds.ColorPicker, StringComparison.OrdinalIgnoreCase);
            if (isColor && GetToolColorPickerIcon() is { } colorIcon)
            {
                iconContent = new Image
                {
                    Source = colorIcon,
                    WidthRequest = 36,
                    HeightRequest = 36,
                    Aspect = Aspect.AspectFit,
                    HorizontalOptions = LayoutOptions.Center,
                    VerticalOptions = LayoutOptions.Center
                };
            }
            else
            {
                var iconBody = new Microsoft.Maui.Controls.Shapes.Path
                {
                    Data = (Geometry?)new PathGeometryConverter().ConvertFromInvariantString(ToolIconSharedDevices),
                    Stroke = text,
                    StrokeThickness = 1.4,
                    Aspect = Stretch.Uniform,
                    WidthRequest = 28,
                    HeightRequest = 28,
                    HorizontalOptions = LayoutOptions.Center,
                    VerticalOptions = LayoutOptions.Center
                };
                var iconAccent = new Microsoft.Maui.Controls.Shapes.Path
                {
                    Data = (Geometry?)new PathGeometryConverter().ConvertFromInvariantString(ToolIconSharedDevicesAccent),
                    Fill = Color.FromArgb("#F59E0B"),
                    Aspect = Stretch.Uniform,
                    WidthRequest = 28,
                    HeightRequest = 28,
                    HorizontalOptions = LayoutOptions.Center,
                    VerticalOptions = LayoutOptions.Center,
                    InputTransparent = true
                };
                iconContent = new Grid { Children = { iconBody, iconAccent } };
            }
        }
        catch
        {
            iconContent = new Label
            {
                Text = "🎙",
                FontSize = 22,
                HorizontalOptions = LayoutOptions.Center,
                VerticalOptions = LayoutOptions.Center
            };
        }

        var iconTile = new Border
        {
            WidthRequest = 48,
            HeightRequest = 48,
            Stroke = line,
            StrokeThickness = 1,
            StrokeShape = new RoundRectangle { CornerRadius = 10 },
            BackgroundColor = subtle,
            VerticalOptions = LayoutOptions.Center,
            Content = iconContent
        };

        var titleRow = new HorizontalStackLayout
        {
            Spacing = 8,
            Children =
            {
                new Label
                {
                    Text = row.Name,
                    FontAttributes = FontAttributes.Bold,
                    FontSize = 15,
                    TextColor = text,
                    LineBreakMode = LineBreakMode.TailTruncation,
                    VerticalOptions = LayoutOptions.Center
                },
                new Label
                {
                    Text = row.StatusText,
                    FontSize = 11,
                    TextColor = muted,
                    VerticalOptions = LayoutOptions.Center
                }
            }
        };

        var summary = new Label
        {
            Text = row.Summary,
            FontSize = 12,
            Margin = new Thickness(0, 4, 0, 0),
            TextColor = muted,
            LineBreakMode = LineBreakMode.WordWrap,
            MaxLines = 2,
            HorizontalOptions = LayoutOptions.Fill
        };

        var textCol = new VerticalStackLayout
        {
            VerticalOptions = LayoutOptions.Center,
            HorizontalOptions = LayoutOptions.Fill,
            Children = { titleRow, summary }
        };

        var moreBtn = new Button
        {
            Text = "⋯",
            FontSize = 16,
            FontAttributes = FontAttributes.Bold,
            TextColor = muted,
            BackgroundColor = Colors.Transparent,
            BorderWidth = 0,
            WidthRequest = 28,
            HeightRequest = 28,
            Padding = 0,
            Margin = new Thickness(0, -2, 0, 0),
            VerticalOptions = LayoutOptions.Start,
            HorizontalOptions = LayoutOptions.End
        };
        ToolTipProperties.SetText(moreBtn, "更多操作");
        moreBtn.Clicked += async (_, _) => await ShowToolCardMenuAsync(moreBtn, row);

        var body = new Grid
        {
            Padding = new Thickness(12, 10, 8, 10),
            ColumnDefinitions =
            [
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Auto)
            ],
            Children = { iconTile, textCol, moreBtn }
        };
        Grid.SetColumn(iconTile, 0);
        Grid.SetColumn(textCol, 1);
        Grid.SetColumn(moreBtn, 2);
        textCol.Margin = new Thickness(12, 0, 6, 0);

        var border = new Border
        {
            HeightRequest = 104,
            Margin = new Thickness(6),
            Stroke = line,
            StrokeThickness = 1,
            StrokeShape = new RoundRectangle { CornerRadius = 10 },
            BackgroundColor = baseBg,
            Content = new Grid { Children = { progress, hoverWash, body } }
        };
        if (fillWidth)
        {
            border.HorizontalOptions = LayoutOptions.Fill;
            border.WidthRequest = -1;
        }
        else
        {
            border.WidthRequest = cardWidth;
            border.HorizontalOptions = LayoutOptions.Start;
        }
        // 工具卡片不显示悬停说明（状态已在卡片内展示）

        var tap = new TapGestureRecognizer { NumberOfTapsRequired = 1 };
        tap.Tapped += async (_, _) =>
        {
            // 已打开则跳转工作区；停止仍走 ⋯ 菜单
            await HandleToolActionAsync(row, primary: true);
        };
        border.GestureRecognizers.Add(tap);

        var pointer = new PointerGestureRecognizer();
        pointer.PointerEntered += (_, _) =>
        {
            border.Stroke = blue;
            hoverWash.IsVisible = true;
        };
        pointer.PointerExited += (_, _) =>
        {
            border.Stroke = line;
            hoverWash.IsVisible = false;
        };
        border.GestureRecognizers.Add(pointer);

        // 手机无三点时仍可用左滑卸载
        if (DeviceInfo.Platform == DevicePlatform.Android
            || DeviceInfo.Platform == DevicePlatform.iOS)
        {
            var swipe = new SwipeGestureRecognizer { Direction = SwipeDirection.Left };
            swipe.Swiped += async (_, _) =>
            {
                if (!row.IsInstalled) return;
                var ok = await DisplayAlertAsync(
                    Loc.T("Tool.Uninstall"),
                    row.Name,
                    Loc.T("Tool.Uninstall"),
                    Loc.T("Tool.SharedDevices.Cancel"));
                if (ok)
                    await HandleToolActionAsync(row, primary: false);
            };
            border.GestureRecognizers.Add(swipe);
        }

        return border;
    }

    private static IEnumerable<(string Text, string Action)> EnumerateToolMenuActions(ToolCardRow row)
    {
        if (!row.IsInstalled)
        {
            yield return (Loc.T("Tool.Install"), "primary");
            yield break;
        }

        if (row.CanStart)
            yield return (Loc.T("Tool.Start"), "primary");
        if (row.IsRunning)
            yield return (Loc.T("Tool.Stop"), "stop");
        if (string.Equals(row.Id, ToolIds.SharedDevices, StringComparison.OrdinalIgnoreCase))
        {
            if (row.CanConsume)
                yield return (Loc.T("Tool.Test"), "test");
            if (row.CanProvide)
                yield return (Loc.T("Tool.Manage"), "manage");
        }

        if (row.SecondaryEnabled || row.IsInstalled)
            yield return (Loc.T("Tool.Uninstall"), "uninstall");
    }

    /// <summary>三点按钮弹出操作菜单。</summary>
    private async Task ShowToolCardMenuAsync(View anchor, ToolCardRow row)
    {
#if WINDOWS
        if (anchor.Handler?.PlatformView is Microsoft.UI.Xaml.FrameworkElement native)
        {
            var flyout = new Microsoft.UI.Xaml.Controls.MenuFlyout();
            foreach (var (text, action) in EnumerateToolMenuActions(row))
            {
                var item = new Microsoft.UI.Xaml.Controls.MenuFlyoutItem { Text = text };
                var act = action;
                item.Click += async (_, _) => await InvokeToolMenuActionAsync(row, act);
                flyout.Items.Add(item);
            }

            flyout.ShowAt(native);
            return;
        }
#endif
        var actions = EnumerateToolMenuActions(row).Select(a => a.Text).ToArray();
        if (actions.Length == 0) return;
        var pick = await DisplayActionSheetAsync(
            row.Name,
            Loc.T("Tool.SharedDevices.Cancel"),
            null,
            actions);
        if (string.IsNullOrEmpty(pick) || pick == Loc.T("Tool.SharedDevices.Cancel"))
            return;
        foreach (var (text, action) in EnumerateToolMenuActions(row))
        {
            if (string.Equals(text, pick, StringComparison.Ordinal))
            {
                await InvokeToolMenuActionAsync(row, action);
                break;
            }
        }
    }

    private async Task InvokeToolMenuActionAsync(ToolCardRow row, string action)
    {
        if (action == "test")
        {
            var page = new SharedDevicesTestPage();
            await Navigation.PushModalAsync(page, animated: true);
            await page.Closed;
            return;
        }

        if (action == "manage")
        {
            var sd = ToolService.Instance.SharedDevices;
            if (sd == null) return;
            var page = new SharedDevicesManagePage(sd);
            await Navigation.PushModalAsync(page, animated: true);
            await page.Closed;
            return;
        }

        if (action == "stop")
        {
            await HandleToolActionAsync(row, primary: true, fromMenu: true);
            return;
        }

        await HandleToolActionAsync(row, primary: action != "uninstall");
    }

    private static Color? ResolveThemeColor(string key)
    {
        if (Application.Current?.Resources.TryGetValue(key, out var v) == true && v is Color c)
            return c;
        return null;
    }

    private static string ResolveToolCardBgHex(string visualState, bool dark)
        => visualState switch
        {
            ToolVisualStates.Running => dark ? "#1E3A5F" : "#DBEAFE",
            ToolVisualStates.Installed => dark ? "#0F3D2E" : "#ECFDF5",
            ToolVisualStates.Installing => dark ? "#243447" : "#EFF6FF",
            _ => dark ? "#1E293B" : "#F3F4F6"
        };

    private async Task HandleToolActionAsync(ToolCardRow row, bool primary, bool fromMenu = false)
    {
        try
        {
            if (!row.IsInstalled && primary)
            {
                if (row.IsInstalling) return;
                var r = await ToolService.Instance.InstallAsync(row.Id);
                StatusLabel.Text = r.Message;
                if (!r.Success)
                    await DisplayAlertAsync(Loc.T("Tool.Install"), r.Message, "OK");
                RefreshToolsPage();
                return;
            }

            if (string.Equals(row.Id, ToolIds.ColorPicker, StringComparison.OrdinalIgnoreCase))
            {
                if (primary && row.IsRunning)
                {
                    if (!fromMenu)
                    {
                        // 目录页再次点卡片 → 跳回已打开的工作区
                        ToolService.Instance.OpenColorPickerWorkspace();
                        RefreshToolsPage();
                        return;
                    }

                    ToolService.Instance.StopColorPickerWorkspace();
                    RefreshToolsPage();
                    return;
                }

                if (primary && row.CanStart)
                {
                    ToolService.Instance.OpenColorPickerWorkspace();
                    RefreshToolsPage();
                    return;
                }

                if (!primary && row.IsInstalled)
                {
                    var r = await ToolService.Instance.UninstallAsync(row.Id);
                    StatusLabel.Text = r.Message;
                    RefreshToolsPage();
                }

                return;
            }

            if (string.Equals(row.Id, ToolIds.SharedDevices, StringComparison.OrdinalIgnoreCase))
            {
                if (primary && row.IsRunning)
                {
                    if (!fromMenu) return; // 仅 ⋯ 菜单可停止
                    var sd = ToolService.Instance.SharedDevices;
                    if (sd != null) await sd.StopAsync();
                    RefreshToolsPage();
                    return;
                }

                if (primary && row.CanStart)
                {
                    await StartSharedDevicesAsync();
                    return;
                }

                if (!primary && row.IsInstalled)
                {
                    var r = await ToolService.Instance.UninstallAsync(row.Id);
                    StatusLabel.Text = r.Message;
                    RefreshToolsPage();
                }

                return;
            }

            if (!primary && row.IsInstalled)
            {
                var ur = await ToolService.Instance.UninstallAsync(row.Id);
                StatusLabel.Text = ur.Message;
                RefreshToolsPage();
            }
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync(Loc.T("Tool.Title"), ex.Message, "OK");
        }
    }

    private async Task StartSharedDevicesAsync()
    {
        var sd = ToolService.Instance.SharedDevices;
        if (sd == null)
        {
            await DisplayAlertAsync(Loc.T("Tool.Title"), "共享设备服务未就绪", "OK");
            return;
        }

        var page = new SharedDevicesStartPage(sd.ListApplicantTargets());
        await Navigation.PushModalAsync(page, animated: true);
        var (peer, kinds) = await page.Result;
        if (string.IsNullOrEmpty(peer) || kinds.Length == 0)
            return;

        var (ok, message) = await sd.StartRequestAsync(peer, kinds);
        StatusLabel.Text = message;
        if (!ok)
            await DisplayAlertAsync(Loc.T("Tool.SharedDevices.StartTitle"), message, "OK");
        RefreshToolsPage();
    }

    private void OnSharedDevicesIncomingRequest(SharedDevicesIncomingRequest req)
    {
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            var sd = ToolService.Instance.SharedDevices;
            if (sd == null) return;
            var page = new SharedDevicesIncomingPage(sd, req);
            await Navigation.PushModalAsync(page, animated: true);
            var (accept, remember, devices) = await page.Result;
            if (!accept)
                await sd.RejectIncomingAsync(req);
            else
                await sd.AcceptIncomingAsync(req, remember, devices);
            RefreshToolsPage();
        });
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
        foreach (var p in ProgramService.Instance.Catalog.Items)
            p.IsSelected = e.Value;
    }

    private Task PushModalAsync(Page page)
        => Navigation.PushModalAsync(new NavigationPage(page));

    private async Task<UiConfirmChoice> ShowConfirmAsync(ConfirmPage page)
    {
        // 无 NavigationPage 外壳，避免顶栏；半透明遮罩直接盖主页
        await Navigation.PushModalAsync(page, animated: true);
        return await page.Result;
    }

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
        // 用导航栈而非 Modal，便于 XAML 实时预览跟随到编辑页
        await Navigation.PushAsync(page);
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
        if (prog.IsBuiltin
            || string.Equals(prog.Id, BuiltinPrograms.GenericSwipeId, StringComparison.OrdinalIgnoreCase))
        {
            await DisplayAlertAsync(Loc.T("Prog.ActDelete"), Loc.T("Prog.BuiltinNoDelete"), "OK");
            return;
        }
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
        _logPageFromLatest = 0;
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
        var baseHint = string.IsNullOrEmpty(_logProgramName)
            ? Loc.T("Log.Hint")
            : string.Format(Loc.T("Prog.LogFilter"), _logProgramName);
        LogHintText.Text = baseHint + " " + Loc.T("Log.SelectTip");
    }

    private AppLogLevelFilter CurrentLogLevelFilter()
    {
        return LogLevelFilter.SelectedIndex switch
        {
            1 => AppLogLevelFilter.Debug,
            2 => AppLogLevelFilter.Info,
            3 => AppLogLevelFilter.Warn,
            4 => AppLogLevelFilter.Error,
            _ => AppLogLevelFilter.All
        };
    }

    private void EnsureLogLevelFilterItems()
    {
        var selected = CurrentLogLevelFilter();
        _logFilterReady = false;
        LogLevelFilter.Items.Clear();
        LogLevelFilter.Items.Add(Loc.T("Log.LevelAll"));
        LogLevelFilter.Items.Add(Loc.T("Log.LevelDebug"));
        LogLevelFilter.Items.Add(Loc.T("Log.LevelInfo"));
        LogLevelFilter.Items.Add(Loc.T("Log.LevelWarn"));
        LogLevelFilter.Items.Add(Loc.T("Log.LevelError"));
        LogLevelFilter.SelectedIndex = selected switch
        {
            AppLogLevelFilter.Debug => 1,
            AppLogLevelFilter.Info => 2,
            AppLogLevelFilter.Warn => 3,
            AppLogLevelFilter.Error => 4,
            _ => 0
        };
        _logFilterReady = true;
        if (LogList.ItemsSource != _logItems)
            LogList.ItemsSource = _logItems;
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
            _logItems.Add(new LogListItem(e));
    }

    private void ApplyLogToolbarLabels(bool iconOnly)
    {
        if (LogPrevBtn == null) return;
        _logToolsIconOnly = iconOnly;
        LogPrevBtn.Text = iconOnly ? LogIcPrev : $"{LogIcPrev}  {Loc.T("Log.Prev")}";
        LogNextBtn.Text = iconOnly ? LogIcNext : $"{LogIcNext}  {Loc.T("Log.Next")}";
        LogLatestBtn.Text = iconOnly ? LogIcLatest : $"{LogIcLatest}  {Loc.T("Log.Latest")}";
        LogDateBtn.Text = iconOnly ? LogIcDate : $"{LogIcDate}  {Loc.T("Log.Date")}";
        LogDownloadBtn.Text = iconOnly ? LogIcDownload : $"{LogIcDownload}  {Loc.T("Log.Download")}";
        LogCopyBtn.Text = iconOnly ? LogIcCopy : $"{LogIcCopy}  {Loc.T("Log.Copy")}";
        LogClearBtn.Text = iconOnly ? LogIcClear : $"{LogIcClear}  {Loc.T("Log.Clear")}";
        // 覆盖 NormalBtn 默认 MinimumWidthRequest=88，窄屏图标钮约 30px
        var minW = iconOnly ? 30.0 : 0.0;
        var maxW = iconOnly ? 30.0 : 120.0;
        var pad = iconOnly ? new Thickness(0) : new Thickness(8, 0);
        foreach (var b in new[] { LogPrevBtn, LogNextBtn, LogLatestBtn, LogDateBtn, LogDownloadBtn, LogCopyBtn, LogClearBtn })
        {
            b.Padding = pad;
            b.MinimumWidthRequest = minW;
            b.MaximumWidthRequest = maxW;
            b.WidthRequest = iconOnly ? 30 : -1;
        }
        var hostW = LogToolBarHost?.Width ?? 0;
        LogLevelFilter.WidthRequest = iconOnly
            ? (hostW > 0 && hostW < 240 ? 72 : 84)
            : 96;
        ToolTipProperties.SetText(LogPrevBtn, Loc.T("Log.Prev"));
        ToolTipProperties.SetText(LogNextBtn, Loc.T("Log.Next"));
        ToolTipProperties.SetText(LogLatestBtn, Loc.T("Log.Latest"));
        ToolTipProperties.SetText(LogDateBtn, Loc.T("Log.Date"));
        ToolTipProperties.SetText(LogDownloadBtn, Loc.T("Log.Download"));
        ToolTipProperties.SetText(LogCopyBtn, Loc.T("Log.SelectTip"));
        ToolTipProperties.SetText(LogClearBtn, Loc.T("Log.Clear"));
    }

    private void LogToolBarHost_SizeChanged(object? sender, EventArgs e)
    {
        if (LogToolBarHost == null || Width <= 1) return;
        // 整行工具栏（标题已分行）：约 5×30+筛选 仍放不下时再压图标
        var forceIcon = _compactLayout || Width < 720
                        || (LogToolBarHost.Width > 1 && LogToolBarHost.Width < 360);
        ApplyLogToolbarLabels(forceIcon);
    }

    private void ScrollLogToEnd()
    {
        if (_logItems.Count == 0) return;
        try
        {
            LogList.ScrollTo(_logItems[^1], position: ScrollToPosition.End, animate: false);
        }
        catch
        {
            // CollectionView 尚未布局完成时忽略
        }
    }

    private bool LogItemsSameTail(IReadOnlyList<AppLogEntry> items)
    {
        if (_logItems.Count != items.Count) return false;
        if (items.Count == 0) return true;
        return ReferenceEquals(_logItems[0].Entry, items[0])
               && ReferenceEquals(_logItems[^1].Entry, items[^1]);
    }

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
            _logWatermark = _logItems.Count > 0 ? _logItems[^1].Entry : null;
            if (uiChanged)
                _logKnownTotal = Math.Max(_logKnownTotal, _logItems.Count);
        }
        else if (delta.Items.Count > 0)
        {
            foreach (var e in delta.Items)
                _logItems.Add(new LogListItem(e));
            _logWatermark = _logItems[^1].Entry;
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
            ScrollLogToEnd();
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
            var delta = AppLog.DeltaSince(
                null, level, _logProgramId, _logProgramName, AppLog.DefaultPageSize, _logFrom, _logTo);
            if (delta.Items.Count > 0)
                ReplaceLogItems(delta.Items);
            else
                ReplaceLogItems(page.Items);
            _logWatermark = _logItems.Count > 0 && delta.Items.Count > 0 ? _logItems[^1].Entry : null;
            _logStickToBottom = true;
            ScrollLogToEnd();
        }
        else
        {
            ReplaceLogItems(page.Items);
            _logWatermark = null;
            _logStickToBottom = page.PageFromLatest == 0;
            if (_logStickToBottom)
                ScrollLogToEnd();
        }
    }

    private void LogRow_Tapped(object? sender, TappedEventArgs e)
    {
        // sender 可能是 GestureRecognizer 或 Border
        var item = (sender as BindableObject)?.BindingContext as LogListItem
                   ?? ((sender as TapGestureRecognizer)?.Parent as BindableObject)?.BindingContext as LogListItem;
        if (item == null) return;
        item.IsSelected = !item.IsSelected;
    }

    private void LogList_Scrolled(object? sender, ItemsViewScrolledEventArgs e)
    {
        _logStickToBottom = e.LastVisibleItemIndex >= Math.Max(0, _logItems.Count - 3);
    }

    private void LogFilter_Changed(object? sender, EventArgs e)
    {
        if (!_logFilterReady) return;
        _logPageFromLatest = 0;
        if (PageLog.IsVisible)
            RefreshLogPage();
    }

    private void LogPrev_Clicked(object? sender, EventArgs e)
    {
        _logPageFromLatest++;
        RefreshLogPage();
    }

    private void LogNext_Clicked(object? sender, EventArgs e)
    {
        if (_logPageFromLatest > 0)
            _logPageFromLatest--;
        RefreshLogPage();
    }

    private void LogLatest_Clicked(object? sender, EventArgs e)
    {
        _logFrom = null;
        _logTo = null;
        _logPageFromLatest = 0;
        RefreshLogPage();
    }

    private async void LogDate_Clicked(object? sender, EventArgs e)
    {
        var page = new LogDateRangePage(_logFrom, _logTo);
        NavigationPage.SetHasNavigationBar(page, false);
        await Navigation.PushModalAsync(page).ConfigureAwait(true);
        // 模态关闭后读取结果
        if (page.Result == LogDateRangePage.ResultKind.Cancel) return;
        if (page.Result == LogDateRangePage.ResultKind.Clear)
        {
            _logFrom = null;
            _logTo = null;
        }
        else
        {
            _logFrom = page.From;
            _logTo = page.To;
        }
        _logPageFromLatest = 0;
        RefreshLogPage();
    }

    private void LogClear_Clicked(object? sender, EventArgs e)
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

    private async void LogCopy_Clicked(object? sender, EventArgs e)
    {
        var selected = _logItems.Where(i => i.IsSelected).ToList();
        if (selected.Count == 0)
        {
            StatusLabel.Text = Loc.T("Log.CopyEmpty");
            return;
        }

        var text = string.Join(Environment.NewLine, selected.Select(i => i.Display));
        try
        {
            await Clipboard.Default.SetTextAsync(text).ConfigureAwait(true);
            StatusLabel.Text = string.Format(Loc.T("Log.CopyOk"), selected.Count);
        }
        catch (Exception ex)
        {
            StatusLabel.Text = ex.Message;
        }
    }

    private async void LogDownload_Clicked(object? sender, EventArgs e)
    {
        var text = AppLog.FormatExport(
            CurrentLogLevelFilter(),
            _logProgramId,
            _logProgramName,
            _logFrom,
            _logTo);
        if (string.IsNullOrEmpty(text))
        {
            await DisplayAlertAsync(Loc.T("Log.Download"), Loc.T("Log.DownloadEmpty"), "OK");
            return;
        }

        var fileName = AppLog.SuggestExportFileName();
        try
        {
#if ANDROID
            var saved = await TrySaveLogToAndroidDownloadsAsync(fileName, text).ConfigureAwait(true);
            if (saved != null)
            {
                await DisplayAlertAsync(Loc.T("Log.Download"),
                    string.Format(Loc.T("Log.DownloadOk"), saved), "OK");
                return;
            }
#endif
            var path = System.IO.Path.Combine(FileSystem.CacheDirectory, fileName);
            await File.WriteAllTextAsync(path, text, new UTF8Encoding(false)).ConfigureAwait(true);
            await Share.Default.RequestAsync(new ShareFileRequest
            {
                Title = Loc.T("Log.Download"),
                File = new ShareFile(path, "text/plain")
            }).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync(Loc.T("Log.Download"),
                string.Format(Loc.T("Log.DownloadFail"), ex.Message), "OK");
        }
    }

#if ANDROID
    private static async Task<string?> TrySaveLogToAndroidDownloadsAsync(string fileName, string text)
    {
        try
        {
            var activity = Platform.CurrentActivity;
            var resolver = activity?.ContentResolver;
            if (resolver == null) return null;

            if (!OperatingSystem.IsAndroidVersionAtLeast(29))
                return null;

            var values = new Android.Content.ContentValues();
            values.Put(Android.Provider.MediaStore.IMediaColumns.DisplayName, fileName);
            values.Put(Android.Provider.MediaStore.IMediaColumns.MimeType, "text/plain");
            values.Put(Android.Provider.MediaStore.IMediaColumns.RelativePath,
                Android.OS.Environment.DirectoryDownloads);

            var collection = Android.Provider.MediaStore.Downloads.ExternalContentUri;
            if (collection == null) return null;
            var uri = resolver.Insert(collection, values);
            if (uri == null) return null;

            await using var stream = resolver.OpenOutputStream(uri);
            if (stream == null) return null;
            var bytes = Encoding.UTF8.GetBytes(text);
            await stream.WriteAsync(bytes).ConfigureAwait(false);
            await stream.FlushAsync().ConfigureAwait(false);
            return System.IO.Path.Combine(Android.OS.Environment.DirectoryDownloads ?? "Download", fileName);
        }
        catch
        {
            return null;
        }
    }
#endif

    /// <summary>手机端高级选项：通知/无障碍注入（Android 完整能力；iOS 通知本地提示，无障碍受系统限制）。</summary>
    private static bool SupportsPhoneInjectUi()
        => DeviceInfo.Platform == DevicePlatform.Android
           || DeviceInfo.Platform == DevicePlatform.iOS;

    private void WireNotifyInjectUi()
    {
        NotifyInjectRow.IsVisible = SupportsPhoneInjectUi();
        if (!NotifyInjectRow.IsVisible) return;
        if (DeviceInfo.Platform == DevicePlatform.iOS)
            NotifyInjectTipLabel.Text = Loc.T("Opt.NotifyInjectTip.iOS");
    }

    private void SyncNotifyInjectCheck()
    {
        if (!NotifyInjectRow.IsVisible) return;
        _notifyInjectUpdating = true;
        try { NotifyInjectCheck.IsChecked = _host.Settings.NotificationInject; }
        finally { _notifyInjectUpdating = false; }
    }

    private async void NotifyInject_Changed(object? sender, CheckedChangedEventArgs e)
    {
        // 仅改通知注入；不影响无障碍注入勾选/服务状态
        if (_notifyInjectUpdating || !SupportsPhoneInjectUi()) return;
        var want = e.Value;
        _host.Settings.NotificationInject = want;
        _host.Settings.Save();

#if ANDROID
        if (want && OperatingSystem.IsAndroidVersionAtLeast(33))
        {
            try
            {
                var status = await Permissions.CheckStatusAsync<Permissions.PostNotifications>();
                if (status != PermissionStatus.Granted)
                    status = await Permissions.RequestAsync<Permissions.PostNotifications>();
                if (status != PermissionStatus.Granted)
                    StatusLabel.Text = "未授予通知权限：系统可能不显示保活通知";
            }
            catch { /* ignore */ }
        }

        Platforms.Android.AndroidProgramWakeLock.RefreshNotificationInject(want);
        StatusLabel.Text = want
            ? "已开启通知注入：有子程序运行时将显示前台通知"
            : "已关闭通知注入：不再使用通知保活";
#elif IOS && !MACCATALYST
        if (want)
        {
            var granted = await Platforms.iOS.IosProgramNotifyHelper.RequestPermissionAsync();
            if (!granted)
                StatusLabel.Text = "未授予通知权限：系统可能不显示运行通知";
        }

        Platforms.iOS.IosProgramNotifyHelper.RefreshNotificationInject(want);
        StatusLabel.Text = want
            ? "已开启通知注入：有子程序运行时将显示本地通知"
            : "已关闭通知注入：不再显示运行通知";
#else
        await Task.CompletedTask;
#endif
    }

    /// <summary>勾选无障碍时顺带打开通知注入（可再单独取消；取消不影响无障碍）。</summary>
    private void EnsureNotificationInjectOnFromA11y()
    {
        if (!SupportsPhoneInjectUi()) return;
        if (_host.Settings.NotificationInject && NotifyInjectCheck.IsChecked == true)
            return;
        _host.Settings.NotificationInject = true;
        _host.Settings.Save();
        _notifyInjectUpdating = true;
        try { NotifyInjectCheck.IsChecked = true; }
        finally { _notifyInjectUpdating = false; }
#if ANDROID
        Platforms.Android.AndroidProgramWakeLock.RefreshNotificationInject(true);
        _ = RequestNotifyPermissionQuietAsync();
#elif IOS && !MACCATALYST
        Platforms.iOS.IosProgramNotifyHelper.RefreshNotificationInject(true);
        _ = Platforms.iOS.IosProgramNotifyHelper.RequestPermissionAsync();
#endif
    }

#if ANDROID
    private static async Task RequestNotifyPermissionQuietAsync()
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(33)) return;
        try
        {
            var status = await Permissions.CheckStatusAsync<Permissions.PostNotifications>();
            if (status != PermissionStatus.Granted)
                await Permissions.RequestAsync<Permissions.PostNotifications>();
        }
        catch { /* ignore */ }
    }
#endif

    private void WireA11yInjectUi()
    {
        A11yInjectRow.IsVisible = SupportsPhoneInjectUi();
        if (!A11yInjectRow.IsVisible) return;

        if (DeviceInfo.Platform == DevicePlatform.iOS)
        {
            A11yInjectTipLabel.Text = Loc.T("Opt.A11yInjectTip.iOS");
            return;
        }

#if ANDROID
        if (_a11yInjectWired) return;
        _a11yInjectWired = true;
        Platforms.Android.FlyScreenInputAccessibilityService.EnsureSettingsWatch();
        Platforms.Android.FlyScreenInputAccessibilityService.EnabledChanged += () =>
            MainThread.BeginInvokeOnMainThread(SyncA11yInjectCheck);
#endif
    }

    private void SyncA11yInjectCheck()
    {
        if (!A11yInjectRow.IsVisible) return;
        _a11yInjectUpdating = true;
        try
        {
#if ANDROID
            // 只跟服务是否真的在跑。系统里开关还在、服务已停止时取消勾选。
            A11yInjectCheck.IsChecked = Platforms.Android.FlyScreenInputAccessibilityService.IsConnected;
#else
            // iOS 无跨应用注入服务，保持未勾选
            A11yInjectCheck.IsChecked = false;
#endif
        }
        finally
        {
            _a11yInjectUpdating = false;
        }
    }

    /// <summary>从系统设置返回时可调用，刷新无障碍勾选状态。绑定可能略晚，补两次。</summary>
    public void RefreshA11yInjectFromResume()
    {
        SyncA11yInjectCheck();
        var gen = ++_a11yRefreshGen;
        Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(400), () =>
        {
            if (gen == _a11yRefreshGen) SyncA11yInjectCheck();
        });
        Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(1200), () =>
        {
            if (gen == _a11yRefreshGen) SyncA11yInjectCheck();
        });
    }

    private async void A11yInject_Changed(object? sender, CheckedChangedEventArgs e)
    {
        if (_a11yInjectUpdating || !SupportsPhoneInjectUi()) return;

#if ANDROID
        var wantOn = e.Value;
        var connected = Platforms.Android.FlyScreenInputAccessibilityService.IsConnected;
        var listed = Platforms.Android.FlyScreenInputAccessibilityService.IsListedInSecureSettings();

        if (wantOn)
        {
            // 开启无障碍时自动勾选通知注入；之后可单独取消通知，不牵动无障碍
            EnsureNotificationInjectOnFromA11y();
            if (connected) return;
            // 设置里已开但未连上：仍引导去设置重新开关
            var go = await DisplayAlertAsync(
                Loc.T("Opt.A11yInjectOpenTitle"),
                Loc.T("Opt.A11yInjectOpenBody"),
                Loc.T("Opt.A11yInjectGo"),
                Loc.T("Opt.A11yInjectLater"));
            if (go)
                Platforms.Android.FlyScreenInputAccessibilityService.OpenAccessibilitySettings();
            // 未真正连上前：设置已开则保持勾选，否则弹回
            SyncA11yInjectCheck();
            return;
        }

        // 关闭无障碍：不改动通知注入勾选。停用是异步的，成功时等服务断开再刷新，避免马上被旧状态勾回去。
        if (connected)
        {
            if (!Platforms.Android.FlyScreenInputAccessibilityService.TryDisableSelf())
                SyncA11yInjectCheck();
            return;
        }

        if (listed)
            Platforms.Android.FlyScreenInputAccessibilityService.OpenAccessibilitySettings();
        SyncA11yInjectCheck();
#elif IOS && !MACCATALYST
        if (e.Value)
        {
            await DisplayAlertAsync(
                Loc.T("Opt.A11yInjectOpenTitle"),
                Loc.T("Opt.A11yInjectOpenBody.iOS"),
                "确定");
            SyncA11yInjectCheck();
        }
#else
        await Task.CompletedTask;
#endif
    }

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

    private async void SyncKey_Clicked(object? sender, EventArgs e)
    {
        var prevKey = _host.Settings.SecurityKey ?? "";
        _host.Settings.SecurityKey = (KeyEntry.Text ?? "").Trim();
        _host.Settings.Save();
        var (ok, msg) = await _host.SyncSecurityKeyAsync();
        if (ok)
        {
            await _host.ApplyNetworkingAsync(
                _host.Settings.TcpPort,
                _host.Settings.UdpPort,
                _host.Settings.PreferredNicId ?? "",
                _host.Settings.PreferredLocalIp ?? "",
                prevKey);
        }
        StatusLabel.Text = msg;
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

        var prevTcp = _host.Settings.TcpPort;
        var prevUdp = _host.Settings.UdpPort;
        var prevNic = _host.Settings.PreferredNicId ?? "";
        var prevIp = _host.Settings.PreferredLocalIp ?? "";
        var prevKey = _host.Settings.SecurityKey ?? "";

        var wantAutoStart = AutoStartCheck.IsChecked == true;
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
            wantAutoStart,
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
        if (PageTools.IsVisible)
            RefreshToolsPage();
        // 本地化会改 Radio 文案；再强制字色（含延迟，避免 WinUI 仍用旧主题前景）
        RefreshOptionsRadioChrome();
        Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(80), RefreshOptionsRadioChrome);
        Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(250), RefreshOptionsRadioChrome);
        RebuildMatrix();
        if (layoutMerge)
            await _host.BroadcastLayoutMergeAsync();
        else
            await _host.BroadcastLayoutSyncAsync();
        await _host.BroadcastBehaviorSyncAsync();
        // 默认保持连接；仅端口/网卡/密钥变化才整网重启
        await _host.ApplyNetworkingAsync(prevTcp, prevUdp, prevNic, prevIp, prevKey);
        if (_host.Platform.SupportsAutoStart
            && _host.Platform.GetAutoStartEnabled() != wantAutoStart)
        {
            StatusLabel.Text = wantAutoStart
                ? "开机自启设置失败（需权限创建计划任务）"
                : "关闭开机自启失败";
        }
        else
            StatusLabel.Text = Loc.T("Sys.Applied");
    }

    public async Task OpenProjectionDialogAsync()
    {
        if (_host.ScreenExtend.IsActive
            && _host.ScreenExtend.Role == FlyScreen.Services.ScreenExtend.ScreenExtendRole.Sink)
        {
            await DisplayAlertAsync(
                "投影",
                FlyScreen.Services.ScreenExtend.ScreenExtendConflict.DescribeLocalBusy(
                    FlyScreen.Services.ScreenExtend.ScreenExtendRole.Sink,
                    _host.ScreenExtend.Peer),
                "确定");
            return;
        }

        // 无 NavigationPage，避免系统顶栏白条；页内自定义标题 + 居中卡片
        await Navigation.PushModalAsync(new ProjectionDialogPage(_host), animated: true);
#if WINDOWS
        // Modal 会重置 WinUI 标题栏模板为浅色默认 → 跟刷，避免左右两截
        Platforms.Windows.MauiWindowChrome.RefreshAfterModal();
#endif
    }

    private void PhoneProject_Clicked(object? sender, EventArgs e)
        => _ = RunProjectionEntryAsync();

    /// <summary>主界面 / 托盘投影入口：进行中则停止，接收中则提示，否则弹窗。</summary>
    private async Task RunProjectionEntryAsync()
    {
        try
        {
            if (_host.ScreenExtend.IsActive
                && _host.ScreenExtend.Role == FlyScreen.Services.ScreenExtend.ScreenExtendRole.Host)
            {
                await _host.StopProjectionAsync();
                RefreshPhoneProjectButton();
                return;
            }

            await OpenProjectionDialogAsync();
            RefreshPhoneProjectButton();
        }
        catch
        {
            /* ignore */
        }
    }

    private void RefreshPhoneProjectButton()
    {
        if (PhoneProjectBtn == null) return;
        var phase = ProjectionUi.Resolve(_host.ScreenExtend);
        var chrome = ProjectionUi.Title(phase);
        PhoneProjectBtn.Text = ProjectionUi.FormatLine(chrome);
        PhoneProjectBtn.Style = (Style)Application.Current!.Resources[
            chrome.Emphasized ? "SegBtnActive" : "NormalBtn"];
#if WINDOWS
        Platforms.Windows.MauiProjectionTitleBar.Refresh();
#endif
    }

    /// <summary>
    /// Maui-Win：内容放得下时禁用高级页滚动，避免滚轮橡皮筋「微压缩」；
    /// 窗口过矮仍允许纵向滚动。手机端保持默认可滚。
    /// </summary>
    private void EnsureOptionsScrollPolicy()
    {
#if WINDOWS
        if (PageOptions == null) return;
        if (!_optionsScrollHooked)
        {
            _optionsScrollHooked = true;
            PageOptions.SizeChanged += (_, _) => UpdateOptionsScrollPolicy();
            PageOptions.HandlerChanged += (_, _) => Dispatcher.Dispatch(UpdateOptionsScrollPolicy);
            if (OptionsContentRoot != null)
                OptionsContentRoot.SizeChanged += (_, _) => UpdateOptionsScrollPolicy();
            // 默认先关滚动，布局后再按需打开
            PageOptions.Orientation = ScrollOrientation.Neither;
        }

        UpdateOptionsScrollPolicy();
#endif
    }

    private void UpdateOptionsScrollPolicy()
    {
#if WINDOWS
        if (PageOptions == null) return;
        var viewport = PageOptions.Height;
        var contentH = OptionsContentRoot?.Height ?? 0;
        var needScroll = PageOptions.IsVisible
                         && viewport > 1
                         && contentH > viewport + 2;
        PageOptions.Orientation = needScroll ? ScrollOrientation.Vertical : ScrollOrientation.Neither;
        if (PageOptions.Handler?.PlatformView is Microsoft.UI.Xaml.Controls.ScrollViewer sv)
        {
            sv.VerticalScrollMode = needScroll
                ? Microsoft.UI.Xaml.Controls.ScrollMode.Enabled
                : Microsoft.UI.Xaml.Controls.ScrollMode.Disabled;
            sv.VerticalScrollBarVisibility = needScroll
                ? Microsoft.UI.Xaml.Controls.ScrollBarVisibility.Auto
                : Microsoft.UI.Xaml.Controls.ScrollBarVisibility.Hidden;
            sv.IsScrollInertiaEnabled = needScroll;
        }
#endif
    }

    private void LoadEdgeCrossModeUi(int mode)
    {
        EdgeCrossBarrierRadio.IsChecked = mode is < 1 or > 3;
        EdgeCrossDoubleRadio.IsChecked = mode == 1;
        EdgeCrossSpeedRadio.IsChecked = mode == 2;
        EdgeCrossHotkeyRadio.IsChecked = mode == 3;
        RefreshEdgeCrossTip();
    }

    private int ReadEdgeCrossModeUi()
    {
        if (EdgeCrossDoubleRadio.IsChecked == true) return 1;
        if (EdgeCrossSpeedRadio.IsChecked == true) return 2;
        if (EdgeCrossHotkeyRadio.IsChecked == true) return 3;
        return 0;
    }

    private void EdgeCrossMode_Changed(object? sender, CheckedChangedEventArgs e)
    {
        if (sender is RadioButton { IsChecked: not true }) return;
        RefreshEdgeCrossTip();
    }

    private void EnsureEdgeCrossTipWidthHook()
    {
        if (_edgeCrossTipWidthHooked) return;
        _edgeCrossTipWidthHooked = true;
        if (EdgeCrossColumn != null)
            EdgeCrossColumn.SizeChanged += (_, _) => ConstrainEdgeCrossTipWidth();
        if (OptionsFeatGrid != null)
            OptionsFeatGrid.SizeChanged += (_, _) => ConstrainEdgeCrossTipWidth();
        if (EdgeCrossTipText != null)
            EdgeCrossTipText.HandlerChanged += (_, _) => ConstrainEdgeCrossTipWidth();
    }

    /// <summary>
    /// Maui-Win Label 在 VerticalStack 里常按无限宽测量，WordWrap 无效；
    /// 必须把列宽写进 WidthRequest，并同步原生 TextBlock.TextWrapping。
    /// </summary>
    private void ConstrainEdgeCrossTipWidth()
    {
        if (EdgeCrossTipText == null) return;
        EnsureEdgeCrossTipWidthHook();

        double w = 0;
        if (EdgeCrossColumn is { Width: > 1 })
            w = EdgeCrossColumn.Width;
        else if (OptionsFeatGrid is { Width: > 1 })
            w = (OptionsFeatGrid.Width - OptionsFeatGrid.ColumnSpacing * 2) / 3.0;

        if (w < 8)
            return;

        // 略留余量，避免贴边裁切
        var tipW = Math.Max(40, w - 2);
        if (Math.Abs(EdgeCrossTipText.WidthRequest - tipW) > 0.5)
            EdgeCrossTipText.WidthRequest = tipW;

        EdgeCrossTipText.LineBreakMode = LineBreakMode.WordWrap;
        EdgeCrossTipText.MaxLines = 8;

#if WINDOWS
        if (EdgeCrossTipText.Handler?.PlatformView is Microsoft.UI.Xaml.Controls.TextBlock tb)
        {
            tb.TextWrapping = Microsoft.UI.Xaml.TextWrapping.WrapWholeWords;
            tb.Width = tipW;
            tb.MaxWidth = tipW;
        }
#endif
        EdgeCrossTipText.InvalidateMeasure();
    }

    private void RefreshEdgeCrossTip()
    {
        if (EdgeCrossTipText == null) return;
        var key = ReadEdgeCrossModeUi() switch
        {
            1 => "Tip.EdgeCross.DoubleTap",
            2 => "Tip.EdgeCross.SpeedBurst",
            3 => "Tip.EdgeCross.HotkeyOnly",
            _ => "Tip.EdgeCross.BarrierFree"
        };
        EdgeCrossTipText.Text = Loc.T(key);
        ConstrainEdgeCrossTipWidth();
    }

    /// <summary>高级页显隐后强制重测：穿边说明换行、快捷键行高。</summary>
    private void RelayoutOptionsPage()
    {
        EnsureEdgeCrossTipWidthHook();
        RefreshEdgeCrossTip();
        HotSwitchRow?.InvalidateMeasure();
        HotCard?.InvalidateMeasure();
        EdgeCrossColumn?.InvalidateMeasure();
        OptionsContentRoot?.InvalidateMeasure();
        PageOptions?.InvalidateMeasure();
#if WINDOWS
        // 列宽往往在下一帧才稳定，连补两帧
        foreach (var ms in new[] { 16, 50 })
        {
            var delay = ms;
            Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(delay), () =>
            {
                if (PageOptions?.IsVisible != true) return;
                ConstrainEdgeCrossTipWidth();
                HotSwitchRow?.InvalidateMeasure();
                OptionsContentRoot?.InvalidateMeasure();
                UpdateOptionsScrollPolicy();
            });
        }
#endif
    }

    /// <summary>
    /// 主题切换或进入高级页：强制 RadioButton 字色/背景与资源 Text 一致。
    /// WinUI 上 DynamicResource 常不刷新 Radio；Android 首次可见也会吃到 Material 深色底。
    /// </summary>
    private void RefreshOptionsRadioChrome()
        => MauiThemeService.RefreshRadioButtonsInVisualTree(this);

    private async void Reconnect_Clicked(object? sender, EventArgs e)
    {
        await _host.Connections.ReconnectNowAsync();
        StatusLabel.Text = "正在重新连接…";
    }

    private async void OpenTouchpad_Clicked(object? sender, EventArgs e)
    {
        if (_selected == null || _selected.IsEmpty)
        {
            StatusLabel.Text = "请先在布局中选中要控制的电脑";
            return;
        }

        if (_selected.IsLocal)
        {
            StatusLabel.Text = "请选择一台非本机的电脑再打开触控板";
            return;
        }

        var peer = (_selected.Name ?? "").Trim();
        if (string.IsNullOrEmpty(peer))
        {
            StatusLabel.Text = "所选槽位无效";
            return;
        }

        if (!_host.Connections.GetPeerStatus(peer).AnyAlive)
        {
            StatusLabel.Text = $"「{peer}」未连接，请先点「刷新」或检查密钥/网络";
            return;
        }

        try
        {
            TouchpadBtn.IsEnabled = false;
            var page = new PhoneTouchpadPage(peer);
            // Shell 内 Push；若无 Shell 则用页内 Navigation
            var nav = Shell.Current?.Navigation ?? Navigation;
            await nav.PushAsync(page, animated: true);
        }
        catch (Exception ex)
        {
            StatusLabel.Text = $"打开触控板失败：{ex.Message}";
        }
        finally
        {
            TouchpadBtn.IsEnabled = true;
        }
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
        var page = new DirectTransferPage(_host);
        // 无 NavigationPage，避免顶栏「直接传输」标题
        await Navigation.PushModalAsync(page, animated: true);
        if (await page.Result)
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
#elif ANDROID
            // 优先打开下载/XuanNiao（文件管理可直接看）；失败则提示路径
            var ok = false;
            try
            {
                var parent = Directory.GetParent(dir)?.FullName ?? dir;
                await Launcher.Default.OpenAsync(new OpenFileRequest
                {
                    File = new ReadOnlyFile(Directory.Exists(parent) ? parent : dir)
                });
                ok = true;
            }
            catch { /* fall through */ }

            if (!ok)
            {
                await DisplayAlertAsync(
                    Loc.T("Xfer.OpenInbox"),
                    $"请在文件管理中打开：\n下载/XuanNiao/Inbox\n\n{dir}",
                    "OK");
            }
#else
            await Launcher.Default.OpenAsync(new OpenFileRequest { File = new ReadOnlyFile(dir) });
#endif
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync(Loc.T("Xfer.OpenInbox"), $"{dir}\n{ex.Message}", "OK");
        }
    }

    private async void ReceiveClipboardFiles_Clicked(object? sender, EventArgs e)
    {
        if (!_host.Transfers.HasRemotePendingFiles)
        {
            StatusLabel.Text = Loc.T("Xfer.NoPendingClipboard");
            return;
        }

        StatusLabel.Text = Loc.T("Xfer.ReceivingClipboard");
        try
        {
            var files = await _host.Transfers.PullRemoteFilesForPasteAsync().ConfigureAwait(true);
            StatusLabel.Text = files is { Count: > 0 }
                ? string.Format(Loc.T("Xfer.ReceivedClipboard"), files.Count)
                : Loc.T("Xfer.ReceiveClipboardFail");
        }
        catch (Exception ex)
        {
            StatusLabel.Text = ex.Message;
        }
    }

    private async void ClearTransfers_Clicked(object? sender, EventArgs e)
    {
        var hasReceive = _host.Transfers.Items.Any(i =>
            i.Direction == TransferDirection.Receive
            && i.State is TransferState.Completed or TransferState.Failed or TransferState.Cancelled);
        if (!hasReceive)
        {
            _host.Transfers.ClearCompleted(deleteReceiveFiles: false);
            return;
        }

        var choice = await ShowConfirmAsync(ConfirmPage.ForTransferDelete(
            Loc.T("Xfer.ConfirmClearTitle"),
            Loc.T("Xfer.ConfirmClearReceive")));
        if (choice == UiConfirmChoice.Cancel) return;
        _host.Transfers.ClearCompleted(deleteReceiveFiles: choice == UiConfirmChoice.Primary);
    }

    private void TransferFileName_Tapped(object? sender, TappedEventArgs e)
    {
        TransferItem? item = e.Parameter as TransferItem
            ?? (sender as BindableObject)?.BindingContext as TransferItem;
        if (item != null)
            RevealTransferPath(item);
    }

    internal void RevealTransferPath(TransferItem item)
    {
        if (!FlyScreen.Helpers.PathReveal.TryReveal(item.FullPath, out var error))
            StatusLabel.Text = string.IsNullOrWhiteSpace(error) ? "无法打开所在文件夹" : error;
    }

    private async void DeleteTransfer_Clicked(object? sender, EventArgs e)
    {
        if (sender is not Button { CommandParameter: TransferItem item }) return;
        await DeleteTransferAsync(item);
    }

    internal async Task DeleteTransferAsync(TransferItem item)
    {
        if (item.State is TransferState.Queued or TransferState.Transferring)
        {
            _host.Transfers.RemoveItem(item, deleteCache: true);
            return;
        }

        if (item.Direction != TransferDirection.Receive)
        {
            _host.Transfers.RemoveItem(item, deleteCache: false);
            return;
        }

        var choice = await ShowConfirmAsync(ConfirmPage.ForTransferDelete(
            Loc.T("Xfer.ConfirmDeleteTitle"),
            Loc.T("Xfer.ConfirmDeleteReceive")));
        if (choice == UiConfirmChoice.Cancel) return;
        _host.Transfers.RemoveItem(item, deleteCache: choice == UiConfirmChoice.Primary);
    }

    private void Tray_Clicked(object? sender, EventArgs e)
    {
        EnsureDesktopTray();
        _host.Platform.HideToBackground(
            showMain: () => { },
            reconnect: () => _ = _host.Connections.ReconnectNowAsync(),
            project: () => MainThread.BeginInvokeOnMainThread(() => _ = OpenProjectionFromTrayAsync()),
            about: () => MainThread.BeginInvokeOnMainThread(() => ShowPage(5)),
            exit: RequestAppExit,
            projectPhase: () => ProjectionUi.Resolve(_host.ScreenExtend));
    }

    private void RefreshAboutPage()
    {
        AboutTitleText.Text = Loc.T("About.Title");
        AboutBodyHost.Children.Clear();
        var linkColor = Colors.DodgerBlue;
        if (Application.Current?.Resources.TryGetValue("Blue", out var blue) == true)
        {
            if (blue is Color c) linkColor = c;
            else if (blue is SolidColorBrush scb) linkColor = scb.Color;
        }

        foreach (var para in AboutDocument.Build(AppVersionDisplay.Current))
        {
            if (para.Spans.Count == 0)
            {
                AboutBodyHost.Children.Add(new BoxView { HeightRequest = 8, Color = Colors.Transparent });
                continue;
            }

            var fs = new FormattedString();
            foreach (var span in para.Spans)
            {
                if (!string.IsNullOrEmpty(span.Url))
                {
                    var url = span.Url;
                    var s = new Span
                    {
                        Text = span.Text,
                        TextColor = linkColor,
                        TextDecorations = TextDecorations.Underline,
                        FontSize = 13
                    };
                    s.GestureRecognizers.Add(new TapGestureRecognizer
                    {
                        Command = new Command(async () =>
                        {
                            try { await Launcher.Default.OpenAsync(url); }
                            catch { /* ignore */ }
                        })
                    });
                    fs.Spans.Add(s);
                }
                else
                {
                    fs.Spans.Add(new Span
                    {
                        Text = span.Text,
                        FontSize = 13,
                        FontAttributes = para.IsHeading ? FontAttributes.Bold : FontAttributes.None
                    });
                }
            }

            AboutBodyHost.Children.Add(new Label
            {
                FormattedText = fs,
                LineBreakMode = LineBreakMode.WordWrap
            });
        }
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
            ShowHelpLog(_helpLastCheck.Message + "\n\n" + url + "\n" + MauiUpdateChecker.GiteeReleasesUrl);
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
                RequestAppExit();
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
        HelpContent.Children.Add(HelpPara(MauiUpdateChecker.GiteeReleasesUrl));
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
        // 勿设 FontFamily=Consolas：Maui-Win 未打包会探测 Assets/Fonts/*.ttf 并刷 relative URI 错
        StyleClass = new List<string> { "Mono" },
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
        => RequestAppExit();

    /// <summary>
    /// 日志行：点选切换选中高亮（无复选框）。
    /// Win CollectionView Multiple 会画平台勾选框，故用 SelectionMode=None + 自绘背景。
    /// </summary>
    private sealed class LogListItem(AppLogEntry entry) : System.ComponentModel.INotifyPropertyChanged
    {
        private bool _isSelected;

        public AppLogEntry Entry { get; } = entry;
        public string TimeText => Entry.TimeText;
        public string LevelText => Entry.LevelText;
        public string Message => Entry.Message;
        public AppLogLevel Level => Entry.Level;
        public string Display => Entry.Display;
        public Color RowBg => _isSelected ? LogRowSelectedBg : LogRowNormalBg;

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected == value) return;
                _isSelected = value;
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsSelected)));
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(RowBg)));
            }
        }

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    }
}
