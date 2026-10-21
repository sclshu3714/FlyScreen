using FlyScreen.Models;
using FlyScreen.Services.Clipboard;
using FlyScreen.Services.Input;
using FlyScreen.Services.Network;
using FlyScreen.Services.Programs;

namespace FlyScreen.Services;

public sealed class AppHost : IAsyncDisposable
{
    public static AppHost Instance { get; private set; } = null!;

    public AppSettings Settings { get; }
    public ConnectionManager Connections { get; }
    public InputRouter Input { get; }
    public FileTransferService Transfers { get; }
    public ClipboardMonitor Clipboard { get; }

    public event Action<string>? StatusMessage;
    /// <summary>收到对端布局并已写入设置（UI 需刷新矩阵）。</summary>
    public event Action<string>? LayoutSyncedFromPeer;
    /// <summary>收到对端功能/行为并已写入设置（UI 需刷新选项）。</summary>
    public event Action<string>? BehaviorSyncedFromPeer;

    private BehaviorSyncHelper? _behaviorSync;
    private LayoutSyncHelper? _layoutSync;

    private AppHost()
    {
        Settings = AppSettings.Load();
        Settings.EnsureLocalMachineSlot();
        Connections = new ConnectionManager(Settings);
        ProgramShareService.Attach(Connections);
        Input = new InputRouter(Settings, Connections);
        Transfers = new FileTransferService(Settings, Connections, new WindowsClipboardApplySink());
        Clipboard = new ClipboardMonitor(Settings, Transfers);
        _behaviorSync = new BehaviorSyncHelper(Settings, Connections);
        _behaviorSync.StatusMessage += PublishStatus;
        _behaviorSync.BehaviorSyncedFromPeer += from =>
        {
            Input.EasyMouseEnabled = Settings.EasyMouse;
            try { BehaviorSyncedFromPeer?.Invoke(from); } catch { /* ignore */ }
        };
        _layoutSync = new LayoutSyncHelper(Settings, Connections);
        _layoutSync.StatusMessage += PublishStatus;
        _layoutSync.LayoutSyncedFromPeer += from =>
        {
            // 只刷新 UI；由 MaintainLoop 补连/剪枝，避免整网重启导致状态来回闪
            RestoreCursorIfLayoutSolo();
            try { LayoutSyncedFromPeer?.Invoke(from); } catch { /* ignore */ }
        };
        _layoutSync.KickedFromLayout += () =>
        {
            // 被踢出布局、断开前：先恢复本机光标
            Input.RestoreCursorWhenLeavingCluster();
            try { LayoutSyncedFromPeer?.Invoke(LocalPeerIdentity.MachineName); } catch { /* ignore */ }
        };

        Connections.StatusMessage += PublishStatus;
        Transfers.StatusMessage += PublishStatus;
        Connections.MessageReceived += OnPeerMessage;

        // 鼠标回到本机：后台异步预取远程剪贴板（文件收完后再可粘贴，不堵键鼠）
        Input.ControlTargetChanged += peer =>
        {
            if (peer == null)
                Clipboard.ArmRemoteClipboardForPaste();
        };

        // 焦点已在本机时对端才复制：立即开始预取
        Transfers.RemoteFilesAnnounced += () =>
        {
            if (Input.IsFocusOnLocalMachine)
                Clipboard.ArmRemoteClipboardForPaste();
        };
        Transfers.RemoteInlineAnnounced += () =>
        {
            if (Input.IsFocusOnLocalMachine)
                Clipboard.ArmRemoteClipboardForPaste();
        };
    }

    private void PublishStatus(string message)
    {
        AppLog.RecordStatus(message);
        StatusMessage?.Invoke(AppLog.ForStatusBar(message));
    }

    public static AppHost EnsureCreated()
    {
        Instance ??= new AppHost();
        return Instance;
    }

    public async Task StartAsync()
    {
        await Connections.StartAsync().ConfigureAwait(false);
        Input.EasyMouseEnabled = Settings.EasyMouse;
        Input.Start();
    }

    public async Task RestartNetworkingAsync()
    {
        Settings.Save();
        await Connections.StartAsync().ConfigureAwait(false);
    }

