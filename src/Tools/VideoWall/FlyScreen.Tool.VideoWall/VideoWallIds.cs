namespace FlyScreen.Tool.VideoWall;

/// <summary>视频墙工具标识。落地时同步到 <c>FlyScreen.Core</c> 的 <c>ToolIds</c>。</summary>
public static class VideoWallIds
{
    public const string ToolId = "video-wall";
    public const string IconKey = "tool-video-wall";
    public const string Group = "media";

    /// <summary>后端与 <see cref="Wall.VideoWallService"/> 已接线；VLC 缺失时 UI 仍可打开并显示横幅。</summary>
    public static readonly bool BackendWired = true;
}
