using NotyWin.App.Geometry;
using NotyWin.App.Models;

namespace NotyWin.Rendering;

public enum RenderItemKind
{
    Pill,
    Tab,
    ChipTab,
    EmptyTab,
    MoreTab,
    PlusButton,
    CogButton,
    EdgeSpine,
    NotePreview,
    ExpandedNote,
}

/// <summary>
/// Single paint primitive. Coords are in panel-local Win32 (Y down) units;
/// the host multiplies by the current scale on apply. All rectangles include
/// the bleed for tabs so they draw flush against the screen edge.
/// </summary>
public sealed class RenderItem
{
    public required RenderItemKind Kind { get; init; }
    public required double X { get; init; }
    public required double Y { get; init; }
    public required double Width { get; init; }
    public required double Height { get; init; }
    public Note? Note { get; init; }
    public bool IsOpen { get; init; }
    public bool Lifted { get; init; }
    public bool Pinned { get; init; }
    public bool Hovering { get; init; }
    public int TabIndex { get; init; }
    public int HiddenCount { get; init; }
    /// <summary>For pills: per-dash colours (truncated to MaxDashes + an overflow marker).</summary>
    public IReadOnlyList<int>? DashColors { get; init; }
    public bool PillOverflow { get; init; }

    /// <summary>0..1 for staged-reveal animations. 1 = fully revealed.</summary>
    public double RevealProgress { get; init; } = 1.0;
}

/// <summary>
/// Snapshot of every draw primitive for one paint pass. The host iterates this
/// list in order (ZStack declaration order) so the shingle laps naturally —
/// later items paint on top of earlier ones.
/// </summary>
public sealed class DeckFrame
{
    public required IReadOnlyList<RenderItem> Items { get; init; }
    public required bool PillVisible { get; init; }
    public required bool FanVisible { get; init; }
    public required bool ShowExpanded { get; init; }
}

/// <summary>
/// Computes a paint frame for the current state. Pure — no Win32 / WinUI deps.
/// The fan-stage "reveal" is read from a separately maintained
/// <see cref="RevealProgressTracker"/> so animations are reproducible.
/// </summary>
public sealed class DeckViewModel
{
    public NoteList Notes { get; }
    public Func<SettingsSnapshot> Settings { get; }
    public int FanLimit => 5;
    public string UntitledLabel { get; set; } = "Untitled";

    public DeckViewModel(NoteList notes, Func<SettingsSnapshot> settings)
    {
        Notes = notes;
        Settings = settings;
    }

    public IReadOnlyList<Note> VisibleActive()
        => Notes.Active.Take(FanLimit).ToList();

    public int HiddenCount() => Math.Max(0, Notes.ActiveCount - FanLimit);
    public bool ShowsMoreTab() => HiddenCount() > 0;
    public int ItemCount() => Math.Max(1, VisibleActive().Count);

    public double LongestLabelWidth(LabelWidthCache cache)
    {
        var titles = VisibleActive().Select(n => n.DisplayTitle(UntitledLabel));
        return titles.Any() ? titles.Max(cache.Width) : 0;
    }

