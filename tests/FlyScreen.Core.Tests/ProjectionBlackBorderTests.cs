using FlyScreen.Services.ScreenExtend;
using Xunit;

namespace FlyScreen.Core.Tests;

public class ProjectionBlackBorderTests
{
    [Fact]
    public void Uniform_insets_crop_all_four_sides()
    {
        var crop = ProjectionBlackBorder.FromInsets(1000, 800, 20, 16, 20, 16);

        Assert.Equal(20, crop.Left);
        Assert.Equal(16, crop.Top);
        Assert.Equal(960, crop.Width);
        Assert.Equal(768, crop.Height);
    }

    [Fact]
    public void Wide_phone_pillarbox_crops_the_side_pair()
    {
        // 2400×1080 里居中一块 1920×1080 的 16:9 画面
        var crop = ProjectionBlackBorder.FromInsets(2400, 1080, 240, 0, 240, 0);

        Assert.Equal(240, crop.Left);
        Assert.Equal(0, crop.Top);
        Assert.Equal(1920, crop.Width);
        Assert.Equal(1080, crop.Height);
    }

    [Fact]
    public void Slightly_uneven_pair_crops_the_narrower_side()
    {
        var crop = ProjectionBlackBorder.FromInsets(2400, 1080, 250, 0, 230, 0);

        Assert.Equal(230, crop.Left);
        Assert.Equal(1940, crop.Width);
        Assert.Equal(1080, crop.Height);
    }

    [Fact]
    public void Portrait_letterbox_crops_top_and_bottom()
    {
        var crop = ProjectionBlackBorder.FromInsets(1080, 2400, 0, 896, 0, 896);

        Assert.Equal(896, crop.Top);
        Assert.Equal(1080, crop.Width);
        Assert.Equal(608, crop.Height);
    }

    [Fact]
    public void Status_bar_alone_is_kept()
    {
        var crop = ProjectionBlackBorder.FromInsets(1080, 2400, 0, 80, 0, 0);

        Assert.True(crop.IsFullFrame(1080, 2400));
    }

    [Fact]
    public void Asymmetric_bars_are_kept()
    {
        var crop = ProjectionBlackBorder.FromInsets(2400, 1080, 400, 0, 80, 0);

        Assert.True(crop.IsFullFrame(2400, 1080));
    }

    [Fact]
    public void Crop_that_would_eat_the_frame_is_rejected()
    {
        var crop = ProjectionBlackBorder.FromInsets(400, 400, 150, 150, 150, 150);

        Assert.True(crop.IsFullFrame(400, 400));
    }
}
