using System.Collections.ObjectModel;
using FlyScreen.DesktopUi;
using FlyScreen.DesktopUi.Services;
using FlyScreen.DesktopUi.Shell;
using FlyScreen.Mac;
using FlyScreen.Mac.Permissions;
using FlyScreen.Models;
using FlyScreen.Services.Clipboard;
using FlyScreen.Services.Network;
using FlyScreen.Services.ScreenExtend;

namespace FlyScreen.MacApp;

internal sealed class MacDesktopShellHost : IDesktopShellHost
{
    private readonly MacAppHost _host;
    private readonly AvaloniaClipboardApplySink _clipSink = new();
    private readonly DesktopClipboardCoordinator _clipCoord;
    private readonly ScreenExtendDesktopController _screenExtendUi;

    public MacDesktopShellHost(MacAppHost host)
    {
        _host = host;
        _host.Transfers.SetClipboardSink(_clipSink);
        _clipCoord = new DesktopClipboardCoordinator(host.Settings, host.Transfers, _clipSink);
        // 网络启动前挂好从机 UI，避免 Offer 已 Accept 却无全屏窗
        _screenExtendUi = new ScreenExtendDesktopController(host.ScreenExtend);
    }

    public AppSettings Settings => _host.Settings;
    public ConnectionManager Connections => _host.Connections;
    public FileTransferService Transfers => _host.Transfers;
    public ObservableCollection<TransferItem> TransferItems => _host.Transfers.Items;

    public event Action<string>? StatusMessage
    {
        add => _host.StatusMessage += value;
        remove => _host.StatusMessage -= value;
    }

    public event Action<string>? LayoutSyncedFromPeer
    {
        add => _host.LayoutSyncedFromPeer += value;
        remove => _host.LayoutSyncedFromPeer -= value;
    }

    public event Action<string>? BehaviorSyncedFromPeer
    {
        add => _host.BehaviorSyncedFromPeer += value;
        remove => _host.BehaviorSyncedFromPeer -= value;
    }

    public event Action<string>? SecurityKeySyncedFromPeer
    {
        add => _host.SecurityKeySyncedFromPeer += value;
        remove => _host.SecurityKeySyncedFromPeer -= value;
    }

    public string PlatformStatusLine
    {
        get
        {
            var trusted = AccessibilityPermission.IsTrusted();
            return AccessibilityPermission.IsMacOS
                ? $"macOS · 辅助功能={(trusted ? "已授权" : "未授权")} · Caps={string.Join(",", LocalPeerIdentity.Caps)}"
                : $"非 macOS 运行 · Caps={string.Join(",", LocalPeerIdentity.Caps)}";
        }
    }

    public IReadOnlyList<(string Label, Action Invoke)> PlatformActions { get; } =
    [
        ("打开辅助功能设置", AccessibilityPermission.OpenSystemSettings)
    ];

    public bool SupportsTray => true;
    public bool SupportsAutoStart => false;

    public Task StartAsync() => _host.StartAsync();
    public Task RestartNetworkingAsync() => _host.RestartNetworkingAsync();
    public Task ApplyNetworkingAsync(
        int previousTcpPort,
        int previousUdpPort,
        string previousNicId,
        string previousLocalIp,
        string previousSecurityKey)
        => _host.ApplyNetworkingAsync(previousTcpPort, previousUdpPort, previousNicId, previousLocalIp, previousSecurityKey);
    public Task BroadcastLayoutSyncAsync() => _host.BroadcastLayoutSyncAsync();
    public Task BroadcastLayoutMergeAsync() => _host.BroadcastLayoutMergeAsync();
    public Task BroadcastBehaviorSyncAsync() => _host.BroadcastBehaviorSyncAsync();
    public Task<(bool Ok, string Message)> SyncSecurityKeyAsync(CancellationToken ct = default)
        => _host.SyncSecurityKeyAsync(ct);

    public ScreenExtendService ScreenExtend => _host.ScreenExtend;

    public async Task<(bool Ok, string? Message)> StartProjectionAsync(
        ProjectionTarget? target,
        DisplayProjectionMode mode)
    {
        mode = DisplayProjectionModeInfo.ClampForPicker((int)mode);
        _host.Settings.ScreenExtendProjectionMode = (int)mode;
        if (target?.Kind == ProjectionTargetKind.Peer && !string.IsNullOrEmpty(target.Id))
            _host.Settings.ScreenExtendPeer = target.Id;
        _host.Settings.Save();
        await Task.CompletedTask;
        return (false, "macOS 暂不支持发起投影，请从 Windows 端发起；本机可接收投影");
    }

    public async Task StopProjectionAsync()
    {
        if (ScreenExtend.IsActive)
            await ScreenExtend.StopAsync("stopped").ConfigureAwait(false);
    }

    public bool GetAutoStartEnabled() => false;
    public void SetAutoStartEnabled(bool enabled) { }
}
