using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using FlyScreen.Tool.VideoWall.Contracts;
using FlyScreen.Tool.VideoWall.Wall;

namespace FlyScreen.Tool.VideoWall.Sources;

/// <summary>扫描进度。</summary>
public sealed record LanScanProgress(int Done, int Total, string? CurrentHost, int Found);

/// <summary>局域网扫描到的视频设备候选。</summary>
public sealed record ScannedLanDevice(
    string Host,
    int RtspPort,
    string KindHint,
    string DisplayName,
    IReadOnlyList<int> OpenPorts);

/// <summary>
/// 用户显式触发的局域网视频设备扫描（TCP 探测常见端口）。
/// 不做后台常驻、不爆破口令；仅在用户点击「扫描」后运行。
/// </summary>
public sealed class LanDeviceScanService
{
    /// <summary>RTSP / 常见 NVR Web / 厂商私有端口。</summary>
    public static readonly int[] ProbePorts = [554, 8554, 80, 8080, 8000, 37777, 34567];

    private const int ConnectTimeoutMs = 220;
    private const int MaxHosts = 1024;
    /// <summary>过高并发会打满线程池/套接字，整机（含鼠标）都会卡。</summary>
    private const int MaxDegree = 16;
    /// <summary>进度回调经 Progress&lt;T&gt; 会打到 UI 线程，必须节流。</summary>
    private const int ProgressMinIntervalMs = 120;

    /// <summary>枚举本机各活动网卡的 IPv4 网段（去重，如 <c>192.168.1.0/24</c>）。</summary>
    public static IReadOnlyList<string> ListLocalCidrs()
    {
        var list = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                    continue;

                var props = nic.GetIPProperties();
                foreach (var uni in props.UnicastAddresses)
                {
                    if (uni.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    if (IPAddress.IsLoopback(uni.Address)) continue;

                    var bytes = uni.Address.GetAddressBytes();
                    if (bytes[0] == 169 && bytes[1] == 254) continue; // link-local

                    var prefix = uni.PrefixLength;
                    if (prefix is < 8 or > 30)
                        prefix = 24;

                    var network = ToNetworkAddress(uni.Address, prefix);
                    var cidr = $"{network}/{prefix}";
                    if (seen.Add(cidr))
                        list.Add(cidr);
                }
            }
        }
        catch
        {
            // ignore
        }

