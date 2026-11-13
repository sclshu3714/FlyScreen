using System.Collections.Concurrent;
using FlyScreen.Relay.Models;
using FlyScreen.Relay.Options;
using Microsoft.Extensions.Options;

namespace FlyScreen.Relay.Services;

public sealed class RelaySession
{
    public required string SessionId { get; init; }
    public required string RoomId { get; init; }
    public required string DeviceA { get; init; }
    public required string DeviceB { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastActivity { get; set; } = DateTimeOffset.UtcNow;
    public long BytesAtoB { get; set; }
    public long BytesBtoA { get; set; }

    public string? PeerOf(string deviceId)
    {
        if (string.Equals(deviceId, DeviceA, StringComparison.OrdinalIgnoreCase)) return DeviceB;
        if (string.Equals(deviceId, DeviceB, StringComparison.OrdinalIgnoreCase)) return DeviceA;
        return null;
    }

    public bool Has(string deviceId)
        => string.Equals(deviceId, DeviceA, StringComparison.OrdinalIgnoreCase)
           || string.Equals(deviceId, DeviceB, StringComparison.OrdinalIgnoreCase);
}

/// <summary>TURN 类中继：两端主动出站连入后，按 session 互转密文字节。</summary>
public sealed class RelaySessionManager
{
    private readonly ConcurrentDictionary<string, RelaySession> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, PendingOpen> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly DeviceRegistry _devices;
    private readonly IOptionsMonitor<RelayOptions> _options;
    private readonly ILogger<RelaySessionManager> _log;
    private readonly RelayStats _stats;
    private readonly AdminChangeNotifier _adminNotify;

    private sealed record PendingOpen(string SessionId, string RoomId, string DeviceId, string PeerId, DateTimeOffset CreatedAt);

    public RelaySessionManager(
        DeviceRegistry devices,
        IOptionsMonitor<RelayOptions> options,
        RelayStats stats,
        AdminChangeNotifier adminNotify,
        ILogger<RelaySessionManager> log)
    {
        _devices = devices;
        _options = options;
        _stats = stats;
        _adminNotify = adminNotify;
        _log = log;
    }

    public int ActiveCount => _sessions.Count;

    public IReadOnlyList<RelaySession> List()
        => _sessions.Values.OrderByDescending(s => s.LastActivity).ToList();

    public bool TryGet(string sessionId, out RelaySession? session)
        => _sessions.TryGetValue(sessionId, out session);

    public async Task<(bool Ok, string? Error, RelaySession? Session)> OpenAsync(
        DeviceConnection self, string peerId, string? sessionId, CancellationToken ct)
    {
        peerId = (peerId ?? "").Trim();
        if (string.IsNullOrWhiteSpace(peerId))
            return (false, "peerId 为空", null);
        if (string.Equals(peerId, self.DeviceId, StringComparison.OrdinalIgnoreCase))
            return (false, "不能中继到自己", null);

        if (!_devices.TryGetByDeviceId(peerId, out var peer) || peer is null)
            return (false, "对端不在线", null);
        if (!string.Equals(peer.RoomId, self.RoomId, StringComparison.Ordinal))
            return (false, "对端不在同一房间（安全密钥不一致）", null);

        var sid = string.IsNullOrWhiteSpace(sessionId) ? Guid.NewGuid().ToString("N") : sessionId!.Trim();
        var opts = _options.CurrentValue;

        if (_sessions.TryGetValue(sid, out var existing))
        {
            if (!existing.Has(self.DeviceId) || !existing.Has(peerId))
                return (false, "sessionId 已被其它会话占用", null);
            existing.LastActivity = DateTimeOffset.UtcNow;
            self.RelaySessions[sid] = 0;
            return (true, null, existing);
        }

        if (_sessions.Count >= opts.MaxRelaySessions)
            return (false, $"中继会话已满（上限 {opts.MaxRelaySessions}）", null);

        var pairKey = PairKey(self.DeviceId, peerId);
        var pending = new PendingOpen(sid, self.RoomId, self.DeviceId, peerId, DateTimeOffset.UtcNow);

        if (_pending.TryGetValue(pairKey, out var held)
            && string.Equals(held.DeviceId, self.DeviceId, StringComparison.OrdinalIgnoreCase))
        {
            // 同一发起方重试：刷新等待，避免被当成对端加入而连成自环
            _pending[pairKey] = pending;
            self.RelaySessions[sid] = 0;
            await NotifyRelayRequestAsync(peer, self.DeviceId, peerId, sid, ct).ConfigureAwait(false);
            return (true, null, null);
        }

        if (_pending.TryAdd(pairKey, pending))
        {
            // 首端：等待对端以同一 sessionId（或任意，匹配 pair）打开
            self.RelaySessions[sid] = 0;
            _log.LogInformation("中继等待对端 session={Session} {A}->{B}", sid, self.DeviceId, peerId);
            await NotifyRelayRequestAsync(peer, self.DeviceId, peerId, sid, ct).ConfigureAwait(false);
            return (true, null, null); // 尚未 ready
        }

        // 对端已 pending：建立会话
        if (!_pending.TryRemove(pairKey, out var first))
            return (false, "中继握手冲突，请重试", null);

        if (string.Equals(first.DeviceId, self.DeviceId, StringComparison.OrdinalIgnoreCase))
        {
            _pending[pairKey] = pending;
            self.RelaySessions[sid] = 0;
            await NotifyRelayRequestAsync(peer, self.DeviceId, peerId, sid, ct).ConfigureAwait(false);
            return (true, null, null);
        }

        // 允许第二端带不同 sessionId：统一用首端的
        sid = first.SessionId;
        var session = new RelaySession
        {
            SessionId = sid,
            RoomId = self.RoomId,
            DeviceA = first.DeviceId,
            DeviceB = self.DeviceId
        };

        if (!_sessions.TryAdd(sid, session))
            return (false, "无法创建中继会话", null);

        self.RelaySessions[sid] = 0;
        if (_devices.TryGetByDeviceId(first.DeviceId, out var a) && a is not null)
            a.RelaySessions[sid] = 0;

        _stats.SessionsOpened++;
        _log.LogInformation("中继就绪 session={Session} {A}<->{B}", sid, session.DeviceA, session.DeviceB);

        var ready = DeviceRegistry.JsonSerialize(new WireMessage
        {
            Type = MessageTypes.RelayReady,
            SessionId = sid,
            PeerId = peerId,
            Ok = true
        });
        var readyToFirst = DeviceRegistry.JsonSerialize(new WireMessage
        {
            Type = MessageTypes.RelayReady,
            SessionId = sid,
            PeerId = self.DeviceId,
            Ok = true
        });

        // 先发 RelayReady，再唤醒管理页，避免 SSE 组包与就绪信令抢发送路径
        await self.SendTextAsync(ready, ct).ConfigureAwait(false);
        if (a is not null)
            await a.SendTextAsync(readyToFirst, ct).ConfigureAwait(false);
        _adminNotify.Notify();

        return (true, null, session);
    }

