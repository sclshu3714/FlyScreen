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
    public const int GoodMs = 100;
    public const int BadMs = 500;

    /// <summary>信号格：1–4；颜色档：0 未知灰 / 1 绿 / 2 黄 / 3 红。</summary>
    public readonly record struct Signal(int Bars, int Tier);

    /// <summary>
    /// 用帧上的 UTC 毫秒估算单向延迟。两端时钟有偏差时用历史最小值作基线校正。
    /// </summary>
    public static int? ComputeMs(long capturedUnixMs, ref long skewBaselineMs)
    {
        if (capturedUnixMs <= 0) return null;
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var raw = now - capturedUnixMs;
        // 离谱样本（时钟差过大 / 坏帧）丢掉
        if (raw is < -120_000 or > 120_000) return null;

        // 基线取历史最小 raw（≈ -时钟偏差 + 最小链路时延）
        if (skewBaselineMs == long.MinValue || raw < skewBaselineMs)
            skewBaselineMs = raw;
        else if (raw - skewBaselineMs > 30_000)
        {
            // 基线过旧（对端时钟跳变），缓慢跟上
            skewBaselineMs += (raw - skewBaselineMs) / 8;
        }

        var oneWay = raw - skewBaselineMs;
        if (oneWay < 0) oneWay = 0;
        if (oneWay > 15_000) return null;
        // 至少显示 1ms，避免 0 被当成「无数据」画成灰格
        if (oneWay == 0) oneWay = 1;
        return (int)oneWay;
    }

    /// <summary>兼容旧调用。</summary>
    public static int? ComputeMs(long capturedUnixMs)
    {
        var skew = long.MinValue;
        return ComputeMs(capturedUnixMs, ref skew);
    }

    /// <summary>指数平滑，减轻抖动。</summary>
    public static int Smooth(int? sampleMs, ref double ema, double alpha = 0.35)
    {
        if (sampleMs is not int ms) return ema > 0 ? (int)Math.Round(ema) : 0;
        if (ema <= 0) ema = ms;
        else ema = ema * (1 - alpha) + ms * alpha;
        return (int)Math.Round(ema);
    }

    /// <summary>
    /// &lt;100ms 绿满格；≥500ms 红一格；中间黄，格数随延迟递减。
    /// </summary>
    public static Signal ToSignal(int? latencyMs)
    {
        if (latencyMs is not int ms || ms <= 0)
            return new Signal(0, 0);
        if (ms < GoodMs)
            return new Signal(4, 1);
        if (ms >= BadMs)
            return new Signal(1, 3);
        // 100–500：3→2 格黄
        var t = (ms - GoodMs) / (double)(BadMs - GoodMs);
        var bars = t < 0.5 ? 3 : 2;
        return new Signal(bars, 2);
    }
}
