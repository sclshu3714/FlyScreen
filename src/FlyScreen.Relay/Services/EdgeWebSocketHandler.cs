using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using FlyScreen.Relay.Models;
using FlyScreen.Relay.Options;
using Microsoft.Extensions.Options;

namespace FlyScreen.Relay.Services;

public sealed class EdgeWebSocketHandler
{
    private readonly DeviceRegistry _devices;
    private readonly RelaySessionManager _sessions;
    private readonly RelayStats _stats;
    private readonly IOptionsMonitor<RelayOptions> _options;
    private readonly ILogger<EdgeWebSocketHandler> _log;

    public EdgeWebSocketHandler(
        DeviceRegistry devices,
        RelaySessionManager sessions,
        RelayStats stats,
        IOptionsMonitor<RelayOptions> options,
        ILogger<EdgeWebSocketHandler> log)
    {
        _devices = devices;
        _sessions = sessions;
        _stats = stats;
        _options = options;
        _log = log;
    }

    public async Task HandleAsync(HttpContext ctx)
    {
        if (!ctx.WebSockets.IsWebSocketRequest)
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            await ctx.Response.WriteAsync("Expected WebSocket upgrade").ConfigureAwait(false);
            return;
        }

        using var socket = await ctx.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
        var connectionId = Guid.NewGuid().ToString("N");
        var remote = ctx.Connection.RemoteIpAddress?.ToString();
        if (ctx.Connection.RemotePort > 0 && remote is not null)
            remote = $"{remote}:{ctx.Connection.RemotePort}";

        var opts = _options.CurrentValue;
        DeviceConnection? device = null;

