using FlyScreen.Tool.VideoWall.Contracts;

namespace FlyScreen.Tool.VideoWall.Sources;

/// <summary>表单控件类型（UI 卫星据此选控件，业务层不引用任何 UI 类型）。</summary>
public enum SourceFieldKind
{
    Text,
    Password,
    Number,
    Select,
    Radio,
    Checkbox,
    File,
    KeyValueList,
    MultilineText
}

public sealed record SourceFieldOption(string Value, string LabelKey);

/// <summary>
/// 单个字段规格。<see cref="VisibleWhen"/> 形如 <c>"auth!=none"</c> / <c>"protocol==rtsp"</c>，
/// 由 <see cref="SourceFieldVisibility.IsVisible"/> 求值；null 表示始终可见。
/// </summary>
public sealed record SourceFieldSpec
{
    public required string Key { get; init; }
    public required string LabelKey { get; init; }
    public required SourceFieldKind Kind { get; init; }
    public bool Required { get; init; }
    public string? DefaultValue { get; init; }
    public string? Placeholder { get; init; }

    /// <summary>字段下方说明（可选）。</summary>
    public string? HintKey { get; init; }

    public IReadOnlyList<SourceFieldOption> Options { get; init; } = [];

    public string? VisibleWhen { get; init; }

    public int? Min { get; init; }
    public int? Max { get; init; }
}

/// <summary>某一源类型的表单定义（对应 docs/video-wall-prototype.md §3.3 的字段矩阵）。</summary>
public sealed record SourceTypeSpec
{
    public required VideoSourceKind Kind { get; init; }
    public required string LabelKey { get; init; }
    public required string IconKey { get; init; }

    /// <summary>是否已实现；未实现的类型 MUST NOT 出现在「类型选择」里。</summary>
    public bool Implemented { get; init; }

    public required IReadOnlyList<SourceFieldSpec> Fields { get; init; }
}

/// <summary>
/// 按源类型定义表单字段。字段矩阵的唯一实现位置：UI 只渲染，不再写分支判断。
/// 对应文档 docs/video-wall-prototype.md §3.3。
/// </summary>
public static class SourceFormSchema
{
    public const string AuthNone = "none";
    public const string AuthBasic = "basic";
    public const string AuthDigest = "digest";

    private static readonly IReadOnlyList<SourceFieldOption> AuthOptions =
    [
        new(AuthNone, "Tool.VideoWall.Auth.None"),
        new(AuthBasic, "Tool.VideoWall.Auth.Basic"),
        new(AuthDigest, "Tool.VideoWall.Auth.Digest")
    ];

    private static readonly IReadOnlyList<SourceFieldOption> DecodeOptions =
    [
        new("auto", "Tool.VideoWall.Decode.Auto"),
        new("hardware", "Tool.VideoWall.Decode.Hardware"),
        new("software", "Tool.VideoWall.Decode.Software")
    ];

    private static readonly IReadOnlyList<SourceFieldOption> VendorOptions =
    [
        new("generic", "Tool.VideoWall.Vendor.Generic"),
        new("hikvision", "Tool.VideoWall.Vendor.Hikvision"),
        new("dahua", "Tool.VideoWall.Vendor.Dahua"),
        new("uniview", "Tool.VideoWall.Vendor.Uniview"),
        new("axis", "Tool.VideoWall.Vendor.Axis")
    ];

    private static readonly IReadOnlyList<SourceFieldOption> TransportOptions =
    [
        new("tcp", "Tool.VideoWall.Transport.Tcp"),
        new("udp", "Tool.VideoWall.Transport.Udp")
    ];

    private static readonly IReadOnlyList<SourceFieldOption> StreamTypeOptions =
    [
        new("main", "Tool.VideoWall.Stream.Main"),
        new("sub", "Tool.VideoWall.Stream.Sub")
    ];

