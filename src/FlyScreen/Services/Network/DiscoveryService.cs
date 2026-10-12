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
/// UDP 广播自动发现局域网内的飞鼠实例；绑定到选定网卡。
/// </summary>
public sealed class DiscoveryService : IAsyncDisposable
{
    private readonly object _gate = new();
    private UdpClient? _udp;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private readonly Dictionary<string, DiscoveredPeer> _peers = new(StringComparer.OrdinalIgnoreCase);

    public string LocalMachine { get; } = Environment.MachineName;
    public string BoundIp { get; private set; } = "";
    public int UdpPort { get; private set; }
    public int AnnounceTcpPort { get; set; } = 15155;

    public event Action<IReadOnlyList<DiscoveredPeer>>? PeersUpdated;

    public void Start(string localIp, int udpPort)
    {
        Stop();
        BoundIp = localIp;
        UdpPort = udpPort;
        _cts = new CancellationTokenSource();

        var local = IPAddress.Parse(localIp);
        _udp = new UdpClient(new IPEndPoint(local, udpPort));
        _udp.EnableBroadcast = true;
        _udp.MulticastLoopback = false;

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
                        // 不向 ReceiveAsync 传入 ct：停止时通过 Close() 解除阻塞，
                        // 避免调试器对 OperationCanceledException 刷屏。
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
            if (string.Equals(pkt.MachineName, LocalMachine, StringComparison.OrdinalIgnoreCase))
                return;

            var ip = string.IsNullOrWhiteSpace(pkt.Ip) ? remote.Address.ToString() : pkt.Ip;
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

    public void Stop()
    {
        var loop = _loop;
        var cts = _cts;
        var udp = _udp;

        _loop = null;
        _cts = null;
        _udp = null;

        try { cts?.Cancel(); } catch { /* ignore */ }

        // 关闭套接字以唤醒阻塞中的 ReceiveAsync
        try { udp?.Close(); } catch { /* ignore */ }
        try { udp?.Dispose(); } catch { /* ignore */ }

        try { cts?.Dispose(); } catch { /* ignore */ }

        // 短暂等待循环退出，避免重启时端口占用
        try { loop?.Wait(TimeSpan.FromMilliseconds(500)); } catch { /* ignore */ }
    }

    public ValueTask DisposeAsync()
    {
        Stop();
        return ValueTask.CompletedTask;
    }

    private sealed class DiscoveryPacket
    {
        public string MachineName { get; set; } = "";
        public string Ip { get; set; } = "";
        public int TcpPort { get; set; }
    }
}
