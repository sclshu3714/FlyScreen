using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using FlyScreen.Relay.Models;

namespace FlyScreen.Relay.Services;

public sealed class SignalLogEntry
{
    public string Id { get; init; } = "";
    public DateTimeOffset At { get; init; }
    public string Kind { get; init; } = "signal";
    public string RoomId { get; init; } = "";
    public string RoomNo { get; init; } = "";
    public string FromId { get; init; } = "";
    public string FromName { get; init; } = "";
    public string ToId { get; init; } = "";
    public string ToName { get; init; } = "";
    public string MsgType { get; init; } = "";
    public string? SessionId { get; init; }
    public bool Ok { get; init; }
    public string? Error { get; init; }
    public string? PayloadJson { get; init; }
    public int PayloadBytes { get; init; }
    /// <summary>房间广播时实际投递数（列表仍记 1 条，与 KPI 条数一致）。</summary>
    public int Delivered { get; init; } = 1;
}

/// <summary>
/// 内存环形缓冲：一条记录 = 一次信令事件 = KPI「信令次数」+1。
/// </summary>
public sealed class SignalLog
{
    public const int Capacity = 500;
    public const int MaxPayloadChars = 16_384;

    private readonly object _gate = new();
    private readonly LinkedList<SignalLogEntry> _entries = new();
    private readonly ConcurrentDictionary<string, SignalLogEntry> _byId = new(StringComparer.Ordinal);
    private readonly DeviceRegistry _devices;
    private readonly AdminCatalog _catalog;
    private readonly AdminChangeNotifier _notify;
    private readonly RelayStats _stats;
    private readonly ConcurrentDictionary<string, string> _roomNoCache = new(StringComparer.Ordinal);
    private long _seq;

    public SignalLog(
        DeviceRegistry devices,
        AdminCatalog catalog,
        AdminChangeNotifier notify,
        RelayStats stats)
    {
        _devices = devices;
        _catalog = catalog;
        _notify = notify;
        _stats = stats;
    }

    /// <summary>列表中当前条数（环形缓冲内）。</summary>
    public int Count
    {
        get { lock (_gate) return _entries.Count; }
    }

    public void RecordForward(
        DeviceConnection from,
        string toId,
        string? msgType,
        string? sessionId,
        JsonElement? payload,
        bool ok,
        string? error = null)
    {
        var payloadJson = FormatPayload(payload, out var bytes);
        Add(new SignalLogEntry
        {
            Id = NextId(),
            At = DateTimeOffset.UtcNow,
            Kind = "signal",
            RoomId = from.RoomId,
            RoomNo = ResolveRoomNo(from.RoomId),
            FromId = from.DeviceId,
            FromName = string.IsNullOrWhiteSpace(from.Name) ? from.DeviceId : from.Name,
            ToId = toId,
            ToName = ResolveName(toId),
            MsgType = string.IsNullOrWhiteSpace(msgType) ? "(empty)" : msgType!.Trim(),
            SessionId = string.IsNullOrWhiteSpace(sessionId) ? null : sessionId.Trim(),
            Ok = ok,
            Error = error,
            PayloadJson = payloadJson,
            PayloadBytes = bytes,
            Delivered = ok ? 1 : 0
        });
    }

    public void RecordServerSignal(
        string roomId,
        string fromId,
        string toId,
        string msgType,
        string? sessionId,
        JsonElement? payload,
        bool ok,
        string? error = null)
    {
        var payloadJson = FormatPayload(payload, out var bytes);
        Add(new SignalLogEntry
        {
            Id = NextId(),
            At = DateTimeOffset.UtcNow,
            Kind = "signal",
            RoomId = roomId,
            RoomNo = ResolveRoomNo(roomId),
            FromId = fromId,
            FromName = ResolveName(fromId),
            ToId = toId,
            ToName = ResolveName(toId),
            MsgType = msgType,
            SessionId = string.IsNullOrWhiteSpace(sessionId) ? null : sessionId.Trim(),
            Ok = ok,
            Error = error,
            PayloadJson = payloadJson,
            PayloadBytes = bytes,
            Delivered = ok ? 1 : 0
        });
    }

