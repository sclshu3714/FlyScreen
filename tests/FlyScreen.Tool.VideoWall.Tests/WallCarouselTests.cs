using FlyScreen.Tool.VideoWall.Wall;
using Xunit;

namespace FlyScreen.Tool.VideoWall.Tests;

public class WallCarouselTests
{
    [Fact]
    public void TwoByTwo_WithFiveSources_HasTwoPages()
    {
        var c = new WallCarousel();
        c.SetPageSize(4);
        c.SetSources(["a", "b", "c", "d", "e"]);

        Assert.Equal(2, c.PageCount);
        Assert.Equal(["a", "b", "c", "d"], c.GetPageSlots());
        c.NextPage();
        Assert.Equal(1, c.CurrentPage);
        Assert.Equal(["e", null, null, null], c.GetPageSlots());
    }

    [Fact]
    public void OneByOne_WithFiveSources_HasFivePages()
    {
        var c = new WallCarousel();
        c.SetPageSize(1);
        c.SetSources(["a", "b", "c", "d", "e"]);

        Assert.Equal(5, c.PageCount);
        Assert.Equal(["a"], c.GetPageSlots());
        c.NextPage();
        Assert.Equal(["b"], c.GetPageSlots());
    }

    [Fact]
    public void Disabled_ResetsToFirstPage()
    {
        var c = new WallCarousel();
        c.SetPageSize(1);
        c.SetSources(["a", "b"]);
        c.SetEnabled(true);
        c.NextPage();
        Assert.Equal(1, c.CurrentPage);
        c.SetEnabled(false);
        Assert.Equal(0, c.CurrentPage);
    }
}
