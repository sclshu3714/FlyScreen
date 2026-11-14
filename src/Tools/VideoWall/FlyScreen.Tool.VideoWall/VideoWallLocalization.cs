using FlyScreen.Helpers.Localization;
using FlyScreen.ToolSdk;

namespace FlyScreen.Tool.VideoWall;

/// <summary>
/// 视频墙文案（工具自带，MUST NOT 往主程序 Loc 表加键）。
/// 骨架阶段只提供卡片与主要界面文案；接线后在 <c>Module.OnLoadedAsync</c> 调用 <c>Loc.RegisterPlugin</c> 注册。
/// </summary>
public sealed class VideoWallLocalization : IToolLocalization
{
    public static VideoWallLocalization Instance { get; } = new();

    static VideoWallLocalization() => EnsureRegistered();

    public string SourceId => VideoWallIds.ToolId;

    public static void EnsureRegistered() => Loc.RegisterPlugin(Instance);

    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Tables { get; } = Build();

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Build()
        => new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["zh-CN"] = Zh(),
            ["en-US"] = En()
        };

    private static Dictionary<string, string> Zh() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["Tool.VideoWall.Name"] = "视频墙",
        ["Tool.VideoWall.Summary"] = "按你添加的地址播放摄像头、NVR 与网络视频，支持九宫格与合流。",

        ["Tool.VideoWall.AddSource"] = "添加源",
        ["Tool.VideoWall.NvrImport"] = "NVR 批量",
        ["Tool.VideoWall.Scenes"] = "场景",
        ["Tool.VideoWall.Composite"] = "合流",
        ["Tool.VideoWall.FullWall"] = "整墙全屏",
        ["Tool.VideoWall.Stop"] = "停止",
        ["Tool.VideoWall.Reconnect"] = "重新连接",
        ["Tool.VideoWall.Test"] = "测试",
        ["Tool.VideoWall.CopyAddress"] = "复制地址",
        ["Tool.VideoWall.Editing"] = "编辑",

        ["Tool.VideoWall.EmptyTitle"] = "还没有视频源",
        ["Tool.VideoWall.EmptyHint"] = "添加一个地址，或先试试本地文件。",
        ["Tool.VideoWall.EmptyTile"] = "双击添加源",
        ["Tool.VideoWall.StatusConnecting"] = "连接中…",
        ["Tool.VideoWall.StatusBuffering"] = "缓冲中",
        ["Tool.VideoWall.StatusStalled"] = "卡顿",
        ["Tool.VideoWall.StatusReconnecting"] = "重连中",
        ["Tool.VideoWall.StatusStopped"] = "已停止",
        ["Tool.VideoWall.SoftwareDecode"] = "软解",

        ["Tool.VideoWall.ErrorNetDns"] = "地址或 DNS 不可用。",
        ["Tool.VideoWall.ErrorNetTimeout"] = "连接超时：网络不可达或端口不通。",
        ["Tool.VideoWall.ErrorNetReset"] = "连接被重置，将自动重连。",
        ["Tool.VideoWall.ErrorAuthRequired"] = "需要登录：检查用户名与密码。",
        ["Tool.VideoWall.ErrorAuthForbidden"] = "无权限或地址已过期（403）。",
        ["Tool.VideoWall.ErrorNotFound"] = "路径或通道不存在（404）。",
        ["Tool.VideoWall.ErrorUnsupported"] = "该编码或加密方式不受支持。",
        ["Tool.VideoWall.ErrorDecodeFailed"] = "解码失败：码流损坏或参数异常。",
        ["Tool.VideoWall.ErrorHardwareFallback"] = "硬解不可用，已回退软解。",
        ["Tool.VideoWall.ErrorOverBudget"] = "当前布局超出本机解码预算，建议降低布局或分辨率。",

        ["Tool.VideoWall.NotWired"] = "视频墙骨架：播放后端尚未接线。",

        // 源类型（SourceFormSchema.LabelKey）
        ["Tool.VideoWall.Type.Camera"] = "摄像头",
        ["Tool.VideoWall.Type.Nvr"] = "NVR 通道",
        ["Tool.VideoWall.Type.Network"] = "网络地址",
        ["Tool.VideoWall.Type.LocalFile"] = "本机文件",

        // 字段标签
        ["Tool.VideoWall.Field.Name"] = "名称",
        ["Tool.VideoWall.Field.NamePlaceholder"] = "例如：前台-01",
        ["Tool.VideoWall.Field.VendorPreset"] = "厂商预设",
        ["Tool.VideoWall.Field.Address"] = "地址",
        ["Tool.VideoWall.Field.Transport"] = "传输方式",
        ["Tool.VideoWall.Field.Auth"] = "认证",
        ["Tool.VideoWall.Field.Username"] = "用户名",
        ["Tool.VideoWall.Field.Password"] = "密码",
        ["Tool.VideoWall.Field.Buffer"] = "缓冲（毫秒）",
        ["Tool.VideoWall.Field.Decode"] = "解码",
        ["Tool.VideoWall.Field.Group"] = "分组",
        ["Tool.VideoWall.Field.Note"] = "备注",
        ["Tool.VideoWall.Field.Host"] = "主机",
        ["Tool.VideoWall.Field.Port"] = "端口",
        ["Tool.VideoWall.Field.StreamType"] = "码流",
        ["Tool.VideoWall.Field.ChannelFrom"] = "起始通道",
        ["Tool.VideoWall.Field.ChannelTo"] = "结束通道",
        ["Tool.VideoWall.Field.NameTemplate"] = "命名模板",
        ["Tool.VideoWall.Field.AddressTemplate"] = "地址模板",
        ["Tool.VideoWall.Field.Protocol"] = "协议",
        ["Tool.VideoWall.Field.Headers"] = "自定义 Header",
        ["Tool.VideoWall.Field.FilePath"] = "文件",
        ["Tool.VideoWall.Field.Loop"] = "循环播放",

        // 字段提示
        ["Tool.VideoWall.Hint.NameTemplate"] = "可用 {ch} 通道号、{ch2} 两位通道号。",
        ["Tool.VideoWall.Hint.AddressTemplate"] = "可用 {host} {port} {user} {pass} {ch} {ch2}。",
        ["Tool.VideoWall.Hint.Headers"] = "最多 8 条，例如 Referer / User-Agent；不要在 Header 里写密码。",
        ["Tool.VideoWall.Hint.BufferRtspOnly"] = "仅 RTSP 生效；网络差时调大。",
        ["Tool.VideoWall.Hint.TransportRtspOnly"] = "TCP 更稳，UDP 延迟更低；仅 RTSP 生效。",

        // 选项
        ["Tool.VideoWall.Auth.None"] = "无",
        ["Tool.VideoWall.Auth.Basic"] = "Basic",
        ["Tool.VideoWall.Auth.Digest"] = "Digest",
        ["Tool.VideoWall.Decode.Auto"] = "自动",
        ["Tool.VideoWall.Decode.Hardware"] = "优先硬解",
        ["Tool.VideoWall.Decode.Software"] = "强制软解",
        ["Tool.VideoWall.Vendor.Generic"] = "通用（手填地址）",
        ["Tool.VideoWall.Vendor.Hikvision"] = "海康",
        ["Tool.VideoWall.Vendor.Dahua"] = "大华",
        ["Tool.VideoWall.Vendor.Uniview"] = "宇视",
        ["Tool.VideoWall.Vendor.Axis"] = "Axis",
        ["Tool.VideoWall.Transport.Tcp"] = "TCP",
        ["Tool.VideoWall.Transport.Udp"] = "UDP",
        ["Tool.VideoWall.Stream.Main"] = "主码流",
        ["Tool.VideoWall.Stream.Sub"] = "子码流",
        ["Tool.VideoWall.Protocol.Auto"] = "自动识别",
        ["Tool.VideoWall.Protocol.Hls"] = "HLS",
        ["Tool.VideoWall.Protocol.Dash"] = "DASH",
        ["Tool.VideoWall.Protocol.Rtmp"] = "RTMP",
        ["Tool.VideoWall.Protocol.Srt"] = "SRT",
        ["Tool.VideoWall.Protocol.Http"] = "HTTP 直链",

        // 源管理页
        ["Tool.VideoWall.ViewList"] = "列表",
        ["Tool.VideoWall.ViewCard"] = "卡片",
        ["Tool.VideoWall.ViewCanvas"] = "画布",
        ["Tool.VideoWall.Search"] = "搜索名称或地址",
        ["Tool.VideoWall.Save"] = "保存",
        ["Tool.VideoWall.Cancel"] = "取消",
        ["Tool.VideoWall.Add"] = "添加",
        ["Tool.VideoWall.Delete"] = "删除",
        ["Tool.VideoWall.SelectAll"] = "全选",
        ["Tool.VideoWall.ColName"] = "名称",
        ["Tool.VideoWall.ColKind"] = "类型",
        ["Tool.VideoWall.ColStatus"] = "状态",
        ["Tool.VideoWall.ColActions"] = "操作",
        ["Tool.VideoWall.Back"] = "返回",
        ["Tool.VideoWall.Play"] = "播放",
        ["Tool.VideoWall.Pause"] = "暂停",
        ["Tool.VideoWall.Resume"] = "继续",
        ["Tool.VideoWall.SeekBack"] = "快退 10 秒",
        ["Tool.VideoWall.SeekForward"] = "快进 10 秒",
        ["Tool.VideoWall.Mute"] = "静音",
        ["Tool.VideoWall.Unmute"] = "取消静音",
        ["Tool.VideoWall.Volume"] = "音量",
        ["Tool.VideoWall.Fullscreen"] = "全屏",
        ["Tool.VideoWall.FullscreenExit"] = "退出全屏",
        ["Tool.VideoWall.SelectType"] = "选择源类型",
        ["Tool.VideoWall.Capturing"] = "正在抓取首帧…",
        ["Tool.VideoWall.CaptureFailed"] = "抓帧失败：已保存为「未验证」，可稍后重试。",

        ["Tool.VideoWall.Sources"] = "源",
        ["Tool.VideoWall.Wall"] = "墙",
        ["Tool.VideoWall.Layout"] = "布局",
        ["Tool.VideoWall.GroupAll"] = "全部分组",
        ["Tool.VideoWall.BackendUnavailable"] = "播放后端不可用（LibVLC 未就绪）；可先添加源，画面嵌入请用 WPF 桌面版。",
        ["Tool.VideoWall.DesktopOnly"] = "视频墙完整画面嵌入请用 WPF 桌面版；当前宿主仍可管理源与播放控制。",
        ["Tool.VideoWall.ConfirmDelete"] = "确定删除源「{0}」？",
        ["Tool.VideoWall.VideoSurfaceTodo"] = "Avalonia 视频表面尚未接线；音频/状态由后端播放。"
    };

    private static Dictionary<string, string> En() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["Tool.VideoWall.Name"] = "Video wall",
        ["Tool.VideoWall.Summary"] = "Play cameras, NVR channels and network streams from addresses you add, with a 3×3 grid and composite view.",

        ["Tool.VideoWall.AddSource"] = "Add source",
        ["Tool.VideoWall.NvrImport"] = "Import NVR",
        ["Tool.VideoWall.Scenes"] = "Scenes",
        ["Tool.VideoWall.Composite"] = "Composite",
        ["Tool.VideoWall.FullWall"] = "Full wall",
        ["Tool.VideoWall.Stop"] = "Stop",
        ["Tool.VideoWall.Reconnect"] = "Reconnect",
        ["Tool.VideoWall.Test"] = "Test",
        ["Tool.VideoWall.CopyAddress"] = "Copy address",
        ["Tool.VideoWall.Editing"] = "Edit",

        ["Tool.VideoWall.EmptyTitle"] = "No video sources yet",
        ["Tool.VideoWall.EmptyHint"] = "Add an address, or open a local file first.",
        ["Tool.VideoWall.EmptyTile"] = "Double-click to add",
        ["Tool.VideoWall.StatusConnecting"] = "Connecting…",
        ["Tool.VideoWall.StatusBuffering"] = "Buffering",
        ["Tool.VideoWall.StatusStalled"] = "Stalled",
        ["Tool.VideoWall.StatusReconnecting"] = "Reconnecting",
        ["Tool.VideoWall.StatusStopped"] = "Stopped",
        ["Tool.VideoWall.SoftwareDecode"] = "SW",

        ["Tool.VideoWall.ErrorNetDns"] = "Address or DNS is unavailable.",
        ["Tool.VideoWall.ErrorNetTimeout"] = "Connection timed out: host unreachable or port closed.",
        ["Tool.VideoWall.ErrorNetReset"] = "Connection reset; reconnecting.",
        ["Tool.VideoWall.ErrorAuthRequired"] = "Sign-in required: check the username and password.",
        ["Tool.VideoWall.ErrorAuthForbidden"] = "Forbidden or expired address (403).",
        ["Tool.VideoWall.ErrorNotFound"] = "Path or channel not found (404).",
        ["Tool.VideoWall.ErrorUnsupported"] = "This codec or encryption is not supported.",
        ["Tool.VideoWall.ErrorDecodeFailed"] = "Decode failed: corrupt stream or invalid parameters.",
        ["Tool.VideoWall.ErrorHardwareFallback"] = "Hardware decode unavailable; fell back to software.",
        ["Tool.VideoWall.ErrorOverBudget"] = "This layout exceeds the local decode budget; reduce tiles or resolution.",

        ["Tool.VideoWall.NotWired"] = "Video wall skeleton: playback backend is not wired yet.",

        // Source types
        ["Tool.VideoWall.Type.Camera"] = "Camera",
        ["Tool.VideoWall.Type.Nvr"] = "NVR channels",
        ["Tool.VideoWall.Type.Network"] = "Network address",
        ["Tool.VideoWall.Type.LocalFile"] = "Local file",

        // Field labels
        ["Tool.VideoWall.Field.Name"] = "Name",
        ["Tool.VideoWall.Field.NamePlaceholder"] = "e.g. Front desk 01",
        ["Tool.VideoWall.Field.VendorPreset"] = "Vendor preset",
        ["Tool.VideoWall.Field.Address"] = "Address",
        ["Tool.VideoWall.Field.Transport"] = "Transport",
        ["Tool.VideoWall.Field.Auth"] = "Authentication",
        ["Tool.VideoWall.Field.Username"] = "Username",
        ["Tool.VideoWall.Field.Password"] = "Password",
        ["Tool.VideoWall.Field.Buffer"] = "Buffer (ms)",
        ["Tool.VideoWall.Field.Decode"] = "Decode",
        ["Tool.VideoWall.Field.Group"] = "Group",
        ["Tool.VideoWall.Field.Note"] = "Note",
        ["Tool.VideoWall.Field.Host"] = "Host",
        ["Tool.VideoWall.Field.Port"] = "Port",
        ["Tool.VideoWall.Field.StreamType"] = "Stream",
        ["Tool.VideoWall.Field.ChannelFrom"] = "First channel",
        ["Tool.VideoWall.Field.ChannelTo"] = "Last channel",
        ["Tool.VideoWall.Field.NameTemplate"] = "Name template",
        ["Tool.VideoWall.Field.AddressTemplate"] = "Address template",
        ["Tool.VideoWall.Field.Protocol"] = "Protocol",
        ["Tool.VideoWall.Field.Headers"] = "Custom headers",
        ["Tool.VideoWall.Field.FilePath"] = "File",
        ["Tool.VideoWall.Field.Loop"] = "Loop",

        // Field hints
        ["Tool.VideoWall.Hint.NameTemplate"] = "{ch} = channel number, {ch2} = zero-padded channel.",
        ["Tool.VideoWall.Hint.AddressTemplate"] = "Available: {host} {port} {user} {pass} {ch} {ch2}.",
        ["Tool.VideoWall.Hint.Headers"] = "Up to 8 entries, e.g. Referer / User-Agent; never put a password in a header.",
        ["Tool.VideoWall.Hint.BufferRtspOnly"] = "RTSP only; increase it on a flaky network.",
        ["Tool.VideoWall.Hint.TransportRtspOnly"] = "TCP is steadier, UDP has lower latency; RTSP only.",

        // Options
        ["Tool.VideoWall.Auth.None"] = "None",
        ["Tool.VideoWall.Auth.Basic"] = "Basic",
        ["Tool.VideoWall.Auth.Digest"] = "Digest",
        ["Tool.VideoWall.Decode.Auto"] = "Auto",
        ["Tool.VideoWall.Decode.Hardware"] = "Prefer hardware",
        ["Tool.VideoWall.Decode.Software"] = "Force software",
        ["Tool.VideoWall.Vendor.Generic"] = "Generic (enter address)",
        ["Tool.VideoWall.Vendor.Hikvision"] = "Hikvision",
        ["Tool.VideoWall.Vendor.Dahua"] = "Dahua",
        ["Tool.VideoWall.Vendor.Uniview"] = "Uniview",
        ["Tool.VideoWall.Vendor.Axis"] = "Axis",
        ["Tool.VideoWall.Transport.Tcp"] = "TCP",
        ["Tool.VideoWall.Transport.Udp"] = "UDP",
        ["Tool.VideoWall.Stream.Main"] = "Main stream",
        ["Tool.VideoWall.Stream.Sub"] = "Sub stream",
        ["Tool.VideoWall.Protocol.Auto"] = "Auto detect",
        ["Tool.VideoWall.Protocol.Hls"] = "HLS",
        ["Tool.VideoWall.Protocol.Dash"] = "DASH",
        ["Tool.VideoWall.Protocol.Rtmp"] = "RTMP",
        ["Tool.VideoWall.Protocol.Srt"] = "SRT",
        ["Tool.VideoWall.Protocol.Http"] = "HTTP direct",

        // Source management page
        ["Tool.VideoWall.ViewList"] = "List",
        ["Tool.VideoWall.ViewCard"] = "Cards",
        ["Tool.VideoWall.ViewCanvas"] = "Canvas",
        ["Tool.VideoWall.Search"] = "Search name or address",
        ["Tool.VideoWall.Save"] = "Save",
        ["Tool.VideoWall.Cancel"] = "Cancel",
        ["Tool.VideoWall.Add"] = "Add",
        ["Tool.VideoWall.Delete"] = "Delete",
        ["Tool.VideoWall.SelectAll"] = "Select all",
        ["Tool.VideoWall.ColName"] = "Name",
        ["Tool.VideoWall.ColKind"] = "Type",
        ["Tool.VideoWall.ColStatus"] = "Status",
        ["Tool.VideoWall.ColActions"] = "Actions",
        ["Tool.VideoWall.Back"] = "Back",
        ["Tool.VideoWall.Play"] = "Play",
        ["Tool.VideoWall.Pause"] = "Pause",
        ["Tool.VideoWall.Resume"] = "Resume",
        ["Tool.VideoWall.SeekBack"] = "Rewind 10s",
        ["Tool.VideoWall.SeekForward"] = "Forward 10s",
        ["Tool.VideoWall.Mute"] = "Mute",
        ["Tool.VideoWall.Unmute"] = "Unmute",
        ["Tool.VideoWall.Volume"] = "Volume",
        ["Tool.VideoWall.Fullscreen"] = "Fullscreen",
        ["Tool.VideoWall.FullscreenExit"] = "Exit fullscreen",
        ["Tool.VideoWall.SelectType"] = "Choose a source type",
        ["Tool.VideoWall.Capturing"] = "Capturing the first frame…",
        ["Tool.VideoWall.CaptureFailed"] = "Capture failed: saved as unverified; you can retry later.",

        ["Tool.VideoWall.Sources"] = "Sources",
        ["Tool.VideoWall.Wall"] = "Wall",
        ["Tool.VideoWall.Layout"] = "Layout",
        ["Tool.VideoWall.GroupAll"] = "All groups",
        ["Tool.VideoWall.BackendUnavailable"] = "Playback backend unavailable (LibVLC not ready). You can still manage sources; use the WPF desktop app for embedded video.",
        ["Tool.VideoWall.DesktopOnly"] = "Full embedded video is on the WPF desktop app; this host still supports source management and playback control.",
        ["Tool.VideoWall.ConfirmDelete"] = "Delete source \"{0}\"?",
        ["Tool.VideoWall.VideoSurfaceTodo"] = "Avalonia video surface is not wired yet; playback runs without a visible surface."
    };
}
