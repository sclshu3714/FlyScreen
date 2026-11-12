using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using FlyScreen.Models;

namespace FlyScreen.Services.Network;

public sealed class DiscoveredPeer
{
    public string MachineName { get; set; } = "";
    /// <summary>对端宣告的 IPv4。</summary>
    public string Ip { get; set; } = "";
    /// <summary>实际收到 UDP 的来源 IPv4（回连优先用它）。</summary>
    public string ObservedIp { get; set; } = "";
    public int TcpPort { get; set; }
    public DateTime LastSeenUtc { get; set; } = DateTime.UtcNow;
    /// <summary>探测学入的条目在此之前不因 UDP 静默被 prune（Tailscale 无广播时保活）。</summary>
    public DateTime StickyUntilUtc { get; set; }

    /// <summary>
    /// 发现来源：0=局域网广播 1=连接配置「远程」探测 2=中转/信令服务宣告。
    /// 与 <see cref="Models.DeviceInventoryItem"/> 的 Kind 对齐。
    /// </summary>
    public int Origin { get; set; } = DiscoveryOrigin.Lan;
}

/// <summary>发现来源（写入 DiscoveredPeer.Origin）。</summary>
public static class DiscoveryOrigin
{
    public const int Lan = 0;
    public const int Remote = 1;
    public const int Service = 2;

    public static int Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return Lan;
        raw = raw.Trim();
        if (raw.Equals("service", StringComparison.OrdinalIgnoreCase)
            || raw.Equals("relay", StringComparison.OrdinalIgnoreCase)
            || raw.Equals("2", StringComparison.Ordinal))
            return Service;
        if (raw.Equals("remote", StringComparison.OrdinalIgnoreCase)
            || raw.Equals("1", StringComparison.Ordinal))
            return Remote;
        if (raw.Equals("local", StringComparison.OrdinalIgnoreCase)
            || raw.Equals("lan", StringComparison.OrdinalIgnoreCase)
            || raw.Equals("0", StringComparison.Ordinal))
            return Lan;
        return Lan;
    }

    /// <summary>
    /// 合并来源：
    /// 远程探测可纠正误标的局域网；已是远程则不被普通局域网包降级；
    /// 局域网仍优先于中转（同机双路径时避免被中继标成服务）。
    /// </summary>
    public static int Prefer(int existing, int incoming)
    {
        if (existing is not (Lan or Remote or Service)) existing = Lan;
        if (incoming is not (Lan or Remote or Service)) incoming = Lan;

        // 远程配置探测 ↔ 误标 Lan：以远程为准
        if (existing == Remote || incoming == Remote)
        {
            if (existing == Lan || incoming == Lan)
                return Remote;
            if (existing == Service || incoming == Service)
                return Remote;
            return Remote;
        }

        // 中转 ↔ 局域网：局域网优先（同机在局域网出现）
        if (existing == Service || incoming == Service)
        {
            if (existing == Lan || incoming == Lan)
                return Lan;
            return Service;
        }

        return Lan;
    }
}

/// <summary>
/// UDP 广播自动发现局域网内的玄鸟实例；绑定到选定网卡。
/// </summary>
public sealed class DiscoveryService : IAsyncDisposable
{
    private readonly object _gate = new();
    private UdpClient? _udp;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private readonly Dictionary<string, DiscoveredPeer> _peers = new(StringComparer.OrdinalIgnoreCase);
    private IPAddress? _subnetBroadcast;
    private bool _announceAllUsable;
    private string[] _probeHosts = [];
    /// <summary>探测目标 host/IP → 发现来源（远程/服务）。</summary>
    private Dictionary<string, int> _probeHostOrigins = new(StringComparer.OrdinalIgnoreCase);
    private int _probeHostTick;

    public string LocalMachine => LocalPeerIdentity.MachineName;
    public string BoundIp { get; private set; } = "";
    public int UdpPort { get; private set; }
    public int AnnounceTcpPort { get; set; } = 15155;

    public event Action<IReadOnlyList<DiscoveredPeer>>? PeersUpdated;

    /// <summary>收到 UDP 安全密钥同步包（密钥不同时无法走 TCP）。</summary>
    public event Action<SecurityKeySyncPayload>? SecurityKeySyncPacketReceived;

