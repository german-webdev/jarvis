using System.Drawing;
using Jarvis.App.Services;
using Xunit;

namespace Jarvis.App.Tests;

public class CompanionPlacementTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(2260, 0)]
    [InlineData(0, 1040)]
    [InlineData(2260, 1040)]
    [InlineData(1130, 520)]
    public void FitsWithoutCoveringCompanionAtScreenEdges(int x, int y)
    {
        var area = new Rectangle(0, 0, 2560, 1440);
        var owner = new Rectangle(x, y, 300, 400);
        var result = CompanionPlacement.Choose(owner, new Size(475, 300), [area], 12);
        Assert.True(area.Contains(result)); Assert.False(owner.IntersectsWith(result));
    }
    [Fact]
    public void UsesNegativeMonitorCoordinatesAndKeepsStableDialogDuringResize()
    {
        var areas = new[] { new Rectangle(-1920, 120, 1920, 1040), new Rectangle(0, 0, 2560, 1400) };
        var owner = new Rectangle(-300, 500, 260, 360);
        var initial = CompanionPlacement.Choose(owner, new Size(475, 300), areas, 12);
        Assert.True(areas[0].Contains(initial)); Assert.False(owner.IntersectsWith(initial));
        var smaller = new Rectangle(-280, 600, 220, 260);
        Assert.Equal(initial, CompanionPlacement.Choose(smaller, initial.Size, areas, 12, initial));
        var larger = new Rectangle(-360, 350, 360, 510);
        var after = CompanionPlacement.Choose(larger, initial.Size, areas, 12, initial);
        Assert.Contains(areas, area => area.Contains(after)); Assert.False(larger.IntersectsWith(after));
    }
}
