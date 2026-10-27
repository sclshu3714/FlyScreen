using System.Collections.ObjectModel;
using FlyScreen.DesktopUi.Helpers;
using FlyScreen.DesktopUi.Services;
using FlyScreen.DesktopUi.Shell;
using FlyScreen.Linux;
using FlyScreen.Models;
using FlyScreen.Services.Clipboard;
using FlyScreen.Services.Network;

namespace FlyScreen.LinuxApp.Gui;

internal sealed class LinuxDesktopShellHost : IDesktopShellHost
{
    private readonly LinuxAppHost _host;
    private readonly AvaloniaClipboardApplySink _clipSink = new();
    private DesktopClipboardCoordinator? _clipCoord;
    private RemotePointerOverlay? _pointerOverlay;

    public LinuxDesktopShellHost(LinuxAppHost host)
    {
        _host = host;
        _host.Transfers.SetClipboardSink(_clipSink);
        _clipCoord = new DesktopClipboardCoordinator(host.Settings, host.Transfers, _clipSink);
        _host.ReceivingRemoteControlChanged += OnReceivingRemote;
        _host.RemotePointerMoved += OnRemotePointer;
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

    public string PlatformStatusLine
    {
        get
        {
            var session = _host.SessionKind.ToString();
            var inj = _host.Injector.IsAvailable ? "可用" : "不可用";
            return $"{session} · {_host.Injector.BackendName}({inj}) · {LocalPeerIdentity.MachineName}";
        }
    }

    public IReadOnlyList<(string Label, Action Invoke)> PlatformActions { get; } =
        Array.Empty<(string, Action)>();

    public bool SupportsTray => true;
    public bool SupportsAutoStart => false;

    public Task StartAsync() => _host.StartAsync();
    public Task RestartNetworkingAsync() => _host.RestartNetworkingAsync();
    public Task BroadcastLayoutSyncAsync() => _host.BroadcastLayoutSyncAsync();
    public Task BroadcastLayoutMergeAsync() => _host.BroadcastLayoutMergeAsync();
    public Task BroadcastBehaviorSyncAsync() => _host.BroadcastBehaviorSyncAsync();

    public bool GetAutoStartEnabled() => false;
    public void SetAutoStartEnabled(bool enabled) { }

    private void OnReceivingRemote(bool active)
    {
        if (!_host.Settings.DrawMouseCursor)
        {
            _pointerOverlay?.SetActive(false);
            return;
        }

        _pointerOverlay ??= new RemotePointerOverlay();
        _pointerOverlay.SetActive(active);
    }

    private void OnRemotePointer(double nx, double ny)
    {
        if (!_host.Settings.DrawMouseCursor) return;
        _pointerOverlay ??= new RemotePointerOverlay();
        if (!_pointerOverlay.IsVisible)
            _pointerOverlay.SetActive(true);
        _pointerOverlay.MoveNormalized(nx, ny);
    }
}
