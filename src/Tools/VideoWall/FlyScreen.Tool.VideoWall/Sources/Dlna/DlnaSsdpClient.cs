using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace FlyScreen.Tool.VideoWall.Sources.Dlna;

/// <summary>
/// 用户显式触发的 SSDP M-SEARCH（MediaServer）。禁止后台常驻。
/// </summary>
public sealed class DlnaSsdpClient
{
    private static readonly IPAddress SsdpGroup = IPAddress.Parse("239.255.255.250");
    private const int SsdpPort = 1900;
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(4);

    private static readonly string SearchMessage =
        "M-SEARCH * HTTP/1.1\r\n" +
        "HOST: 239.255.255.250:1900\r\n" +
        "MAN: \"ssdp:discover\"\r\n" +
        "MX: 2\r\n" +
        "ST: urn:schemas-upnp-org:device:MediaServer:1\r\n" +
        "\r\n";

    /// <summary>发现局域网 MediaServer；按 LOCATION 去重。</summary>
    public async Task<IReadOnlyList<DlnaSsdpHit>> DiscoverAsync(
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        var budget = timeout ?? DefaultTimeout;
        var hits = new Dictionary<string, DlnaSsdpHit>(StringComparer.OrdinalIgnoreCase);
        var interfaces = ListIpv4Interfaces();
        if (interfaces.Count == 0)
            interfaces.Add(IPAddress.Any);

        var tasks = interfaces.Select(addr => ListenOnInterfaceAsync(addr, hits, budget, ct)).ToArray();
        await Task.WhenAll(tasks).ConfigureAwait(false);

        return hits.Values
            .OrderBy(h => h.FriendlyNameHint ?? h.Location.Host, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static async Task ListenOnInterfaceAsync(
        IPAddress localAddress,
        Dictionary<string, DlnaSsdpHit> hits,
        TimeSpan budget,
        CancellationToken ct)
    {
        using var udp = new UdpClient(AddressFamily.InterNetwork);
        try
        {
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.Client.Bind(new IPEndPoint(localAddress, 0));
        }
        catch
        {
            return;
        }

        var payload = Encoding.UTF8.GetBytes(SearchMessage);
        var remote = new IPEndPoint(SsdpGroup, SsdpPort);
        try
        {
            await udp.SendAsync(payload, payload.Length, remote).ConfigureAwait(false);
            await Task.Delay(80, CancellationToken.None).ConfigureAwait(false);
            if (!ct.IsCancellationRequested)
                await udp.SendAsync(payload, payload.Length, remote).ConfigureAwait(false);
        }
        catch
        {
            return;
        }

        // 用 Available + 墙钟超时 + 无 token Delay，避免 CancelAfter/Delay(ct) 刷出首遇 OCE
        var sw = Stopwatch.StartNew();
        while (!ct.IsCancellationRequested && sw.Elapsed < budget)
        {
            try
            {
                if (udp.Client.Available > 0)
                {
                    var result = await udp.ReceiveAsync(CancellationToken.None).ConfigureAwait(false);
                    if (!TryParseResponse(Encoding.UTF8.GetString(result.Buffer), out var hit))
                        continue;
                    lock (hits)
                        hits[hit.Location.AbsoluteUri] = hit;
                }
                else
                {
                    await Task.Delay(80, CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch (SocketException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
        }
    }

    public static bool TryParseResponse(string text, out DlnaSsdpHit hit)
    {
        hit = null!;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var st = HeaderValue(text, "ST") ?? HeaderValue(text, "NT") ?? "";
        if (!st.Contains("MediaServer", StringComparison.OrdinalIgnoreCase)
            && !text.Contains("MediaServer", StringComparison.OrdinalIgnoreCase))
            return false;

        var location = HeaderValue(text, "LOCATION");
        if (string.IsNullOrWhiteSpace(location) || !Uri.TryCreate(location.Trim(), UriKind.Absolute, out var uri))
            return false;
        if (uri.Scheme is not ("http" or "https"))
            return false;

        var usn = HeaderValue(text, "USN") ?? uri.AbsoluteUri;
        var server = HeaderValue(text, "SERVER");
        hit = new DlnaSsdpHit(uri, usn, server);
        return true;
    }

    private static string? HeaderValue(string text, string name)
    {
        foreach (var line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var idx = line.IndexOf(':');
            if (idx <= 0) continue;
            var key = line[..idx].Trim();
            if (!key.Equals(name, StringComparison.OrdinalIgnoreCase))
                continue;
            return line[(idx + 1)..].Trim();
        }

        return null;
    }

    private static List<IPAddress> ListIpv4Interfaces()
    {
        var list = new List<IPAddress>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                    continue;
                foreach (var uni in nic.GetIPProperties().UnicastAddresses)
                {
                    if (uni.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    if (IPAddress.IsLoopback(uni.Address)) continue;
                    var bytes = uni.Address.GetAddressBytes();
                    if (bytes[0] == 169 && bytes[1] == 254) continue;
                    list.Add(uni.Address);
                }
            }
        }
        catch
        {
            // ignore
        }

        return list;
    }
}

/// <summary>SSDP 原始命中（尚未拉设备描述）。</summary>
public sealed record DlnaSsdpHit(Uri Location, string Usn, string? ServerHeader)
{
    public string? FriendlyNameHint
        => string.IsNullOrWhiteSpace(ServerHeader) ? null : ServerHeader;
}
