using FlyScreen.Helpers.Localization;

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

    private string DownSuffix => IsUp ? "" : $" [{Loc.T("Net.NicDown")}]";

    private string LocalizedName => LocalizeAdapterName(Name);

    public string Display => IsAutomatic
        ? Name
        : $"{LocalizedName} — {IPv4}{DownSuffix}";

    /// <summary>下拉框用短文案，避免超长适配器名撑破布局。</summary>
    public string ShortDisplay
    {
        get
        {
            if (IsAutomatic) return Name;
            var name = LocalizedName;
            if (name.Length > 18) name = name[..16] + "…";
            return $"{name} — {IPv4}{DownSuffix}";
        }
    }

    public override string ToString() => ShortDisplay;

    /// <summary>
    /// 系统网卡名常随 OS 语言变化（如中文「以太网」）；按前缀映射到当前 UI 语言。
    /// </summary>
    internal static string LocalizeAdapterName(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return raw ?? "";
        // 长前缀优先
        (string Prefix, string Key)[] map =
        [
            ("无线网络连接", "Net.NicType.Wireless"),
            ("蓝牙网络连接", "Net.NicType.Bluetooth"),
            ("本地连接", "Net.NicType.Local"),
            ("以太网", "Net.NicType.Ethernet"),
            ("Ethernet", "Net.NicType.Ethernet"),
            ("Wi-Fi", "Net.NicType.WiFi"),
            ("WiFi", "Net.NicType.WiFi"),
            ("WLAN", "Net.NicType.Wlan"),
            ("wlan", "Net.NicType.Wlan"),
        ];
        foreach (var (prefix, key) in map)
        {
            if (raw.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return Loc.T(key) + raw[prefix.Length..];
        }

        return raw;
    }
}
