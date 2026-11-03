using FlyScreen.Services.ScreenExtend;
using Xunit;

namespace FlyScreen.Core.Tests;

public class ProjectionCropHoldTests
{
    private static ProjectionBlackBorder Crop() => new(240, 0, 1920, 1080);

    private static ProjectionBlackBorder Full() => new(0, 0, 2400, 1080);

    [Fact]
    public void First_crop_locks_and_full_frames_do_not_release_immediately()
    {
        var hold = new ProjectionCropHold();
        var locked = hold.Stabilize(2400, 1080, Crop());
        Assert.Equal(240, locked.Left);
        Assert.Equal(1920, locked.Width);

        for (var i = 0; i < ProjectionCropHold.ReleaseFrames - 1; i++)
        {
            var kept = hold.Stabilize(2400, 1080, Full());
            Assert.Equal(1920, kept.Width);
        }

        var released = hold.Stabilize(2400, 1080, Full());
        Assert.True(released.IsFullFrame(2400, 1080));
    }

    [Fact]
    public void Small_inset_jitter_keeps_the_locked_rect()
    {
        var hold = new ProjectionCropHold();
        hold.Stabilize(2400, 1080, Crop());

        var kept = hold.Stabilize(2400, 1080, new ProjectionBlackBorder(250, 0, 1900, 1080));

        Assert.Equal(240, kept.Left);
        Assert.Equal(1920, kept.Width);
    }

    [Fact]
    public void A_different_crop_switches_only_after_it_repeats()
    {
        var hold = new ProjectionCropHold();
        hold.Stabilize(2400, 1080, Crop());
        var next = new ProjectionBlackBorder(0, 180, 2400, 720);

        for (var i = 0; i < ProjectionCropHold.SwitchFrames - 1; i++)
            Assert.Equal(1920, hold.Stabilize(2400, 1080, next).Width);

        var switched = hold.Stabilize(2400, 1080, next);
        Assert.Equal(180, switched.Top);
        Assert.Equal(720, switched.Height);
    }

    [Fact]
    public void Size_change_drops_the_lock()
    {
        var hold = new ProjectionCropHold();
        hold.Stabilize(2400, 1080, Crop());

        var fresh = hold.Stabilize(1080, 2400, new ProjectionBlackBorder(0, 0, 1080, 2400));

        Assert.True(fresh.IsFullFrame(1080, 2400));
    }
}
