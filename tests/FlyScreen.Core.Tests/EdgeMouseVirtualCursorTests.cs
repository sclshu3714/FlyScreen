using FlyScreen.Services.Input;
using Xunit;

namespace FlyScreen.Core.Tests;

public class EdgeMouseVirtualCursorTests
{
    [Fact]
    public void Entry_from_left_blocks_immediate_exit_left()
    {
        var c = new EdgeMouseVirtualCursor();
        // 本机右侧对端 → peerSide=+1 → 从对端左缘进入
        c.BeginRemote(0.001, 0.5, peerSide: 1);
        Assert.True(c.EntryStickyActive);
        Assert.Equal(-1, c.EntryEdge);

        // 刚进入就往左抖：不应出界
        Assert.True(c.TryApplyPixelDelta(-40, 0, 1920, 1080));
        Assert.False(c.TryGetExitDirection(-40, 0, allowVertical: false, out _, out _));
    }

    [Fact]
    public void After_moving_inward_can_exit_entry_edge_with_overshoot()
    {
        var c = new EdgeMouseVirtualCursor();
        c.BeginRemote(0.001, 0.5, peerSide: 1);

        // 先往右移入内侧，解除粘滞
        for (var i = 0; i < 20; i++)
            Assert.True(c.TryApplyPixelDelta(40, 0, 1920, 1080));
        Assert.False(c.EntryStickyActive);

        // 再持续往左推出界
        c.Set(0, 0.5);
        for (var i = 0; i < 8; i++)
            c.TryApplyPixelDelta(-40, 0, 1920, 1080);

        Assert.True(c.TryGetExitDirection(-40, 0, allowVertical: false, out var dirH, out var dirV));
        Assert.Equal(-1, dirH);
        Assert.Equal(0, dirV);
    }

    [Fact]
    public void Opposite_edge_exit_not_blocked_by_entry_sticky()
    {
        var c = new EdgeMouseVirtualCursor();
        c.BeginRemote(0.001, 0.5, peerSide: 1); // 左缘进入，粘滞挡左边

        // 直接推到右缘外侧
        c.Set(1.0, 0.5);
        for (var i = 0; i < 10; i++)
            c.TryApplyPixelDelta(40, 0, 1920, 1080);

        Assert.True(c.TryGetExitDirection(40, 0, allowVertical: false, out var dirH, out _));
        Assert.Equal(1, dirH);
    }

    [Fact]
    public void Huge_frame_delta_rejected()
    {
        var c = new EdgeMouseVirtualCursor();
        c.BeginRemote(0.5, 0.5, peerSide: 1);
        var before = c.Nx;
        Assert.False(c.TryApplyPixelDelta(900, 0, 1920, 1080));
        Assert.Equal(before, c.Nx);
    }
}
