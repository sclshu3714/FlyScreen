using System.Collections.ObjectModel;
using FlyScreen.DesktopUi.Services;
using FlyScreen.DesktopUi.Shell;
using FlyScreen.Mac;
using FlyScreen.Mac.Permissions;
using FlyScreen.Models;
using FlyScreen.Services.Clipboard;
using FlyScreen.Services.Network;

namespace FlyScreen.MacApp;

internal sealed class MacDesktopShellHost : IDesktopShellHost
{
    private readonly MacAppHost _host;
    private readonly AvaloniaClipboardApplySink _clipSink = new();
    private readonly DesktopClipboardCoordinator _clipCoord;

    public MacDesktopShellHost(MacAppHost host)
    {
        _host = host;
        _host.Transfers.SetClipboardSink(_clipSink);
        _clipCoord = new DesktopClipboardCoordinator(host.Settings, host.Transfers, _clipSink);
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
    public Task BroadcastLayoutSyncAsync() => _host.BroadcastLayoutSyncAsync();

    public bool GetAutoStartEnabled() => false;
    public void SetAutoStartEnabled(bool enabled) { }
}
