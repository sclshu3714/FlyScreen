using FlyScreen.Services.Tools;
using FlyScreen.ToolSdk;

namespace FlyScreen.Tool.VideoWall;

/// <summary>视频墙卡片图标（嵌入本程序集）。</summary>
public sealed class VideoWallIcons : IToolIconContribution
{
    public static VideoWallIcons Instance { get; } = new();

    public string IconKey => VideoWallIds.IconKey;

    public Stream? OpenBitmap()
    {
        const string name = "FlyScreen.Tool.VideoWall.Icons.tool-video-wall.png";
        return typeof(VideoWallIcons).Assembly.GetManifestResourceStream(name);
    }

    public static void EnsureRegistered() => ToolIconRegistry.Register(Instance);
}