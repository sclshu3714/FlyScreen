using FlyScreen.Models;

namespace FlyScreen.Services.ScreenExtend;

/// <summary>设备管理「投影到电脑」对话框结果。</summary>
public sealed class RemoteProjectChoice
{
    public string DeviceId { get; init; } = "";
    public int Width { get; init; }
    public int Height { get; init; }
    /// <summary>采集帧率；UI 标为码率（JPEG 无独立 Mbps 控流，帧率主导带宽）。</summary>
    public int Fps { get; init; } = ProjectionDisplayPresets.DefaultFps;
    public int JpegQuality { get; init; } = ProjectionDisplayPresets.DefaultJpegQuality;

    public static RemoteProjectChoice FromSettings(AppSettings settings, string deviceId)
        => new()
        {
            DeviceId = deviceId,
            Width = settings.ScreenExtendWidth,
            Height = settings.ScreenExtendHeight,
            Fps = ProjectionDisplayPresets.RememberedFps(settings),
            JpegQuality = ProjectionDisplayPresets.RememberedJpegQuality(settings)
        };

    public void WriteRemembered(AppSettings settings)
    {
        settings.ScreenExtendWidth = Width > 0 ? Width : 0;
        settings.ScreenExtendHeight = Height > 0 ? Height : 0;
        settings.ScreenExtendFps = ProjectionDisplayPresets.ClampFps(Fps > 0 ? Fps : ProjectionDisplayPresets.DefaultFps);
        settings.ScreenExtendJpegQuality = ProjectionDisplayPresets.ClampJpegQuality(JpegQuality);
    }
}

/// <summary>从采集时间戳估算端到端延迟（毫秒）。</summary>
public static class ProjectionLatency
{
    /// <summary>
    /// 用帧上的 UTC 毫秒与本机 UTC 求差。时钟偏差过大时返回 null。
    /// </summary>
    public static int? ComputeMs(long capturedUnixMs)
    {
        if (capturedUnixMs <= 0) return null;
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var d = (int)(now - capturedUnixMs);
        if (d is < 0 or > 15_000) return null;
        return d;
    }

    /// <summary>指数平滑，减轻抖动。</summary>
    public static int Smooth(int? sampleMs, ref double ema, double alpha = 0.35)
    {
        if (sampleMs is not int ms) return ema > 0 ? (int)Math.Round(ema) : 0;
        if (ema <= 0) ema = ms;
        else ema = ema * (1 - alpha) + ms * alpha;
        return (int)Math.Round(ema);
    }
}
