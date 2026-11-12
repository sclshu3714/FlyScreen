using FlyScreen.AppMaui.Services;
using FlyScreen.Helpers;
using FlyScreen.Input;
using FlyScreen.Models;
using FlyScreen.Services;
using FlyScreen.Services.Clipboard;
using FlyScreen.Services.Network;
using FlyScreen.Services.ScreenExtend;
using DisplayProjectionMode = FlyScreen.Services.ScreenExtend.DisplayProjectionMode;

namespace FlyScreen.AppMaui.Platforms.Windows;

/// <summary>MAUI-Windows 桌面适配：对照 WPF AppHost，不修改 App.Windows。</summary>
public sealed class WindowsMauiPlatformServices : IMauiPlatformServices
{
    private readonly AppSettings _settings;
    private readonly ConnectionManager _connections;
    private readonly FileTransferService _transfers;
    private readonly ScreenExtendService _screenExtend;
    private readonly MauiClipboardApplySink _clipSink = new();
    private MauiInputRouter? _router;
    private EdgeFileDragService? _edgeDrag;
    private MauiTextClipboardMonitor? _clipMonitor;
    private MauiTrayService? _tray;
    private MauiScreenExtendController? _screenExtendUi;

    public WindowsMauiPlatformServices(
        AppSettings settings,
        ConnectionManager connections,
        FileTransferService transfers,
        ScreenExtendService screenExtend)
    {
        _settings = settings;
        _connections = connections;
        _transfers = transfers;
        _screenExtend = screenExtend;
        _screenExtendUi = new MauiScreenExtendController(screenExtend);
        // 从机期间穿边由 ShouldPauseEasyMouseForExtend 暂停，勿永久关掉 EasyMouseEnabled
        _screenExtend.SessionEnded += () =>
        {
            try
            {
                if (_router != null)
                    _router.EasyMouseEnabled = _settings.EasyMouse;
            }
            catch { /* ignore */ }
        };
    }

    public IClipboardApplySink ClipboardSink => _clipSink;
    public bool SupportsDesktopInput => true;
    public bool SupportsTray => true;
    public bool SupportsAutoStart => true;
    public bool IsFocusOnLocalMachine => _router?.IsFocusOnLocalMachine ?? true;
    public bool AcceptsRemoteInput => _router?.IsReceivingRemoteControl ?? false;

    public event Action<string?>? ControlTargetChanged;

    /// <summary>托盘切换热键模式变更后回调（用于跨机 BehaviorSync）。</summary>
    public Action? AfterSwitchHotkeyModeChanged { get; set; }

    public bool TrySwitchToMachine(string machineName)
        => _router?.TrySwitchToMachine(machineName) == true;

    public void SetRemotePointerSuppressed(bool suppressed) { /* 桌面不需要触控板屏蔽 */ }

    public void AttachEdgeFileDrag(EdgeFileDragService drag)
    {
        if (_edgeDrag != null)
            _edgeDrag.CueChanged -= OnEdgeDragCue;
        _edgeDrag = drag;
        drag.CueChanged += OnEdgeDragCue;
        if (_router != null)
            _router.EdgeFileDrag = drag;
    }

    private static void OnEdgeDragCue(EdgeDragCue? cue)
    {
        if (cue == null || cue.Count <= 0)
            EdgeFileDragCue.Hide();
        else
            EdgeFileDragCue.Show(cue.Count, cue.Names, WindowsDropFolder.Resolve);
    }

    public void StartDesktopInput()
    {
        _router?.Dispose();
        _router = new MauiInputRouter(_settings, _connections);
        _router.EdgeFileDrag = _edgeDrag;
        _router.EasyMouseEnabled = _settings.EasyMouse;
        _router.IsEdgeCrossExcluded = peer => _screenExtend.IsExcludedFromEdgeCross(peer);
        _router.ShouldPauseEasyMouseForExtend = () => _screenExtend.ShouldPauseLocalEasyMouse;
        _router.TryHandleLocalFilePasteHotkey = () =>
            _clipMonitor?.TryHandlePasteHotkey() == true;
        _router.ControlTargetChanged += peer =>
        {
            ControlTargetChanged?.Invoke(peer);
            if (peer == null)
                ArmRemoteClipboardForPaste();
        };
        _router.RequestShowUi += () => MainThread.BeginInvokeOnMainThread(ShowMainWindow);
        _router.Start();
        if (!_router.HooksInstalled)
        {
            AppLog.Warn("Maui-Win 键鼠钩子未装上：轻松鼠标穿边不可用。请以管理员运行（清单已要求 requireAdministrator）。");
        }
    }

    private static void ShowMainWindow()
        => MauiAutostartUi.ActivateMainWindow();

    private static void HideMainWindow()
    {
        var window = Application.Current?.Windows.FirstOrDefault();
        MauiAutostartUi.TryHideNative(window);
    }

    public void StopDesktopInput()
    {
        _router?.Dispose();
        _router = null;
    }

    public void SetEasyMouse(bool enabled)
    {
        if (_router == null) return;
        _router.EasyMouseEnabled = enabled;
        _router.RefreshEdgeCrossFromSettings();
    }

    public void InjectMouseMoveAbsolute(double nx, double ny)
        => MauiInputInjector.MoveAbsoluteNormalized(nx, ny);

    public void InjectMouseMoveRelative(int dx, int dy)
        => MauiInputInjector.MoveRelative(dx, dy);

