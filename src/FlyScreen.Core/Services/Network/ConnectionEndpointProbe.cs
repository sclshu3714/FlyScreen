using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using FlyScreen.Models;

namespace FlyScreen.Services.Network;

/// <summary>
/// 连接配置「测试 / 刷新验证」：不改现有局域网握手与发现协议。
/// 远程一对一：TCP 可达且解析到对端名 → 可进设备管理。
/// 中转一对多：需扫到真实注册设备才进设备管理（中继协议未接入前不造假名）。
/// </summary>
public static class ConnectionEndpointProbe
{
    /// <summary>Tailscale/跨网延迟常到数百 ms，SYN 重试需要更长窗口。</summary>
    private static readonly TimeSpan RemoteConnectTimeout = TimeSpan.FromSeconds(15);

    public sealed record Result(
        bool Ok,
        int? RttMs,
        string Message,
        string? VerifiedPeerName = null,
        IReadOnlyList<RelayDiscoveredDevice>? DiscoveredDevices = null);

    public static async Task<Result> TestAsync(
        ConnectionEndpoint endpoint,
        CancellationToken ct = default)
    {
        var raw = (endpoint.Address ?? "").Trim();
        if (string.IsNullOrWhiteSpace(raw))
            return new Result(false, null, "地址为空");

        if (raw.StartsWith("wss://", StringComparison.OrdinalIgnoreCase)
            || raw.StartsWith("ws://", StringComparison.OrdinalIgnoreCase))
            endpoint.Kind = ConnectionEndpoint.KindRelay;
        else if (endpoint.Kind == ConnectionEndpoint.KindRelay
                 && !raw.Contains("://", StringComparison.Ordinal))
            endpoint.Kind = ConnectionEndpoint.KindRemote;

        if (endpoint.Kind == ConnectionEndpoint.KindRelay)
            return await TestRelayAsync(endpoint, raw, ct).ConfigureAwait(false);

        return await TestRemoteAsync(raw, ct).ConfigureAwait(false);
    }

    private static async Task<Result> TestRemoteAsync(string raw, CancellationToken ct)
    {
        if (!TryParseHostPort(raw, out var host, out var port))
            return new Result(false, null, "远程请填写 host:port 或 IP（默认端口 15155）");

        var target = $"{host}:{port}";
        var tcp = await ProbeTcpAsync(host, port, RemoteConnectTimeout, ct).ConfigureAwait(false);
        if (!tcp.Ok)
            return new Result(false, null, $"{tcp.Message}（{target}）");

        var name = await ResolvePeerNameAsync(host, ct).ConfigureAwait(false);
        return new Result(true, tcp.RttMs, $"TCP 可达 {target}", VerifiedPeerName: name);
    }

    private static async Task<Result> TestRelayAsync(
        ConnectionEndpoint endpoint, string raw, CancellationToken ct)
    {
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri)
            || (uri.Scheme != "wss" && uri.Scheme != "ws" && uri.Scheme != "http" && uri.Scheme != "https")
            || string.IsNullOrWhiteSpace(uri.Host))
        {
            if (!TryParseHostPort(raw, out var host, out var port))
                return new Result(false, null, "中转请填写 wss://… 或 host:port");

            var tcp = await ProbeTcpAsync(host, port, RemoteConnectTimeout, ct).ConfigureAwait(false);
            if (!tcp.Ok)
                return new Result(false, null, $"{tcp.Message}（{host}:{port}）");

            return new Result(
                true,
                tcp.RttMs,
                "中转入口可达；设备注册发现尚未接入，暂不加入设备管理",
                DiscoveredDevices: Array.Empty<RelayDiscoveredDevice>());
        }

        _ = endpoint;
        await Task.Yield();
        return new Result(
            true,
            null,
            "中转地址有效；注册设备发现尚未接入，暂不加入设备管理",
            DiscoveredDevices: Array.Empty<RelayDiscoveredDevice>());
    }

    private static async Task<Result> ProbeTcpAsync(
        string host, int port, TimeSpan timeout, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var client = new TcpClient();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linked.CancelAfter(timeout);
            try
            {
                await client.ConnectAsync(host, port, linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return new Result(false, null, $"连接超时（>{timeout.TotalSeconds:0}s，高延迟链路请用 Tailscale 虚拟 IP 并放行 TCP {port}）");
            }

            sw.Stop();
            return new Result(true, (int)Math.Max(1, sw.ElapsedMilliseconds), "可达");
        }
        catch (SocketException ex)
        {
            return new Result(false, null, DescribeSocketError(ex, port));
        }
        catch (Exception ex)
        {
            return new Result(false, null, ex.Message);
        }
    }

    private static string DescribeSocketError(SocketException ex, int port)
        => ex.SocketErrorCode switch
        {
            SocketError.ConnectionRefused =>
                $"连接被拒绝（对端未在 TCP {port} 监听，或玄鸟绑在了其它网卡 IP）",
            SocketError.TimedOut =>
                $"连接超时（请确认防火墙放行 TCP {port}，Tailscale 建议填 100.x 虚拟 IP）",
            SocketError.HostUnreachable or SocketError.NetworkUnreachable =>
                "主机/网络不可达",
            SocketError.NetworkDown => "网络不可用",
            _ => string.IsNullOrWhiteSpace(ex.Message) ? ex.SocketErrorCode.ToString() : ex.Message
        };

    private static async Task<string> ResolvePeerNameAsync(string host, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(host))
            return host;
        if (!IPAddress.TryParse(host, out _))
            return host;

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(2));
            var entry = await Dns.GetHostEntryAsync(host, cts.Token).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(entry.HostName)
                && !string.Equals(entry.HostName, host, StringComparison.OrdinalIgnoreCase))
            {
                var shortName = entry.HostName.Split('.')[0];
                return string.IsNullOrWhiteSpace(shortName) ? entry.HostName : shortName;
            }
        }
        catch
        {
            // 无反查时用 IP
        }

        return host;
    }

    public static bool TryParseHostPort(string raw, out string host, out int port)
    {
        host = "";
        port = 15155;
        raw = raw.Trim();
        if (raw.StartsWith("["))
        {
            var end = raw.IndexOf(']');
            if (end <= 1) return false;
            host = raw[1..end];
            var rest = raw[(end + 1)..];
            if (rest.StartsWith(':') && int.TryParse(rest[1..], out var p6))
            {
                port = p6;
                return !string.IsNullOrWhiteSpace(host);
            }
            return !string.IsNullOrWhiteSpace(host);
        }

        var idx = raw.LastIndexOf(':');
        if (idx > 0 && int.TryParse(raw[(idx + 1)..], out var p))
        {
            host = raw[..idx];
            port = p;
            return !string.IsNullOrWhiteSpace(host);
        }

        host = raw;
        return !string.IsNullOrWhiteSpace(host);
    }
}