    /// <summary>房间广播（如 device.updated）：记 1 条，KPI +1；Delivered 为实际投递台数。</summary>
    public void RecordRoomBroadcast(
        DeviceConnection from,
        string msgType,
        int delivered,
        JsonElement? payload = null)
    {
        if (delivered <= 0 && payload is null) return;
        var payloadJson = FormatPayload(payload, out var bytes);
        Add(new SignalLogEntry
        {
            Id = NextId(),
            At = DateTimeOffset.UtcNow,
            Kind = "broadcast",
            RoomId = from.RoomId,
            RoomNo = ResolveRoomNo(from.RoomId),
            FromId = from.DeviceId,
            FromName = string.IsNullOrWhiteSpace(from.Name) ? from.DeviceId : from.Name,
            ToId = "*",
            ToName = delivered > 0 ? $"房间广播 ×{delivered}" : "房间广播",
            MsgType = msgType,
            Ok = delivered > 0,
            Error = delivered > 0 ? null : "无接收方",
            PayloadJson = payloadJson,
            PayloadBytes = bytes,
            Delivered = Math.Max(0, delivered)
        });
    }

    public IReadOnlyList<object> ListSummaries(int limit = 100)
    {
        limit = Math.Clamp(limit, 1, Capacity);
        lock (_gate)
        {
            return _entries.Take(limit).Select(ToSummary).ToList();
        }
    }

    public SignalLogEntry? Find(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        return _byId.TryGetValue(id.Trim(), out var e) ? e : null;
    }

    private void Add(SignalLogEntry entry)
    {
        lock (_gate)
        {
            _entries.AddFirst(entry);
            _byId[entry.Id] = entry;
            while (_entries.Count > Capacity)
            {
                var last = _entries.Last!;
                _entries.RemoveLast();
                _byId.TryRemove(last.Value.Id, out _);
            }
        }

        // KPI 与列表事件一一对应：每记一条 +1
        Interlocked.Increment(ref _stats.SignalsForwarded);
        _notify.Notify();
    }

    private string NextId()
    {
        var n = Interlocked.Increment(ref _seq);
        return $"{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-{n:x4}";
    }

    private string ResolveName(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId) || deviceId == "*") return "—";
        if (_devices.TryGetByDeviceId(deviceId, out var conn) && conn is not null
            && !string.IsNullOrWhiteSpace(conn.Name))
            return conn.Name;
        return deviceId;
    }

    private string ResolveRoomNo(string roomId)
    {
        if (string.IsNullOrWhiteSpace(roomId)) return "—";
        if (_roomNoCache.TryGetValue(roomId, out var cached)) return cached;
        var room = _catalog.FindRoom(roomId);
        if (room != null && !string.IsNullOrWhiteSpace(room.RoomNo))
        {
            _roomNoCache[roomId] = room.RoomNo;
            return room.RoomNo;
        }

        return roomId;
    }

    private static string? FormatPayload(JsonElement? payload, out int bytes)
    {
        bytes = 0;
        if (payload is null) return null;
        try
        {
            var raw = payload.Value.GetRawText();
            bytes = Encoding.UTF8.GetByteCount(raw);
            if (raw.Length <= MaxPayloadChars) return raw;
            return raw[..MaxPayloadChars] + "\n…(已截断)";
        }
        catch
        {
            return null;
        }
    }

    private static object ToSummary(SignalLogEntry e) => new
    {
        e.Id,
        e.At,
        e.Kind,
        e.RoomId,
        e.RoomNo,
        e.FromId,
        e.FromName,
        e.ToId,
        e.ToName,
        e.MsgType,
        e.SessionId,
        e.Ok,
        e.Error,
        e.PayloadBytes,
        e.Delivered,
        hasPayload = !string.IsNullOrEmpty(e.PayloadJson)
    };
}