    private static readonly IReadOnlyList<SourceFieldOption> ProtocolOptions =
    [
        new("auto", "Tool.VideoWall.Protocol.Auto"),
        new("hls", "Tool.VideoWall.Protocol.Hls"),
        new("dash", "Tool.VideoWall.Protocol.Dash"),
        new("rtmp", "Tool.VideoWall.Protocol.Rtmp"),
        new("srt", "Tool.VideoWall.Protocol.Srt"),
        new("http", "Tool.VideoWall.Protocol.Http")
    ];

    private static readonly SourceFieldSpec NameField = new()
    {
        Key = "name",
        LabelKey = "Tool.VideoWall.Field.Name",
        Kind = SourceFieldKind.Text,
        Required = true,
        Placeholder = "Tool.VideoWall.Field.NamePlaceholder"
    };

    private static readonly SourceFieldSpec GroupField = new()
    {
        Key = "groupId",
        LabelKey = "Tool.VideoWall.Field.Group",
        Kind = SourceFieldKind.Select
    };

    private static readonly SourceFieldSpec DecodeField = new()
    {
        Key = "decode",
        LabelKey = "Tool.VideoWall.Field.Decode",
        Kind = SourceFieldKind.Select,
        DefaultValue = "auto",
        Options = DecodeOptions
    };

    private static readonly SourceFieldSpec BufferField = new()
    {
        Key = "bufferMs",
        LabelKey = "Tool.VideoWall.Field.Buffer",
        Kind = SourceFieldKind.Number,
        DefaultValue = "800",
        Min = 100,
        Max = 5000,
        HintKey = "Tool.VideoWall.Hint.BufferRtspOnly"
    };

    private static readonly SourceFieldSpec NoteField = new()
    {
        Key = "note",
        LabelKey = "Tool.VideoWall.Field.Note",
        Kind = SourceFieldKind.Text
    };

    public static IReadOnlyList<SourceTypeSpec> Types { get; } = Build();

    public static SourceTypeSpec? For(VideoSourceKind kind)
        => Types.FirstOrDefault(t => t.Kind == kind);

    private static IReadOnlyList<SourceTypeSpec> Build() =>
    [
        Camera(),
        Nvr(),
        Network(),
        LocalFile()
    ];

    // A. 摄像头：地址 + 传输 + 认证（条件字段）
    private static SourceTypeSpec Camera() => new()
    {
        Kind = VideoSourceKind.Camera,
        LabelKey = "Tool.VideoWall.Type.Camera",
        IconKey = "tool-video-wall-camera",
        Implemented = true,
        Fields =
        [
            NameField,
            new SourceFieldSpec
            {
                Key = "vendorPreset",
                LabelKey = "Tool.VideoWall.Field.VendorPreset",
                Kind = SourceFieldKind.Select,
                DefaultValue = "generic",
                Options = VendorOptions
            },
            new SourceFieldSpec
            {
                Key = "address",
                LabelKey = "Tool.VideoWall.Field.Address",
                Kind = SourceFieldKind.Text,
                Required = true,
                Placeholder = "rtsp://192.168.1.64:554/Streaming/Channels/101"
            },
            new SourceFieldSpec
            {
                Key = "transport",
                LabelKey = "Tool.VideoWall.Field.Transport",
                Kind = SourceFieldKind.Radio,
                DefaultValue = "tcp",
                Options = TransportOptions,
                HintKey = "Tool.VideoWall.Hint.TransportRtspOnly"
            },
            new SourceFieldSpec
            {
                Key = "auth",
                LabelKey = "Tool.VideoWall.Field.Auth",
                Kind = SourceFieldKind.Select,
                DefaultValue = AuthNone,
                Options = AuthOptions
            },
            new SourceFieldSpec
            {
                Key = "username",
                LabelKey = "Tool.VideoWall.Field.Username",
                Kind = SourceFieldKind.Text,
                Required = true,
                DefaultValue = "admin",
                VisibleWhen = "auth!=none"
            },
            new SourceFieldSpec
            {
                Key = "password",
                LabelKey = "Tool.VideoWall.Field.Password",
                Kind = SourceFieldKind.Password,
                Required = true,
                VisibleWhen = "auth!=none"
            },
            BufferField,
            DecodeField,
            GroupField,
            NoteField
        ]
    };

