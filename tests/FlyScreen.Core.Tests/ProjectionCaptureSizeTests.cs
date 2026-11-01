using FlyScreen.Services.ScreenExtend;
using Xunit;

namespace FlyScreen.Core.Tests;

public class ProjectionCaptureSizeTests
{
    [Fact]
    public void Landscape_phone_keeps_aspect_when_choice_is_16_9()
    {
        // Mi 10 横屏 2340×1080，弹窗选 1920×1080 时不能再收成 16:9
        var size = ProjectionCaptureSize.ForPhoneScreen(2340, 1080, 1920, 1080);

        Assert.Equal(1920, size.Width);
        Assert.Equal(886, size.Height);
        Assert.Equal(0, size.Width % 2);
        Assert.Equal(0, size.Height % 2);
    }

    [Fact]
    public void Portrait_uses_the_same_edges_swapped()
    {
        var size = ProjectionCaptureSize.ForPhoneScreen(1080, 2340, 1920, 1080);

        Assert.Equal(886, size.Width);
        Assert.Equal(1920, size.Height);
    }

    [Fact]
    public void Matching_aspect_fills_the_chosen_long_edge()
    {
        var size = ProjectionCaptureSize.ForPhoneScreen(1920, 1080, 1920, 1080);

        Assert.Equal(1920, size.Width);
        Assert.Equal(1080, size.Height);
    }

    [Fact]
    public void Choice_long_edge_caps_a_taller_panel()
    {
        var size = ProjectionCaptureSize.ForPhoneScreen(1080, 2400, 1280, 720);

        Assert.Equal(576, size.Width);
        Assert.Equal(1280, size.Height);
    }

    [Fact]
    public void Frame_rate_maps_to_the_capture_interval()
    {
        Assert.Equal(12, ProjectionDisplayPresets.ClampFps(0));
        Assert.Equal(20, ProjectionDisplayPresets.ClampFps(21));
        Assert.Equal(24, ProjectionDisplayPresets.ClampFps(24));
        Assert.Equal(120, ProjectionDisplayPresets.ClampFps(120));
        Assert.Equal(83, ProjectionDisplayPresets.FpsToIntervalMs(12));
        Assert.Equal(50, ProjectionDisplayPresets.FpsToIntervalMs(20));
        Assert.Equal(33, ProjectionDisplayPresets.FpsToIntervalMs(30));
        Assert.Equal(8, ProjectionDisplayPresets.FpsToIntervalMs(120));
        Assert.Equal(45, ProjectionDisplayPresets.ClampJpegQuality(55));
        Assert.Equal(70, ProjectionDisplayPresets.ClampJpegQuality(80));
        Assert.Equal(0, ProjectionDisplayPresets.ClampJpegQuality(0));
        Assert.Equal(100, ProjectionDisplayPresets.ClampJpegQuality(100));
        Assert.True(ProjectionDisplayPresets.DescribeQuality(0).Passthrough);
        Assert.False(ProjectionDisplayPresets.DescribeQuality(100).Passthrough);
    }
}
