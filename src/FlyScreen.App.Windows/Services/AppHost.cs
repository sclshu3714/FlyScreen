using FlyScreen.Models;
using FlyScreen.Services.Clipboard;
using FlyScreen.Services.Input;
using FlyScreen.Services.Network;

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

    private int _applyingRemoteLayout;

    private AppHost()
    {
        Settings = AppSettings.Load();
        Settings.EnsureLocalMachineSlot();
        Connections = new ConnectionManager(Settings);
        Input = new InputRouter(Settings, Connections);
        Transfers = new FileTransferService(Settings, Connections, new WindowsClipboardApplySink());
        Clipboard = new ClipboardMonitor(Settings, Transfers);

        Connections.StatusMessage += m => StatusMessage?.Invoke(m);
        Transfers.StatusMessage += m => StatusMessage?.Invoke(m);
        Connections.MessageReceived += OnPeerMessage;
        Connections.PeerLinkChanged += OnPeerLinkChangedForLayout;

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
            case MessageType.LayoutSync:
                HandleLayoutSync(peer, msg.Payload);
                break;
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

    /// <summary>本机修改布局后广播给已连接对端。</summary>
    public async Task BroadcastLayoutSyncAsync()
    {
        if (Volatile.Read(ref _applyingRemoteLayout) != 0) return;

        Settings.LayoutRevision = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        Settings.EnsureLocalMachineSlot();
        Settings.Save();

        var payload = MessageCodec.JsonBytes(BuildLayoutPayload());
        await Connections.BroadcastAsync(MessageType.LayoutSync, payload, PeerCaps.LayoutSync)
            .ConfigureAwait(false);
        StatusMessage?.Invoke("布局已同步到其他电脑");
    }

    /// <summary>向指定对端推送当前布局（新连接时补发，不抬高修订号）。</summary>
    public async Task SendLayoutToPeerAsync(string peer)
    {
        if (Volatile.Read(ref _applyingRemoteLayout) != 0) return;
        if (Settings.LayoutRevision <= 0) return; // 从未主动同步过则不推，避免抢布局
        if (!Connections.IsPeerReachable(peer)) return;
        if (!Connections.PeerSupports(peer, PeerCaps.LayoutSync)) return;

        var payload = MessageCodec.JsonBytes(BuildLayoutPayload());
        await Connections.SendToAsync(peer, MessageType.LayoutSync, payload).ConfigureAwait(false);
    }

    private LayoutSyncPayload BuildLayoutPayload()
    {
        var slots = Settings.MachineSlots.ToList();
        while (slots.Count < 4) slots.Add("");
        if (slots.Count > 4) slots = slots.Take(4).ToList();

        return new LayoutSyncPayload
        {
            SourceMachine = LocalPeerIdentity.MachineName,
            MachineSlots = slots,
            TwoRow = Settings.TwoRow,
            Revision = Settings.LayoutRevision
        };
    }

    private void HandleLayoutSync(string peer, byte[] payload)
    {
        var sync = MessageCodec.DecodeJson<LayoutSyncPayload>(payload);
        if (sync == null) return;
        if (IsLocalMachineName(sync.SourceMachine))
            return;

        if (sync.Revision > 0 && sync.Revision <= Settings.LayoutRevision)
            return;

        if (sync.MachineSlots == null || sync.MachineSlots.Count == 0)
            return;

        if (Interlocked.CompareExchange(ref _applyingRemoteLayout, 1, 0) != 0)
            return;

        try
        {
            var slots = sync.MachineSlots.Select(s => s?.Trim() ?? "").ToList();
            while (slots.Count < 4) slots.Add("");
            if (slots.Count > 4) slots = slots.Take(4).ToList();

            Settings.MachineSlots = slots;
            Settings.TwoRow = sync.TwoRow;
            Settings.LayoutRevision = sync.Revision > 0
                ? sync.Revision
                : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            Settings.EnsureLocalMachineSlot();
            Settings.Save();

            var from = string.IsNullOrWhiteSpace(sync.SourceMachine) ? peer : sync.SourceMachine;
            StatusMessage?.Invoke($"已同步布局 ← {from}");
            try { LayoutSyncedFromPeer?.Invoke(from); } catch { /* ignore */ }

            _ = RestartNetworkingQuietAsync();
        }
        finally
        {
            Interlocked.Exchange(ref _applyingRemoteLayout, 0);
        }
    }

    private async Task RestartNetworkingQuietAsync()
    {
        try
        {
            await RestartNetworkingAsync().ConfigureAwait(false);
        }
        catch
        {
            // ignore
        }
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

    private void OnPeerLinkChangedForLayout(string name, bool outbound, bool inbound)
    {
        // 新链路可用时补发布局，让晚加入的电脑也对齐
        if (!(outbound || inbound)) return;
        if (Volatile.Read(ref _applyingRemoteLayout) != 0) return;
        _ = SendLayoutToPeerAsync(name);
    }

    public async ValueTask DisposeAsync()
    {
        Connections.PeerLinkChanged -= OnPeerLinkChangedForLayout;
        Clipboard.Dispose();
        Input.Dispose();
        await Connections.DisposeAsync().ConfigureAwait(false);
        Settings.Save();
    }
}