    /// <summary>对端请本机主动连过去（name, 优先 IP, tcp 端口）。</summary>
    public event Action<string, string, int>? PeerDialRequested;

    public const string DialRequestKind = "DialRequest";

    /// <param name="announceIp">发现包中宣告的本机 IPv4（对端用来回连）。</param>
    /// <param name="bindAny">true 时绑定 0.0.0.0（Android / 自动监听：可收齐各网卡广播）。</param>
    /// <param name="announceAllUsable">
    /// true 时从每块网卡各发一遍广播（含 Tailscale 等 overlay；避免绑 Any 时只从默认路由口发出）。
    /// </param>
    public void Start(string announceIp, int udpPort, string? subnetMask = null, bool bindAny = false,
        bool announceAllUsable = false)
    {
        Stop();
        BoundIp = string.IsNullOrWhiteSpace(announceIp) ? "0.0.0.0" : announceIp;
        UdpPort = udpPort;
        // 默认局域网发现：始终按「全网卡」宣告（含 overlay）；参数保留兼容旧调用
        _announceAllUsable = true;
        _ = announceAllUsable;
        _subnetBroadcast = BoundIp == "0.0.0.0"
            ? null
            : TryComputeBroadcast(BoundIp, subnetMask);
        _cts = new CancellationTokenSource();

        var bindAddr = bindAny || BoundIp == "0.0.0.0"
            ? IPAddress.Any
            : IPAddress.Parse(BoundIp);
        // 先建未绑定套接字，再开 ReuseAddress，避免与同机另一实例（WPF/MAUI）争用 UDP 发现端口时直接崩
        var udp = new UdpClient();
        try
        {
            udp.ExclusiveAddressUse = false;
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        }
        catch
        {
            // ignore
        }

        try
        {
            udp.Client.Bind(new IPEndPoint(bindAddr, udpPort));
        }
        catch (SocketException) when (!bindAny && bindAddr != IPAddress.Any)
        {
            // 单地址绑定失败时回退 Any（常见于 Android / 切换 Wi‑Fi 后）
            udp.Dispose();
            Start(announceIp, udpPort, subnetMask, bindAny: true, announceAllUsable: true);
            return;
        }

        udp.EnableBroadcast = true;
        udp.MulticastLoopback = false;
        try
        {
            // 部分环境（含 WSL 桥接）需要显式允许广播
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Broadcast, true);
        }
        catch
        {
            // ignore
        }

        _udp = udp;

