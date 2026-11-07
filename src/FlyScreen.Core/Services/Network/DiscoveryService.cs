using System.Net;
using System.Net.Sockets;
using System.Text.Json;

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
    /// <param name="bindAny">true 时绑定 0.0.0.0（Android 等上绑定单网卡 IP 常收不到广播）。</param>
    public void Start(string announceIp, int udpPort, string? subnetMask = null, bool bindAny = false)
    {
        Stop();
        BoundIp = string.IsNullOrWhiteSpace(announceIp) ? "0.0.0.0" : announceIp;
        UdpPort = udpPort;
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
            Start(announceIp, udpPort, subnetMask, bindAny: true);
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
                var payload = JsonSerializer.SerializeToUtf8Bytes(new DiscoveryPacket
                {
                    MachineName = LocalMachine,
                    TcpPort = AnnounceTcpPort,
                    Ip = BoundIp
                });

                // 限播 + 全播：WSL 桥接上 255.255.255.255 常不可达，子网广播更稳
                if (_subnetBroadcast != null)
                {
                    await udp.SendAsync(payload, payload.Length, new IPEndPoint(_subnetBroadcast, UdpPort))
                        .ConfigureAwait(false);
                }

                await udp.SendAsync(payload, payload.Length, new IPEndPoint(IPAddress.Broadcast, UdpPort))
                    .ConfigureAwait(false);
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
            var ip = IsUsableLanIpv4(pkt.Ip) ? pkt.Ip : (observedIp ?? pkt.Ip);

            // 同名且同 IP → 本机回环，忽略
            if (string.Equals(pkt.MachineName, LocalMachine, StringComparison.OrdinalIgnoreCase)
                && (string.IsNullOrWhiteSpace(BoundIp) || string.Equals(ip, BoundIp, StringComparison.OrdinalIgnoreCase)
                    || IPAddress.IsLoopback(remote.Address)))
                return;

            // 同名但不同 IP：视为冲突对端（例如旧 WSL 与宿主机撞名），仍登记，便于排查
            // 正常情况下 WSL 已使用带后缀的 MachineName，不会走到此分支

            lock (_gate)
            {
                _peers[pkt.MachineName] = new DiscoveredPeer
                {
                    MachineName = pkt.MachineName,
                    Ip = ip,
                    ObservedIp = observedIp ?? "",
                    TcpPort = pkt.TcpPort <= 0 ? 15155 : pkt.TcpPort,
                    LastSeenUtc = DateTime.UtcNow
                };
            }

            PeersUpdated?.Invoke(GetPeers());
        }
        catch
        {
            // ignore bad packets
        }
    }

    public const string SecurityKeySyncPayloadKind = "SecurityKeySync";

    /// <summary>经发现口 UDP 广播（子网广播 + 全网广播）；发现未启动时返回 false。</summary>
    public async Task<bool> TryBroadcastRawAsync(byte[] payload, CancellationToken ct = default)
    {
        var udp = _udp;
        if (udp == null || payload.Length == 0 || UdpPort <= 0)
            return false;
        try
        {
            if (_subnetBroadcast != null)
            {
                await udp.SendAsync(payload, payload.Length, new IPEndPoint(_subnetBroadcast, UdpPort))
                    .ConfigureAwait(false);
            }

            await udp.SendAsync(payload, payload.Length, new IPEndPoint(IPAddress.Broadcast, UdpPort))
                .ConfigureAwait(false);
            return true;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
        catch (SocketException)
        {
            return false;
        }
        catch
        {
            return false;
        }
    }

    private void Prune()
    {
        lock (_gate)
        {
            var stale = _peers.Where(kv => (DateTime.UtcNow - kv.Value.LastSeenUtc).TotalSeconds > 8)
                .Select(kv => kv.Key).ToList();
            foreach (var k in stale)
                _peers.Remove(k);
        }
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
    public void RememberPeer(string machineName, string ip, int tcpPort = 0, string? observedIp = null)
    {
        if (string.IsNullOrWhiteSpace(machineName) || string.IsNullOrWhiteSpace(ip))
            return;
        if (!IsUsableLanIpv4(ip) && !IsUsableLanIpv4(observedIp))
            return;
        if (!IsUsableLanIpv4(ip))
            ip = observedIp!;

        lock (_gate)
        {
            if (!_peers.TryGetValue(machineName, out var existing))
            {
                _peers[machineName] = new DiscoveredPeer
                {
                    MachineName = machineName,
                    Ip = ip,
                    ObservedIp = IsUsableLanIpv4(observedIp) ? observedIp! : "",
                    TcpPort = tcpPort <= 0 ? AnnounceTcpPort : tcpPort,
                    LastSeenUtc = DateTime.UtcNow
                };
                return;
            }

            existing.Ip = ip;
            existing.LastSeenUtc = DateTime.UtcNow;
            if (tcpPort > 0)
                existing.TcpPort = tcpPort;
            if (IsUsableLanIpv4(observedIp))
                existing.ObservedIp = observedIp!;
        }
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
        if (remote.Address.AddressFamily != AddressFamily.InterNetwork)
            return null;
        var text = remote.Address.ToString();
        return IsUsableLanIpv4(text) ? text : null;
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
    }
}