    public DeckFrame Render(double panelHeight, double panelWidth, LabelWidthCache cache, RevealProgressTracker reveal, double now = 0)
    {
        var s = Settings();
        DeckGeom.Scale = s.DeckScale;
        var onRight = !s.DeckOnLeftEdge;

        var visible = VisibleActive();
        var hidden = HiddenCount();
        var itemCount = ItemCount();
        var longest = LongestLabelWidth(cache);
        var lay = DeckGeom.Layout(panelHeight, itemCount, hidden > 0, s.DeckStyle, longest);

        var items = new List<RenderItem>();
        // SwiftUI shows the fan whenever the state is not .rest; at rest the
        // panel is only as tall as the pill, so the stack never fits.
        var fanVisible = panelHeight > lay.StackHeight;
        // Pill placement: same formula as macOS, expressed in top-down coords.
        var pillCount = Math.Max(1, Notes.ActiveCount);
        var pillH = DeckGeom.PillHeight(pillCount);
        var availableH = Math.Max(1, panelHeight - pillH);
        var pillTopY = (1.0 - s.DeckYRatio) * availableH;

        // Fan vertical centering keeps the pill in place as the panel grows.
        var fanIdeal = pillTopY + pillH / 2.0 - lay.StackHeight / 2.0;
        var fanTop = Math.Min(Math.Max(12, fanIdeal), Math.Max(12, panelHeight - lay.StackHeight - 12));

        if (fanVisible && visible.Count > 0)
        {
            AddTabs(items, visible, lay, s, panelWidth, fanTop, reveal, now);
        }
        else if (fanVisible)
        {
            AddEmptyTab(items, lay, s, panelWidth, fanTop, reveal, now);
            AddFooterButtons(items, visible.Count, lay, s, panelWidth, fanTop, reveal, now);
        }

        // The pill only exists at rest — the fan replaces it, exactly as the
        // SwiftUI ZStack swaps PillView for FanColumn.
        if (!fanVisible)
        {
            var pillColors = Notes.Active
                .Take(DeckGeom.MaxDashes)
                .Select(n => n.Palette.DashArgb)
                .ToList();
            items.Add(new RenderItem
            {
                Kind = RenderItemKind.Pill,
                X = onRight ? panelWidth - DeckGeom.PillWidth : 0,
                Y = pillTopY,
                Width = DeckGeom.PillWidth,
                Height = pillH,
                RevealProgress = reveal.PillProgress,
                DashColors = pillColors,
                PillOverflow = Notes.ActiveCount > DeckGeom.MaxDashes,
            });
        }

        // Edge spine (the dashed rule on the screen-edge side).
        items.Add(new RenderItem
        {
            Kind = RenderItemKind.EdgeSpine,
            X = onRight ? panelWidth - 4 : 0,
            Y = fanTop,
            Width = 1,
            Height = Math.Min(lay.StackHeight + 26, lay.Cap),
        });

        // Note preview (flyout card on tab hover).
        var preview = Notes.Notes.FirstOrDefault(n => reveal.PreviewNoteId == n.Id);
        if (preview is not null && reveal.PreviewNoteId is not null)
        {
            var idx = visible.ToList().FindIndex(n => n.Id == preview.Id);
            if (idx >= 0)
            {
                items.Add(new RenderItem
                {
                    Kind = RenderItemKind.NotePreview,
                    X = onRight ? panelWidth - DeckGeom.TabWidth - 10 - 210 : DeckGeom.TabWidth + 10,
                    Y = fanTop + idx * lay.Pitch,
                    Width = 210,
                    Height = 120,
                    Note = preview,
                    RevealProgress = reveal.PreviewProgress,
                });
            }
        }

        // Expanded note: paints a paper-coloured rect with the body as a placeholder
        // until the full editor (Markdown-as-you-type, find bar, etc.) ships.
        if (reveal.ExpandedNoteId is { } eid && Notes.ById(eid) is { } en)
        {
            var editorW = s.FloatingNoteWidth > 0 ? s.FloatingNoteWidth : 360;
            var editorH = s.FloatingNoteHeight > 0 ? s.FloatingNoteHeight : 380;
            var x = onRight ? panelWidth - DeckGeom.ExpandedWidth(editorW) : 0;
            items.Add(new RenderItem
            {
                Kind = RenderItemKind.ExpandedNote,
                X = x,
                Y = (panelHeight - editorH) / 2,
                Width = editorW,
                Height = editorH,
                Note = en,
            });
        }

        return new DeckFrame
        {
            Items = items,
            PillVisible = !fanVisible,
            FanVisible = fanVisible,
            ShowExpanded = reveal.ExpandedNoteId is not null,
        };
    }

    private void AddTabs(
        List<RenderItem> items,
        IReadOnlyList<Note> visible,
        DeckLayout lay,
        SettingsSnapshot s,
        double panelWidth,
        double fanTop,
        RevealProgressTracker reveal,
        double now)
    {
        var onRight = !s.DeckOnLeftEdge;
        var total = visible.Count + (ShowsMoreTab() ? 1 : 0) + 2;

        for (var i = 0; i < visible.Count; i++)
        {
            var n = visible[i];
            var shift = reveal.DragTarget.HasValue ? reveal.ShiftY(i, lay.Pitch) : 0;
            var isOpen = reveal.ExpandedNoteId == n.Id;
            var lifted = reveal.DraggedNoteId == n.Id;
            var stage = reveal.StageProgress(i, total, now);
            var y = fanTop + i * lay.Pitch + (lifted ? reveal.DragDy : 0) + shift;
            items.Add(new RenderItem
            {
                Kind = s.DeckStyle == DeckStyle.Compact ? RenderItemKind.ChipTab : RenderItemKind.Tab,
                X = onRight ? panelWidth - DeckGeom.TabWidth : 0,
                Y = y,
                Width = DeckGeom.TabWidth,
                Height = lay.ItemHeight,
                Note = n,
                IsOpen = isOpen,
                Lifted = lifted,
                Pinned = n.Pinned,
                Hovering = reveal.HoverTabId == n.Id,
                TabIndex = i,
                RevealProgress = stage,
            });
        }

        if (ShowsMoreTab())
        {
            var stage = reveal.StageProgress(visible.Count, total, now);
            items.Add(new RenderItem
            {
                Kind = RenderItemKind.MoreTab,
                X = onRight ? panelWidth - DeckGeom.TabWidth : 0,
                Y = fanTop + visible.Count * lay.Pitch + lay.MoreGap - lay.Spacing,
                Width = DeckGeom.TabWidth,
                Height = lay.MoreHeight,
                HiddenCount = HiddenCount(),
                RevealProgress = stage,
            });
        }

        AddFooterButtons(items, visible.Count, lay, s, panelWidth, fanTop, reveal, now);
    }

