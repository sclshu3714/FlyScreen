using FlyScreen.Models;
using FlyScreen.Services.Input;
using Xunit;

namespace FlyScreen.Core.Tests;

public class LayoutEdgeTests
{
    [Fact]
    public void Row_walks_one_machine_at_a_time()
    {
        var settings = Settings("A", "B", "C", "D");
        Assert.Equal("B", LayoutEdge.Neighbor(settings, "A", 1));
        Assert.Equal("C", LayoutEdge.Neighbor(settings, "B", 1));
        Assert.Equal("D", LayoutEdge.Neighbor(settings, "C", 1));
        Assert.Null(LayoutEdge.Neighbor(settings, "D", 1));
    }

    [Fact]
    public void Empty_slot_is_skipped()
    {
        var settings = Settings("A", "", "C", "D");
        Assert.Equal("C", LayoutEdge.Neighbor(settings, "A", 1));
        Assert.Equal("D", LayoutEdge.Neighbor(settings, "C", 1));
    }

    [Fact]
    public void Two_row_uses_vertical_edge()
    {
        var settings = Settings("A", "B", "C", "D");
        settings.TwoRow = true;
        Assert.Equal("B", LayoutEdge.Neighbor(settings, "A", 1));
        Assert.Equal("C", LayoutEdge.Neighbor(settings, "A", 2));
        Assert.Equal("D", LayoutEdge.Neighbor(settings, "B", 2));
        Assert.Equal("D", LayoutEdge.Neighbor(settings, "C", 1));
    }

    private static AppSettings Settings(params string[] slots)
    {
        var settings = new AppSettings { TwoRow = false, WrapMouse = false };
        settings.MachineSlots.Clear();
        settings.MachineSlots.AddRange(slots);
        return settings;
    }
}
