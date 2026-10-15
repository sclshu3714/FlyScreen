using FlyScreen.Models;

namespace FlyScreen.Services.Network;

/// <summary>
/// 跨平台共用的布局同步逻辑（Win AppHost / Mac / Linux 宿主均可调用）。
/// </summary>
public sealed class LayoutSyncHelper
{
    private readonly AppSettings _settings;
    private readonly ConnectionManager _connections;
    private int _applyingRemoteLayout;

    public event Action<string>? LayoutSyncedFromPeer;
    public event Action<string>? StatusMessage;

    public LayoutSyncHelper(AppSettings settings, ConnectionManager connections)
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
        if (Volatile.Read(ref _applyingRemoteLayout) != 0) return;

        _settings.LayoutRevision = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _settings.EnsureLocalMachineSlot();
        _settings.Save();

        var payload = MessageCodec.JsonBytes(BuildPayload());
        await _connections.BroadcastAsync(MessageType.LayoutSync, payload, PeerCaps.LayoutSync)
            .ConfigureAwait(false);
        StatusMessage?.Invoke("布局已同步到其他电脑");
    }

    public async Task SendToPeerAsync(string peer)
    {
        if (Volatile.Read(ref _applyingRemoteLayout) != 0) return;
        if (_settings.LayoutRevision <= 0) return;
        if (!_connections.IsPeerReachable(peer)) return;
        if (!_connections.PeerSupports(peer, PeerCaps.LayoutSync)) return;

        var payload = MessageCodec.JsonBytes(BuildPayload());
        await _connections.SendToAsync(peer, MessageType.LayoutSync, payload).ConfigureAwait(false);
    }

    private LayoutSyncPayload BuildPayload()
    {
        var slots = _settings.MachineSlots.ToList();
        while (slots.Count < 4) slots.Add("");
        if (slots.Count > 4) slots = slots.Take(4).ToList();

        return new LayoutSyncPayload
        {
            SourceMachine = Environment.MachineName,
            MachineSlots = slots,
            TwoRow = _settings.TwoRow,
            Revision = _settings.LayoutRevision
        };
    }

    private void OnMessageReceived(string peer, NetMessage msg)
    {
        if (msg.Type != MessageType.LayoutSync) return;
        HandleIncoming(peer, msg.Payload);
    }

    private void HandleIncoming(string peer, byte[] payload)
    {
        var sync = MessageCodec.DecodeJson<LayoutSyncPayload>(payload);
        if (sync == null) return;
        if (string.Equals(sync.SourceMachine, Environment.MachineName, StringComparison.OrdinalIgnoreCase))
            return;
        if (sync.Revision > 0 && sync.Revision <= _settings.LayoutRevision)
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

            _settings.MachineSlots = slots;
            _settings.TwoRow = sync.TwoRow;
            _settings.LayoutRevision = sync.Revision > 0
                ? sync.Revision
                : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _settings.EnsureLocalMachineSlot();
            _settings.Save();

            var from = string.IsNullOrWhiteSpace(sync.SourceMachine) ? peer : sync.SourceMachine;
            StatusMessage?.Invoke($"已同步布局 ← {from}");
            try { LayoutSyncedFromPeer?.Invoke(from); } catch { /* ignore */ }
        }
        finally
        {
            Interlocked.Exchange(ref _applyingRemoteLayout, 0);
        }
    }

    private void OnPeerLinkChanged(string name, bool outbound, bool inbound)
    {
        if (!(outbound || inbound)) return;
        if (Volatile.Read(ref _applyingRemoteLayout) != 0) return;
        _ = SendToPeerAsync(name);
    }
}
