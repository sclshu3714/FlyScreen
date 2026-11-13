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

    /// <summary>注册库。默认内嵌 SQLite；只有 <see cref="DatabaseOptions.UseEmbedded"/> 为 false 时才使用外部库。</summary>
    public DatabaseOptions Database { get; set; } = new();

    public int DeviceOfflineAfterSeconds { get; set; } = 90;
    public int RelayIdleTimeoutSeconds { get; set; } = 600;
    public int MaxDevicesPerRoom { get; set; } = 64;
    public int MaxRelaySessions { get; set; } = 128;
    public int MaxRelayFrameBytes { get; set; } = 1_048_576;
}

/// <summary>管理端注册库。默认内嵌 SQLite，不读取安全密钥、也不作为管理令牌。</summary>
public sealed class DatabaseOptions
{
    /// <summary>
    /// true（默认）：使用内嵌 SQLite。
    /// 只有显式设为 false 时才改用外部数据库，此时必须填写 <see cref="Provider"/> 与 <see cref="ConnectionString"/>。
    /// </summary>
    public bool UseEmbedded { get; set; } = true;

    /// <summary>内嵌库文件。相对路径相对于内容根目录。仅 <see cref="UseEmbedded"/> 为 true 时使用。</summary>
    public string EmbeddedPath { get; set; } = "data/relay.db";

    /// <summary>外部库类型。当前支持 mysql、mariadb。</summary>
    public string Provider { get; set; } = "";

    /// <summary>外部库连接串。例如 Server=127.0.0.1;Port=3306;Database=flyscreen;User ID=fly;Password=secret;</summary>
    public string ConnectionString { get; set; } = "";
}
