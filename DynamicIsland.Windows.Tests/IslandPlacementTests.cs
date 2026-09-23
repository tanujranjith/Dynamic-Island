using DynamicIsland.Windows.Infrastructure;
using Xunit;

namespace DynamicIsland.Windows.Tests;

public class IslandPlacementTests
{
    [Theory]
    [InlineData(0, 0, 0, 0)]
    [InlineData(1, 0, 860, 0)]
    [InlineData(2, 0, 1720, 0)]
    [InlineData(0, 1, 0, 500)]
    [InlineData(1, 1, 860, 500)]
    [InlineData(2, 1, 1720, 500)]
    [InlineData(0, 2, 0, 1000)]
    [InlineData(1, 2, 860, 1000)]
    [InlineData(2, 2, 1720, 1000)]
    public void PresetsReachEveryEdge(int horizontal, int vertical, double left, double top)
    {
        var p = IslandPlacement.Place(0, 0, 1920, 1040, 1200, 200, 40, 8, horizontal, vertical, 0, 0);
        Assert.Equal(left, p.X + 500);
        Assert.Equal(top, p.Y + 8);
    }

    [Fact]
    public void FullScreenOffsetsAndExpansionKeepVisibleContentOnScreen()
    {
        var p = IslandPlacement.Place(0, 0, 1920, 1040, 1200, 200, 40, 8, 0, 0, 1500, 900);
        Assert.Equal((1000d, 892d), p);
        var expanded = IslandPlacement.Place(0, 0, 1920, 1040, 1200, 900, 600, 8, 0, 0, 1500, 900);
        Assert.Equal((870d, 432d), expanded);
    }

    [Fact]
    public void RightAndBottomOffsetsMoveInward()
    {
        var p = IslandPlacement.Place(0, 0, 1920, 1040, 1200, 200, 40, 8, 2, 2, 30, 50);
        Assert.Equal((1190d, 942d), p);
    }

    [Fact]
    public void NegativeOffsetsMoveCenterToTopLeftOnSecondaryDisplay()
    {
        var p = IslandPlacement.Place(-1920, -1080, 1920, 1040, 1200, 200, 40, 8, 1, 1, -1920, -1080);
        Assert.Equal((-2420d, -1088d), p);
    }

    [Fact]
    public void ManualPositionOffsetsAndDpiAreMeasuredInSameUnits()
    {
        var p = IslandPlacement.Place(0, 0, 3840, 2080, 2400, 400, 80, 16, 0, 0, 100, 200, 500, 600);
        Assert.Equal((600d, 800d), p);
    }
}
