using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using FlyScreen.Relay.Models;
using FlyScreen.Relay.Options;
using Microsoft.Extensions.Options;

namespace FlyScreen.Relay.Services;

public sealed class DeviceConnection
{
    public required string ConnectionId { get; init; }
    public required string DeviceId { get; init; }
    public required string RoomId { get; init; }
    public required WebSocket Socket { get; init; }
    public required CancellationTokenSource Lifetime { get; init; }
    public string Name { get; set; } = "";
    public string? Platform { get; set; }
    public string? PublicKey { get; set; }
    public int? TcpPort { get; set; }
    public List<AddressCandidate> Candidates { get; set; } = [];
    public string? RemoteAddress { get; set; }
    public DateTimeOffset ConnectedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastSeen { get; set; } = DateTimeOffset.UtcNow;
    public long BytesSent { get; set; }
    public long BytesReceived { get; set; }

    /// <summary>当前占用的中继会话（一设备可有多个，用 ConcurrentDictionary）。</summary>
    public ConcurrentDictionary<string, byte> RelaySessions { get; } = new(StringComparer.OrdinalIgnoreCase);

    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public async Task SendTextAsync(string json, CancellationToken ct = default)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (Socket.State != WebSocketState.Open) return;
            await Socket.SendAsync(bytes, WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
            BytesSent += bytes.Length;
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async Task SendBinaryAsync(ReadOnlyMemory<byte> payload, CancellationToken ct = default)
    {
        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (Socket.State != WebSocketState.Open) return;
            await Socket.SendAsync(payload, WebSocketMessageType.Binary, true, ct).ConfigureAwait(false);
            BytesSent += payload.Length;
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public DeviceDto ToDto() => new()
    {
        Id = DeviceId,
        Name = Name,
        RoomId = RoomId,
        Online = Socket.State == WebSocketState.Open,
        Platform = Platform,
        PublicKey = PublicKey,
        TcpPort = TcpPort,
        Candidates = Candidates.Count == 0 ? null : [.. Candidates],
        LastSeen = LastSeen,
        RemoteAddress = RemoteAddress
    };
}

public sealed class DeviceRegistry
{
    private readonly ConcurrentDictionary<string, DeviceConnection> _byConnection = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _deviceToConnection = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _rooms = new(StringComparer.Ordinal);
    private readonly IOptionsMonitor<RelayOptions> _options;
    private readonly RelayStore _store;
    private readonly AdminChangeNotifier _adminNotify;
    private readonly ILogger<DeviceRegistry> _log;

    public DeviceRegistry(
        IOptionsMonitor<RelayOptions> options,
        RelayStore store,
        AdminChangeNotifier adminNotify,
        ILogger<DeviceRegistry> log)
    {
        _options = options;
        _store = store;
        _adminNotify = adminNotify;
        _log = log;
    }

    public int OnlineCount => _byConnection.Count;
    public int RoomCount => _rooms.Count;

    public bool ContainsRoom(string? roomId)
        => !string.IsNullOrEmpty(roomId) && _rooms.ContainsKey(roomId);

    public static string HashRoomKey(string roomKey)
    {
        var raw = (roomKey ?? "").Trim();
        if (raw.Length == 0) raw = "default";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    public DeviceConnection Register(
        string connectionId,
        WebSocket socket,
        WireMessage hello,
        string? remoteAddress)
    {
        var opts = _options.CurrentValue;
        var deviceId = string.IsNullOrWhiteSpace(hello.DeviceId)
            ? Guid.NewGuid().ToString("N")
            : hello.DeviceId!.Trim();
        var roomId = HashRoomKey(hello.RoomKey ?? "");
        var name = string.IsNullOrWhiteSpace(hello.Name) ? deviceId[..Math.Min(8, deviceId.Length)] : hello.Name!.Trim();
        if (name.Length > 200) name = name[..200];

        if (_rooms.TryGetValue(roomId, out var existing) && existing.Count >= opts.MaxDevicesPerRoom
            && !_deviceToConnection.ContainsKey(deviceId))
        {
            throw new InvalidOperationException($"房间已满（上限 {opts.MaxDevicesPerRoom}）");
        }

        // 同 deviceId 重连：踢掉旧连接
        if (_deviceToConnection.TryGetValue(deviceId, out var oldConnId)
            && _byConnection.TryRemove(oldConnId, out var old))
        {
            _log.LogInformation("设备 {DeviceId} 重连，关闭旧连接 {Old}", deviceId, oldConnId);
            try { old.Lifetime.Cancel(); } catch { /* ignore */ }
            RemoveFromRoom(old.RoomId, old.DeviceId);
        }

        var conn = new DeviceConnection
        {
            ConnectionId = connectionId,
            DeviceId = deviceId,
            RoomId = roomId,
            Socket = socket,
            Lifetime = new CancellationTokenSource(),
            Name = name,
            Platform = hello.Platform,
            PublicKey = hello.PublicKey,
            TcpPort = hello.TcpPort,
            Candidates = hello.Candidates ?? [],
            RemoteAddress = remoteAddress,
        };

        _byConnection[connectionId] = conn;
        _deviceToConnection[deviceId] = connectionId;
        var room = _rooms.GetOrAdd(roomId, _ => new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase));
        room[deviceId] = 0;

        var roomNo = name;
        try
        {
            roomNo = _store.EnsureRoom(roomId, name, deviceId);
            _store.UpsertDevice(conn);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "写入注册库失败 {DeviceId}", deviceId);
        }

        _log.LogInformation("设备注册 {DeviceId} name={Name} roomNo={RoomNo} room={Room} from={Remote}",
            deviceId, name, roomNo, roomId, remoteAddress);
        _adminNotify.Notify();
        return conn;
    }

    public void Touch(DeviceConnection conn)
        => conn.LastSeen = DateTimeOffset.UtcNow;

    public void UpdateCandidates(DeviceConnection conn, List<AddressCandidate>? candidates)
    {
        if (candidates is null) return;
        conn.Candidates = candidates;
        conn.LastSeen = DateTimeOffset.UtcNow;
        try { _store.UpsertDevice(conn); }
        catch (Exception ex) { _log.LogDebug(ex, "更新设备候选失败 {DeviceId}", conn.DeviceId); }
        _adminNotify.Notify();
    }

    public bool TryGetByDeviceId(string deviceId, out DeviceConnection? conn)
    {
        conn = null;
        if (!_deviceToConnection.TryGetValue(deviceId, out var cid)) return false;
        return _byConnection.TryGetValue(cid, out conn);
    }

    public bool TryGetByConnectionId(string connectionId, out DeviceConnection? conn)
        => _byConnection.TryGetValue(connectionId, out conn);

    public IReadOnlyList<DeviceDto> ListRoom(string roomId)
    {
        if (!_rooms.TryGetValue(roomId, out var set)) return [];
        var list = new List<DeviceDto>();
        foreach (var deviceId in set.Keys)
        {
            if (TryGetByDeviceId(deviceId, out var c) && c is not null)
                list.Add(c.ToDto());
        }
        return list.OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public IReadOnlyList<DeviceDto> ListAll()
        => _byConnection.Values.Select(c => c.ToDto())
            .OrderBy(d => d.RoomId).ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToList();

    public IReadOnlyList<(string RoomId, int Count)> ListRooms()
        => _rooms.Select(kv => (kv.Key, kv.Value.Count)).OrderBy(x => x.Key).ToList();

    public void Unregister(string connectionId)
    {
        if (!_byConnection.TryRemove(connectionId, out var conn)) return;
        if (_deviceToConnection.TryGetValue(conn.DeviceId, out var cid) && cid == connectionId)
            _deviceToConnection.TryRemove(conn.DeviceId, out _);
        RemoveFromRoom(conn.RoomId, conn.DeviceId);
        try { conn.Lifetime.Cancel(); } catch { /* ignore */ }
        try { _store.Touch(conn.DeviceId, conn.LastSeen); }
        catch (Exception ex) { _log.LogDebug(ex, "更新离线时间失败 {DeviceId}", conn.DeviceId); }
        _log.LogInformation("设备离线 {DeviceId} room={Room}", conn.DeviceId, conn.RoomId);
        _adminNotify.Notify();
    }

    public async Task<int> BroadcastRoomAsync(string roomId, string json, string? exceptDeviceId = null, CancellationToken ct = default)
    {
        if (!_rooms.TryGetValue(roomId, out var set)) return 0;
        var delivered = 0;
        foreach (var deviceId in set.Keys)
        {
            if (exceptDeviceId is not null
                && string.Equals(deviceId, exceptDeviceId, StringComparison.OrdinalIgnoreCase))
                continue;
            if (TryGetByDeviceId(deviceId, out var c) && c is not null)
            {
                try
                {
                    await c.SendTextAsync(json, ct).ConfigureAwait(false);
                    delivered++;
                }
                catch (Exception ex) { _log.LogDebug(ex, "广播失败 {DeviceId}", deviceId); }
            }
        }

        return delivered;
    }

    public async Task KickAsync(string deviceId)
    {
        if (!TryGetByDeviceId(deviceId, out var conn) || conn is null) return;
        try
        {
            await conn.SendTextAsync(JsonSerialize(new WireMessage
            {
                Type = MessageTypes.Error,
                Code = "kicked",
                Message = "已被管理员踢出"
            })).ConfigureAwait(false);
            if (conn.Socket.State == WebSocketState.Open)
                await conn.Socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "kicked", CancellationToken.None)
                    .ConfigureAwait(false);
        }
        catch { /* ignore */ }
        finally
        {
            conn.Lifetime.Cancel();
            Unregister(conn.ConnectionId);
        }
    }

    private void RemoveFromRoom(string roomId, string deviceId)
    {
        if (!_rooms.TryGetValue(roomId, out var set)) return;
        set.TryRemove(deviceId, out _);
        if (set.IsEmpty) _rooms.TryRemove(roomId, out _);
    }

    public static string JsonSerialize(WireMessage msg)
        => System.Text.Json.JsonSerializer.Serialize(msg, Wire.JsonOptions);
}