    private void AddFooterButtons(
    List<RenderItem> items,
    int visibleCount,
    DeckLayout lay,
    SettingsSnapshot s,
    double panelWidth,
    double fanTop,
    RevealProgressTracker reveal,
    double now)
    {
        var onRight = !s.DeckOnLeftEdge;
        var showsMore = ShowsMoreTab();
        var total = visibleCount + (showsMore ? 1 : 0) + 2;
        var moreStackHeight = showsMore ? lay.MoreHeight + lay.MoreGap : 0;

        items.Add(new RenderItem
        {
            Kind = RenderItemKind.PlusButton,
            X = onRight ? panelWidth - DeckGeom.PlusSize : 0,
            Y = fanTop + visibleCount * lay.Pitch + DeckGeom.PlusGap - lay.Spacing + moreStackHeight,
            Width = DeckGeom.PlusSize,
            Height = DeckGeom.PlusSize,
            RevealProgress = reveal.StageProgress(visibleCount + (showsMore ? 1 : 0), total, now),
        });

        items.Add(new RenderItem
        {
            Kind = RenderItemKind.CogButton,
            X = onRight ? panelWidth - DeckGeom.CogSize : 0,
            Y = fanTop + visibleCount * lay.Pitch + DeckGeom.PlusGap - lay.Spacing
                + DeckGeom.PlusSize + DeckGeom.CogGap + moreStackHeight,
            Width = DeckGeom.CogSize,
            Height = DeckGeom.CogSize,
            RevealProgress = reveal.StageProgress(visibleCount + (showsMore ? 1 : 0) + 1, total, now),
        });
    }

    private void AddEmptyTab(List<RenderItem> items, DeckLayout lay, SettingsSnapshot s, double panelWidth, double fanTop, RevealProgressTracker reveal, double now)
    {
        var onRight = !s.DeckOnLeftEdge;
        items.Add(new RenderItem
        {
            Kind = RenderItemKind.EmptyTab,
            X = onRight ? panelWidth - DeckGeom.TabWidth : 0,
            Y = fanTop,
            Width = DeckGeom.TabWidth,
            Height = lay.ItemHeight,
            RevealProgress = reveal.StageProgress(0, 3, now),
        });
    }
}

/// <summary>
/// Tracks the animation state of the deck so <see cref="DeckViewModel.Render"/>
/// is reproducible and the fan-stagger / drag-shift can advance independently
/// of the state machine. The host updates this from a render loop or timer.
/// </summary>
public sealed class RevealProgressTracker
{
    public int RevealTick { get; set; }
    public double RevealStart { get; set; } = -1;
    public double PillRevealStart { get; set; } = -1;

    public string? DraggedNoteId { get; set; }
    public int? DragFrom { get; set; }
    public int? DragTarget { get; set; }
    public double DragDy { get; set; }

    public string? HoverTabId { get; set; }
    public string? ExpandedNoteId { get; set; }
    public string? PreviewNoteId { get; set; }
    public double PreviewProgress { get; set; }

    public double StageProgress(int index, int total, double now = 0)
    {
        if (RevealStart < 0) return 1;
        var elapsed = now - RevealStart;
        var delay = index * 0.042;
        if (elapsed < delay) return 0;
        var t = (elapsed - delay) / 0.34;
        return Math.Clamp(t, 0, 1);
    }

    public double PillStageProgress(double now)
    {
        if (PillRevealStart < 0) return 1;
        var t = (now - PillRevealStart) / 0.20;
        return Math.Clamp(t, 0, 1);
    }

    public double PillProgress => PillStageProgress(0);

    public double ShiftY(int index, double pitch)
    {
        if (DragFrom is not { } from || DragTarget is not { } to) return 0;
        if (index == from) return 0;
        if (from < to && index > from && index <= to) return -pitch;
        if (from > to && index < from && index >= to) return pitch;
        return 0;
    }
}