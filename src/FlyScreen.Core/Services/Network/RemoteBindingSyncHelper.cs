using FlyScreen.Models;

namespace FlyScreen.Services.Network;

/// <summary>
/// 远程连接配置与绑定池跨机同步：以修改端为准推送给具备 LayoutSync 的布局电脑；
/// 只同步「配置身份」（地址/类型/备注/期望对端名），不同步本机探测结果
/// （A 测通不代表 B 通；避免出现「同步过来的测试通过」）。
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
            ? "远程绑定已同步到布局内电脑"
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

        // 有地址即同步（配置条目）；不要求本机已测通
        var endpoints = _settings.ConnectionEndpoints
            .Where(e => e != null && !string.IsNullOrWhiteSpace(e.Address))
            .GroupBy(e => EndpointKey(e.Kind, e.Address), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .Select(e => new RemoteEndpointSyncItem
            {
                Id = e.Id,
                Kind = e.Kind,
                Address = (e.Address ?? "").Trim(),
                DisplayName = e.DisplayName ?? "",
                // 期望对端名仅作建连提示，不代表接收方已测通
                VerifiedPeerName = e.VerifiedPeerName ?? "",
                // 故意不传本机探测态；旧端忽略或收到 0 亦不表示测通
                State = ConnectionEndpoint.StateUnknown,
                LastRttMs = null
            })
            .ToList();

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
            Endpoints = endpoints,
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
            StatusMessage?.Invoke($"已同步远程配置 ← {from}（须本机重新测试可达性）");
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

        var incoming = (sync.Endpoints ?? [])
            .Where(e => e != null
                        && !string.IsNullOrWhiteSpace(e.Address)
                        && !string.IsNullOrWhiteSpace(e.Id))
            .GroupBy(e => EndpointKey(e.Kind, e.Address), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        var incomingKeys = new HashSet<string>(
            incoming.Select(e => EndpointKey(e.Kind, e.Address)),
            StringComparer.OrdinalIgnoreCase);
        var incomingIds = new HashSet<string>(incoming.Select(e => e.Id), StringComparer.OrdinalIgnoreCase);

        // 保留本机草稿：无地址或地址未被权威列表覆盖
        var localDrafts = _settings.ConnectionEndpoints
            .Where(e => e != null
                        && !incomingIds.Contains(e.Id)
                        && !incomingKeys.Contains(EndpointKey(e.Kind, e.Address))
                        && (string.IsNullOrWhiteSpace(e.Address)
                            || (string.IsNullOrWhiteSpace(e.VerifiedPeerName)
                                && e.State != ConnectionEndpoint.StateConnected)))
            .ToList();

        var merged = new List<ConnectionEndpoint>();
        foreach (var item in incoming)
        {
            var ep = FindLocalEndpoint(item)
                     ?? new ConnectionEndpoint { Id = item.Id };
            // 权威列表以发送方 Id 为准，避免同址多 Id
            if (!string.IsNullOrWhiteSpace(item.Id))
                ep.Id = item.Id;
            ep.Kind = item.Kind == ConnectionEndpoint.KindRelay
                ? ConnectionEndpoint.KindRelay
                : ConnectionEndpoint.KindRemote;
            ep.Address = (item.Address ?? "").Trim();
            ep.DisplayName = item.DisplayName ?? "";
            if (!string.IsNullOrWhiteSpace(item.VerifiedPeerName))
                ep.VerifiedPeerName = item.VerifiedPeerName.Trim();
            // 本机探测态绝不覆盖：同步来的配置视为「未在本机测试」
            ep.State = ConnectionEndpoint.StateDisconnected;
            ep.LastRttMs = null;
            ep.LastError = "";
            ep.DiscoveredDevices = [];
            merged.Add(ep);
        }

        merged.AddRange(localDrafts);
        _settings.ConnectionEndpoints = merged;
        DeduplicateEndpoints(_settings);

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

    private ConnectionEndpoint? FindLocalEndpoint(RemoteEndpointSyncItem item)
    {
        var byId = _settings.ConnectionEndpoints.FirstOrDefault(e =>
            string.Equals(e.Id, item.Id, StringComparison.OrdinalIgnoreCase));
        if (byId != null) return byId;

        var key = EndpointKey(item.Kind, item.Address);
        return _settings.ConnectionEndpoints.FirstOrDefault(e =>
            string.Equals(EndpointKey(e.Kind, e.Address), key, StringComparison.OrdinalIgnoreCase));
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
