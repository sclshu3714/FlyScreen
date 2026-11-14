namespace FlyScreen.Models;

/// <summary>
/// 服务（FlyScreen.Relay）上注册/扫描到的设备（一对多）。仅本机配置缓存，不进 LayoutSync。
/// </summary>
public sealed class RelayDiscoveredDevice
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Online { get; set; }
    public int? RttMs { get; set; }
    public int? TcpPort { get; set; }
    /// <summary>信令服务看到的对端地址（host:port）。端口是 WebSocket 源端口，P2P 时改用 <see cref="TcpPort"/>。</summary>
    public string? RemoteAddress { get; set; }
}
