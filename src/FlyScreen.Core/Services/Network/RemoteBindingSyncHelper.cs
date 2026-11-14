using FlyScreen.Models;

namespace FlyScreen.Services.Network;

/// <summary>
/// 远程投影关联跨机同步。连接配置（地址、类型）只留在本机，不进入同步报文。
/// </summary>
public sealed class RemoteBindingSyncHelper
{
    private readonly AppSettings _settings;
    private readonly ConnectionManager _connections;
    private int _applyingRemote;

    public event Action<string>? RemoteBindingSyncedFromPeer;
    public event Action<string>? StatusMessage;

    public RemoteBindingSyncHelper(AppSettings settings, ConnectionManager connections)
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

    public async Task BroadcastAsync(string? statusHint = null)
    {
        if (Volatile.Read(ref _applyingRemote) != 0) return;

        _settings.RemoteBindingRevision = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _settings.EnsureCollections();
        DeduplicateEndpoints(_settings);
        _settings.Save();

        var payload = MessageCodec.JsonBytes(BuildPayload());
        await _connections.BroadcastAsync(MessageType.RemoteBindingSync, payload, PeerCaps.LayoutSync)
            .ConfigureAwait(false);
        StatusMessage?.Invoke(string.IsNullOrWhiteSpace(statusHint)
            ? "远程投影关联已同步到布局内电脑"
            : statusHint);
    }

    public async Task SendToPeerAsync(string peer)
    {
        if (Volatile.Read(ref _applyingRemote) != 0) return;
        if (_settings.RemoteBindingRevision <= 0) return;
        if (!_connections.IsPeerReachable(peer)) return;
        if (!_connections.PeerSupports(peer, PeerCaps.LayoutSync)) return;

        var payload = MessageCodec.JsonBytes(BuildPayload());
        await _connections.SendToAsync(peer, MessageType.RemoteBindingSync, payload).ConfigureAwait(false);
    }

    private RemoteBindingSyncPayload BuildPayload()
    {
        _settings.EnsureCollections();
        DeduplicateEndpoints(_settings);

        var bindings = _settings.RemoteBindings
            .Where(b => b != null && !string.IsNullOrWhiteSpace(b.RemoteName))
            .Select(b => new RemoteBindingSyncItem
            {
                RemoteId = string.IsNullOrWhiteSpace(b.RemoteId) ? Guid.NewGuid().ToString("N") : b.RemoteId,
                RemoteName = b.RemoteName.Trim(),
                BoundLocalDeviceId = b.BoundLocalDeviceId?.Trim() ?? "",
                EndpointId = b.EndpointId ?? "",
                Endpoint = b.Endpoint ?? "",
                Mode = b.Mode,
                // 投影/绑定业务态可同步；RTT 属本机探测，不传
                State = b.State == RemoteBinding.StateProjecting
                    ? RemoteBinding.StateProjecting
                    : RemoteBinding.StateBound,
                RttMs = null
            })
            .ToList();

        return new RemoteBindingSyncPayload
        {
            SourceMachine = LocalPeerIdentity.MachineName,
            Revision = _settings.RemoteBindingRevision,
            Endpoints = [],
            Bindings = bindings,
            ActiveRemoteByLocalDevice = new Dictionary<string, string>(
                _settings.ActiveRemoteByLocalDevice ?? new Dictionary<string, string>(),
                StringComparer.OrdinalIgnoreCase)
        };
    }

    private void OnMessageReceived(string peer, NetMessage msg)
    {
        if (msg.Type != MessageType.RemoteBindingSync) return;
        HandleIncoming(peer, msg.Payload);
    }

