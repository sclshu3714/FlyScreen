namespace FlyScreen.Relay.Options;

/// <summary>自托管中继服务配置（对标 Tailscale：控制面管理 + DERP 边缘面）。</summary>
public sealed class RelayOptions
{
    public const string SectionName = "Relay";

    /// <summary>服务显示名，会出现在 /v1/info。</summary>
    public string ServerName { get; set; } = "flyscreen-relay";

    /// <summary>边缘口：注册 / 发现 / 信令 / WebSocket 中继。例 0.0.0.0:8443</summary>
    public string EdgeListen { get; set; } = "0.0.0.0:8443";

    /// <summary>管理口：网页与 REST。例 0.0.0.0:9080</summary>
    public string AdminListen { get; set; } = "0.0.0.0:9080";

    /// <summary>对外公布的边缘 URL（wss://host:8443）。空则按请求 Host 推断。</summary>
    public string PublicEdgeUrl { get; set; } = "";

    /// <summary>STUN UDP 端口（可与边缘 TCP 同号；0=关闭）。</summary>
    public int StunUdpPort { get; set; } = 8443;

    /// <summary>
    /// 兼容客户端默认 TCP 端口（连接配置未写端口时用 15155）。
    /// 承接「测试」可达性与刷新发现的 ProbeOnly 握手；0=关闭。
    /// 多设备列表仍靠 <see cref="DiscoveryUdpPort"/>。
    /// </summary>
    public int PeerTcpPort { get; set; } = 15155;

    /// <summary>
    /// 兼容客户端 DiscoveryService 的 UDP 端口（默认 15156）。
    /// 客户端把本服务 IP 配进「连接配置」后，「刷新发现」会向该口单播；
    /// 本服务应答全部在线注册设备的 DiscoveryPacket。0=关闭。
    /// </summary>
    public int DiscoveryUdpPort { get; set; } = 15156;

    /// <summary>是否周期性向 IPv4 广播宣告在线设备（同局域网/VPN 时无需单播也能扫到）。</summary>
    public bool DiscoveryBroadcastAnnounce { get; set; } = true;

    /// <summary>
    /// 管理 API / 页面鉴权令牌。建议与客户端「安全密钥」设为同一字符串，便于同钥同房与管理面统一。
    /// 设备 WebSocket 注册仍用 roomKey（=客户端安全密钥），不校验本令牌。
    /// </summary>
    public string AdminToken { get; set; } = "change-me";

    public int DeviceOfflineAfterSeconds { get; set; } = 90;
    public int RelayIdleTimeoutSeconds { get; set; } = 600;
    public int MaxDevicesPerRoom { get; set; } = 64;
    public int MaxRelaySessions { get; set; } = 128;
    public int MaxRelayFrameBytes { get; set; } = 1_048_576;
    public bool AllowAnonymousAdminRead { get; set; }
}