    public void Close(string sessionId, string? byDeviceId = null)
    {
        if (!_sessions.TryRemove(sessionId, out var session)) return;
        _stats.SessionsClosed++;
        _log.LogInformation("中继关闭 session={Session} by={By}", sessionId, byDeviceId);

        NotifyPeerLeft(session, session.DeviceA, sessionId);
        NotifyPeerLeft(session, session.DeviceB, sessionId);
        _adminNotify.Notify();
    }

    public void OnDeviceGone(string deviceId)
    {
        foreach (var kv in _pending.ToArray())
        {
            if (string.Equals(kv.Value.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase)
                || string.Equals(kv.Value.PeerId, deviceId, StringComparison.OrdinalIgnoreCase))
                _pending.TryRemove(kv.Key, out _);
        }

        foreach (var s in _sessions.Values.Where(s => s.Has(deviceId)).ToList())
            Close(s.SessionId, deviceId);
    }

    public async Task<bool> ForwardAsync(DeviceConnection from, Guid sessionGuid, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        var sid = sessionGuid.ToString("N");
        if (!_sessions.TryGetValue(sid, out var session))
            return false;
        if (!session.Has(from.DeviceId))
            return false;

        var peerId = session.PeerOf(from.DeviceId);
        if (peerId is null || !_devices.TryGetByDeviceId(peerId, out var peer) || peer is null)
            return false;

        // 组装：magic(1) + guid(16) + payload
        var frame = new byte[1 + 16 + payload.Length];
        frame[0] = Wire.RelayFrameMagic;
        sessionGuid.TryWriteBytes(frame.AsSpan(1, 16));
        payload.CopyTo(frame.AsMemory(17));

        await peer.SendBinaryAsync(frame, ct).ConfigureAwait(false);
        session.LastActivity = DateTimeOffset.UtcNow;
        if (string.Equals(from.DeviceId, session.DeviceA, StringComparison.OrdinalIgnoreCase))
            session.BytesAtoB += payload.Length;
        else
            session.BytesBtoA += payload.Length;
        _stats.RelayBytes += payload.Length;
        from.BytesReceived += payload.Length;
        return true;
    }

    public int SweepIdle(TimeSpan idleTimeout)
    {
        var now = DateTimeOffset.UtcNow;
        var n = 0;
        foreach (var s in _sessions.Values.ToArray())
        {
            if (now - s.LastActivity > idleTimeout)
            {
                Close(s.SessionId, "idle");
                n++;
            }
        }

        foreach (var kv in _pending.ToArray())
        {
            if (now - kv.Value.CreatedAt > TimeSpan.FromSeconds(60))
                _pending.TryRemove(kv.Key, out _);
        }

        return n;
    }

    private async Task NotifyRelayRequestAsync(
        DeviceConnection peer, string fromDeviceId, string peerId, string sessionId, CancellationToken ct)
    {
        await peer.SendTextAsync(DeviceRegistry.JsonSerialize(new WireMessage
        {
            Type = MessageTypes.Signal,
            From = fromDeviceId,
            To = peerId,
            MsgType = "relay-request",
            SessionId = sessionId,
            Payload = System.Text.Json.JsonSerializer.SerializeToElement(new
            {
                sessionId,
                from = fromDeviceId,
                reason = "p2p-failed-or-preferred-relay"
            }, Wire.JsonOptions)
        }), ct).ConfigureAwait(false);
        _stats.SignalsForwarded++;
    }

    private void NotifyPeerLeft(RelaySession session, string deviceId, string sessionId)
    {
        if (_devices.TryGetByDeviceId(deviceId, out var c) && c is not null)
        {
            c.RelaySessions.TryRemove(sessionId, out _);
            _ = c.SendTextAsync(DeviceRegistry.JsonSerialize(new WireMessage
            {
                Type = MessageTypes.RelayPeerLeft,
                SessionId = sessionId,
                PeerId = session.PeerOf(deviceId)
            }));
        }
    }

    private static string PairKey(string a, string b)
    {
        var x = string.CompareOrdinal(a, b) <= 0 ? a : b;
        var y = string.CompareOrdinal(a, b) <= 0 ? b : a;
        return $"{x}|{y}";
    }
}

public sealed class RelayStats
{
    public long SessionsOpened;
    public long SessionsClosed;
    public long RelayBytes;
    public long SignalsForwarded;
    public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;
}
