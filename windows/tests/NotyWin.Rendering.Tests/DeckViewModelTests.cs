using NotyWin.App.Geometry;
using NotyWin.App.Models;
using NotyWin.Rendering;
using Xunit;

namespace NotyWin.Rendering.Tests;

public class DeckViewModelTests
{
    private static NoteList Empty() => new(Array.Empty<Note>());

    private static NoteList WithNotes(int n)
    {
        var list = new NoteList(Array.Empty<Note>());
        for (var i = 0; i < n; i++) list.Create("note " + i);
        return list;
    }

    private static SettingsSnapshot Defaults() => new();

    /// <summary>Panel height at rest for a note count — the pill-only frame.</summary>
    private static double RestHeight(int noteCount) => DeckGeom.PillHeight(Math.Max(1, noteCount));

    [Fact]
    public void VisibleActive_PrefixesFanLimit()
    {
        var list = WithNotes(10);
        var vm = new DeckViewModel(list, () => Defaults());
        Assert.Equal(5, vm.VisibleActive().Count);
    }

    [Fact]
    public void HiddenCount_IsOverflow()
    {
        var vm = new DeckViewModel(WithNotes(8), () => Defaults());
        Assert.Equal(3, vm.HiddenCount());
        Assert.True(vm.ShowsMoreTab());
    }

    [Fact]
    public void HiddenCount_Zero_NoMoreTab()
    {
        var vm = new DeckViewModel(WithNotes(3), () => Defaults());
        Assert.Equal(0, vm.HiddenCount());
        Assert.False(vm.ShowsMoreTab());
    }

    [Fact]
    public void ItemCount_AlwaysAtLeastOne()
    {
        var vm = new DeckViewModel(Empty(), () => Defaults());
        Assert.Equal(1, vm.ItemCount());
    }

    [Fact]
    public void Render_EmptyDeck_AtRest_ProducesPill()
    {
        var vm = new DeckViewModel(Empty(), () => Defaults());
        var cache = new LabelWidthCache(new LabelWidthCacheTests_Stub());
        var reveal = new RevealProgressTracker();
        var frame = vm.Render(panelHeight: RestHeight(0), panelWidth: 360, cache, reveal);
        Assert.Contains(frame.Items, i => i.Kind == RenderItemKind.Pill);
        Assert.DoesNotContain(frame.Items, i => i.Kind == RenderItemKind.EmptyTab);
    }

    [Fact]
    public void Render_EmptyDeck_Fanned_ProducesEmptyTabAndButtons()
    {
        var vm = new DeckViewModel(Empty(), () => Defaults());
        var cache = new LabelWidthCache(new LabelWidthCacheTests_Stub());
        var reveal = new RevealProgressTracker();
        var frame = vm.Render(panelHeight: 1080, panelWidth: 360, cache, reveal);
        Assert.Contains(frame.Items, i => i.Kind == RenderItemKind.EmptyTab);
        Assert.Contains(frame.Items, i => i.Kind == RenderItemKind.PlusButton);
        Assert.Contains(frame.Items, i => i.Kind == RenderItemKind.CogButton);
        Assert.DoesNotContain(frame.Items, i => i.Kind == RenderItemKind.Pill);
    }

    [Fact]
    public void Render_WithNotes_ProducesTabs()
    {
        var vm = new DeckViewModel(WithNotes(3), () => Defaults());
        var cache = new LabelWidthCache(new LabelWidthCacheTests_Stub());
        var frame = vm.Render(1080, 360, cache, new RevealProgressTracker());
        Assert.Equal(3, frame.Items.Count(i => i.Kind == RenderItemKind.Tab));
    }

    [Fact]
    public void Render_OverflowNotes_ProducesMoreTab()
    {
        var vm = new DeckViewModel(WithNotes(8), () => Defaults());
        var frame = vm.Render(1080, 360, new LabelWidthCache(new LabelWidthCacheTests_Stub()),
            new RevealProgressTracker());
        Assert.Contains(frame.Items, i => i.Kind == RenderItemKind.MoreTab && i.HiddenCount == 3);
    }

