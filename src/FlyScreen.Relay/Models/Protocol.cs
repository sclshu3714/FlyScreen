using System.Text.Json;
using System.Text.Json.Serialization;

namespace FlyScreen.Relay.Models;

/// <summary>
/// 边缘 WebSocket 文本帧协议（JSON）。
/// 二进制帧：16 字节 sessionId (GUID) + 密文载荷，仅在中继会话建立后使用。
/// </summary>
public static class Wire
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    public const byte RelayFrameMagic = 0x01;
}

public sealed class WireMessage
{
    public string Type { get; set; } = "";

    // hello / register
    public string? DeviceId { get; set; }
    public string? Name { get; set; }
    public string? RoomKey { get; set; }
    public string? PublicKey { get; set; }
    public string? Platform { get; set; }
    public int? TcpPort { get; set; }
    public List<AddressCandidate>? Candidates { get; set; }

    // ping / pong
    public long? Ts { get; set; }

    // signal
    public string? To { get; set; }
    public string? From { get; set; }
    public string? MsgType { get; set; }
    public JsonElement? Payload { get; set; }

    // relay
    public string? PeerId { get; set; }
    public string? SessionId { get; set; }

    // server replies
    public string? ServerId { get; set; }
    public string? ServerName { get; set; }
    public DateTimeOffset? ServerTime { get; set; }
    public string? RoomId { get; set; }
    public StunHint? Stun { get; set; }
    public List<DeviceDto>? Devices { get; set; }
    public DeviceDto? Device { get; set; }
    public string? Code { get; set; }
    public string? Message { get; set; }
    public bool? Ok { get; set; }
    public ConnectAdvice? ConnectAdvice { get; set; }
}

public sealed class StunHint
{
    public string Host { get; set; } = "";
    public int Port { get; set; }
}

/// <summary>客户端侧连接建议：先 P2P（含 IPv6），失败再中继。</summary>
public sealed class ConnectAdvice
{
    /// <summary>prefer-p2p | prefer-relay</summary>
    public string Mode { get; set; } = "prefer-p2p";

    /// <summary>P2P 探测超时建议（毫秒）。</summary>
    public int P2PTimeoutMs { get; set; } = 5000;

    public string[] TryOrder { get; set; } = ["ipv6", "host", "srflx", "relay"];
}

public sealed class AddressCandidate
{
    /// <summary>host | srflx | ipv6 | relay</summary>
    public string Kind { get; set; } = "host";

    /// <summary>host:port 或 [ipv6]:port</summary>
    public string Address { get; set; } = "";

    public int Priority { get; set; }
}

public sealed class DeviceDto
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string RoomId { get; set; } = "";
    public bool Online { get; set; }
    public string? Platform { get; set; }
    public string? PublicKey { get; set; }
    public int? TcpPort { get; set; }
    public List<AddressCandidate>? Candidates { get; set; }
    public DateTimeOffset LastSeen { get; set; }
    public string? RemoteAddress { get; set; }
}

public static class MessageTypes
{
    // client → server
    public const string Hello = "hello";
    public const string Ping = "ping";
    public const string Candidates = "candidates";
    public const string Signal = "signal";
    public const string RelayOpen = "relay.open";
    public const string RelayClose = "relay.close";
    public const string DevicesQuery = "devices.query";

    // server → client
    public const string Welcome = "welcome";
    public const string Registered = "registered";
    public const string Devices = "devices";
    public const string DeviceUpdated = "device.updated";
    public const string Pong = "pong";
    public const string RelayReady = "relay.ready";
    public const string RelayPeerLeft = "relay.peer-left";
    public const string Error = "error";
}
