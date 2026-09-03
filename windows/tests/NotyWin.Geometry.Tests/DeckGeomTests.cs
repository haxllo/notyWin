using NotyWin.App.Geometry;
using Xunit;

namespace NotyWin.Geometry.Tests;

/// <summary>
/// Parity tests: the WinUI DeckGeometry constants and formulas must match the
/// macOS Swift implementation (Sources/DeckPanel.swift) at scale = 1.0.
/// </summary>
public class DeckGeomTests
{
    [Fact]
    public void Scale_RoundsToWholePoints()
    {
        DeckGeom.Scale = 1.0;
        Assert.Equal(12, DeckGeom.PillWidth);
        Assert.Equal(14, DeckGeom.PillTouchWidth);
        Assert.Equal(14, DeckGeom.DashHeight);
        Assert.Equal(7, DeckGeom.DashWidth);
        Assert.Equal(5, DeckGeom.DashGap);
        Assert.Equal(7, DeckGeom.PillPad);
        Assert.Equal(30, DeckGeom.TabWidth);
        Assert.Equal(7, DeckGeom.TabGap);
        Assert.Equal(40, DeckGeom.TabLap);
        Assert.Equal(56, DeckGeom.PitchMin);
        Assert.Equal(106, DeckGeom.PitchMax);
        Assert.Equal(36, DeckGeom.PitchFloor);
        Assert.Equal(20, DeckGeom.LabelPad);
        Assert.Equal(12, DeckGeom.LabelInset);
        Assert.Equal(14, DeckGeom.Bleed);
        Assert.Equal(30, DeckGeom.ChipWidth);
        Assert.Equal(24, DeckGeom.ChipHeight);
        Assert.Equal(6, DeckGeom.ChipGap);
        Assert.Equal(50, DeckGeom.FanWidth);
        Assert.Equal(28, DeckGeom.PlusSize);
        Assert.Equal(12, DeckGeom.PlusGap);
        Assert.Equal(24, DeckGeom.CogSize);
        Assert.Equal(8, DeckGeom.CogGap);
        Assert.Equal(34, DeckGeom.MoreTabHeight);
        Assert.Equal(3.0, DeckGeom.LeanDegrees);
        Assert.Equal(0.68, DeckGeom.HeightBudget);
        Assert.Equal(14, DeckGeom.MaxDashes);
    }

    [Fact]
    public void Lean_FlipsByEdge()
    {
        DeckGeom.Scale = 1.0;
        Assert.Equal(-3.0, DeckGeom.Lean(onRight: true));
        Assert.Equal(3.0, DeckGeom.Lean(onRight: false));
    }

    [Fact]
    public void PillHeight_SingleNote()
    {
        DeckGeom.Scale = 1.0;
        // PillPad*2 + 1*DashHeight + 0*DashGap = 7*2 + 14 = 28
        Assert.Equal(28, DeckGeom.PillHeight(1));
    }

    [Fact]
    public void PillHeight_TwoNotes()
    {
        DeckGeom.Scale = 1.0;
        // 14 + 2*14 + 1*5 = 14 + 28 + 5 = 47
        Assert.Equal(47, DeckGeom.PillHeight(2));
    }

    [Fact]
    public void PillHeight_CapsAtMaxDashes()
    {
        DeckGeom.Scale = 1.0;
        // Cap at 14 dashes; >MaxDashes adds the "+N" indicator
        var atCap = DeckGeom.PillHeight(14);
        var overCap = DeckGeom.PillHeight(15);
        Assert.Equal(atCap + DeckGeom.DashHeight + DeckGeom.DashGap, overCap);
    }

    [Fact]
    public void Scale_ProportionsAllMetrics()
    {
        DeckGeom.Scale = 1.5;
        Assert.Equal(18, DeckGeom.PillWidth);    // 12 * 1.5 = 18
        Assert.Equal(75, DeckGeom.FanWidth);     // 50 * 1.5 = 75
        DeckGeom.Scale = 1.0;
    }

