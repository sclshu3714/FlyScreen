using System.Net;
using FlyScreen.Models;

namespace FlyScreen.Services.Network;

/// <summary>
/// 安全密钥同步：仅面向电脑布局内的设备。
/// TCP 只推布局内已连接对端；UDP 全网卡广播 + 向布局内已知地址单播（含 Tailscale），
/// 以便密钥不同尚未连上的布局电脑也能收到。接收方仍校验发送方属于布局关系。
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

    /// <summary>把当前设置中的密钥同步到布局内所有电脑（已连接 + 仅发现/探测地址）。</summary>
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

        // TCP：只发给布局内已连接的电脑（不广播到远程影子等非布局链路）
        var tcpPeers = 0;
        foreach (var peer in slots)
        {
            ct.ThrowIfCancellationRequested();
            if (string.Equals(peer, LocalPeerIdentity.MachineName, StringComparison.OrdinalIgnoreCase))
                continue;
            try
            {
                if (await _connections.SendToAsync(peer, MessageType.SecurityKeySync, bytes)
                        .ConfigureAwait(false))
                    tcpPeers++;
            }
            catch { /* UDP 仍可推 */ }
        }

        // 布局内对端的已知探测地址（发现表 + 连接配置命中机名）→ UDP 单播
        var unicastHosts = CollectLayoutUnicastHosts(slots);

        // 多发几次：全网卡广播 + 布局内单播，提高密钥不同未连上实例的送达率
        var udpOk = false;
        for (var i = 0; i < 3; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (await _discovery.TryBroadcastRawEverywhereAsync(bytes, unicastHosts, ct)
                    .ConfigureAwait(false))
                udpOk = true;
            if (i < 2)
                await Task.Delay(80, ct).ConfigureAwait(false);
        }

        // 发现未启动时兜底
        if (!udpOk)
            udpOk = await FallbackUdpBroadcastAsync(bytes, unicastHosts, ct).ConfigureAwait(false);

        lock (_gate)
            _lastAppliedRevision = Math.Max(_lastAppliedRevision, revision);

        var msg = udpOk
            ? $"已同步安全密钥到布局内设备（TCP 约 {tcpPeers} 台，UDP 已全网卡广播/单播）。"
            : $"已向布局内已连接对端同步安全密钥（约 {tcpPeers} 台）；UDP 发送失败，密钥不同的未连设备可能收不到。";
        StatusMessage?.Invoke(msg);
        return (true, msg);
    }

    /// <summary>仅收集布局槽位电脑的可达地址，不含「只在连接配置、未进布局」的远程。</summary>
    private List<string> CollectLayoutUnicastHosts(IReadOnlyList<string> layoutSlots)
    {
        var hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var tcpPort = _settings.TcpPort > 0 ? _settings.TcpPort : 15155;

        foreach (var peer in layoutSlots)
        {
            if (string.Equals(peer, LocalPeerIdentity.MachineName, StringComparison.OrdinalIgnoreCase))
                continue;

            if (_discovery.TryGetConnectTargets(peer, tcpPort, out var targets))
            {
                foreach (var (ip, _) in targets)
                {
                    if (!string.IsNullOrWhiteSpace(ip))
                        hosts.Add(ip);
                }
            }
            else
            {
                var ip = _discovery.FindIp(peer);
                if (!string.IsNullOrWhiteSpace(ip))
                    hosts.Add(ip!);
            }

            foreach (var ep in _settings.ConnectionEndpoints ?? [])
            {
                if (ep.Kind != ConnectionEndpoint.KindRemote) continue;
                if (!string.Equals(ep.VerifiedPeerName, peer, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!ConnectionEndpointProbe.TryParseHostPort(ep.Address?.Trim() ?? "", out var host, out _, tcpPort))
                    continue;
                hosts.Add(host);
            }
        }

        return hosts.ToList();
    }

    private async Task<bool> FallbackUdpBroadcastAsync(
        byte[] bytes, IReadOnlyList<string> unicastHosts, CancellationToken ct)
    {
        try
        {
            var port = _discovery.UdpPort > 0 ? _discovery.UdpPort : _settings.UdpPort;
            if (port <= 0) port = 15156;
            var any = false;
            foreach (var nic in NicService.ListAnnounceIpv4Nics(includeOverlay: true))
            {
                if (ct.IsCancellationRequested) break;
                if (string.IsNullOrWhiteSpace(nic.IPv4) || nic.IPv4 == "0.0.0.0") continue;
                try
                {
                    using var udp = new System.Net.Sockets.UdpClient();
                    udp.Client.Bind(new IPEndPoint(IPAddress.Parse(nic.IPv4), 0));
                    udp.EnableBroadcast = true;
                    await udp.SendAsync(bytes, bytes.Length, new IPEndPoint(IPAddress.Broadcast, port))
                        .ConfigureAwait(false);
                    any = true;
                }
                catch { /* continue */ }
            }

            foreach (var host in unicastHosts)
            {
                if (ct.IsCancellationRequested) break;
                if (!IPAddress.TryParse(host, out var ip)) continue;
                try
                {
                    using var udp = new System.Net.Sockets.UdpClient();
                    await udp.SendAsync(bytes, bytes.Length, new IPEndPoint(ip, port)).ConfigureAwait(false);
                    any = true;
                }
                catch { /* continue */ }
            }

            if (any) return true;

            using var fallback = new System.Net.Sockets.UdpClient();
            fallback.EnableBroadcast = true;
            await fallback.SendAsync(bytes, bytes.Length, new IPEndPoint(IPAddress.Broadcast, port))
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

        // 仅布局关系：发送方在本机布局中，或本机出现在对方声明的布局槽位中
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
