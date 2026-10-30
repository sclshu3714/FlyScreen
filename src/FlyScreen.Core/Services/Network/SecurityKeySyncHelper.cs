using FlyScreen.Models;

namespace FlyScreen.Services.Network;

/// <summary>
/// 安全密钥同步：TCP 推已连接对端；UDP 广播推布局内因密钥不同未连上的实例。
/// 接收方仅当发送方在本机电脑布局槽位中（或本机在对方布局中）时才应用。
/// </summary>
public sealed class SecurityKeySyncHelper : IDisposable
{
    private readonly AppSettings _settings;
    private readonly ConnectionManager _connections;
    private readonly DiscoveryService _discovery;
    private readonly object _gate = new();
    private long _lastAppliedRevision;
    private bool _disposed;

    /// <summary>本机已应用对端推送的密钥（UI 应刷新并整网重启）。</summary>
    public event Action<string, string>? SecurityKeyAppliedFromPeer; // fromMachine, newKey

    public event Action<string>? StatusMessage;

    public SecurityKeySyncHelper(AppSettings settings, ConnectionManager connections, DiscoveryService discovery)
    {
        _settings = settings;
        _connections = connections;
        _discovery = discovery;
        _connections.MessageReceived += OnMessage;
        _discovery.SecurityKeySyncPacketReceived += OnUdpKeySync;
    }

    /// <summary>把当前设置中的密钥同步到布局内所有电脑（已连接 + 仅发现）。</summary>
    public async Task<(bool Ok, string Message)> SyncCurrentKeyAsync(CancellationToken ct = default)
    {
        var key = (_settings.SecurityKey ?? "").Trim();
        if (key.Length < 4)
            return (false, "安全密钥过短，无法同步");

        var slots = LayoutSlotMerge.NonEmptyNames(_settings.MachineSlots).ToList();
        if (slots.Count <= 1)
            return (false, "电脑布局中没有其他设备，无需同步");

        var revision = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var payload = new SecurityKeySyncPayload
        {
            Kind = DiscoveryService.SecurityKeySyncPayloadKind,
            SourceMachine = LocalPeerIdentity.MachineName,
            SecurityKey = key,
            Revision = revision,
            LayoutSlots = slots,
            Ip = _discovery.BoundIp,
            TcpPort = _discovery.AnnounceTcpPort > 0 ? _discovery.AnnounceTcpPort : _settings.TcpPort
        };
        var bytes = MessageCodec.JsonBytes(payload);

        var tcpPeers = 0;
        try
        {
            await _connections.BroadcastAsync(MessageType.SecurityKeySync, bytes, requiredCap: null)
                .ConfigureAwait(false);
            foreach (var peer in slots)
            {
                if (string.Equals(peer, LocalPeerIdentity.MachineName, StringComparison.OrdinalIgnoreCase))
                    continue;
                var st = _connections.GetPeerStatus(peer);
                if (st.AnyAlive) tcpPeers++;
            }
        }
        catch { /* UDP 仍可推密钥不同未连上的机 */ }

        // 多发几次 UDP，提高密钥不同未连上实例的送达率
        var udpOk = false;
        for (var i = 0; i < 3; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (await _discovery.TryBroadcastRawAsync(bytes, ct).ConfigureAwait(false))
                udpOk = true;
            if (i < 2)
                await Task.Delay(80, ct).ConfigureAwait(false);
        }

        // 发现未启动时兜底新建广播套接字
        if (!udpOk)
            udpOk = await FallbackUdpBroadcastAsync(bytes, ct).ConfigureAwait(false);

        lock (_gate)
            _lastAppliedRevision = Math.Max(_lastAppliedRevision, revision);

        var msg = udpOk
            ? $"已同步安全密钥（已连接约 {tcpPeers} 台，UDP 已广播布局内设备）。"
            : $"已向已连接对端同步安全密钥（约 {tcpPeers} 台）；UDP 广播失败，密钥不同的未连设备可能收不到。";
        StatusMessage?.Invoke(msg);
        return (true, msg);
    }

    private async Task<bool> FallbackUdpBroadcastAsync(byte[] bytes, CancellationToken ct)
    {
        try
        {
            var port = _discovery.UdpPort > 0 ? _discovery.UdpPort : _settings.UdpPort;
            if (port <= 0) port = 15156;
            using var udp = new System.Net.Sockets.UdpClient();
            udp.EnableBroadcast = true;
            await udp.SendAsync(bytes, bytes.Length,
                    new System.Net.IPEndPoint(System.Net.IPAddress.Broadcast, port))
                .ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void OnMessage(string peer, NetMessage msg)
    {
        if (msg.Type != MessageType.SecurityKeySync) return;
        var sync = MessageCodec.DecodeJson<SecurityKeySyncPayload>(msg.Payload);
        if (sync == null) return;
        if (string.IsNullOrWhiteSpace(sync.SourceMachine))
            sync.SourceMachine = peer;
        TryApplyFrom(sync);
    }

    private void OnUdpKeySync(SecurityKeySyncPayload sync)
        => TryApplyFrom(sync);

    private void TryApplyFrom(SecurityKeySyncPayload sync)
    {
        if (_disposed) return;
        var key = (sync.SecurityKey ?? "").Trim();
        var source = (sync.SourceMachine ?? "").Trim();
        if (key.Length < 4 || string.IsNullOrWhiteSpace(source)) return;
        if (string.Equals(source, LocalPeerIdentity.MachineName, StringComparison.OrdinalIgnoreCase))
            return;

        lock (_gate)
        {
            if (sync.Revision > 0 && sync.Revision <= _lastAppliedRevision)
                return;
        }

        if (string.Equals(key, (_settings.SecurityKey ?? "").Trim(), StringComparison.Ordinal))
        {
            lock (_gate)
            {
                if (sync.Revision > _lastAppliedRevision)
                    _lastAppliedRevision = sync.Revision;
            }
            return;
        }

        // 发送方须在本机布局中，或本机出现在对方声明的布局槽位中
        var localSlots = LayoutSlotMerge.NonEmptyNames(_settings.MachineSlots).ToList();
        var sourceInLocal = localSlots.Any(s => string.Equals(s, source, StringComparison.OrdinalIgnoreCase));
        var selfInRemote = sync.LayoutSlots != null && sync.LayoutSlots.Any(s =>
            string.Equals(s, LocalPeerIdentity.MachineName, StringComparison.OrdinalIgnoreCase)
            || (!LocalPeerIdentity.IsBogusMachineName(Environment.MachineName)
                && string.Equals(s, Environment.MachineName, StringComparison.OrdinalIgnoreCase)));
        if (!sourceInLocal && !selfInRemote)
            return;

        lock (_gate)
        {
            if (sync.Revision > 0 && sync.Revision <= _lastAppliedRevision)
                return;
            if (sync.Revision > 0)
                _lastAppliedRevision = sync.Revision;
        }

        _settings.SecurityKey = key;
        _settings.Save();
        StatusMessage?.Invoke($"已接收并应用安全密钥 ← {source}");
        try { SecurityKeyAppliedFromPeer?.Invoke(source, key); } catch { /* ignore */ }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _connections.MessageReceived -= OnMessage;
        _discovery.SecurityKeySyncPacketReceived -= OnUdpKeySync;
    }
}