        if (list.Count == 0)
            list.Add("192.168.1.0/24");
        return list;
    }

    /// <summary>根据本机网卡建议默认网段（取 <see cref="ListLocalCidrs"/> 首项）。</summary>
    public static string SuggestLocalCidr()
        => ListLocalCidrs()[0];

    /// <summary>解析网段文本为待探测主机列表。</summary>
    public static IReadOnlyList<IPAddress> ParseHosts(string rangeText)
    {
        var text = (rangeText ?? "").Trim();
        if (text.Length == 0)
            throw new ArgumentException("请填写网段");

        // a.b.c.d/n
        if (text.Contains('/'))
        {
            var parts = text.Split('/', 2, StringSplitOptions.TrimEntries);
            if (!IPAddress.TryParse(parts[0], out var baseIp) || baseIp.AddressFamily != AddressFamily.InterNetwork)
                throw new ArgumentException("网段地址无效");
            if (!int.TryParse(parts[1], out var prefix) || prefix is < 16 or > 30)
                throw new ArgumentException("前缀长度须在 16–30（过大网段会很慢）");

            return EnumerateCidr(baseIp, prefix);
        }

        // a.b.c.d-a.b.c.e
        if (text.Contains('-'))
        {
            var parts = text.Split('-', 2, StringSplitOptions.TrimEntries);
            if (!IPAddress.TryParse(parts[0], out var from) || !IPAddress.TryParse(parts[1], out var to))
                throw new ArgumentException("起止 IP 无效");
            return EnumerateRange(from, to);
        }

        // a.b.c → a.b.c.0/24
        var segs = text.Split('.', StringSplitOptions.TrimEntries);
        if (segs.Length == 3
            && byte.TryParse(segs[0], out var a)
            && byte.TryParse(segs[1], out var b)
            && byte.TryParse(segs[2], out var c))
        {
            return EnumerateCidr(new IPAddress(new byte[] { a, b, c, 0 }), 24);
        }

        // 单 IP
        if (IPAddress.TryParse(text, out var one) && one.AddressFamily == AddressFamily.InterNetwork)
            return [one];

        throw new ArgumentException("网段格式应为 192.168.1.0/24 或 192.168.1.1-192.168.1.50");
    }

    /// <summary>扫描网段，发现开放视频相关端口的主机（边扫边产出，进度节流）。</summary>
    public async IAsyncEnumerable<ScannedLanDevice> ScanAsync(
        string rangeText,
        IProgress<LanScanProgress>? progress = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var hosts = ParseHosts(rangeText);
        if (hosts.Count == 0)
            yield break;
        if (hosts.Count > MaxHosts)
            throw new ArgumentException($"主机数过多（{hosts.Count}），请缩小网段（最多 {MaxHosts}）");

        var channel = Channel.CreateUnbounded<ScannedLanDevice>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

        var done = 0;
        var found = 0;
        var lastReportMs = 0L;
        using var gate = new SemaphoreSlim(MaxDegree, MaxDegree);

        void Report(int d, IPAddress? host, bool force)
        {
            if (progress is null) return;
            var now = Environment.TickCount64;
            if (!force)
            {
                var last = Volatile.Read(ref lastReportMs);
                if (now - last < ProgressMinIntervalMs)
                    return;
                if (Interlocked.CompareExchange(ref lastReportMs, now, last) != last)
                    return;
            }
            else
            {
                Volatile.Write(ref lastReportMs, now);
            }

            progress.Report(new LanScanProgress(
                d,
                hosts.Count,
                host?.ToString(),
                Volatile.Read(ref found)));
        }

        var producer = Task.Run(async () =>
        {
            try
            {
                var tasks = new Task[hosts.Count];
                for (var i = 0; i < hosts.Count; i++)
                {
                    var host = hosts[i];
                    tasks[i] = ProbeHostAsync(
                        host,
                        gate,
                        channel.Writer,
                        ct,
                        onDone: () =>
                        {
                            var d = Interlocked.Increment(ref done);
                            Report(d, host, force: d >= hosts.Count);
                        },
                        onFound: () => Interlocked.Increment(ref found));
                }

                await Task.WhenAll(tasks).ConfigureAwait(false);
                Report(hosts.Count, null, force: true);
                channel.Writer.TryComplete();
            }
            catch (OperationCanceledException)
            {
                // 取消时正常结束通道，勿把异常写入 Writer（否则 ReadAllAsync 会炸）
                channel.Writer.TryComplete();
            }
            catch (Exception)
            {
                channel.Writer.TryComplete();
            }
        }, CancellationToken.None);

        try
        {
            while (await channel.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                while (channel.Reader.TryRead(out var device))
                    yield return device;
            }
        }
        finally
        {
            try
            {
                await producer.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // ignore
            }
            catch
            {
                // producer 已结束；勿向外抛
            }
        }
    }

    private static async Task ProbeHostAsync(
        IPAddress host,
        SemaphoreSlim gate,
        ChannelWriter<ScannedLanDevice> writer,
        CancellationToken ct,
        Action onDone,
        Action onFound)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ct.ThrowIfCancellationRequested();
            var open = new List<int>(4);
            foreach (var port in ProbePorts)
            {
                ct.ThrowIfCancellationRequested();
                if (await IsPortOpenAsync(host, port, ct).ConfigureAwait(false))
                    open.Add(port);
            }

            if (open.Count == 0)
                return;

            var hasRtsp = open.Contains(554) || open.Contains(8554);
            if (!hasRtsp && !open.Exists(p => p is 80 or 8080 or 8000 or 37777 or 34567))
                return;

            var rtspPort = open.Contains(554) ? 554 : open.Contains(8554) ? 8554 : 554;
            var kind = GuessKind(open);
            var device = new ScannedLanDevice(host.ToString(), rtspPort, kind, $"{kind} {host}", open);
            onFound();
            await writer.WriteAsync(device, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // 单主机失败不影响整段扫描
        }
        finally
        {
            gate.Release();
            onDone();
        }
    }

    /// <summary>用扫描结果 + 凭据组装摄像头源（默认海康主码流通道 1）。</summary>
    public static VideoSourceDescriptor BuildCameraSource(
        ScannedLanDevice device,
        CredentialStore credentials,
        string? username,
        string? password,
        string vendorId = "hikvision")
    {
        var channels = NvrTemplateExpander.ExpandVendor(
            vendorId,
            device.Host,
            device.RtspPort,
            channelFrom: 1,
            channelTo: 1,
            streamType: "main");
        var ch = channels[0];
        var parse = VideoEndpointParser.ParseAndNormalize(ch.Address);
        if (!parse.Ok || parse.Endpoint is null)
            throw new InvalidOperationException(parse.Message ?? "无法构造 RTSP 地址");

        string? credRef = null;
        if (!string.IsNullOrWhiteSpace(username))
            credRef = credentials.Save(null, username.Trim(), password ?? "", SourceFormSchema.AuthBasic);

        var endpoint = parse.Endpoint with
        {
            Transport = "tcp",
            BufferMs = 800,
            CredentialRef = credRef
        };

        return new VideoSourceDescriptor
        {
            Id = VideoSourceCatalog.NewId(),
            Name = string.IsNullOrWhiteSpace(device.DisplayName) ? ch.Name : device.DisplayName,
            Kind = VideoSourceKind.Camera,
            Origin = VideoSourceOrigin.Fixed,
            Endpoint = endpoint,
            DecodePreference = "auto",
            Note = $"扫描添加 · {device.Host}:{device.RtspPort}",
            ValidationState = SourceValidationState.Unknown
        };
    }

    /// <summary>是否已有同主机 RTSP 源（按地址主机匹配）。</summary>
    public static bool IsAlreadyAdded(IVideoWallService service, ScannedLanDevice device)
    {
        foreach (var s in service.Catalog.Sources)
        {
            if (!string.Equals(s.Endpoint.Protocol, "rtsp", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!Uri.TryCreate(s.Endpoint.Address, UriKind.Absolute, out var uri))
                continue;
            if (string.Equals(uri.Host, device.Host, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static string GuessKind(IReadOnlyList<int> ports)
    {
        if (ports.Contains(37777) || ports.Contains(34567))
            return "NVR";
        if (ports.Contains(554) || ports.Contains(8554))
            return ports.Any(p => p is 80 or 8000 or 8080) ? "摄像机/NVR" : "摄像机";
        return "设备";
    }

    private static async Task<bool> IsPortOpenAsync(IPAddress host, int port, CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(ConnectTimeoutMs);
            using var client = new TcpClient();
            await client.ConnectAsync(host, port, timeout.Token).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static List<IPAddress> EnumerateCidr(IPAddress anyInNetwork, int prefix)
    {
        var network = ToNetworkAddress(anyInNetwork, prefix);
        var hostBits = 32 - prefix;
        var count = 1 << hostBits;
        // 排除网络地址与广播
        var start = 1;
        var end = count - 2;
        if (end < start)
        {
            start = 0;
            end = count - 1;
        }

        var baseNum = ToUInt32(network);
        var list = new List<IPAddress>(Math.Min(MaxHosts, end - start + 1));
        for (var i = start; i <= end && list.Count < MaxHosts; i++)
            list.Add(FromUInt32(baseNum + (uint)i));
        return list;
    }

    private static List<IPAddress> EnumerateRange(IPAddress from, IPAddress to)
    {
        var a = ToUInt32(from);
        var b = ToUInt32(to);
        if (b < a) (a, b) = (b, a);
        if (b - a + 1 > MaxHosts)
            throw new ArgumentException($"主机数过多，请缩小范围（最多 {MaxHosts}）");

        var list = new List<IPAddress>((int)(b - a + 1));
        for (var i = a; i <= b; i++)
            list.Add(FromUInt32(i));
        return list;
    }

    private static IPAddress ToNetworkAddress(IPAddress address, int prefix)
    {
        var mask = prefix == 0 ? 0u : uint.MaxValue << (32 - prefix);
        var num = ToUInt32(address) & mask;
        return FromUInt32(num);
    }

    private static uint ToUInt32(IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        return ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
    }

    private static IPAddress FromUInt32(uint value)
        => new(new[]
        {
            (byte)(value >> 24),
            (byte)(value >> 16),
            (byte)(value >> 8),
            (byte)value
        });
}