    // B. NVR：主机 + 通道范围 + 模板，一次生成 N 条通道记录
    private static SourceTypeSpec Nvr() => new()
    {
        Kind = VideoSourceKind.NvrChannel,
        LabelKey = "Tool.VideoWall.Type.Nvr",
        IconKey = "tool-video-wall-nvr",
        Implemented = true,
        Fields =
        [
            new SourceFieldSpec
            {
                Key = "host",
                LabelKey = "Tool.VideoWall.Field.Host",
                Kind = SourceFieldKind.Text,
                Required = true,
                Placeholder = "192.168.1.100"
            },
            new SourceFieldSpec
            {
                Key = "port",
                LabelKey = "Tool.VideoWall.Field.Port",
                Kind = SourceFieldKind.Number,
                DefaultValue = "554",
                Min = 1,
                Max = 65535
            },
            new SourceFieldSpec
            {
                Key = "auth",
                LabelKey = "Tool.VideoWall.Field.Auth",
                Kind = SourceFieldKind.Select,
                DefaultValue = AuthDigest,
                Options = AuthOptions
            },
            new SourceFieldSpec
            {
                Key = "username",
                LabelKey = "Tool.VideoWall.Field.Username",
                Kind = SourceFieldKind.Text,
                Required = true,
                DefaultValue = "admin",
                VisibleWhen = "auth!=none"
            },
            new SourceFieldSpec
            {
                Key = "password",
                LabelKey = "Tool.VideoWall.Field.Password",
                Kind = SourceFieldKind.Password,
                Required = true,
                VisibleWhen = "auth!=none"
            },
            new SourceFieldSpec
            {
                Key = "streamType",
                LabelKey = "Tool.VideoWall.Field.StreamType",
                Kind = SourceFieldKind.Radio,
                DefaultValue = "main",
                Options = StreamTypeOptions
            },
            new SourceFieldSpec
            {
                Key = "channelFrom",
                LabelKey = "Tool.VideoWall.Field.ChannelFrom",
                Kind = SourceFieldKind.Number,
                Required = true,
                DefaultValue = "1",
                Min = 1,
                Max = 256
            },
            new SourceFieldSpec
            {
                Key = "channelTo",
                LabelKey = "Tool.VideoWall.Field.ChannelTo",
                Kind = SourceFieldKind.Number,
                Required = true,
                DefaultValue = "16",
                Min = 1,
                Max = 256
            },
            new SourceFieldSpec
            {
                Key = "nameTemplate",
                LabelKey = "Tool.VideoWall.Field.NameTemplate",
                Kind = SourceFieldKind.Text,
                DefaultValue = "通道-{ch2}",
                HintKey = "Tool.VideoWall.Hint.NameTemplate"
            },
            new SourceFieldSpec
            {
                Key = "addressTemplate",
                LabelKey = "Tool.VideoWall.Field.AddressTemplate",
                Kind = SourceFieldKind.Text,
                Required = true,
                Placeholder = "rtsp://{host}:{port}/Streaming/Channels/{ch}01",
                HintKey = "Tool.VideoWall.Hint.AddressTemplate"
            },
            BufferField,
            DecodeField,
            GroupField
        ]
    };

