using System.Text.Json;
using System.Text.Json.Serialization;

namespace FlyScreen.Services.Network.Relay;

/// <summary>
/// 中转边缘 WebSocket JSON 协议（与 FlyScreen.Relay 一致，camelCase；不引用 Relay 工程）。
/// </summary>
public static class RelayWire
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };
}

public sealed class RelayWireMessage
{
    public string Type { get; set; } = "";

    public string? DeviceId { get; set; }
    public string? Name { get; set; }
    public string? RoomKey { get; set; }
    public string? Platform { get; set; }
    public int? TcpPort { get; set; }
    public List<RelayAddressCandidate>? Candidates { get; set; }

    public long? Ts { get; set; }

    public string? ServerId { get; set; }
    public string? ServerName { get; set; }
    public DateTimeOffset? ServerTime { get; set; }
    public string? RoomId { get; set; }
    public List<RelayDeviceDto>? Devices { get; set; }
    public RelayDeviceDto? Device { get; set; }
    public string? Code { get; set; }
    public string? Message { get; set; }
    public bool? Ok { get; set; }
}

public sealed class RelayAddressCandidate
{
    public string Kind { get; set; } = "host";
    public string Address { get; set; } = "";
    public int Priority { get; set; }
}

public sealed class RelayDeviceDto
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string RoomId { get; set; } = "";
    public bool Online { get; set; }
    public string? Platform { get; set; }
    public int? TcpPort { get; set; }
    public List<RelayAddressCandidate>? Candidates { get; set; }
    public DateTimeOffset LastSeen { get; set; }
    public string? RemoteAddress { get; set; }
}

public static class RelayMessageTypes
{
    public const string Hello = "hello";
    public const string Ping = "ping";
    public const string Welcome = "welcome";
    public const string Registered = "registered";
    public const string Devices = "devices";
    public const string DeviceUpdated = "device.updated";
    public const string Pong = "pong";
    public const string Error = "error";
}
