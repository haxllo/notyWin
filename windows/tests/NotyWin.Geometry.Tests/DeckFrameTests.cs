using NotyWin.App.Geometry;
using Xunit;

namespace NotyWin.Geometry.Tests;

public class DeckFrameTests
{
    private static DisplayRect MakeDisplay(double fl, double ft, double fr, double fb,
                                            double vl, double vt, double vr, double vb)
        => new(1, fl, ft, fr, fb, vl, vt, vr, vb);

    [Fact]
    public void Rest_PillSitsAtRightEdge()
    {
        DeckGeom.Scale = 1.0;
        var d = MakeDisplay(0, 0, 1920, 1080, 0, 0, 1920, 1040); // taskbar 40px at bottom
        var frame = DeckFrame.Layout(DeckState.Rest, d, onLeftEdge: false,
                                     noteCount: 1, noteWidth: 360, edgeWidth: 28, deckYRatio: 0.5);
        // Pill touches right edge.
        Assert.Equal(1920 - frame.Width, frame.X);
        // Pill height = 28 (PillPad*2 + DashHeight = 14+14)
        Assert.Equal(28, frame.Height);
    }

    [Fact]
    public void Rest_PillOnLeftEdge()
    {
        DeckGeom.Scale = 1.0;
        var d = MakeDisplay(0, 0, 1920, 1080, 0, 0, 1920, 1040);
        var frame = DeckFrame.Layout(DeckState.Rest, d, onLeftEdge: true,
                                     noteCount: 1, noteWidth: 360, edgeWidth: 28, deckYRatio: 0.5);
        Assert.Equal(0, frame.X);
    }

    [Fact]
    public void Rest_YRatioZero_PillAtBottom()
    {
        DeckGeom.Scale = 1.0;
        var d = MakeDisplay(0, 0, 1920, 1080, 0, 0, 1920, 1040);
        var frame = DeckFrame.Layout(DeckState.Rest, d, onLeftEdge: false,
                                     noteCount: 1, noteWidth: 360, edgeWidth: 28, deckYRatio: 0);
        // availableH = 1040 - 28 = 1012. yFromBottom = 0. yWin = 1040 - 28 = 1012 (top of pill at 1012).
        Assert.Equal(1012, frame.Y);
    }

    [Fact]
    public void Rest_YRatioOne_PillAtTop()
    {
        DeckGeom.Scale = 1.0;
        var d = MakeDisplay(0, 0, 1920, 1080, 0, 0, 1920, 1040);
        var frame = DeckFrame.Layout(DeckState.Rest, d, onLeftEdge: false,
                                     noteCount: 1, noteWidth: 360, edgeWidth: 28, deckYRatio: 1);
        // yWin = 1040 - 28 - 1012 = 0
        Assert.Equal(0, frame.Y);
    }

    [Fact]
    public void FanPanel_IsNarrowerThanExpanded()
    {
        DeckGeom.Scale = 1.0;
        var d = MakeDisplay(0, 0, 1920, 1080, 0, 0, 1920, 1040);
        var fan = DeckFrame.Layout(DeckState.Fan, d, onLeftEdge: false,
                                   noteCount: 3, noteWidth: 360, edgeWidth: 28, deckYRatio: 0.5);
        var expanded = DeckFrame.Layout(DeckState.Expanded, d, onLeftEdge: false,
                                        noteCount: 3, noteWidth: 360, edgeWidth: 28, deckYRatio: 0.5);
        // Fan is narrow (just tab edges), Expanded is wide (note + tab gutter).
        Assert.True(fan.Width < expanded.Width,
            $"Fan width {fan.Width} should be less than Expanded width {expanded.Width}");
        // Both span full visible height.
        Assert.Equal(d.VisHeight, fan.Height);
        Assert.Equal(d.VisHeight, expanded.Height);
        // Both docked to the right edge.
        Assert.Equal(d.FullRight - fan.Width, fan.X);
        Assert.Equal(d.FullRight - expanded.Width, expanded.X);
    }

    [Fact]
    public void RestPanel_DetectionStripMatchesEdgeWidth()
    {
        DeckGeom.Scale = 1.0;
        var d = MakeDisplay(0, 0, 1920, 1080, 0, 0, 1920, 1040);
        var frame = DeckFrame.Layout(DeckState.Rest, d, onLeftEdge: false,
                                     noteCount: 1, noteWidth: 360, edgeWidth: 28, deckYRatio: 0.5);
        // Wider of PillWidth+2 (14) and edgeWidth (28).
        Assert.Equal(28, frame.Width);
    }
}