using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace FlyScreen.Services.Network;

public sealed class DiscoveredPeer
{
    public string MachineName { get; set; } = "";
    public string Ip { get; set; } = "";
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

    public void Start(string localIp, int udpPort, string? subnetMask = null)
    {
        Stop();
        BoundIp = localIp;
        UdpPort = udpPort;
        _subnetBroadcast = TryComputeBroadcast(localIp, subnetMask);
        _cts = new CancellationTokenSource();

        var local = IPAddress.Parse(localIp);
        _udp = new UdpClient(new IPEndPoint(local, udpPort));
        _udp.EnableBroadcast = true;
        _udp.MulticastLoopback = false;
        try
        {
            // 部分环境（含 WSL 桥接）需要显式允许广播
            _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Broadcast, true);
        }
        catch
        {
            // ignore
        }

        var ct = _cts.Token;
        var udp = _udp;
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
            var pkt = JsonSerializer.Deserialize<DiscoveryPacket>(buffer);
            if (pkt == null || string.IsNullOrWhiteSpace(pkt.MachineName))
                return;

            var ip = string.IsNullOrWhiteSpace(pkt.Ip) ? remote.Address.ToString() : pkt.Ip;

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
            return _peers.TryGetValue(machineName, out var p) ? p.Ip : null;
    }

    /// <summary>
    /// 从已建立的 TCP/握手学习对端地址（UDP 发现不对称时仍可补齐出站）。
    /// 各平台通用，不依赖特定虚拟化环境。
    /// </summary>
    public void RememberPeer(string machineName, string ip, int tcpPort = 0)
    {
        if (string.IsNullOrWhiteSpace(machineName) || string.IsNullOrWhiteSpace(ip))
            return;
        if (IPAddress.TryParse(ip, out var addr) && IPAddress.IsLoopback(addr))
            return;

        lock (_gate)
        {
            _peers[machineName] = new DiscoveredPeer
            {
                MachineName = machineName,
                Ip = ip,
                TcpPort = tcpPort <= 0 ? AnnounceTcpPort : tcpPort,
                LastSeenUtc = DateTime.UtcNow
            };
        }
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