    [Fact]
    public void Render_CompactStyle_ProducesChipTabs()
    {
        var vm = new DeckViewModel(WithNotes(3), () => Defaults() with { DeckStyle = DeckStyle.Compact });
        var frame = vm.Render(1080, 360, new LabelWidthCache(new LabelWidthCacheTests_Stub()),
            new RevealProgressTracker());
        Assert.Equal(3, frame.Items.Count(i => i.Kind == RenderItemKind.ChipTab));
    }

    [Fact]
    public void Pill_DashColors_ArePerNotePalette()
    {
        var vm = new DeckViewModel(WithNotes(3), () => Defaults());
        var frame = vm.Render(RestHeight(3), 360, new LabelWidthCache(new LabelWidthCacheTests_Stub()),
            new RevealProgressTracker());
        var pill = frame.Items.First(i => i.Kind == RenderItemKind.Pill);
        Assert.NotNull(pill.DashColors);
        Assert.Equal(3, pill.DashColors!.Count);
        Assert.False(pill.PillOverflow);
    }

    [Fact]
    public void Pill_Overflow_WhenNotesExceedMaxDashes()
    {
        var vm = new DeckViewModel(WithNotes(DeckGeom.MaxDashes + 2), () => Defaults());
        var frame = vm.Render(RestHeight(DeckGeom.MaxDashes + 2), 360, new LabelWidthCache(new LabelWidthCacheTests_Stub()),
            new RevealProgressTracker());
        var pill = frame.Items.First(i => i.Kind == RenderItemKind.Pill);
        Assert.Equal(DeckGeom.MaxDashes, pill.DashColors!.Count);
        Assert.True(pill.PillOverflow);
    }

    [Fact]
    public void ExpandedNote_AppearsInFrame_WhenSet()
    {
        var list = WithNotes(1);
        var note = list.Active.First();
        var vm = new DeckViewModel(list, () => Defaults());
        var reveal = new RevealProgressTracker { ExpandedNoteId = note.Id };
        var frame = vm.Render(1080, 360, new LabelWidthCache(new LabelWidthCacheTests_Stub()), reveal);
        var expanded = Assert.Single(frame.Items, i => i.Kind == RenderItemKind.ExpandedNote);
        Assert.Equal(note.Id, expanded.Note!.Id);
    }

    [Fact]
    public void ExpandedNote_UsesEditorSizeFromSettings()
    {
        var list = WithNotes(1);
        var note = list.Active.First();
        var vm = new DeckViewModel(list, () => Defaults() with { FloatingNoteWidth = 200, FloatingNoteHeight = 150 });
        var reveal = new RevealProgressTracker { ExpandedNoteId = note.Id };
        var frame = vm.Render(1080, 360, new LabelWidthCache(new LabelWidthCacheTests_Stub()), reveal);
        var expanded = frame.Items.First(i => i.Kind == RenderItemKind.ExpandedNote);
        Assert.Equal(200, expanded.Width);
        Assert.Equal(150, expanded.Height);
    }

    [Fact]
    public void Reveal_Lifted_SetsOnRenderItem()
    {
        var vm = new DeckViewModel(WithNotes(3), () => Defaults());
        var reveal = new RevealProgressTracker { DraggedNoteId = vm.Notes.Notes.First().Id };
        var frame = vm.Render(1080, 360, new LabelWidthCache(new LabelWidthCacheTests_Stub()), reveal);
        Assert.Contains(frame.Items, i => i.Lifted && i.Kind == RenderItemKind.Tab);
    }

    [Fact]
    public void Render_OnLeftEdge_ReversesX()
    {
        var vm = new DeckViewModel(WithNotes(3), () => Defaults() with { DeckOnLeftEdge = true });
        var frame = vm.Render(1080, 360, new LabelWidthCache(new LabelWidthCacheTests_Stub()),
            new RevealProgressTracker());
        var tab = frame.Items.First(i => i.Kind == RenderItemKind.Tab);
        Assert.Equal(0, tab.X);
    }

