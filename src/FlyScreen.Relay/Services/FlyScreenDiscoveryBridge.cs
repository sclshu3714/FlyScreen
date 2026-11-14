using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using FlyScreen.Relay.Models;
using FlyScreen.Relay.Options;
using Microsoft.Extensions.Options;

namespace FlyScreen.Relay.Services;

/// <summary>
/// 兼容玄鸟客户端 <c>DiscoveryService</c> 的 UDP 发现：
/// 收到局域网广播或对「连接配置」里服务器 IP 的单播探测后，
/// 把本服务上所有已注册在线设备各发一条 <c>DiscoveryPacket</c> 回去，
/// 从而在不改客户端的前提下让「设备管理 → 刷新发现」列出中继上的设备。
/// </summary>
public sealed class FlyScreenDiscoveryBridge : BackgroundService
{
    /// <summary>与 Core DiscoveryService 私有包格式一致（System.Text.Json 默认 PascalCase）。</summary>
    private sealed class DiscoveryPacket
    {
        public string MachineName { get; set; } = "";
        public string Ip { get; set; } = "";
        public int TcpPort { get; set; }
        /// <summary>客户端据此标为「服务」；旧端忽略未知字段。</summary>
        public string Origin { get; set; } = "service";
    }

    private readonly DeviceRegistry _devices;
    private readonly IOptionsMonitor<RelayOptions> _options;
    private readonly ILogger<FlyScreenDiscoveryBridge> _log;

    public FlyScreenDiscoveryBridge(
        DeviceRegistry devices,
        IOptionsMonitor<RelayOptions> options,
        ILogger<FlyScreenDiscoveryBridge> log)
    {
        _devices = devices;
        _options = options;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var port = _options.CurrentValue.DiscoveryUdpPort;
        if (port <= 0)
        {
            _log.LogInformation("FlyScreen UDP 发现桥接已关闭（DiscoveryUdpPort=0）");
            return;
        }

        using var udp = new UdpClient(AddressFamily.InterNetworkV6);
        udp.Client.DualMode = true;
        try
        {
            udp.ExclusiveAddressUse = false;
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        }
        catch { /* ignore */ }

        udp.Client.Bind(new IPEndPoint(IPAddress.IPv6Any, port));
        udp.EnableBroadcast = true;
        _log.LogInformation("FlyScreen UDP 发现桥接监听 {Port}（兼容 DiscoveryPacket，应答全部在线注册设备）", port);

        var receiveTask = ReceiveLoopAsync(udp, stoppingToken);
        var announceTask = AnnounceLoopAsync(udp, stoppingToken);
        await Task.WhenAll(receiveTask, announceTask).ConfigureAwait(false);
    }