    [Fact]
    public void TabsLayout_PitchClampedByLongestLabel()
    {
        DeckGeom.Scale = 1.0;
        // PitchMax=106, PitchMin=56. With no label, pitch=56.
        var l = DeckGeom.Layout(panelHeight: 900, count: 5, hasMore: false,
                                style: DeckStyle.Tabs, longestLabel: 0);
        Assert.Equal(56, l.Pitch);
        Assert.Equal(96, l.ItemHeight);   // pitch + tabLap
    }

    [Fact]
    public void TabsLayout_GuardRailShrinksOnShortScreen()
    {
        DeckGeom.Scale = 1.0;
        // heightBudget=0.68, panelHeight=400 → budget=272.
        // reserved=0 (no "+N"). With n=10 and pitch=PitchMax=106, 10*106+40=1100>272.
        // pitch = max(36, (272 - 40)/10) = 23.2 → clamped to floor=36.
        var l = DeckGeom.Layout(panelHeight: 400, count: 10, hasMore: false,
                                style: DeckStyle.Tabs, longestLabel: 200);
        Assert.Equal(DeckGeom.PitchFloor, l.Pitch);
    }

    [Fact]
    public void CompactLayout_StacksChips()
    {
        DeckGeom.Scale = 1.0;
        var l = DeckGeom.Layout(panelHeight: 900, count: 3, hasMore: false,
                                style: DeckStyle.Compact);
        Assert.Equal(24, l.ItemHeight);  // ChipHeight
        Assert.Equal(30, l.Pitch);       // ChipHeight + ChipGap
        Assert.Equal(6, l.MoreGap);      // ChipGap
        Assert.Equal(22, l.MoreHeight);
    }

    [Fact]
    public void StackHeight_AddsPlusAndCog()
    {
        DeckGeom.Scale = 1.0;
        var l = DeckGeom.Layout(panelHeight: 900, count: 3, hasMore: false,
                                style: DeckStyle.Compact);
        // 2*30 + 24 + 12 + 28 + 8 + 24 = 60+24+12+28+8+24 = 156
        Assert.Equal(156, l.StackHeight);
    }

    [Fact]
    public void Center_NonLastUsesPitch_NotItemHeight()
    {
        DeckGeom.Scale = 1.0;
        var l = DeckGeom.Layout(panelHeight: 900, count: 3, hasMore: false,
                                style: DeckStyle.Compact);
        // top = max(12, (panelHeight - stackHeight)/2)
        // center(i) = top + i * pitch + strip/2  where strip = pitch unless last.
        var top = Math.Max(12, (900 - l.StackHeight) / 2);
        Assert.Equal(top + 0 * 30 + 15, l.Center(0));   // strip=pitch=30
        Assert.Equal(top + 1 * 30 + 15, l.Center(1));
        Assert.Equal(top + 2 * 30 + 12, l.Center(2));   // strip=ItemHeight=24
    }

    [Fact]
    public void Spacing_NegativeForShingledTabs()
    {
        DeckGeom.Scale = 1.0;
        var l = DeckGeom.Layout(panelHeight: 900, count: 3, hasMore: false,
                                style: DeckStyle.Tabs, longestLabel: 0);
        // Pitch (56) < ItemHeight (96) → negative spacing makes VStack overlap.
        Assert.True(l.Spacing < 0);
        Assert.Equal(l.Pitch - l.ItemHeight, l.Spacing);
    }

    [Fact]
    public void ExpandedWidth_GrowsWithNoteAndFanFloor()
    {
        DeckGeom.Scale = 1.0;
        Assert.Equal(72, DeckGeom.ExpandedWidth(50));    // max(50,50)+22 = 72
        Assert.Equal(150, DeckGeom.ExpandedWidth(128));   // max(50,128)+22 = 150
    }
}