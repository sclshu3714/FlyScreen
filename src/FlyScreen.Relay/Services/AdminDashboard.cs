using Microsoft.Extensions.Options;
using FlyScreen.Relay.Options;

namespace FlyScreen.Relay.Services;

/// <summary>管理页看板快照：供 REST 与 SSE 共用。</summary>
public static class AdminDashboard
{
    public static object Build(
        IOptionsMonitor<RelayOptions> opt,
        AdminCatalog catalog,
        RelaySessionManager sessions,
        RelayStats stats,
        bool includeRoomDevices = false,
        long revision = 0,
        SignalLog? signals = null)
    {
        var rooms = catalog.ListRooms(includeDevices: true);
        var names = rooms.SelectMany(r => r.Devices).ToDictionary(d => d.Id, d => d.Name, StringComparer.OrdinalIgnoreCase);
        var roomNo = rooms.ToDictionary(r => r.RoomId, r => r.RoomNo, StringComparer.Ordinal);
        string NameOf(string id) => names.TryGetValue(id, out var n) && !string.IsNullOrWhiteSpace(n) ? n : id;

        var sessionList = sessions.List().Select(s => new
        {
            s.SessionId,
            s.RoomId,
            roomNo = roomNo.TryGetValue(s.RoomId, out var no) ? no : s.RoomId,
            s.DeviceA,
            deviceAName = NameOf(s.DeviceA),
            s.DeviceB,
            deviceBName = NameOf(s.DeviceB),
            s.CreatedAt,
            s.LastActivity,
            s.BytesAtoB,
            s.BytesBtoA
        });

        var signalList = signals?.ListSummaries(SignalLog.Capacity) ?? [];

        var head = new
        {
            revision,
            server = opt.CurrentValue.ServerName,
            startedAt = stats.StartedAt,
            uptimeSec = (long)(DateTimeOffset.UtcNow - stats.StartedAt).TotalSeconds,
            registeredRooms = rooms.Count,
            registeredDevices = rooms.Sum(r => r.DeviceCount),
            onlineDevices = rooms.Sum(r => r.OnlineCount),
            sessions = sessions.ActiveCount,
            relayBytes = stats.RelayBytes,
            signals = stats.SignalsForwarded
        };

        if (includeRoomDevices)
        {
            return new
            {
                head.revision,
                head.server,
                head.startedAt,
                head.uptimeSec,
                head.registeredRooms,
                head.registeredDevices,
                head.onlineDevices,
                head.sessions,
                head.relayBytes,
                head.signals,
                rooms = rooms.Select(r => new
                {
                    roomId = r.RoomId,
                    roomNo = r.RoomNo,
                    createdAt = r.CreatedAt,
                    deviceCount = r.DeviceCount,
                    onlineCount = r.OnlineCount,
                    lastSeen = r.LastSeen,
                    devices = r.Devices.Select(d => new
                    {
                        id = d.Id,
                        name = d.Name,
                        roomId = d.RoomId,
                        roomNo = d.RoomNo,
                        online = d.Online,
                        platform = d.Platform,
                        tcpPort = d.TcpPort,
                        remoteAddress = d.RemoteAddress,
                        candidates = d.Candidates,
                        registeredAt = d.RegisteredAt,
                        lastSeen = d.LastSeen
                    })
                }),
                sessionList,
                signalList
            };
        }

        return new
        {
            head.revision,
            head.server,
            head.startedAt,
            head.uptimeSec,
            head.registeredRooms,
            head.registeredDevices,
            head.onlineDevices,
            head.sessions,
            head.relayBytes,
            head.signals,
            rooms = rooms.Select(r => new
            {
                roomId = r.RoomId,
                roomNo = r.RoomNo,
                createdAt = r.CreatedAt,
                deviceCount = r.DeviceCount,
                onlineCount = r.OnlineCount,
                lastSeen = r.LastSeen
            }),
            sessionList,
            signalList
        };
    }
}
