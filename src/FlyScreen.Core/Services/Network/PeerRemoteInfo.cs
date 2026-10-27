namespace FlyScreen.Services.Network;

/// <summary>对端在握手中宣告的身份与能力（缺省 = 旧端 Protocol v1）。</summary>
public sealed class PeerRemoteInfo
{
    public int ProtocolVersion { get; init; } = 1;
    public string? Platform { get; init; }
    public string? DeviceClass { get; init; }
    public string[] Caps { get; init; } = [];
    public string? AppVersion { get; init; }

    /// <summary>旧端无 Caps 字段时为空数组，协商时视为「不限制本机能力」（兼容 v1）。</summary>
    public bool IsLegacy => Caps.Length == 0 && ProtocolVersion <= 1;

    public static PeerRemoteInfo FromHandshake(HandshakePayload? hs)
    {
        if (hs == null)
            return new PeerRemoteInfo();

        return new PeerRemoteInfo
        {
            ProtocolVersion = hs.ProtocolVersion <= 0 ? 1 : hs.ProtocolVersion,
            Platform = hs.Platform,
            DeviceClass = hs.DeviceClass,
            Caps = hs.Caps ?? [],
            AppVersion = hs.AppVersion
        };
    }

    public override string ToString()
    {
        var caps = Caps.Length == 0 ? "(legacy/无Caps)" : string.Join(",", Caps);
        return $"v{ProtocolVersion} {Platform ?? "?"}/{DeviceClass ?? "?"} Caps=[{caps}]";
    }
}
