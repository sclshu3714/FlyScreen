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
}

/// <summary>内存环形缓冲：记录边缘口转发的信令，供管理页列表/详情。</summary>
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
    private readonly ConcurrentDictionary<string, string> _roomNoCache = new(StringComparer.Ordinal);
    private long _seq;

    public SignalLog(DeviceRegistry devices, AdminCatalog catalog, AdminChangeNotifier notify)
    {
        _devices = devices;
        _catalog = catalog;
        _notify = notify;
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
        var toName = ResolveName(toId);
        var roomNo = ResolveRoomNo(from.RoomId);
        var payloadJson = FormatPayload(payload, out var bytes);
        Add(new SignalLogEntry
        {
            Id = NextId(),
            At = DateTimeOffset.UtcNow,
            Kind = "signal",
            RoomId = from.RoomId,
            RoomNo = roomNo,
            FromId = from.DeviceId,
            FromName = string.IsNullOrWhiteSpace(from.Name) ? from.DeviceId : from.Name,
            ToId = toId,
            ToName = toName,
            MsgType = string.IsNullOrWhiteSpace(msgType) ? "(empty)" : msgType!.Trim(),
            SessionId = string.IsNullOrWhiteSpace(sessionId) ? null : sessionId.Trim(),
            Ok = ok,
            Error = error,
            PayloadJson = payloadJson,
            PayloadBytes = bytes
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
            PayloadBytes = bytes
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

        _notify.Notify();
    }

    private string NextId()
    {
        var n = Interlocked.Increment(ref _seq);
        return $"{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-{n:x4}";
    }

    private string ResolveName(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId)) return "—";
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
        hasPayload = !string.IsNullOrEmpty(e.PayloadJson)
    };
}