    private async Task ReceiveLoopAsync(UdpClient udp, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var result = await udp.ReceiveAsync(ct).ConfigureAwait(false);
                if (!LooksLikeDiscovery(result.Buffer))
                    continue;

                // 单播回探测方：客户端 BurstProbeAsync / 局域网广播都会触发
                await ReplyAllDevicesAsync(udp, result.RemoteEndPoint, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "UDP 发现收包异常");
            }
        }
    }

    private async Task AnnounceLoopAsync(UdpClient udp, CancellationToken ct)
    {
        // 客户端 Prune 约 8s，周期略短保证列表不掉
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
                if (!_options.CurrentValue.DiscoveryBroadcastAnnounce)
                    continue;

                var targets = BuildBroadcastTargets(udp);
                foreach (var ep in targets)
                    await ReplyAllDevicesAsync(udp, ep, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "UDP 周期宣告异常");
            }
        }
    }

    private Task ReplyAllDevicesAsync(UdpClient udp, IPEndPoint to, CancellationToken ct)
        => SendAllAsync(udp, _devices, to, ct, _log);

    /// <summary>供 TCP ProbeOnly 成功后向客户端探测地址补发 UDP 多设备宣告。</summary>
    public static async Task UnicastAnnounceAllAsync(
        DeviceRegistry devices, IPAddress target, int discoveryUdpPort, CancellationToken ct)
    {
        if (discoveryUdpPort <= 0) return;
        using var udp = new UdpClient(AddressFamily.InterNetworkV6);
        udp.Client.DualMode = true;
        await SendAllAsync(udp, devices, new IPEndPoint(target, discoveryUdpPort), ct, null)
            .ConfigureAwait(false);
    }

    private static async Task SendAllAsync(
        UdpClient udp,
        DeviceRegistry devices,
        IPEndPoint to,
        CancellationToken ct,
        ILogger? log)
    {
        var list = devices.ListAll();
        if (list.Count == 0) return;

        foreach (var d in list)
        {
            if (ct.IsCancellationRequested) return;
            if (!d.Online) continue;

            var (ip, tcpPort) = PickAnnounceEndpoint(d, fallbackHost: PreferIpv4(to.Address));
            if (string.IsNullOrWhiteSpace(ip))
                continue;

            var pkt = new DiscoveryPacket
            {
                MachineName = d.Name,
                Ip = ip,
                TcpPort = tcpPort > 0 ? tcpPort : 15155,
                Origin = "service"
            };
            var bytes = JsonSerializer.SerializeToUtf8Bytes(pkt);
            try
            {
                await udp.SendAsync(bytes, to, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                log?.LogDebug(ex, "向 {To} 宣告 {Name} 失败", to, d.Name);
            }
        }

        log?.LogDebug("已向 {To} 宣告 {Count} 台在线设备", to, list.Count);
    }

    /// <summary>
    /// 宣告地址优先级：设备上报的 host/ipv6 候选 → 注册时看到的远端地址 → 探测方可见的中继侧地址。
    /// </summary>
    internal static (string Ip, int TcpPort) PickAnnounceEndpoint(DeviceDto d, string? fallbackHost)
    {
        var tcp = d.TcpPort is > 0 and <= 65535 ? d.TcpPort.Value : 15155;

        foreach (var c in d.Candidates ?? [])
        {
            if (!TrySplitHostPort(c.Address, out var host, out var port)) continue;
            if (string.IsNullOrWhiteSpace(host)) continue;
            if (IsLoopbackOrLinkLocal(host)) continue;
            return (host, port > 0 ? port : tcp);
        }

        if (!string.IsNullOrWhiteSpace(d.RemoteAddress)
            && TrySplitHostPort(d.RemoteAddress!, out var remoteHost, out _)
            && !string.IsNullOrWhiteSpace(remoteHost)
            && !IsLoopbackOrLinkLocal(remoteHost))
        {
            return (remoteHost, tcp);
        }

        if (!string.IsNullOrWhiteSpace(fallbackHost) && !IsLoopbackOrLinkLocal(fallbackHost))
            return (fallbackHost, tcp);

        return ("", tcp);
    }

    private static List<IPEndPoint> BuildBroadcastTargets(UdpClient udp)
    {
        var list = new List<IPEndPoint>();
        try
        {
            // IPv4 广播；对端 DiscoveryService 绑 IPv4
            list.Add(new IPEndPoint(IPAddress.Broadcast, ((IPEndPoint)udp.Client.LocalEndPoint!).Port));
        }
        catch { /* ignore */ }
        return list;
    }

    private static bool LooksLikeDiscovery(byte[] buffer)
    {
        if (buffer.Length < 8 || buffer.Length > 4096) return false;
        try
        {
            using var doc = JsonDocument.Parse(buffer);
            var root = doc.RootElement;
            // 标准宣告 / 安全密钥同步 / DialRequest 都当探测触发多设备应答
            if (root.TryGetProperty("MachineName", out _)) return true;
            if (root.TryGetProperty("Kind", out var kind))
            {
                var k = kind.GetString();
                return string.Equals(k, "SecurityKeySync", StringComparison.Ordinal)
                       || string.Equals(k, "DialRequest", StringComparison.Ordinal);
            }
            // camelCase 容错
            if (root.TryGetProperty("machineName", out _)) return true;
            return false;
        }
        catch
        {
            return false;
        }
    }

    private static bool TrySplitHostPort(string raw, out string host, out int port)
    {
        host = "";
        port = 0;
        raw = (raw ?? "").Trim();
        if (string.IsNullOrWhiteSpace(raw)) return false;

        if (raw.StartsWith('['))
        {
            var end = raw.IndexOf(']');
            if (end <= 1) return false;
            host = raw[1..end];
            var rest = raw[(end + 1)..];
            if (rest.StartsWith(':') && int.TryParse(rest[1..], out var p6) && p6 is > 0 and <= 65535)
                port = p6;
            return true;
        }

        var idx = raw.LastIndexOf(':');
        if (idx > 0 && idx < raw.Length - 1
            && int.TryParse(raw[(idx + 1)..], out var p) && p is > 0 and <= 65535
            && raw.IndexOf(':') == idx)
        {
            host = raw[..idx];
            port = p;
            return true;
        }

        host = raw;
        return true;
    }

    private static string? PreferIpv4(IPAddress addr)
    {
        if (addr.AddressFamily == AddressFamily.InterNetwork)
            return addr.ToString();
        if (addr.IsIPv4MappedToIPv6)
            return addr.MapToIPv4().ToString();
        return null;
    }

    private static bool IsLoopbackOrLinkLocal(string host)
    {
        if (!IPAddress.TryParse(host, out var ip)) return false;
        if (IPAddress.IsLoopback(ip)) return true;
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return b[0] == 169 && b[1] == 254;
        }
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
            return ip.IsIPv6LinkLocal;
        return false;
    }
}