    // C. 网络地址：协议覆盖 + 自定义 Header（条件字段）
    private static SourceTypeSpec Network() => new()
    {
        Kind = VideoSourceKind.NetworkUrl,
        LabelKey = "Tool.VideoWall.Type.Network",
        IconKey = "tool-video-wall-network",
        Implemented = true,
        Fields =
        [
            NameField,
            new SourceFieldSpec
            {
                Key = "address",
                LabelKey = "Tool.VideoWall.Field.Address",
                Kind = SourceFieldKind.Text,
                Required = true,
                Placeholder = "https://cdn.example.com/live.m3u8"
            },
            new SourceFieldSpec
            {
                Key = "protocol",
                LabelKey = "Tool.VideoWall.Field.Protocol",
                Kind = SourceFieldKind.Select,
                DefaultValue = "auto",
                Options = ProtocolOptions
            },
            new SourceFieldSpec
            {
                Key = "auth",
                LabelKey = "Tool.VideoWall.Field.Auth",
                Kind = SourceFieldKind.Select,
                DefaultValue = AuthNone,
                Options = AuthOptions,
                VisibleWhen = "protocol==http"
            },
            new SourceFieldSpec
            {
                Key = "username",
                LabelKey = "Tool.VideoWall.Field.Username",
                Kind = SourceFieldKind.Text,
                Required = true,
                VisibleWhen = "auth!=none"
            },
            new SourceFieldSpec
            {
                Key = "password",
                LabelKey = "Tool.VideoWall.Field.Password",
                Kind = SourceFieldKind.Password,
                Required = true,
                VisibleWhen = "auth!=none"
            },
            new SourceFieldSpec
            {
                Key = "headers",
                LabelKey = "Tool.VideoWall.Field.Headers",
                Kind = SourceFieldKind.KeyValueList,
                HintKey = "Tool.VideoWall.Hint.Headers"
            },
            DecodeField,
            GroupField
        ]
    };

    // D. 本机文件：文件选择器 + 循环
    private static SourceTypeSpec LocalFile() => new()
    {
        Kind = VideoSourceKind.LocalFile,
        LabelKey = "Tool.VideoWall.Type.LocalFile",
        IconKey = "tool-video-wall-file",
        Implemented = true,
        Fields =
        [
            new SourceFieldSpec
            {
                Key = "path",
                LabelKey = "Tool.VideoWall.Field.FilePath",
                Kind = SourceFieldKind.File,
                Required = true
            },
            NameField,
            new SourceFieldSpec
            {
                Key = "loop",
                LabelKey = "Tool.VideoWall.Field.Loop",
                Kind = SourceFieldKind.Checkbox,
                DefaultValue = "false"
            },
            DecodeField,
            GroupField
        ]
    };
}

/// <summary>条件显隐求值：只支持 <c>key==value</c> 与 <c>key!=value</c>，足够覆盖本工具表单。</summary>
public static class SourceFieldVisibility
{
    public static bool IsVisible(SourceFieldSpec field, IReadOnlyDictionary<string, string?> values)
        => field.VisibleWhen is null || Evaluate(field.VisibleWhen, values);

    /// <summary>按当前取值过滤出可见字段（UI 直接据此渲染）。</summary>
    public static IReadOnlyList<SourceFieldSpec> VisibleFields(
        SourceTypeSpec spec, IReadOnlyDictionary<string, string?> values)
        => spec.Fields.Where(f => IsVisible(f, values)).ToList();

    /// <summary>求值条件表达式；供 UI 卫星做条件显隐，也便于单测。</summary>
    public static bool Evaluate(string expression, IReadOnlyDictionary<string, string?> values)
    {
        var opIndex = expression.IndexOf("==", StringComparison.Ordinal);
        var negate = false;
        if (opIndex < 0)
        {
            opIndex = expression.IndexOf("!=", StringComparison.Ordinal);
            negate = opIndex >= 0;
        }

        if (opIndex <= 0) return true;

        var key = expression[..opIndex].Trim();
        var expected = expression[(opIndex + 2)..].Trim();
        var actual = values.TryGetValue(key, out var v) ? v : null;

        var equal = string.Equals(actual ?? "", expected, StringComparison.OrdinalIgnoreCase);
        return negate ? !equal : equal;
    }
}
