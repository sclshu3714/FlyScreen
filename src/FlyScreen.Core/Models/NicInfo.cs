namespace FlyScreen.Models;

public sealed class NicInfo
{
    /// <summary>「自动」选项：TCP/UDP 绑定 0.0.0.0，监听所有网卡。</summary>
    public const string AutomaticId = "auto";

    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public string IPv4 { get; init; } = "";
    public string SubnetMask { get; init; } = "";
    public bool IsUp { get; init; }

    public bool IsAutomatic =>
        string.Equals(Id, AutomaticId, StringComparison.OrdinalIgnoreCase)
        || string.Equals(IPv4, "0.0.0.0", StringComparison.OrdinalIgnoreCase);

    public string Display => IsAutomatic
        ? Name
        : $"{Name} — {IPv4}{(IsUp ? "" : " [未连接]")}";

    /// <summary>下拉框用短文案，避免超长适配器名撑破布局。</summary>
    public string ShortDisplay
    {
        get
        {
            if (IsAutomatic) return Name;
            var name = Name.Length > 18 ? Name[..16] + "…" : Name;
            return $"{name} — {IPv4}{(IsUp ? "" : " [未连接]")}";
        }
    }

    public override string ToString() => ShortDisplay;
}
