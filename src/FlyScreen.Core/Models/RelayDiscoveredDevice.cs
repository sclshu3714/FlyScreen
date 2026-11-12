namespace FlyScreen.Models;

/// <summary>
/// 中转服务上注册/扫描到的设备（一对多）。仅本机配置缓存，不进 LayoutSync。
/// </summary>
public sealed class RelayDiscoveredDevice
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Online { get; set; }
    public int? RttMs { get; set; }
}