    public void InjectMouseButton(int button, bool down)
        => MauiInputInjector.MouseButton(new MouseButtonPayload { Button = button, Down = down });

    public void InjectMouseWheel(int delta, bool horizontal)
        => MauiInputInjector.MouseWheel(new MouseWheelPayload { Delta = delta, Horizontal = horizontal });

    public void InjectKey(ushort vk, bool down, bool extended)
        => MauiInputInjector.Key(new KeyEventPayload { VirtualKey = vk, Down = down, Extended = extended });

    public void InjectText(string text)
        => MauiInputInjector.InsertText(text);

    public void BeginReceivingRemoteControl(double entryX, double entryY)
        => _router?.BeginReceivingRemoteControl(entryX, entryY);

    public void OnCursorOwnerChanged(string ownerMachine)
        => _router?.OnCursorOwnerChanged(ownerMachine);

    public void RestoreCursorWhenLeavingCluster()
        => _router?.RestoreCursorWhenLeavingCluster();

    public void StartClipboardMonitor()
    {
        _clipMonitor?.Dispose();
        _clipMonitor = new MauiTextClipboardMonitor(_settings, _transfers, _clipSink);
        _clipMonitor.Start();
    }

    public void StopClipboardMonitor()
    {
        _clipMonitor?.Dispose();
        _clipMonitor = null;
    }

    public void ArmRemoteClipboardForPaste()
        => _clipMonitor?.ArmRemoteClipboardForPaste();

    public bool GetAutoStartEnabled() => AutoStartHelper.IsEnabled();

    public bool SetAutoStartEnabled(bool enabled)
        => AutoStartHelper.SetEnabled(enabled);

    public void EnsureTray(
        Action showMain,
        Action? reconnect = null,
        Action? project = null,
        Action? about = null,
        Action? exit = null,
        Func<FlyScreen.Helpers.ProjectionUiPhase>? projectPhase = null,
        Func<IReadOnlyList<(string Label, Action Action, byte[]? IconPng)>>? toolShortcuts = null)
    {
        _tray ??= new MauiTrayService();
        _tray.Ensure(
            show: () =>
            {
                ShowMainWindow();
                showMain();
            },
            reconnect: () =>
            {
                if (reconnect != null) reconnect();
                else _ = _connections.ReconnectNowAsync();
            },
            project: () =>
            {
                ShowMainWindow();
                project?.Invoke();
            },
            about: () => about?.Invoke(),
            exit: () =>
            {
                if (exit != null) exit();
                else MauiCloseToTray.RequestExit();
            },
            projectPhase: projectPhase,
            switchMachines: () =>
            {
                var list = new List<(string Label, Action Action)>();
                foreach (var item in TraySwitchMenu.BuildItems(_settings))
                {
                    var machine = item.MachineName;
                    var label = item.Label;
                    list.Add((label, () =>
                    {
                        if (TraySwitchMenu.EnsureSwitchHotkeyEnabled(_settings))
                            AfterSwitchHotkeyModeChanged?.Invoke();
                        if (_router == null)
                            AppLog.Warn("托盘切换失败：输入路由未启动");
                        else if (!_router.TrySwitchToMachine(machine))
                            AppLog.Warn($"托盘切换未成功：{machine}");
                    }));
                }

                return list;
            },
            switchDisable: () => (
                TraySwitchMenu.DisableLabel(_settings.SwitchHotkeyMode == 2),
                (Action)(() =>
                {
                    TraySwitchMenu.ToggleSwitchHotkeyDisabled(_settings);
                    AfterSwitchHotkeyModeChanged?.Invoke();
                })),
            toolShortcuts: toolShortcuts);
    }

    public void HideToBackground(
        Action showMain,
        Action? reconnect = null,
        Action? project = null,
        Action? about = null,
        Action? exit = null,
        Func<FlyScreen.Helpers.ProjectionUiPhase>? projectPhase = null,
        Func<IReadOnlyList<(string Label, Action Action, byte[]? IconPng)>>? toolShortcuts = null)
    {
        EnsureTray(showMain, reconnect, project, about, exit, projectPhase, toolShortcuts);
        HideMainWindow();
    }

    public void DisposeTray()
    {
        _tray?.Dispose();
        _tray = null;
    }

    public Task<(bool Ok, string? InfoOrError)> EnsureScreenExtendHostDisplayAsync(
        LayoutEdgeSide layoutSide = LayoutEdgeSide.Unknown)
        => _screenExtendUi?.EnsureHostDisplayAsync(layoutSide: layoutSide)
           ?? Task.FromResult<(bool, string?)>((false, "扩展屏控制器未初始化"));

    public Task<(bool Ok, string? Error)> PrepareScreenExtendCaptureAsync(CancellationToken ct = default)
        => Task.FromResult<(bool, string?)>((true, null));

    public Task ReleaseScreenExtendHostDisplayAsync()
        => _screenExtendUi?.ReleaseHostDisplayAsync() ?? Task.CompletedTask;

    public void SetScreenExtendProjectionMode(DisplayProjectionMode mode)
        => _screenExtendUi?.SetProjectionMode(mode);

    public void SetScreenExtendDisplayChoice(ProjectionDisplayChoice? choice)
        => _screenExtendUi?.SetDisplayChoice(choice);

    /// <summary>退出进程前释放扩展屏控制器（内部会等待拓扑 Finalize）。</summary>
    public void DisposeScreenExtendUi()
    {
        try { _screenExtendUi?.Dispose(); } catch { /* ignore */ }
        _screenExtendUi = null;
    }
}