        var ct = _cts.Token;
        _loop = Task.Run(async () =>
        {
            var announce = AnnounceLoopAsync(udp, ct);
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    UdpReceiveResult result;
                    try
                    {
                        // 不向 ReceiveAsync 传入 ct：停止时通过 Close() 解除阻塞
                        result = await udp.ReceiveAsync().ConfigureAwait(false);
                    }
                    catch (ObjectDisposedException)
                    {
                        break;
                    }
                    catch (SocketException)
                    {
                        if (ct.IsCancellationRequested) break;
                        await Task.Delay(200).ConfigureAwait(false);
                        continue;
                    }

                    if (ct.IsCancellationRequested) break;
                    HandlePacket(result.Buffer, result.RemoteEndPoint);
                }
            }
            finally
            {
                try { await announce.ConfigureAwait(false); } catch { /* ignore */ }
            }
        });
    }

    private async Task AnnounceLoopAsync(UdpClient udp, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var primaryPayload = BuildAnnouncePayload(BoundIp);

                // 子网限播优先；全播仅作回退（WSL/VPN 上 255.255.255.255 常 HostUnreachable）
                await TryAnnounceBroadcastAsync(udp, primaryPayload, UdpPort, _subnetBroadcast, BoundIp)
                    .ConfigureAwait(false);

                // 全网卡逐口宣告（物理 + Tailscale 等 overlay），避免只走默认路由口
                if (_announceAllUsable)
                    await AnnounceFromAllNicsAsync(ct).ConfigureAwait(false);

                // 连接配置探测地址：约每 6s 单播一次（Tailscale 通常不转发广播）
                if ((++_probeHostTick % 3) == 0)
                    await UnicastAnnounceToHostsAsync(_probeHosts, udp, ct).ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException)
            {
                if (ct.IsCancellationRequested) break;
            }
            catch
            {
                // ignore transient send errors
            }

            Prune();

            try
            {
                await Task.Delay(2000, CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                break;
            }

            if (ct.IsCancellationRequested) break;
        }
    }

    private byte[] BuildAnnouncePayload(string announceIp)
        => JsonSerializer.SerializeToUtf8Bytes(new DiscoveryPacket
        {
            MachineName = LocalMachine,
            TcpPort = AnnounceTcpPort,
            Ip = string.IsNullOrWhiteSpace(announceIp) || announceIp == "0.0.0.0"
                ? BoundIp
                : announceIp
        });

    private Task AnnounceFromAllNicsAsync(CancellationToken ct)
        => AnnounceFromNicsAsync(NicService.ListAnnounceIpv4Nics(includeOverlay: true), ct);

    /// <summary>连接配置中的远程探测地址（周期单播 + 刷新突发共用）；默认来源=远程。</summary>
    public void SetProbeHosts(IEnumerable<string>? hosts)
        => SetProbeTargets((hosts ?? []).Select(h => (h, DiscoveryOrigin.Remote)));

    /// <summary>探测目标及来源（远程一对一 / 中转服务）。</summary>
    public void SetProbeTargets(IEnumerable<(string Host, int Origin)>? targets)
    {
        var list = new List<string>();
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (hostRaw, originRaw) in targets ?? [])
        {
            var host = (hostRaw ?? "").Trim();
            if (string.IsNullOrWhiteSpace(host)) continue;
            var origin = originRaw is DiscoveryOrigin.Remote or DiscoveryOrigin.Service
                ? originRaw
                : DiscoveryOrigin.Remote;
            if (!map.ContainsKey(host))
                list.Add(host);
            map[host] = DiscoveryOrigin.Prefer(
                map.TryGetValue(host, out var old) ? old : origin,
                origin);
        }

        _probeHosts = list.ToArray();
        _probeHostOrigins = map;
    }

    /// <summary>
    /// 刷新发现：从全部网卡（可选含 Tailscale 等 overlay）突发广播，
    /// 并对连接配置中的探测地址单播宣告。
    /// </summary>
    public async Task BurstProbeAsync(
        IEnumerable<string>? unicastHosts,
        bool includeOverlayNics,
        CancellationToken ct = default)
    {
        if (UdpPort <= 0) return;

        if (unicastHosts != null)
        {
            // 合并探测目标，保留已通过 SetProbeTargets 设定的来源（勿全部降成远程）
            var merged = _probeHostOrigins
                .Select(kv => (kv.Key, kv.Value))
                .Concat(unicastHosts.Select(h =>
                {
                    var host = (h ?? "").Trim();
                    var origin = _probeHostOrigins.TryGetValue(host, out var o) ? o : DiscoveryOrigin.Remote;
                    return (host, origin);
                }));
            SetProbeTargets(merged);
        }

        await AnnounceFromNicsAsync(NicService.ListAnnounceIpv4Nics(includeOverlayNics), ct)
            .ConfigureAwait(false);

        // 主套接字再发一遍限播/全播（发现已 Start 时）
        var udp = _udp;
        if (udp != null)
        {
            try
            {
                var primary = BuildAnnouncePayload(BoundIp);
                await TryAnnounceBroadcastAsync(udp, primary, UdpPort, _subnetBroadcast, BoundIp)
                    .ConfigureAwait(false);
            }
            catch
            {
                // ignore
            }
        }

        await UnicastAnnounceToHostsAsync(_probeHosts, udp, ct).ConfigureAwait(false);
    }

    private async Task UnicastAnnounceToHostsAsync(
        IReadOnlyList<string> hosts, UdpClient? udp, CancellationToken ct)
    {
        if (hosts.Count == 0 || UdpPort <= 0) return;

        foreach (var host in hosts)
        {
            if (ct.IsCancellationRequested) return;
            if (!IPAddress.TryParse(host, out var ip))
            {
                try
                {
                    var entry = await Dns.GetHostEntryAsync(host, ct).ConfigureAwait(false);
                    ip = entry.AddressList.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
                    if (ip == null) continue;
                }
                catch
                {
                    continue;
                }
            }

            // 解析后的 IP 继承探测目标来源，便于收包时判定「远程/服务」
            if (_probeHostOrigins.TryGetValue(host, out var hostOrigin))
            {
                var ipText = ip.ToString();
                _probeHostOrigins[ipText] = DiscoveryOrigin.Prefer(
                    _probeHostOrigins.TryGetValue(ipText, out var old) ? old : hostOrigin,
                    hostOrigin);
            }

            try
            {
                var payload = BuildAnnouncePayload(
                    string.IsNullOrWhiteSpace(BoundIp) || BoundIp == "0.0.0.0"
                        ? (NicService.PickBestUsableNic()?.IPv4 ?? "0.0.0.0")
                        : BoundIp);
                if (udp != null)
                {
                    await udp.SendAsync(payload, payload.Length, new IPEndPoint(ip, UdpPort))
                        .ConfigureAwait(false);
                }
                else
                {
                    using var sock = new UdpClient();
                    sock.EnableBroadcast = true;
                    await sock.SendAsync(payload, payload.Length, new IPEndPoint(ip, UdpPort))
                        .ConfigureAwait(false);
                }
            }
            catch
            {
                // 单目标失败继续
            }
        }
    }

    private async Task AnnounceFromNicsAsync(IReadOnlyList<NicInfo> nics, CancellationToken ct)
    {
        foreach (var nic in nics)
        {
            if (ct.IsCancellationRequested) return;
            if (string.IsNullOrWhiteSpace(nic.IPv4) || nic.IPv4 == "0.0.0.0")
                continue;
            try
            {
                var local = new IPEndPoint(IPAddress.Parse(nic.IPv4), 0);
                using var sock = new UdpClient();
                try
                {
                    sock.ExclusiveAddressUse = false;
                    sock.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                }
                catch { /* ignore */ }

                sock.Client.Bind(local);
                sock.EnableBroadcast = true;
                try
                {
                    sock.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Broadcast, true);
                }
                catch { /* ignore */ }

                var payload = BuildAnnouncePayload(nic.IPv4);
                var subnet = TryComputeBroadcast(nic.IPv4, nic.SubnetMask);
                await TryAnnounceBroadcastAsync(sock, payload, UdpPort, subnet, nic.IPv4)
                    .ConfigureAwait(false);
            }
            catch
            {
                // 单口失败不影响其它口
            }
        }
    }

    private void HandlePacket(byte[] buffer, IPEndPoint remote)
    {
        try
        {
            using var doc = JsonDocument.Parse(buffer);
            if (doc.RootElement.TryGetProperty("Kind", out var kindEl))
            {
                var kind = kindEl.GetString();
                if (string.Equals(kind, SecurityKeySyncPayloadKind, StringComparison.Ordinal))
                {
                    var sync = JsonSerializer.Deserialize<SecurityKeySyncPayload>(buffer);
                    if (sync == null || string.IsNullOrWhiteSpace(sync.SourceMachine)
                        || string.IsNullOrWhiteSpace(sync.SecurityKey))
                        return;
                    if (string.Equals(sync.SourceMachine, LocalMachine, StringComparison.OrdinalIgnoreCase))
                        return;
                    var observed = ObservedIpv4(remote);
                    if (string.IsNullOrWhiteSpace(sync.Ip))
                        sync.Ip = observed ?? "";
                    var advertised = IsUsableLanIpv4(sync.Ip) ? sync.Ip! : (observed ?? "");
                    if (!string.IsNullOrWhiteSpace(advertised))
                        RememberPeer(sync.SourceMachine, advertised, sync.TcpPort, observed);
                    try { SecurityKeySyncPacketReceived?.Invoke(sync); } catch { /* ignore */ }
                    return;
                }

                if (string.Equals(kind, DialRequestKind, StringComparison.Ordinal))
                {
                    var dial = JsonSerializer.Deserialize<DialRequestPayload>(buffer);
                    if (dial == null || string.IsNullOrWhiteSpace(dial.SourceMachine))
                        return;
                    if (string.Equals(dial.SourceMachine, LocalMachine, StringComparison.OrdinalIgnoreCase))
                        return;
                    var observed = ObservedIpv4(remote);
                    var advertised = IsUsableLanIpv4(dial.Ip) ? dial.Ip! : (observed ?? "");
                    if (!string.IsNullOrWhiteSpace(advertised))
                        RememberPeer(dial.SourceMachine, advertised, dial.TcpPort, observed);
                    var connectIp = OrderConnectIps(advertised, observed).FirstOrDefault() ?? "";
                    if (string.IsNullOrWhiteSpace(connectIp))
                        return;
                    try { PeerDialRequested?.Invoke(dial.SourceMachine, connectIp, dial.TcpPort); }
                    catch { /* ignore */ }
                    return;
                }
            }

            var pkt = JsonSerializer.Deserialize<DiscoveryPacket>(buffer);
            if (pkt == null || string.IsNullOrWhiteSpace(pkt.MachineName))
                return;

            var observedIp = ObservedIpv4(remote);
            var senderIp = SenderIpv4(remote);
            var ip = IsUsableLanIpv4(pkt.Ip) ? pkt.Ip : (observedIp ?? pkt.Ip);

            // 同名且同 IP → 本机回环，忽略
            if (string.Equals(pkt.MachineName, LocalMachine, StringComparison.OrdinalIgnoreCase)
                && (string.IsNullOrWhiteSpace(BoundIp) || string.Equals(ip, BoundIp, StringComparison.OrdinalIgnoreCase)
                    || IPAddress.IsLoopback(remote.Address)))
                return;

            // 同名但不同 IP：视为冲突对端（例如旧 WSL 与宿主机撞名），仍登记，便于排查
            // 正常情况下 WSL 已使用带后缀的 MachineName，不会走到此分支

            var origin = ResolvePacketOrigin(pkt.Origin, senderIp);
            // 仅 IP/端口/来源等实质变化时通知，避免心跳刷 LastSeen 导致设备列表整表重绑闪烁
            UpsertPeer(pkt.MachineName, ip, pkt.TcpPort <= 0 ? 15155 : pkt.TcpPort, observedIp, origin, stickySeconds: 0);
        }
        catch
        {
            // ignore bad packets
        }
    }

    private int ResolvePacketOrigin(string? packetOrigin, string? senderIp)
    {
        // 包内显式标记优先（中继宣告 Origin=service）
        if (!string.IsNullOrWhiteSpace(packetOrigin))
            return DiscoveryOrigin.Parse(packetOrigin);

        if (!string.IsNullOrWhiteSpace(senderIp)
            && _probeHostOrigins.TryGetValue(senderIp, out var byIp))
            return byIp;

        // 探测目标可能是主机名：任意已登记的 host 键与 sender 无法直接比，退回看是否在探测列表的解析缓存式匹配
        foreach (var kv in _probeHostOrigins)
        {
            if (IPAddress.TryParse(kv.Key, out _)) continue;
            // 主机名目标：若宣告来自公网/跨网且我们正在探测该 host，无法在此解析；由 ConnectionManager 的 TCP Remember 补来源
        }

        return DiscoveryOrigin.Lan;
    }

    public const string SecurityKeySyncPayloadKind = "SecurityKeySync";

    /// <summary>经发现口 UDP 广播（子网广播 + 全网广播）；发现未启动时返回 false。</summary>
    public Task<bool> TryBroadcastRawAsync(byte[] payload, CancellationToken ct = default)
        => TryBroadcastRawEverywhereAsync(payload, unicastHosts: null, ct);

    /// <summary>
    /// 全网卡（含 Tailscale）逐口广播 raw 包，并可选单播到指定主机。
    /// 用于安全密钥同步等：发送面与发现一致，业务侧自行限制目标范围。
    /// </summary>
    public async Task<bool> TryBroadcastRawEverywhereAsync(
        byte[] payload,
        IEnumerable<string>? unicastHosts,
        CancellationToken ct = default)
    {
        if (payload.Length == 0 || UdpPort <= 0)
            return false;

        var any = false;
        var udp = _udp;
        if (udp != null)
        {
            try
            {
                if (await TryAnnounceBroadcastAsync(udp, payload, UdpPort, _subnetBroadcast, BoundIp)
                        .ConfigureAwait(false))
                    any = true;
            }
            catch (ObjectDisposedException)
            {
                // fall through to per-nic
            }
            catch
            {
                // ignore
            }
        }

        if (await SendRawFromNicsAsync(payload, NicService.ListAnnounceIpv4Nics(includeOverlay: true), ct)
                .ConfigureAwait(false))
            any = true;

        var hosts = (unicastHosts ?? [])
            .Select(h => (h ?? "").Trim())
            .Where(h => !string.IsNullOrWhiteSpace(h))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (hosts.Count > 0
            && await UnicastRawToHostsAsync(payload, hosts, udp, ct).ConfigureAwait(false))
            any = true;

        return any;
    }

    private async Task<bool> SendRawFromNicsAsync(byte[] payload, IReadOnlyList<NicInfo> nics, CancellationToken ct)
    {
        var any = false;
        foreach (var nic in nics)
        {
            if (ct.IsCancellationRequested) break;
            if (string.IsNullOrWhiteSpace(nic.IPv4) || nic.IPv4 == "0.0.0.0")
                continue;
            try
            {
                var local = new IPEndPoint(IPAddress.Parse(nic.IPv4), 0);
                using var sock = new UdpClient();
                try
                {
                    sock.ExclusiveAddressUse = false;
                    sock.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                }
                catch { /* ignore */ }

                sock.Client.Bind(local);
                sock.EnableBroadcast = true;
                try
                {
                    sock.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Broadcast, true);
                }
                catch { /* ignore */ }

                var subnet = TryComputeBroadcast(nic.IPv4, nic.SubnetMask);
                if (await TryAnnounceBroadcastAsync(sock, payload, UdpPort, subnet, nic.IPv4)
                        .ConfigureAwait(false))
                    any = true;
            }
            catch
            {
                // 单口失败继续
            }
        }

        return any;
    }

    private async Task<bool> UnicastRawToHostsAsync(
        byte[] payload, IReadOnlyList<string> hosts, UdpClient? udp, CancellationToken ct)
    {
        var any = false;
        foreach (var host in hosts)
        {
            if (ct.IsCancellationRequested) break;
            if (!IPAddress.TryParse(host, out var ip))
            {
                try
                {
                    var entry = await Dns.GetHostEntryAsync(host, ct).ConfigureAwait(false);
                    ip = entry.AddressList.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
                    if (ip == null) continue;
                }
                catch
                {
                    continue;
                }
            }

            try
            {
                if (udp != null)
                {
                    await udp.SendAsync(payload, payload.Length, new IPEndPoint(ip, UdpPort))
                        .ConfigureAwait(false);
                }
                else
                {
                    using var sock = new UdpClient();
                    await sock.SendAsync(payload, payload.Length, new IPEndPoint(ip, UdpPort))
                        .ConfigureAwait(false);
                }

                any = true;
            }
            catch
            {
                // continue
            }
        }

        return any;
    }

    private void Prune()
    {
        var removed = false;
        lock (_gate)
        {
            var now = DateTime.UtcNow;
            var stale = _peers
                .Where(kv =>
                {
                    if (kv.Value.StickyUntilUtc > now) return false;
                    return (now - kv.Value.LastSeenUtc).TotalSeconds > 8;
                })
                .Select(kv => kv.Key).ToList();
            foreach (var k in stale)
            {
                _peers.Remove(k);
                removed = true;
            }
        }

        if (removed)
            RaisePeersUpdated();
    }

    public IReadOnlyList<DiscoveredPeer> GetPeers()
    {
        lock (_gate)
            return _peers.Values.OrderBy(p => p.MachineName).ToList();
    }

    public string? FindIp(string machineName)
    {
        lock (_gate)
        {
            if (!_peers.TryGetValue(machineName, out var p))
                return null;
            return OrderConnectIps(p.Ip, p.ObservedIp).FirstOrDefault();
        }
    }

    /// <summary>出站候选：先 UDP 来源地址，再宣告地址；端口为对端宣告的 TCP 端口。</summary>
    public bool TryGetConnectTargets(string machineName, int fallbackTcpPort, out IReadOnlyList<(string Ip, int Port)> targets)
    {
        lock (_gate)
        {
            if (!_peers.TryGetValue(machineName, out var p))
            {
                targets = [];
                return false;
            }

            var port = p.TcpPort > 0 ? p.TcpPort : fallbackTcpPort;
            targets = OrderConnectIps(p.Ip, p.ObservedIp)
                .Select(ip => (ip, port))
                .ToList();
            return targets.Count > 0;
        }
    }

    /// <summary>
    /// 从已建立的 TCP/握手学习对端地址（UDP 发现不对称时仍可补齐出站）。
    /// 各平台通用，不依赖特定虚拟化环境。
    /// </summary>
    public void RememberPeer(string machineName, string ip, int tcpPort = 0, string? observedIp = null,
        int stickySeconds = 0, int origin = DiscoveryOrigin.Lan)
    {
        if (string.IsNullOrWhiteSpace(machineName) || string.IsNullOrWhiteSpace(ip))
            return;
        if (!IsUsableLanIpv4(ip) && !IsUsableLanIpv4(observedIp))
            return;
        if (!IsUsableLanIpv4(ip))
            ip = observedIp!;

        UpsertPeer(machineName, ip, tcpPort, observedIp, origin, stickySeconds);
    }

    private void UpsertPeer(
        string machineName,
        string ip,
        int tcpPort,
        string? observedIp,
        int origin,
        int stickySeconds)
    {
        var changed = false;
        lock (_gate)
        {
            var now = DateTime.UtcNow;
            var stickyUntil = stickySeconds > 0 ? now.AddSeconds(stickySeconds) : DateTime.MinValue;
            if (!_peers.TryGetValue(machineName, out var existing))
            {
                _peers[machineName] = new DiscoveredPeer
                {
                    MachineName = machineName,
                    Ip = ip,
                    ObservedIp = IsUsableLanIpv4(observedIp) ? observedIp! : "",
                    TcpPort = tcpPort <= 0 ? AnnounceTcpPort : tcpPort,
                    LastSeenUtc = now,
                    StickyUntilUtc = stickyUntil,
                    Origin = origin is DiscoveryOrigin.Remote or DiscoveryOrigin.Service
                        ? origin
                        : DiscoveryOrigin.Lan
                };
                changed = true;
            }
            else
            {
                var nextPort = tcpPort > 0 ? tcpPort : existing.TcpPort;
                var nextObserved = IsUsableLanIpv4(observedIp) ? observedIp! : existing.ObservedIp;
                var nextOrigin = DiscoveryOrigin.Prefer(existing.Origin, origin);
                changed = !string.Equals(existing.Ip, ip, StringComparison.OrdinalIgnoreCase)
                          || existing.TcpPort != nextPort
                          || !string.Equals(existing.ObservedIp, nextObserved, StringComparison.OrdinalIgnoreCase)
                          || existing.Origin != nextOrigin;

                existing.Ip = ip;
                existing.LastSeenUtc = now;
                if (tcpPort > 0)
                    existing.TcpPort = tcpPort;
                if (IsUsableLanIpv4(observedIp))
                    existing.ObservedIp = observedIp!;
                if (stickyUntil > existing.StickyUntilUtc)
                    existing.StickyUntilUtc = stickyUntil;
                existing.Origin = nextOrigin;
            }
        }

        if (changed)
            RaisePeersUpdated();
    }

    private void RaisePeersUpdated()
    {
        try { PeersUpdated?.Invoke(GetPeers()); }
        catch { /* ignore */ }
    }

    /// <summary>请名为 <paramref name="targetMachine"/> 的对端主动连回本机。未在布局中的实例会忽略。</summary>
    public async Task RequestDialAsync(string localIp, int tcpPort, CancellationToken ct = default)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new DialRequestPayload
        {
            Kind = DialRequestKind,
            SourceMachine = LocalMachine,
            Ip = localIp,
            TcpPort = tcpPort
        });
        await TryBroadcastRawAsync(payload, ct).ConfigureAwait(false);
    }

    /// <summary>回连顺序：实际收到包的来源地址优先，其次宣告地址。不可路由地址丢弃。</summary>
    public static List<string> OrderConnectIps(string? advertised, string? observed)
    {
        var list = new List<string>(2);
        Add(observed);
        Add(advertised);
        return list;

        void Add(string? ip)
        {
            if (!IsUsableLanIpv4(ip)) return;
            if (list.Exists(x => string.Equals(x, ip, StringComparison.OrdinalIgnoreCase)))
                return;
            list.Add(ip!);
        }
    }

    public static bool IsUsableLanIpv4(string? ip)
    {
        if (string.IsNullOrWhiteSpace(ip)) return false;
        if (!IPAddress.TryParse(ip, out var addr)) return false;
        if (addr.AddressFamily != AddressFamily.InterNetwork) return false;
        if (IPAddress.IsLoopback(addr)) return false;
        var b = addr.GetAddressBytes();
        if (b[0] == 0 || b[0] >= 224) return false;
        if (b[0] == 169 && b[1] == 254) return false;
        return true;
    }

    private static string? ObservedIpv4(IPEndPoint remote)
    {
        var text = SenderIpv4(remote);
        return IsUsableLanIpv4(text) ? text : null;
    }

    /// <summary>UDP 来源 IPv4（含公网），用于匹配探测目标来源。</summary>
    private static string? SenderIpv4(IPEndPoint remote)
    {
        var a = remote.Address;
        if (a.AddressFamily == AddressFamily.InterNetworkV6 && a.IsIPv4MappedToIPv6)
            a = a.MapToIPv4();
        if (a.AddressFamily != AddressFamily.InterNetwork)
            return null;
        if (IPAddress.IsLoopback(a))
            return a.ToString();
        return a.ToString();
    }

    public void Stop()
    {
        var loop = _loop;
        var cts = _cts;
        var udp = _udp;

        _loop = null;
        _cts = null;
        _udp = null;
        _subnetBroadcast = null;

        try { cts?.Cancel(); } catch { /* ignore */ }

        try { udp?.Close(); } catch { /* ignore */ }
        try { udp?.Dispose(); } catch { /* ignore */ }

        try { cts?.Dispose(); } catch { /* ignore */ }

        try { loop?.Wait(TimeSpan.FromMilliseconds(500)); } catch { /* ignore */ }
    }

    public ValueTask DisposeAsync()
    {
        Stop();
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// 子网限播优先；成功则不再发 255.255.255.255（虚拟/VPN/WSL 口上全播常 HostUnreachable，调试器会刷屏）。
    /// /32 点对点（广播地址=本机）无广播面，直接跳过（依赖探测单播）。
    /// </summary>
    private static async Task<bool> TryAnnounceBroadcastAsync(
        UdpClient sock, byte[] payload, int udpPort, IPAddress? subnetBroadcast, string? localIpv4)
    {
        if (subnetBroadcast != null && IsHostOnlyBroadcast(subnetBroadcast, localIpv4))
            return false;

        var sent = false;
        if (subnetBroadcast != null)
        {
            sent = await TrySendUdpAsync(sock, payload, new IPEndPoint(subnetBroadcast, udpPort))
                .ConfigureAwait(false);
        }

        if (!sent)
        {
            sent = await TrySendUdpAsync(sock, payload, new IPEndPoint(IPAddress.Broadcast, udpPort))
                .ConfigureAwait(false);
        }

        return sent;
    }

    private static async Task<bool> TrySendUdpAsync(UdpClient sock, byte[] payload, IPEndPoint ep)
    {
        try
        {
            await sock.SendAsync(payload, payload.Length, ep).ConfigureAwait(false);
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private static bool IsHostOnlyBroadcast(IPAddress broadcast, string? localIpv4)
        => !string.IsNullOrWhiteSpace(localIpv4)
           && IPAddress.TryParse(localIpv4, out var local)
           && broadcast.Equals(local);

    private static IPAddress? TryComputeBroadcast(string ip, string? mask)
    {
        try
        {
            var ipBytes = IPAddress.Parse(ip).GetAddressBytes();
            var maskBytes = IPAddress.Parse(string.IsNullOrWhiteSpace(mask) ? "255.255.255.0" : mask)
                .GetAddressBytes();
            if (ipBytes.Length != 4 || maskBytes.Length != 4) return null;
            var b = new byte[4];
            for (var i = 0; i < 4; i++)
                b[i] = (byte)(ipBytes[i] | (byte)~maskBytes[i]);
            return new IPAddress(b);
        }
        catch
        {
            return null;
        }
    }

    private sealed class DiscoveryPacket
    {
        public string MachineName { get; set; } = "";
        public string Ip { get; set; } = "";
        public int TcpPort { get; set; }
        /// <summary>可选：local/lan | remote | service/relay。旧端忽略。</summary>
        public string? Origin { get; set; }
    }
}