    [Fact]
    public void Render_PillXOnRightSide()
    {
        var vm = new DeckViewModel(WithNotes(3), () => Defaults());
        var frame = vm.Render(RestHeight(3), 360, new LabelWidthCache(new LabelWidthCacheTests_Stub()),
            new RevealProgressTracker());
        var pill = frame.Items.First(i => i.Kind == RenderItemKind.Pill);
        Assert.Equal(360 - DeckGeom.PillWidth, pill.X);
    }

    [Fact]
    public void Reveal_StageProgress_FullyRevealedByDefault()
    {
        // Default state (-1 RevealStart) means settled: every tab is fully visible.
        var r = new RevealProgressTracker();
        Assert.Equal(1, r.StageProgress(0, 5, now: 0));
        Assert.Equal(1, r.StageProgress(0, 5, now: 99));
    }

    [Fact]
    public void Reveal_StageProgress_StaggersFromStart()
    {
        var r = new RevealProgressTracker { RevealStart = 10 };
        // Tab 0 with delay 0 starts revealing immediately (t=0 → 0).
        Assert.Equal(0, r.StageProgress(0, 5, now: 10));
        // Tab 0 fully revealed after the 0.34s spring.
        Assert.Equal(1, r.StageProgress(0, 5, now: 10.4));
        // Tab 4 has a delay of 4*0.042 = 0.168s — not yet revealing at 10.05.
        Assert.Equal(0, r.StageProgress(4, 5, now: 10.05));
        // Tab 4 starts revealing once the delay elapses.
        Assert.True(r.StageProgress(4, 5, now: 10.20) > 0);
    }

    [Fact]
    public void Reveal_ShiftY_ForDragFromAboveToBelow()
    {
        var r = new RevealProgressTracker { DraggedNoteId = "a", DragFrom = 0, DragTarget = 2 };
        Assert.Equal(-10.0, r.ShiftY(1, 10));
        Assert.Equal(-10.0, r.ShiftY(2, 10));
        Assert.Equal(0, r.ShiftY(3, 10));
    }

    [Fact]
    public void Reveal_ShiftY_ForDragFromBelowToAbove()
    {
        var r = new RevealProgressTracker { DraggedNoteId = "c", DragFrom = 2, DragTarget = 0 };
        // Dragged item stays put.
        Assert.Equal(0, r.ShiftY(2, 10));
        // Items between shift down to make room.
        Assert.Equal(10.0, r.ShiftY(0, 10));
        Assert.Equal(10.0, r.ShiftY(1, 10));
        // Item below the target stays put.
        Assert.Equal(0, r.ShiftY(3, 10));
    }

    private sealed class LabelWidthCacheTests_Stub : ITextMeasurer
    {
        public double MeasureWidth(string text, string fontFamily, double fontSize, double trackingPerChar)
            => text.Length * 4.0 + trackingPerChar * text.Length;
    }
}

public class HitTestTests
{
    private static RenderItem Item(RenderItemKind kind, double x, double y, double w, double h)
        => new() { Kind = kind, X = x, Y = y, Width = w, Height = h };

    [Fact]
    public void Hit_ReturnsTopMostItem()
    {
        var items = new List<RenderItem>
        {
            Item(RenderItemKind.Pill, 0, 0, 100, 100),
            Item(RenderItemKind.Tab, 0, 0, 100, 100),  // drawn last → on top
        };
        var frame = new DeckFrame { Items = items, PillVisible = true, FanVisible = true, ShowExpanded = false };
        var hit = HitTest.Test(50, 50, frame, 100, onRight: false);
        Assert.Equal(RenderItemKind.Tab, hit!.Value.Item.Kind);
    }

    [Fact]
    public void Hit_OutsideAll_ReturnsNull()
    {
        var items = new List<RenderItem>
        {
            Item(RenderItemKind.Tab, 0, 0, 50, 50),
        };
        var frame = new DeckFrame { Items = items, PillVisible = true, FanVisible = true, ShowExpanded = false };
        Assert.Null(HitTest.Test(75, 75, frame, 100, onRight: false));
    }
}