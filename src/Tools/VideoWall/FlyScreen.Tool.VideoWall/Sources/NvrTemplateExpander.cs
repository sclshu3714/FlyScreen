namespace FlyScreen.Tool.VideoWall.Sources;

/// <summary>NVR 单路展开结果。</summary>
public sealed record NvrExpandedChannel(string Name, string Address);

/// <summary>厂商预设模板（主/子码流地址与名称模板）。</summary>
public sealed record NvrVendorPreset(
    string AddressTemplateMain,
    string AddressTemplateSub,
    string NameTemplateMain,
    string NameTemplateSub);

/// <summary>
/// NVR 批量通道模板展开（docs/video-wall-sources.md §6.1、§6.2）。
/// 占位符：<c>{host}</c> <c>{port}</c> <c>{ch}</c> <c>{ch2}</c> <c>{prefix}</c>；
/// <c>{user}</c>/<c>{pass}</c> 不写入地址，由凭据表运行时注入。
/// </summary>
public static class NvrTemplateExpander
{
    public const int MaxChannelSpan = 64;

    /// <summary>常见厂商 RTSP 路径预设。</summary>
    public static IReadOnlyDictionary<string, NvrVendorPreset> VendorPresets { get; } =
        new Dictionary<string, NvrVendorPreset>(StringComparer.OrdinalIgnoreCase)
        {
            ["hikvision"] = new(
                "rtsp://{host}:{port}/Streaming/Channels/{ch}01",
                "rtsp://{host}:{port}/Streaming/Channels/{ch}02",
                "通道-{ch2}",
                "通道-{ch2}-子码流"),
            ["dahua"] = new(
                "rtsp://{host}:{port}/cam/realmonitor?channel={ch}&subtype=0",
                "rtsp://{host}:{port}/cam/realmonitor?channel={ch}&subtype=1",
                "通道-{ch2}",
                "通道-{ch2}-子码流"),
            ["uniview"] = new(
                "rtsp://{host}:{port}/unicast/c{ch}/s0/live",
                "rtsp://{host}:{port}/unicast/c{ch}/s1/live",
                "宇视-{ch2}",
                "宇视-{ch2}-子码流"),
            ["axis"] = new(
                "rtsp://{host}/axis-media/media.amp",
                "rtsp://{host}/axis-media/media.amp?resolution=640x480",
                "Axis",
                "Axis-子码流")
        };

    /// <summary>
    /// 按通道范围展开模板列表。
    /// </summary>
    /// <param name="streamType"><c>main</c> 或 <c>sub</c>（影响预设选择，自定义模板时仅作记录）。</param>
    public static IReadOnlyList<NvrExpandedChannel> Expand(
        string host,
        int port,
        string addressTemplate,
        string nameTemplate,
        int channelFrom,
        int channelTo,
        string streamType,
        string? prefix = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentException.ThrowIfNullOrWhiteSpace(addressTemplate);

        if (channelFrom > channelTo)
            throw new ArgumentException("channelFrom 不能大于 channelTo");

        var span = channelTo - channelFrom + 1;
        if (span > MaxChannelSpan)
            throw new ArgumentException($"通道数量不能超过 {MaxChannelSpan}");

        var hasChannelToken = addressTemplate.Contains("{ch}", StringComparison.Ordinal)
                              || addressTemplate.Contains("{ch2}", StringComparison.Ordinal);
        if (!hasChannelToken && channelFrom != channelTo)
            throw new ArgumentException("地址模板必须包含 {ch} 或 {ch2}（多通道时）");

        _ = streamType;

        var channels = new List<NvrExpandedChannel>(span);
        for (var ch = channelFrom; ch <= channelTo; ch++)
        {
            var name = ReplacePlaceholders(nameTemplate, host, port, ch, prefix);
            var address = ReplacePlaceholders(addressTemplate, host, port, ch, prefix);
            channels.Add(new NvrExpandedChannel(name, address));
        }

        return channels;
    }

    /// <summary>按厂商预设填充地址/名称模板后展开。</summary>
    public static IReadOnlyList<NvrExpandedChannel> ExpandVendor(
        string vendorId,
        string host,
        int port,
        int channelFrom,
        int channelTo,
        string streamType,
        string? prefix = null)
    {
        if (!VendorPresets.TryGetValue(vendorId, out var preset))
            throw new ArgumentException($"未知厂商预设：{vendorId}");

        var isSub = string.Equals(streamType, "sub", StringComparison.OrdinalIgnoreCase);
        var addressTemplate = isSub ? preset.AddressTemplateSub : preset.AddressTemplateMain;
        var nameTemplate = isSub ? preset.NameTemplateSub : preset.NameTemplateMain;
        return Expand(host, port, addressTemplate, nameTemplate, channelFrom, channelTo, streamType, prefix);
    }

    private static string ReplacePlaceholders(string template, string host, int port, int channel, string? prefix)
    {
        var ch2 = channel.ToString("D2", System.Globalization.CultureInfo.InvariantCulture);
        return template
            .Replace("{host}", host, StringComparison.Ordinal)
            .Replace("{port}", port.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{ch}", channel.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{ch2}", ch2, StringComparison.Ordinal)
            .Replace("{prefix}", prefix ?? "", StringComparison.Ordinal);
    }
}