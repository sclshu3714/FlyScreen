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

    /// <summary>下拉框用短文案，避免超长适配器名撑破布局。</summary>
    public string ShortDisplay
    {
        get
        {
            var name = Name.Length > 18 ? Name[..16] + "…" : Name;
            return $"{name} — {IPv4}{(IsUp ? "" : " [未连接]")}";
        }
    }

    public override string ToString() => ShortDisplay;
}
