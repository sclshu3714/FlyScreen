using FlyScreen.ToolSdk;

namespace FlyScreen.Tool.VideoWall;

/// <summary>
/// 视频墙文案（工具自带，MUST NOT 往主程序 Loc 表加键）。
/// 骨架阶段只提供卡片与主要界面文案；接线后在 <c>Module.OnLoadedAsync</c> 调用 <c>Loc.RegisterPlugin</c> 注册。
/// </summary>
public sealed class VideoWallLocalization : IToolLocalization
{
    public static VideoWallLocalization Instance { get; } = new();

    public string SourceId => VideoWallIds.ToolId;

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
        ["Tool.VideoWall.EmptyTile"] = "双击添加",
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

        ["Tool.VideoWall.NotWired"] = "视频墙骨架：播放后端尚未接线。"
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

        ["Tool.VideoWall.NotWired"] = "Video wall skeleton: playback backend is not wired yet."
    };
}
