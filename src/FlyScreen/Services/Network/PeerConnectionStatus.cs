namespace FlyScreen.Services.Network;

public sealed record PeerConnectionStatus(
    string Name,
    bool Outbound,
    bool Inbound,
    string? DiscoveredIp)
{
    public bool AnyAlive => Outbound || Inbound;
    public bool Bidirectional => Outbound && Inbound;
}
