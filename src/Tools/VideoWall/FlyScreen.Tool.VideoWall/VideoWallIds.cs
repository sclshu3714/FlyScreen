namespace FlyScreen.Tool.VideoWall;

/// <summary>视频墙工具标识。落地时同步到 <c>FlyScreen.Core</c> 的 <c>ToolIds</c>。</summary>
public static class VideoWallIds
{
    public const string ToolId = "video-wall";
    public const string IconKey = "tool-video-wall";
    public const string Group = "media";

    /// <summary>骨架阶段：后端未接线，卡片主操作只给出提示（接线后改为 true）。</summary>
    public static readonly bool BackendWired = false;
}
