using FlyScreen.Services.ScreenExtend;
using Xunit;

namespace FlyScreen.Core.Tests;

public class LatestFrameSlotTests
{
    [Fact]
    public void Second_offer_before_take_does_not_schedule_again()
    {
        var slot = new LatestFrameSlot();
        var first = new byte[] { 1 };
        var second = new byte[] { 2 };

        Assert.True(slot.Offer(first, 2, 2));
        Assert.False(slot.Offer(second, 3, 3));

        Assert.True(slot.TryTake(out var jpeg, out var w, out var h, out var gen));
        Assert.Equal(second, jpeg);
        Assert.Equal(3, w);
        Assert.Equal(3, h);
        Assert.True(slot.IsCurrent(gen));
    }

    [Fact]
    public void Newer_offer_invalidates_the_frame_being_decoded()
    {
        var slot = new LatestFrameSlot();
        Assert.True(slot.Offer(new byte[] { 1 }, 8, 8));
        Assert.True(slot.TryTake(out _, out _, out _, out var gen));

        Assert.True(slot.Offer(new byte[] { 2 }, 8, 8));
        Assert.False(slot.IsCurrent(gen));
    }

    [Fact]
    public void HasPending_tracks_the_slot()
    {
        var slot = new LatestFrameSlot();
        Assert.False(slot.HasPending);
        Assert.True(slot.Offer(new byte[] { 1 }, 4, 4));
        Assert.True(slot.HasPending);
        Assert.True(slot.TryTake(out _, out _, out _, out _));
        Assert.False(slot.HasPending);
    }

    [Fact]
    public void Clear_drops_pending_and_invalidates_inflight()
    {
        var slot = new LatestFrameSlot();
        Assert.True(slot.Offer(new byte[] { 1 }, 8, 8));
        Assert.True(slot.TryTake(out _, out _, out _, out var gen));
        Assert.True(slot.Offer(new byte[] { 2 }, 8, 8));

        slot.Clear();

        Assert.False(slot.TryTake(out _, out _, out _, out _));
        Assert.False(slot.IsCurrent(gen));
    }
}
