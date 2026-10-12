using System.Net;
using System.Net.Sockets;
using System.Text;
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
        _loop = Task.Run(async () =>
        {
            var announce = Task.Run(() => AnnounceLoopAsync(ct), ct);
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var result = await _udp.ReceiveAsync(ct).ConfigureAwait(false);
                    HandlePacket(result.Buffer, result.RemoteEndPoint);
                }
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
            catch
            {
                // ignore transient UDP errors
            }
            finally
            {
                try { await announce.ConfigureAwait(false); } catch { /* ignore */ }
            }
        }, ct);
    }

    private async Task AnnounceLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var payload = JsonSerializer.SerializeToUtf8Bytes(new DiscoveryPacket
                {
                    MachineName = LocalMachine,
                    TcpPort = /* filled by ConnectionManager via setter */ AnnounceTcpPort,
                    Ip = BoundIp
                });
                // 子网广播
                await _udp!.SendAsync(payload, payload.Length, new IPEndPoint(IPAddress.Broadcast, UdpPort))
                    .ConfigureAwait(false);
            }
            catch
            {
                // ignore
            }

            Prune();
            await Task.Delay(2000, ct).ConfigureAwait(false);
        }
    }

    public int AnnounceTcpPort { get; set; } = 15155;

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
        try { _cts?.Cancel(); } catch { /* ignore */ }
        try { _udp?.Close(); } catch { /* ignore */ }
        try { _udp?.Dispose(); } catch { /* ignore */ }
        _udp = null;
        _cts = null;
        _loop = null;
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
