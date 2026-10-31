using FlyScreen.Models;

namespace FlyScreen.Services.Network;

/// <summary>
/// 高级「功能 / 行为」跨机同步（与 <see cref="LayoutSyncHelper"/> 对称）。
/// 以修改端为准推送给已连接对端；新链路补发当前修订。
/// </summary>
public sealed class BehaviorSyncHelper
{
    private readonly AppSettings _settings;
    private readonly ConnectionManager _connections;
    private int _applyingRemote;

    public event Action<string>? BehaviorSyncedFromPeer;
    public event Action<string>? StatusMessage;

    public BehaviorSyncHelper(AppSettings settings, ConnectionManager connections)
    {
        _settings = settings;
        _connections = connections;
        _connections.PeerLinkChanged += OnPeerLinkChanged;
        _connections.MessageReceived += OnMessageReceived;
    }

    public void Detach()
    {
        _connections.PeerLinkChanged -= OnPeerLinkChanged;
        _connections.MessageReceived -= OnMessageReceived;
    }

    public async Task BroadcastAsync()
    {
        if (Volatile.Read(ref _applyingRemote) != 0) return;

        _settings.BehaviorRevision = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _settings.Save();

        var payload = MessageCodec.JsonBytes(BuildPayload());
        // 功能/行为（含穿边模式）对手机也生效；勿要求 LayoutSync（Android 未宣告该能力）
        await _connections.BroadcastAsync(MessageType.BehaviorSync, payload, requiredCap: null)
            .ConfigureAwait(false);
        StatusMessage?.Invoke("功能与行为已同步到已连接设备");
    }

    public async Task SendToPeerAsync(string peer)
    {
        if (Volatile.Read(ref _applyingRemote) != 0) return;
        if (_settings.BehaviorRevision <= 0) return;
        if (!_connections.IsPeerReachable(peer)) return;

        var payload = MessageCodec.JsonBytes(BuildPayload());
        await _connections.SendToAsync(peer, MessageType.BehaviorSync, payload).ConfigureAwait(false);
    }

    private BehaviorSyncPayload BuildPayload() => new()
    {
        SourceMachine = LocalPeerIdentity.MachineName,
        Revision = _settings.BehaviorRevision,
        WrapMouse = _settings.WrapMouse,
        ShareClipboard = _settings.ShareClipboard,
        TransferFile = _settings.TransferFile,
        HideMouseAtEdge = _settings.HideMouseAtEdge,
        DrawMouseCursor = _settings.DrawMouseCursor,
        SameSubnetOnly = _settings.SameSubnetOnly,
        ShowStatusMessages = _settings.ShowStatusMessages,
        BlockScreenSaver = _settings.BlockScreenSaver,
        MoveMouseRelatively = _settings.MoveMouseRelatively,
        BlockMouseAtCorners = _settings.BlockMouseAtCorners,
        EasyMouse = _settings.EasyMouse,
        EdgeCrossMode = _settings.EdgeCrossMode is >= 0 and <= 2 ? _settings.EdgeCrossMode : 0
    };

    private void OnMessageReceived(string peer, NetMessage msg)
    {
        if (msg.Type != MessageType.BehaviorSync) return;
        HandleIncoming(peer, msg.Payload);
    }

    private void HandleIncoming(string peer, byte[] payload)
    {
        var sync = MessageCodec.DecodeJson<BehaviorSyncPayload>(payload);
        if (sync == null) return;
        if (string.Equals(sync.SourceMachine, LocalPeerIdentity.MachineName, StringComparison.OrdinalIgnoreCase))
            return;
        if (string.Equals(sync.SourceMachine, Environment.MachineName, StringComparison.OrdinalIgnoreCase)
            && !LocalPeerIdentity.IsBogusMachineName(Environment.MachineName))
            return;
        if (sync.Revision > 0 && sync.Revision <= _settings.BehaviorRevision)
            return;
        if (Interlocked.CompareExchange(ref _applyingRemote, 1, 0) != 0)
            return;

        try
        {
            ApplyPayload(sync);
            _settings.BehaviorRevision = sync.Revision > 0
                ? sync.Revision
                : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _settings.Save();

            var from = string.IsNullOrWhiteSpace(sync.SourceMachine) ? peer : sync.SourceMachine;
            StatusMessage?.Invoke($"已同步功能与行为 ← {from}");
            try { BehaviorSyncedFromPeer?.Invoke(from); } catch { /* ignore */ }
        }
        finally
        {
            Interlocked.Exchange(ref _applyingRemote, 0);
        }
    }

    private void ApplyPayload(BehaviorSyncPayload sync)
    {
        _settings.WrapMouse = sync.WrapMouse;
        _settings.ShareClipboard = sync.ShareClipboard;
        _settings.TransferFile = sync.TransferFile;
        _settings.HideMouseAtEdge = sync.HideMouseAtEdge;
        _settings.DrawMouseCursor = sync.DrawMouseCursor;
        _settings.SameSubnetOnly = sync.SameSubnetOnly;
        _settings.ShowStatusMessages = sync.ShowStatusMessages;
        _settings.BlockScreenSaver = sync.BlockScreenSaver;
        _settings.MoveMouseRelatively = sync.MoveMouseRelatively;
        _settings.BlockMouseAtCorners = sync.BlockMouseAtCorners;
        _settings.EasyMouse = sync.EasyMouse;
        _settings.EdgeCrossMode = sync.EdgeCrossMode is >= 0 and <= 2 ? sync.EdgeCrossMode : 0;
    }

    private void OnPeerLinkChanged(string name, bool outbound, bool inbound)
    {
        if (!(outbound || inbound)) return;
        if (Volatile.Read(ref _applyingRemote) != 0) return;
        _ = SendToPeerAsync(name);
    }
}
