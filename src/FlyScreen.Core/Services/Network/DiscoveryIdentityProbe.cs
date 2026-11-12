using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using FlyScreen.Models;

namespace FlyScreen.Services.Network;

/// <summary>
/// 对连接配置中的探测地址做短握手，把对端学进 <see cref="DiscoveryService"/>。
/// 不维持长连接；真正会话仍须加入布局后由 ConnectionManager 建连。
/// </summary>
public static class DiscoveryIdentityProbe
{
    public readonly record struct Result(
        bool Ok,
        string? MachineName,
        string? LocalIp,
        int? RttMs,
        string Message,
        string? Platform = null);

    public static async Task<Result> ProbeAsync(
        string host,
        int tcpPort,
        string? announceIp,
        string securityKey,
        TimeSpan timeout,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(host) || tcpPort <= 0)
            return new Result(false, null, null, null, "地址无效");

        var sw = Stopwatch.StartNew();
        TcpClient? client = null;
        PeerSession? session = null;
        try
        {
            client = new TcpClient();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linked.CancelAfter(timeout);
            try
            {
                await client.ConnectAsync(host, tcpPort, linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return new Result(false, null, null, null, $"连接超时（>{timeout.TotalSeconds:0}s）");
            }

            session = new PeerSession(client, isOutbound: true);
            client = null; // 所有权交给 session

            var hs = MessageCodec.EncodeJson(MessageType.Handshake, LocalPeerIdentity.Enrich(new HandshakePayload
            {
                MachineName = LocalPeerIdentity.MachineName,
                SecurityKey = securityKey ?? "",
                LocalIp = string.IsNullOrWhiteSpace(announceIp) ? "0.0.0.0" : announceIp,
                Channel = PeerChannel.Control,
                IsReply = false,
                ProbeOnly = true
            }));

            var ackTask = WaitAckAsync(session, timeout, ct);
            await session.SendRawAsync(hs).ConfigureAwait(false);
            var ackMsg = await ackTask.ConfigureAwait(false);
            sw.Stop();
            if (ackMsg == null)
                return new Result(false, null, null, null, "握手无响应");

            var ack = MessageCodec.DecodeJson<HandshakePayload>(ackMsg.Value.Payload);
            if (ack == null || string.IsNullOrWhiteSpace(ack.MachineName))
                return new Result(false, null, null, null, "握手无效");
            if (!string.IsNullOrWhiteSpace(ack.RejectReason))
                return new Result(false, null, null, null, $"对端拒绝：{ack.RejectReason}");

            return new Result(
                true,
                ack.MachineName.Trim(),
                string.IsNullOrWhiteSpace(ack.LocalIp) ? null : ack.LocalIp.Trim(),
                (int)Math.Max(1, sw.ElapsedMilliseconds),
                "ok",
                string.IsNullOrWhiteSpace(ack.Platform) ? null : ack.Platform.Trim());
        }
        catch (SocketException ex)
        {
            return new Result(false, null, null, null, ex.SocketErrorCode.ToString());
        }
        catch (Exception ex)
        {
            return new Result(false, null, null, null, ex.Message);
        }
        finally
        {
            if (session != null)
            {
                try { await session.DisposeAsync().ConfigureAwait(false); } catch { /* ignore */ }
            }
            else
            {
                try { client?.Dispose(); } catch { /* ignore */ }
            }
        }
    }

    /// <summary>把探测结果写入发现表；地址优先握手宣告 IP，否则用探测 host。</summary>
    public static void Remember(
        DiscoveryService discovery,
        Result result,
        string probeHost,
        int tcpPort,
        int origin = DiscoveryOrigin.Remote)
    {
        if (!result.Ok || string.IsNullOrWhiteSpace(result.MachineName))
            return;

        var advertised = DiscoveryService.IsUsableLanIpv4(result.LocalIp) ? result.LocalIp! : null;
        var observed = DiscoveryService.IsUsableLanIpv4(probeHost) ? probeHost : null;
        var ip = advertised ?? observed;
        if (string.IsNullOrWhiteSpace(ip))
            return;

        // Tailscale 等无 UDP 广播时，保活数分钟，避免 8s prune 立刻从设备管理消失
        discovery.RememberPeer(
            result.MachineName, ip, tcpPort, observed, stickySeconds: 300, origin: origin);
    }

    private static async Task<NetMessage?> WaitAckAsync(PeerSession session, TimeSpan timeout, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<NetMessage>(TaskCreationOptions.RunContinuationsAsynchronously);

        void Handler(PeerSession _, NetMessage m)
        {
            if (m.Type == MessageType.HandshakeAck)
                tcs.TrySetResult(m);
        }

        session.MessageReceived += Handler;
        try
        {
            var delay = Task.Delay(timeout, ct);
            var completed = await Task.WhenAny(tcs.Task, delay).ConfigureAwait(false);
            if (completed != tcs.Task)
                return null;
            return await tcs.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        finally
        {
            session.MessageReceived -= Handler;
        }
    }
}
