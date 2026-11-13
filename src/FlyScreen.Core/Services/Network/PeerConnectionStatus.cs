namespace FlyScreen.Services.Network;

public sealed record PeerConnectionStatus(
    string Name,
    bool Outbound,
    bool Inbound,
    string? DiscoveredIp)
{
    public bool AnyAlive => Outbound || Inbound;

    /// <summary>
    /// 控制面可用。单条 TCP 全双工即可收发；布局「双向」与投屏等门禁以此为准，
    /// 不要求同时存在出站+入站两条套接字。
    /// </summary>
    public bool Bidirectional => Outbound || Inbound;

    /// <summary>出站与入站均已建立（双 TCP，诊断用）。</summary>
    public bool DualLinked => Outbound && Inbound;
}
