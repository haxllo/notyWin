using NotyWin.App.Geometry;
using Xunit;

namespace NotyWin.Geometry.Tests;

public class HotZoneTests
{
    [Fact]
    public void RightEdge_HotZone_IsAtTheRightOfThePanel()
    {
        DeckGeom.Scale = 1.0;
        var panel = new PanelFrame(1000, 0, 72, 1080);   // right edge of a 1920-wide screen
        var z = HotZone.ForPanel(panel, onLeftEdge: false);
        Assert.True(z.Left > 1000 + 72 - 200);  // far enough from x=0
        Assert.Equal(1000 + 72, z.Right);
        Assert.Equal(1080, z.Height);
        Assert.Equal(DeckGeom.FanWidth + 20, z.Width);
    }

    [Fact]
    public void LeftEdge_HotZone_IsAtTheLeftOfThePanel()
    {
        DeckGeom.Scale = 1.0;
        var panel = new PanelFrame(0, 0, 72, 1080);
        var z = HotZone.ForPanel(panel, onLeftEdge: true);
        Assert.Equal(0, z.Left);
        Assert.Equal(DeckGeom.FanWidth + 20, z.Right);
    }

    [Fact]
    public void Contains_PointerInsideHotZone()
    {
        DeckGeom.Scale = 1.0;
        var z = HotZone.ForPanel(new PanelFrame(0, 0, 72, 1080), onLeftEdge: true);
        Assert.True(z.Contains(20, 500));
        Assert.False(z.Contains(500, 500));   // way off the strip
    }
}

public class DisplaySetTests
{
    private static DisplayRect Disp(uint id, double w = 1920, double h = 1080)
        => new(id, 0, 0, w, h, 0, 0, w, h);

    [Fact]
    public void All_ResolvesToEveryDisplay()
    {
        var map = new Dictionary<uint, DisplayRect> { [1] = Disp(1), [2] = Disp(2) };
        var set = DisplaySetResolver.Resolve(DisplayTarget.Parse("all"), map, mainId: 1);
        Assert.Equal(new HashSet<uint> { 1, 2 }, set);
    }

    [Fact]
    public void Main_ResolvesToMainId()
    {
        var map = new Dictionary<uint, DisplayRect> { [1] = Disp(1), [2] = Disp(2) };
        var set = DisplaySetResolver.Resolve(DisplayTarget.Parse("main"), map, mainId: 1);
        Assert.Equal(new HashSet<uint> { 1 }, set);
    }

    [Fact]
    public void Pinned_PresentDisplay_ResolvesToPinned()
    {
        var map = new Dictionary<uint, DisplayRect> { [1] = Disp(1), [2] = Disp(2) };
        var set = DisplaySetResolver.Resolve(DisplayTarget.Parse("id:2"), map, mainId: 1);
        Assert.Equal(new HashSet<uint> { 2 }, set);
    }

    [Fact]
    public void Pinned_DisplayGone_FallsBackToMain()
    {
        var map = new Dictionary<uint, DisplayRect> { [1] = Disp(1) };   // 2 disconnected
        var set = DisplaySetResolver.Resolve(DisplayTarget.Parse("id:2"), map, mainId: 1);
        Assert.Equal(new HashSet<uint> { 1 }, set);
    }

    [Fact]
    public void Unknown_DefaultsToAll()
    {
        var map = new Dictionary<uint, DisplayRect> { [1] = Disp(1), [2] = Disp(2) };
        var set = DisplaySetResolver.Resolve(DisplayTarget.Parse("garbage"), map, mainId: 1);
        Assert.Equal(new HashSet<uint> { 1, 2 }, set);
    }

    [Fact]
    public void NoDisplays_ResolvesToEmpty()
    {
        var set = DisplaySetResolver.Resolve(DisplayTarget.Parse("all"),
            new Dictionary<uint, DisplayRect>(), mainId: 1);
        Assert.Empty(set);
    }
}