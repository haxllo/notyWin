namespace NotyWin.App.Geometry;

public enum DeckState
{
    Rest,
    Fan,
    Expanded,
}

/// <summary>
/// Edge info for one display: the full screen rect and the visible (work) area
/// excluding the taskbar. All coordinates in screen space (Y grows down).
/// </summary>
public readonly record struct DisplayRect(
    uint DisplayId,
    double FullLeft, double FullTop, double FullRight, double FullBottom,
    double VisLeft, double VisTop, double VisRight, double VisBottom)
{
    public double FullWidth => FullRight - FullLeft;
    public double FullHeight => FullBottom - FullTop;
    public double VisWidth => VisRight - VisLeft;
    public double VisHeight => VisBottom - VisTop;

    /// <summary>Convert from physical screen pixels to DIPs using the given DPI scale.</summary>
    public DisplayRect ToDips(double dpiScale) => new(
        DisplayId,
        FullLeft / dpiScale, FullTop / dpiScale,
        FullRight / dpiScale, FullBottom / dpiScale,
        VisLeft / dpiScale, VisTop / dpiScale,
        VisRight / dpiScale, VisBottom / dpiScale);
}

public sealed class DeckLayoutResult
{
    public required double X { get; init; }
    public required double Y { get; init; }
    public required double Width { get; init; }
    public required double Height { get; init; }
}

/// <summary>
/// One-to-one port of <c>DeckController.layout(for state:)</c> in the macOS app.
/// Translates screen coordinates from macOS (Y up) to top-down (Y down).
/// </summary>
public static class DeckFrame
{
    public static DeckLayoutResult Layout(
        DeckState state,
        DisplayRect display,
        bool onLeftEdge,
        int noteCount,
        double noteWidth,
        double edgeWidth,
        double deckYRatio)
    {
        return state switch
        {
            DeckState.Rest => Rest(noteCount, display, onLeftEdge, edgeWidth, deckYRatio),
            DeckState.Fan => Fan(display, onLeftEdge),
            _ => FanOrExpanded(noteWidth, display, onLeftEdge),
        };
    }

    private static DeckLayoutResult Rest(
        int noteCount,
        DisplayRect display,
        bool onLeftEdge,
        double edgeWidth,
        double deckYRatio)
    {
        // The dormant panel is the detection strip: the pill is drawn at the
        // edge and the rest of the width is transparent and click-through.
        // All values here are in DIPs — the display rect is converted by
        // the caller (DeckController) before this is called.
        var h = DeckGeom.PillHeight(Math.Max(1, noteCount));
        var w = Math.Max(DeckGeom.PillWidth + 2, edgeWidth);
        var availableH = Math.Max(1, display.VisHeight - h);

        // macOS y grows up; Win32 y grows down. yRatio is "fraction of available
        // height from the bottom" on both. Convert to top-down at the end.
        var yFromBottom = availableH * deckYRatio;
        var yWin = display.VisBottom - h - yFromBottom;

        var x = onLeftEdge ? display.FullLeft : display.FullRight - w;
        return new DeckLayoutResult
        {
            X = x, Y = yWin, Width = w, Height = h,
        };
    }

    public static DeckLayoutResult Fan(
        DisplayRect display, bool onLeftEdge)
    {
        // The fan panel is narrow — just wide enough for the tab edges to
        // peek out from behind each other. FanWidth (50pt) matches the
        // macOS app's panel width in Fan state.
        var w = DeckGeom.FanWidth;
        var x = onLeftEdge ? display.FullLeft : display.FullRight - w;
        return new DeckLayoutResult
        {
            X = x, Y = display.VisTop, Width = w, Height = display.VisHeight,
        };
    }

    private static DeckLayoutResult FanOrExpanded(
        double noteWidth,
        DisplayRect display,
        bool onLeftEdge)
    {
        // Same width for both. Resizing the panel as a note opens makes the
        // window resize and the UI relayout land in different frames, and for
        // one frame the deck draws against the panel's far edge — which looks
        // exactly like the note flying in from mid-screen.
        var w = DeckGeom.ExpandedWidth(noteWidth);
        var x = onLeftEdge ? display.FullLeft : display.FullRight - w;
        return new DeckLayoutResult
        {
            X = x, Y = display.VisTop, Width = w, Height = display.VisHeight,
        };
    }
}