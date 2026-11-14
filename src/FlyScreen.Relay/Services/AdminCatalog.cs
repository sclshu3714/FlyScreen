using FlyScreen.Relay.Models;

namespace FlyScreen.Relay.Services;

public sealed class AdminDeviceRow
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string RoomId { get; init; } = "";
    public string RoomNo { get; init; } = "";
    public bool Online { get; init; }
    public string? Platform { get; init; }
    public int? TcpPort { get; init; }
    public string? RemoteAddress { get; init; }
    public List<AddressCandidate>? Candidates { get; init; }
    public DateTimeOffset RegisteredAt { get; init; }
    public DateTimeOffset LastSeen { get; init; }
}

public sealed class AdminRoomRow
{
    public string RoomId { get; init; } = "";
    public string RoomNo { get; init; } = "";
    public DateTimeOffset CreatedAt { get; init; }
    public int DeviceCount { get; init; }
    public int OnlineCount { get; init; }
    public DateTimeOffset? LastSeen { get; init; }
    public List<AdminDeviceRow> Devices { get; init; } = [];
}

/// <summary>管理页读模型：房间号是该房间第一台注册电脑的名称，设备在线状态叠加内存连接。</summary>
public sealed class AdminCatalog
{
    private readonly RelayStore _store;
    private readonly DeviceRegistry _devices;

    public AdminCatalog(RelayStore store, DeviceRegistry devices)
    {
        _store = store;
        _devices = devices;
    }

    public IReadOnlyList<AdminRoomRow> ListRooms(bool includeDevices)
    {
        var savedRooms = _store.ListRooms();
        var savedDevices = _store.ListDevices();
        var live = _devices.ListAll().ToDictionary(d => d.Id, StringComparer.OrdinalIgnoreCase);
        var byRoom = savedDevices.GroupBy(d => d.RoomId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<StoredDevice>)g.ToList(), StringComparer.Ordinal);

        var rows = new List<AdminRoomRow>();
        var known = new HashSet<string>(StringComparer.Ordinal);
        foreach (var room in savedRooms)
        {
            known.Add(room.RoomId);
            rows.Add(Build(room.RoomId, room.RoomNo, room.CreatedAt, byRoom.GetValueOrDefault(room.RoomId) ?? [], live, includeDevices));
        }

        foreach (var group in live.Values.GroupBy(d => d.RoomId, StringComparer.Ordinal))
        {
            if (known.Contains(group.Key)) continue;
            var first = group.OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase).First();
            var synthetic = group.Select(d => new StoredDevice
            {
                DeviceId = d.Id,
                RoomId = d.RoomId,
                Name = d.Name,
                Platform = d.Platform,
                PublicKey = d.PublicKey,
                TcpPort = d.TcpPort,
                RemoteAddress = d.RemoteAddress,
                Candidates = d.Candidates,
                RegisteredAt = d.LastSeen,
                LastSeen = d.LastSeen
            }).ToList();
            rows.Add(Build(group.Key, string.IsNullOrWhiteSpace(first.Name) ? group.Key : first.Name, DateTimeOffset.UtcNow, synthetic, live, includeDevices));
        }

        return rows.OrderBy(r => r.CreatedAt).ToList();
    }

    public AdminRoomRow? FindRoom(string roomId)
        => ListRooms(includeDevices: true).FirstOrDefault(r => string.Equals(r.RoomId, roomId, StringComparison.Ordinal));

    private static AdminRoomRow Build(
        string roomId,
        string roomNo,
        DateTimeOffset createdAt,
        IReadOnlyList<StoredDevice> devices,
        Dictionary<string, DeviceDto> live,
        bool includeDevices)
    {
        var rows = devices.Select(d =>
        {
            live.TryGetValue(d.DeviceId, out var online);
            var isOnline = online is not null;
            return new AdminDeviceRow
            {
                Id = d.DeviceId,
                Name = isOnline && !string.IsNullOrWhiteSpace(online!.Name) ? online.Name : d.Name,
                RoomId = roomId,
                RoomNo = roomNo,
                Online = isOnline,
                Platform = isOnline ? online!.Platform : d.Platform,
                TcpPort = isOnline ? online!.TcpPort : d.TcpPort,
                RemoteAddress = isOnline ? online!.RemoteAddress : d.RemoteAddress,
                Candidates = isOnline ? online!.Candidates : d.Candidates,
                RegisteredAt = d.RegisteredAt,
                LastSeen = isOnline ? online!.LastSeen : d.LastSeen
            };
        }).OrderByDescending(d => d.Online).ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToList();

        return new AdminRoomRow
        {
            RoomId = roomId,
            RoomNo = roomNo,
            CreatedAt = createdAt,
            DeviceCount = rows.Count,
            OnlineCount = rows.Count(d => d.Online),
            LastSeen = rows.Count == 0 ? null : rows.Max(d => d.LastSeen),
            Devices = includeDevices ? rows : []
        };
    }
}
