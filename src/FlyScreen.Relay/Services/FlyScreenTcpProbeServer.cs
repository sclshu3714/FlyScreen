using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using FlyScreen.Relay.Options;
using Microsoft.Extensions.Options;

namespace FlyScreen.Relay.Services;

/// <summary>
/// 兼容客户端默认 TCP 15155：承接连接配置「测试」与刷新发现的 ProbeOnly 握手。
/// 帧格式与 Core <c>MessageCodec</c> 一致（1 type + 4 LE length + JSON）。
/// 多设备列表仍由 UDP 15156 发现桥接负责；本口只证明入口可达并回报服务名。
/// </summary>
public sealed class FlyScreenTcpProbeServer : BackgroundService
{
    private const byte MsgHandshake = 1;
    private const byte MsgHandshakeAck = 2;
    private const int HeaderSize = 5;
    private const int MaxPayload = 256 * 1024;

    private readonly DeviceRegistry _devices;
    private readonly IOptionsMonitor<RelayOptions> _options;
    private readonly ILogger<FlyScreenTcpProbeServer> _log;

    public FlyScreenTcpProbeServer(
        DeviceRegistry devices,
        IOptionsMonitor<RelayOptions> options,
        ILogger<FlyScreenTcpProbeServer> log)
    {
        _devices = devices;
        _options = options;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var port = _options.CurrentValue.PeerTcpPort;
        if (port <= 0)
        {
            _log.LogInformation("FlyScreen TCP 探测口已关闭（PeerTcpPort=0）");
            return;
        }

        var listener = new TcpListener(IPAddress.IPv6Any, port);
        listener.Server.DualMode = true;
        listener.Start();
        _log.LogInformation(
            "FlyScreen TCP 探测口监听 {Port}（兼容默认 15155 ProbeOnly；在线设备 {Count} 台由 UDP 发现列出）",
            port, _devices.OnlineCount);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }

                _ = Task.Run(() => HandleClientAsync(client, stoppingToken), CancellationToken.None);
            }
        }
        finally
        {
            try { listener.Stop(); } catch { /* ignore */ }
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken hostCt)
    {
        using var _ = client;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(hostCt);
        cts.CancelAfter(TimeSpan.FromSeconds(15));
        var ct = cts.Token;

        try
        {
            client.NoDelay = true;
            await using var stream = client.GetStream();
            var msg = await ReadFrameAsync(stream, ct).ConfigureAwait(false);
            if (msg is null) return;

            if (msg.Value.Type != MsgHandshake)
            {
                _log.LogDebug("TCP 探测口忽略非 Handshake 类型 {Type}", msg.Value.Type);
                return;
            }

            using var doc = JsonDocument.Parse(msg.Value.Payload);
            var root = doc.RootElement;
            var probeOnly = root.TryGetProperty("ProbeOnly", out var po) && po.ValueKind == JsonValueKind.True
                            || root.TryGetProperty("probeOnly", out var po2) && po2.ValueKind == JsonValueKind.True;
            var clientKey = ReadString(root, "SecurityKey", "securityKey") ?? "";
            var clientName = ReadString(root, "MachineName", "machineName") ?? "";
            var clientIp = ReadString(root, "LocalIp", "localIp") ?? "";

            var opts = _options.CurrentValue;
            var remote = client.Client.RemoteEndPoint?.ToString();

            // ProbeOnly：回 Ack 后断开。非探测也回 Ack（服务名）后断开，避免被当成可布局 peer。
            var ack = new Dictionary<string, object?>
            {
                ["MachineName"] = opts.ServerName,
                ["SecurityKey"] = clientKey,
                ["LocalIp"] = PickServerAnnounceIp(client) ?? "0.0.0.0",
                ["ProtocolVersion"] = 2,
                ["IsReply"] = true,
                ["ProbeOnly"] = probeOnly,
                ["Platform"] = "relay",
                ["DeviceClass"] = "Relay",
                ["AppVersion"] = typeof(FlyScreenTcpProbeServer).Assembly.GetName().Version?.ToString() ?? "1.0.0",
                ["RejectReason"] = null
            };

            var payload = JsonSerializer.SerializeToUtf8Bytes(ack);
            await stream.WriteAsync(EncodeFrame(MsgHandshakeAck, payload), ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);

            _log.LogInformation(
                "TCP ProbeOnly={Probe} from {Client} ({Remote}) keyLen={KeyLen} onlineDevices={Online}",
                probeOnly, clientName, remote, clientKey.Length, _devices.OnlineCount);

            // 探测方 LocalIp 可用时，主动向其 UDP 发现口补发一遍在线设备（加固跨网单播）
            if (!string.IsNullOrWhiteSpace(clientIp)
                && IPAddress.TryParse(clientIp, out var tip)
                && tip.AddressFamily == AddressFamily.InterNetwork
                && !IPAddress.IsLoopback(tip))
            {
                try
                {
                    await FlyScreenDiscoveryBridge.UnicastAnnounceAllAsync(
                            _devices, tip, opts.DiscoveryUdpPort > 0 ? opts.DiscoveryUdpPort : 15156, ct)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _log.LogDebug(ex, "探测后向 {Ip} 补发 UDP 宣告失败", clientIp);
                }
            }
        }
        catch (OperationCanceledException) { /* timeout / stop */ }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "TCP 探测处理异常");
        }
    }

    private static string? PickServerAnnounceIp(TcpClient client)
    {
        try
        {
            if (client.Client.LocalEndPoint is IPEndPoint lep)
            {
                var a = lep.Address;
                if (a.IsIPv4MappedToIPv6) a = a.MapToIPv4();
                if (a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a))
                    return a.ToString();
            }
        }
        catch { /* ignore */ }
        return null;
    }

    private static string? ReadString(JsonElement root, params string[] names)
    {
        foreach (var n in names)
        {
            if (root.TryGetProperty(n, out var el) && el.ValueKind == JsonValueKind.String)
                return el.GetString();
        }
        return null;
    }

    private static byte[] EncodeFrame(byte type, ReadOnlySpan<byte> payload)
    {
        var buf = new byte[HeaderSize + payload.Length];
        buf[0] = type;
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(1, 4), payload.Length);
        payload.CopyTo(buf.AsSpan(HeaderSize));
        return buf;
    }

    private static async Task<(byte Type, byte[] Payload)?> ReadFrameAsync(NetworkStream stream, CancellationToken ct)
    {
        var header = new byte[HeaderSize];
        if (!await ReadExactAsync(stream, header, ct).ConfigureAwait(false))
            return null;
        var type = header[0];
        var len = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(1, 4));
        if (len < 0 || len > MaxPayload)
            throw new InvalidOperationException($"非法帧长度 {len}");
        var payload = len == 0 ? [] : new byte[len];
        if (len > 0 && !await ReadExactAsync(stream, payload, ct).ConfigureAwait(false))
            return null;
        return (type, payload);
    }

    private static async Task<bool> ReadExactAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(offset), ct).ConfigureAwait(false);
            if (n == 0) return false;
            offset += n;
        }
        return true;
    }
}
