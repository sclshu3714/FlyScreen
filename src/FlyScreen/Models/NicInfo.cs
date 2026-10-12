namespace FlyScreen.Models;

public sealed class NicInfo
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public string IPv4 { get; init; } = "";
    public string SubnetMask { get; init; } = "";
    public bool IsUp { get; init; }
    public string Display => $"{Name} — {IPv4}{(IsUp ? "" : " [未连接]")}";
}
