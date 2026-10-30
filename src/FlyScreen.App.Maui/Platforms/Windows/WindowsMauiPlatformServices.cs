using FlyScreen.AppMaui.Services;
using FlyScreen.Helpers;
using FlyScreen.Models;
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
        _screenExtend.SinkSessionReady += () =>
        {
            try { _router?.SetEasyMousePaused(true); } catch { /* ignore */ }
        };
        _screenExtend.SessionEnded += () =>
        {
            try { _router?.SetEasyMousePaused(false); } catch { /* ignore */ }
        };
    }

    public IClipboardApplySink ClipboardSink => _clipSink;
    public bool SupportsDesktopInput => true;
    public bool SupportsTray => true;
    public bool SupportsAutoStart => true;
    public bool IsFocusOnLocalMachine => _router?.IsFocusOnLocalMachine ?? true;
    public bool AcceptsRemoteInput => _router?.IsReceivingRemoteControl ?? false;

    public event Action<string?>? ControlTargetChanged;

    public void SetRemotePointerSuppressed(bool suppressed) { /* 桌面不需要触控板屏蔽 */ }

    public void StartDesktopInput()
    {
        _router?.Dispose();
        _router = new MauiInputRouter(_settings, _connections);
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
    }

    private static void ShowMainWindow()
    {
        var window = Application.Current?.Windows.FirstOrDefault();
        if (window?.Handler?.PlatformView is Microsoft.UI.Xaml.Window native)
        {
            native.AppWindow.Show();
            native.Activate();
        }
    }

    private static void HideMainWindow()
    {
        var window = Application.Current?.Windows.FirstOrDefault();
        if (window?.Handler?.PlatformView is Microsoft.UI.Xaml.Window native)
            native.AppWindow.Hide();
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
    {
        // Windows MAUI 被控时仍走键鼠注入；成段文本暂以逐字符不可靠，忽略
        _ = text;
    }

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

    public void SetAutoStartEnabled(bool enabled)
        => AutoStartHelper.SetEnabled(enabled);

    public void EnsureTray(Action showMain, Action? reconnect = null, Action? about = null, Action? exit = null)
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
            about: () => about?.Invoke(),
            exit: () =>
            {
                if (exit != null) exit();
                else Application.Current?.Quit();
            });
    }

    public void HideToBackground(Action showMain, Action? reconnect = null, Action? about = null, Action? exit = null)
    {
        EnsureTray(showMain, reconnect, about, exit);
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

    public Task ReleaseScreenExtendHostDisplayAsync()
        => _screenExtendUi?.ReleaseHostDisplayAsync() ?? Task.CompletedTask;

    public void SetScreenExtendProjectionMode(DisplayProjectionMode mode)
        => _screenExtendUi?.SetProjectionMode(mode);

    public void SetScreenExtendDisplayChoice(ProjectionDisplayChoice? choice)
        => _screenExtendUi?.SetDisplayChoice(choice);
}