    private void OnPeerMessage(string peer, NetMessage msg)
    {
        switch (msg.Type)
        {
            case MessageType.MouseMove:
            {
                // EdgeMouse 须 SwitchFocus；AbsPointer（手机触控板）可自动进入被控
                if (!EnsureAcceptsRemoteInput(peer)) return;
                var p = MessageCodec.DecodeJson<MouseMovePayload>(msg.Payload);
                if (p == null) return;
                // 键鼠注入优先，不在此路径做剪贴板/磁盘 I/O（避免卡鼠标）
                if (p.Relative) InputInjector.MoveRelative(p.Dx, p.Dy);
                else InputInjector.MoveAbsoluteNormalized(p.Nx, p.Ny);
                break;
            }
            case MessageType.MouseButton:
            {
                if (!EnsureAcceptsRemoteInput(peer)) return;
                var p = MessageCodec.DecodeJson<MouseButtonPayload>(msg.Payload);
                if (p == null) return;
                InputInjector.MouseButton(p);
                break;
            }
            case MessageType.MouseWheel:
            {
                if (!EnsureAcceptsRemoteInput(peer)) return;
                var p = MessageCodec.DecodeJson<MouseWheelPayload>(msg.Payload);
                if (p == null) return;
                InputInjector.MouseWheel(p);
                break;
            }
            case MessageType.KeyEvent:
            {
                if (!EnsureAcceptsRemoteInput(peer)) return;
                var p = MessageCodec.DecodeJson<KeyEventPayload>(msg.Payload);
                if (p == null) return;
                InputInjector.Key(p);
                break;
            }
            case MessageType.SwitchFocus:
            {
                var p = MessageCodec.DecodeJson<SwitchFocusPayload>(msg.Payload);
                if (p == null) return;
                if (IsLocalMachineName(p.TargetMachine))
                {
                    // 对端鼠标进入本机：显示光标；剪贴板预取由 ControlTargetChanged 异步触发
                    Input.BeginReceivingRemoteControl(p.EntryX, p.EntryY);
                }
                break;
            }
            case MessageType.CursorOwner:
            {
                var p = MessageCodec.DecodeJson<CursorOwnerPayload>(msg.Payload);
                if (p == null) return;
                Input.OnCursorOwnerChanged(p.OwnerMachine);
                break;
            }
            case MessageType.CompositionSelect:
            {
                var p = MessageCodec.DecodeJson<CompositionSelectPayload>(msg.Payload);
                Input.HandleCompositionSelect(peer, p);
                break;
            }
            case MessageType.CompositionCancel:
            {
                // 手机点空白 / 切应用 → 取消本机组字
                Input.HandleCompositionCancel(peer);
                break;
            }
            // LayoutSync 由 LayoutSyncHelper 处理
        }
    }

    /// <summary>
    /// EdgeMouse 依赖 SwitchFocus；手机 AbsPointer 可能未发 SwitchFocus，
    /// 在本机未控远端时自动进入被控，避免触控板键鼠被静默丢弃。
    /// </summary>
    private bool EnsureAcceptsRemoteInput(string peer)
    {
        if (Input.IsReceivingRemoteControl) return true;
        if (Input.IsControllingRemote) return false;
        if (!Connections.PeerSupports(peer, PeerCaps.AbsPointer)) return false;
        Input.BeginReceivingRemoteControl(0.5, 0.5);
        return Input.IsReceivingRemoteControl;
    }

    /// <summary>本机修改布局后权威替换广播（移除电脑）。</summary>
    public Task BroadcastLayoutSyncAsync()
    {
        var task = _layoutSync?.BroadcastReplaceAsync() ?? Task.CompletedTask;
        RestoreCursorIfLayoutSolo();
        return task;
    }

    /// <summary>添加电脑后并集广播，对端自动合并集群（最多 4 台）。</summary>
    public Task BroadcastLayoutMergeAsync()
        => _layoutSync?.BroadcastMergeAsync() ?? Task.CompletedTask;

    /// <summary>本机修改高级「功能 / 行为」后广播给已连接对端。</summary>
    public Task BroadcastBehaviorSyncAsync()
        => _behaviorSync?.BroadcastAsync() ?? Task.CompletedTask;

    /// <summary>布局仅剩本机时恢复可见光标（移除对端 / 同步后调用）。</summary>
    public void RestoreCursorIfLayoutSolo()
    {
        if (LayoutSlotMerge.IsOnlyLocal(Settings.MachineSlots, LocalPeerIdentity.MachineName))
            Input.RestoreCursorWhenLeavingCluster();
    }

    private static bool IsLocalMachineName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        if (string.Equals(name, LocalPeerIdentity.MachineName, StringComparison.OrdinalIgnoreCase))
            return true;
        var env = Environment.MachineName;
        return !LocalPeerIdentity.IsBogusMachineName(env)
               && string.Equals(name, env, StringComparison.OrdinalIgnoreCase);
    }

    public async ValueTask DisposeAsync()
    {
        _layoutSync?.Detach();
        _layoutSync = null;
        _behaviorSync?.Detach();
        _behaviorSync = null;
        Clipboard.Dispose();
        Input.Dispose();
        await Connections.DisposeAsync().ConfigureAwait(false);
        Settings.Save();
    }
}