    private void HandleIncoming(string peer, byte[] payload)
    {
        var sync = MessageCodec.DecodeJson<RemoteBindingSyncPayload>(payload);
        if (sync == null) return;
        if (string.Equals(sync.SourceMachine, LocalPeerIdentity.MachineName, StringComparison.OrdinalIgnoreCase))
            return;
        if (string.Equals(sync.SourceMachine, Environment.MachineName, StringComparison.OrdinalIgnoreCase)
            && !LocalPeerIdentity.IsBogusMachineName(Environment.MachineName))
            return;
        if (sync.Revision > 0 && sync.Revision <= _settings.RemoteBindingRevision)
            return;
        if (Interlocked.CompareExchange(ref _applyingRemote, 1, 0) != 0)
            return;

        try
        {
            ApplyPayload(sync);
            _settings.RemoteBindingRevision = sync.Revision > 0
                ? sync.Revision
                : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _settings.Save();

            var from = string.IsNullOrWhiteSpace(sync.SourceMachine) ? peer : sync.SourceMachine;
            StatusMessage?.Invoke($"已同步远程投影关联 ← {from}");
            try { RemoteBindingSyncedFromPeer?.Invoke(from); } catch { /* ignore */ }
            try { _connections.RefreshDiscoveryProbeHosts(); } catch { /* ignore */ }
            _ = _connections.RefreshConfiguredPeersAsync();
        }
        finally
        {
            Interlocked.Exchange(ref _applyingRemote, 0);
        }
    }

    private void ApplyPayload(RemoteBindingSyncPayload sync)
    {
        _settings.EnsureCollections();

        _settings.RemoteBindings = (sync.Bindings ?? [])
            .Where(b => b != null && !string.IsNullOrWhiteSpace(b.RemoteName))
            .Select(b => new RemoteBinding
            {
                RemoteId = string.IsNullOrWhiteSpace(b.RemoteId) ? Guid.NewGuid().ToString("N") : b.RemoteId,
                RemoteName = b.RemoteName.Trim(),
                BoundLocalDeviceId = b.BoundLocalDeviceId?.Trim() ?? "",
                EndpointId = b.EndpointId ?? "",
                Endpoint = b.Endpoint ?? "",
                Mode = b.Mode,
                State = b.State == RemoteBinding.StateProjecting
                    ? RemoteBinding.StateProjecting
                    : RemoteBinding.StateBound,
                RttMs = null
            })
            .ToList();

        _settings.ActiveRemoteByLocalDevice = new Dictionary<string, string>(
            sync.ActiveRemoteByLocalDevice ?? new Dictionary<string, string>(),
            StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>同类型同地址只保留一行（优先保留已有探测结果较新的本机行）。</summary>
    public static void DeduplicateEndpoints(AppSettings settings)
    {
        settings.EnsureCollections();
        if (settings.ConnectionEndpoints.Count <= 1) return;

        var kept = new List<ConnectionEndpoint>();
        foreach (var group in settings.ConnectionEndpoints
                     .Where(e => e != null)
                     .GroupBy(e => EndpointKey(e.Kind, e.Address), StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(group.Key) || group.Key.StartsWith("0|", StringComparison.Ordinal))
            {
                // 空地址草稿全部保留
                kept.AddRange(group);
                continue;
            }

            // 有本机测通结果的优先留下，否则取第一条
            var best = group
                .OrderByDescending(e => e.State == ConnectionEndpoint.StateConnected ? 2
                    : e.State == ConnectionEndpoint.StateError ? 1 : 0)
                .ThenByDescending(e => e.LastRttMs.HasValue)
                .First();
            kept.Add(best);
        }

        settings.ConnectionEndpoints = kept;
    }

    public static string EndpointKey(int kind, string? address)
    {
        var raw = (address ?? "").Trim();
        if (string.IsNullOrWhiteSpace(raw))
            return kind + "|";

        if (ConnectionEndpointProbe.TryParseHostPort(raw, out var host, out var port, 15155))
            return $"{kind}|{host.Trim().ToLowerInvariant()}:{port}";

        return $"{kind}|{raw.ToLowerInvariant()}";
    }

    private void OnPeerLinkChanged(string name, bool outbound, bool inbound)
    {
        if (!(outbound || inbound)) return;
        if (Volatile.Read(ref _applyingRemote) != 0) return;
        _ = SendToPeerAsync(name);
    }
}
