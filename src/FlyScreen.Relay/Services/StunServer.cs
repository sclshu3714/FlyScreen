using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using FlyScreen.Relay.Options;
using Microsoft.Extensions.Options;

namespace FlyScreen.Relay.Services;

/// <summary>
/// 极简 STUN Binding（RFC 5389）：回复 XOR-MAPPED-ADDRESS，供客户端收集 srflx 候选后做 P2P。
/// </summary>
public sealed class StunServer : BackgroundService
{
    private static readonly byte[] MagicCookie = [0x21, 0x12, 0xA4, 0x42];
    private const int BindingRequest = 0x0001;
    private const int BindingSuccess = 0x0101;
    private const int AttrXorMappedAddress = 0x0020;
    private const int AttrMappedAddress = 0x0001;

    private readonly IOptionsMonitor<RelayOptions> _options;
    private readonly ILogger<StunServer> _log;

    public StunServer(IOptionsMonitor<RelayOptions> options, ILogger<StunServer> log)
    {
        _options = options;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var port = _options.CurrentValue.StunUdpPort;
        if (port <= 0)
        {
            _log.LogInformation("STUN 已关闭（StunUdpPort=0）");
            return;
        }

        using var udp = new UdpClient(AddressFamily.InterNetworkV6);
        udp.Client.DualMode = true;
        udp.Client.Bind(new IPEndPoint(IPAddress.IPv6Any, port));
        _log.LogInformation("STUN 监听 UDP {Port}", port);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var result = await udp.ReceiveAsync(stoppingToken).ConfigureAwait(false);
                var resp = BuildBindingSuccess(result.Buffer, result.RemoteEndPoint);
                if (resp is null) continue;
                await udp.SendAsync(resp, result.RemoteEndPoint, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "STUN 处理异常");
            }
        }
    }

    private static byte[]? BuildBindingSuccess(byte[] req, IPEndPoint remote)
    {
        if (req.Length < 20) return null;
        var type = BinaryPrimitives.ReadUInt16BigEndian(req.AsSpan(0, 2));
        if (type != BindingRequest) return null;
        if (!req.AsSpan(4, 4).SequenceEqual(MagicCookie)) return null;

        var txId = req.AsSpan(8, 12).ToArray();
        var xorAttr = EncodeXorMapped(remote, txId);
        var mappedAttr = EncodeMapped(remote);

        var msgLen = xorAttr.Length + mappedAttr.Length;
        var resp = new byte[20 + msgLen];
        BinaryPrimitives.WriteUInt16BigEndian(resp.AsSpan(0, 2), BindingSuccess);
        BinaryPrimitives.WriteUInt16BigEndian(resp.AsSpan(2, 2), (ushort)msgLen);
        MagicCookie.CopyTo(resp.AsSpan(4, 4));
        txId.CopyTo(resp.AsSpan(8, 12));
        xorAttr.CopyTo(resp.AsSpan(20));
        mappedAttr.CopyTo(resp.AsSpan(20 + xorAttr.Length));
        return resp;
    }

    private static byte[] EncodeXorMapped(IPEndPoint ep, byte[] txId)
    {
        if (ep.Address.AddressFamily == AddressFamily.InterNetwork)
        {
            var addr = ep.Address.GetAddressBytes();
            var buf = new byte[12];
            BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(0, 2), AttrXorMappedAddress);
            BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(2, 2), 8);
            buf[4] = 0;
            buf[5] = 0x01; // IPv4
            var xport = (ushort)(ep.Port ^ 0x2112);
            BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(6, 2), xport);
            for (var i = 0; i < 4; i++)
                buf[8 + i] = (byte)(addr[i] ^ MagicCookie[i]);
            return buf;
        }

        // IPv6
        var v6 = ep.Address.MapToIPv6().GetAddressBytes();
        if (v6.Length != 16) return EncodeMapped(ep);
        var buf6 = new byte[24];
        BinaryPrimitives.WriteUInt16BigEndian(buf6.AsSpan(0, 2), AttrXorMappedAddress);
        BinaryPrimitives.WriteUInt16BigEndian(buf6.AsSpan(2, 2), 20);
        buf6[4] = 0;
        buf6[5] = 0x02; // IPv6
        var xport6 = (ushort)(ep.Port ^ 0x2112);
        BinaryPrimitives.WriteUInt16BigEndian(buf6.AsSpan(6, 2), xport6);
        var xorKey = new byte[16];
        MagicCookie.CopyTo(xorKey.AsSpan(0, 4));
        txId.CopyTo(xorKey.AsSpan(4, 12));
        for (var i = 0; i < 16; i++)
            buf6[8 + i] = (byte)(v6[i] ^ xorKey[i]);
        return buf6;
    }

    private static byte[] EncodeMapped(IPEndPoint ep)
    {
        if (ep.Address.AddressFamily == AddressFamily.InterNetwork)
        {
            var addr = ep.Address.GetAddressBytes();
            var buf = new byte[12];
            BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(0, 2), AttrMappedAddress);
            BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(2, 2), 8);
            buf[4] = 0;
            buf[5] = 0x01;
            BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(6, 2), (ushort)ep.Port);
            addr.CopyTo(buf.AsSpan(8, 4));
            return buf;
        }

        var v6 = ep.Address.MapToIPv6().GetAddressBytes();
        var buf6 = new byte[24];
        BinaryPrimitives.WriteUInt16BigEndian(buf6.AsSpan(0, 2), AttrMappedAddress);
        BinaryPrimitives.WriteUInt16BigEndian(buf6.AsSpan(2, 2), 20);
        buf6[4] = 0;
        buf6[5] = 0x02;
        BinaryPrimitives.WriteUInt16BigEndian(buf6.AsSpan(6, 2), (ushort)ep.Port);
        v6.AsSpan(0, 16).CopyTo(buf6.AsSpan(8, 16));
        return buf6;
    }
}