        try
        {
            await SendAsync(socket, new WireMessage
            {
                Type = MessageTypes.Welcome,
                ServerId = Environment.MachineName,
                ServerName = opts.ServerName,
                ServerTime = DateTimeOffset.UtcNow,
                Stun = opts.StunUdpPort > 0
                    ? new StunHint { Host = ctx.Request.Host.Host, Port = opts.StunUdpPort }
                    : null,
                ConnectAdvice = new ConnectAdvice()
            }).ConfigureAwait(false);

            var buffer = new byte[Math.Max(64 * 1024, opts.MaxRelayFrameBytes + 64)];
            while (socket.State == WebSocketState.Open)
            {
                var result = await ReceiveFullAsync(socket, buffer, device?.Lifetime.Token ?? ctx.RequestAborted)
                    .ConfigureAwait(false);
                if (result is null) break;

                if (result.Value.Type == WebSocketMessageType.Close) break;

                if (result.Value.Type == WebSocketMessageType.Binary)
                {
                    if (device is null) continue;
                    await HandleBinaryAsync(device, buffer.AsMemory(0, result.Value.Count), ctx.RequestAborted)
                        .ConfigureAwait(false);
                    continue;
                }

                var json = Encoding.UTF8.GetString(buffer, 0, result.Value.Count);
                WireMessage? msg;
                try { msg = JsonSerializer.Deserialize<WireMessage>(json, Wire.JsonOptions); }
                catch (Exception ex)
                {
                    _log.LogDebug(ex, "无效 JSON");
                    await SendErrorAsync(socket, "bad_json", "无法解析消息").ConfigureAwait(false);
                    continue;
                }

                if (msg is null || string.IsNullOrWhiteSpace(msg.Type)) continue;

                if (device is null)
                {
                    if (!string.Equals(msg.Type, MessageTypes.Hello, StringComparison.OrdinalIgnoreCase))
                    {
                        await SendErrorAsync(socket, "need_hello", "请先发送 hello 注册").ConfigureAwait(false);
                        continue;
                    }

                    try
                    {
                        device = _devices.Register(connectionId, socket, msg, remote);
                    }
                    catch (Exception ex)
                    {
                        await SendErrorAsync(socket, "register_failed", ex.Message).ConfigureAwait(false);
                        break;
                    }

                    await device.SendTextAsync(DeviceRegistry.JsonSerialize(new WireMessage
                    {
                        Type = MessageTypes.Registered,
                        Ok = true,
                        DeviceId = device.DeviceId,
                        RoomId = device.RoomId,
                        ConnectAdvice = new ConnectAdvice()
                    }), ctx.RequestAborted).ConfigureAwait(false);

                    await PushDeviceListAsync(device).ConfigureAwait(false);
                    var announced = await _devices.BroadcastRoomAsync(
                        device.RoomId,
                        DeviceRegistry.JsonSerialize(new WireMessage
                        {
                            Type = MessageTypes.DeviceUpdated,
                            Device = device.ToDto()
                        }),
                        exceptDeviceId: device.DeviceId,
                        ct: ctx.RequestAborted).ConfigureAwait(false);
                    if (device.Candidates is { Count: > 0 })
                        _stats.SignalsForwarded += announced;
                    continue;
                }

                _devices.Touch(device);
                await DispatchAsync(device, msg, ctx.RequestAborted).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* normal */ }
        catch (WebSocketException ex)
        {
            _log.LogDebug(ex, "WebSocket 断开 {Conn}", connectionId);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "边缘连接异常 {Conn}", connectionId);
        }
        finally
        {
            if (device is not null)
            {
                var roomId = device.RoomId;
                var dto = device.ToDto();
                dto.Online = false;
                _sessions.OnDeviceGone(device.DeviceId);
                _devices.Unregister(connectionId);
                await _devices.BroadcastRoomAsync(
                    roomId,
                    DeviceRegistry.JsonSerialize(new WireMessage
                    {
                        Type = MessageTypes.DeviceUpdated,
                        Device = dto
                    })).ConfigureAwait(false);
            }

            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                try
                {
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch { /* ignore */ }
            }
        }
    }

    private async Task DispatchAsync(DeviceConnection device, WireMessage msg, CancellationToken ct)
    {
        switch (msg.Type.ToLowerInvariant())
        {
            case MessageTypes.Ping:
                await device.SendTextAsync(DeviceRegistry.JsonSerialize(new WireMessage
                {
                    Type = MessageTypes.Pong,
                    Ts = msg.Ts ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                }), ct).ConfigureAwait(false);
                break;

            case MessageTypes.Candidates:
                _devices.UpdateCandidates(device, msg.Candidates);
                var forwarded = await _devices.BroadcastRoomAsync(
                    device.RoomId,
                    DeviceRegistry.JsonSerialize(new WireMessage
                    {
                        Type = MessageTypes.DeviceUpdated,
                        Device = device.ToDto()
                    }),
                    exceptDeviceId: device.DeviceId,
                    ct: ct).ConfigureAwait(false);
                _stats.SignalsForwarded += forwarded;
                break;

            case MessageTypes.DevicesQuery:
                await PushDeviceListAsync(device).ConfigureAwait(false);
                break;

            case MessageTypes.Signal:
                await ForwardSignalAsync(device, msg, ct).ConfigureAwait(false);
                break;

            case MessageTypes.RelayOpen:
            {
                var (ok, err, session) = await _sessions.OpenAsync(device, msg.PeerId ?? "", msg.SessionId, ct)
                    .ConfigureAwait(false);
                if (!ok)
                {
                    await device.SendTextAsync(DeviceRegistry.JsonSerialize(new WireMessage
                    {
                        Type = MessageTypes.Error,
                        Code = "relay_open_failed",
                        Message = err
                    }), ct).ConfigureAwait(false);
                }
                else if (session is null)
                {
                    await device.SendTextAsync(DeviceRegistry.JsonSerialize(new WireMessage
                    {
                        Type = MessageTypes.Signal,
                        MsgType = "relay-waiting",
                        SessionId = msg.SessionId,
                        PeerId = msg.PeerId,
                        Message = "等待对端加入中继"
                    }), ct).ConfigureAwait(false);
                }
                // session 非空时 OpenAsync 已向双方发 RelayReady
                break;
            }

            case MessageTypes.RelayClose:
                if (!string.IsNullOrWhiteSpace(msg.SessionId))
                    _sessions.Close(msg.SessionId!, device.DeviceId);
                break;

            default:
                await device.SendTextAsync(DeviceRegistry.JsonSerialize(new WireMessage
                {
                    Type = MessageTypes.Error,
                    Code = "unknown_type",
                    Message = $"未知消息类型: {msg.Type}"
                }), ct).ConfigureAwait(false);
                break;
        }
    }

    private async Task ForwardSignalAsync(DeviceConnection from, WireMessage msg, CancellationToken ct)
    {
        var to = (msg.To ?? "").Trim();
        if (string.IsNullOrWhiteSpace(to))
        {
            await from.SendTextAsync(DeviceRegistry.JsonSerialize(new WireMessage
            {
                Type = MessageTypes.Error,
                Code = "bad_signal",
                Message = "缺少 to"
            }), ct).ConfigureAwait(false);
            return;
        }

        if (!_devices.TryGetByDeviceId(to, out var peer) || peer is null)
        {
            await from.SendTextAsync(DeviceRegistry.JsonSerialize(new WireMessage
            {
                Type = MessageTypes.Error,
                Code = "peer_offline",
                Message = "对端不在线"
            }), ct).ConfigureAwait(false);
            return;
        }

        if (!string.Equals(peer.RoomId, from.RoomId, StringComparison.Ordinal))
        {
            await from.SendTextAsync(DeviceRegistry.JsonSerialize(new WireMessage
            {
                Type = MessageTypes.Error,
                Code = "room_mismatch",
                Message = "对端不在同一房间"
            }), ct).ConfigureAwait(false);
            return;
        }

        await peer.SendTextAsync(DeviceRegistry.JsonSerialize(new WireMessage
        {
            Type = MessageTypes.Signal,
            From = from.DeviceId,
            To = to,
            MsgType = msg.MsgType,
            Payload = msg.Payload,
            SessionId = msg.SessionId
        }), ct).ConfigureAwait(false);
        _stats.SignalsForwarded++;
    }

    private async Task HandleBinaryAsync(DeviceConnection device, Memory<byte> data, CancellationToken ct)
    {
        var opts = _options.CurrentValue;
        if (data.Length < 17 || data.Span[0] != Wire.RelayFrameMagic)
        {
            _log.LogDebug("忽略非法中继帧 len={Len}", data.Length);
            return;
        }

        if (data.Length - 17 > opts.MaxRelayFrameBytes)
        {
            await device.SendTextAsync(DeviceRegistry.JsonSerialize(new WireMessage
            {
                Type = MessageTypes.Error,
                Code = "frame_too_large",
                Message = $"帧超过 {opts.MaxRelayFrameBytes} 字节"
            }), ct).ConfigureAwait(false);
            return;
        }

        var guid = new Guid(data.Span.Slice(1, 16));
        var payload = data.Slice(17);
        var ok = await _sessions.ForwardAsync(device, guid, payload, ct).ConfigureAwait(false);
        if (!ok)
        {
            await device.SendTextAsync(DeviceRegistry.JsonSerialize(new WireMessage
            {
                Type = MessageTypes.Error,
                Code = "relay_forward_failed",
                Message = "中继转发失败（会话不存在或无权）",
                SessionId = guid.ToString("N")
            }), ct).ConfigureAwait(false);
        }
    }

    private async Task PushDeviceListAsync(DeviceConnection device)
    {
        var list = _devices.ListRoom(device.RoomId).ToList();
        await device.SendTextAsync(DeviceRegistry.JsonSerialize(new WireMessage
        {
            Type = MessageTypes.Devices,
            Devices = list
        })).ConfigureAwait(false);
        if (list.Any(d =>
                !string.Equals(d.Id, device.DeviceId, StringComparison.OrdinalIgnoreCase)
                && d.Candidates is { Count: > 0 }))
            _stats.SignalsForwarded++;
    }

    private static async Task SendAsync(WebSocket socket, WireMessage msg)
    {
        var json = DeviceRegistry.JsonSerialize(msg);
        var bytes = Encoding.UTF8.GetBytes(json);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None).ConfigureAwait(false);
    }

    private static Task SendErrorAsync(WebSocket socket, string code, string message)
        => SendAsync(socket, new WireMessage { Type = MessageTypes.Error, Code = code, Message = message });

    private static async Task<(WebSocketMessageType Type, int Count)?> ReceiveFullAsync(
        WebSocket socket, byte[] buffer, CancellationToken ct)
    {
        var offset = 0;
        while (true)
        {
            if (offset >= buffer.Length) throw new InvalidOperationException("消息过大");
            var slice = buffer.AsMemory(offset);
            var r = await socket.ReceiveAsync(slice, ct).ConfigureAwait(false);
            if (r.MessageType == WebSocketMessageType.Close)
                return (WebSocketMessageType.Close, 0);
            offset += r.Count;
            if (r.EndOfMessage)
                return (r.MessageType, offset);
        }
    }
}
