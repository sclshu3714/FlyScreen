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
}
