namespace NotyWin.App.Geometry;

/// <summary>
/// Deck metrics. Every length is expressed at 100% and passed through
/// <see cref="S"/>, so one preference resizes the deck as a whole. Rounding to
/// whole points keeps the shingled tabs from landing on half pixels and showing
/// a seam.
/// </summary>
public static class DeckGeom
{
    public static double Scale { get; set; } = 1.0;

    private static double S(double v) => Math.Round(v * Scale);

    // Rest — a 12 pt pill of colour dashes
    public static double PillWidth => S(12);
    public static double PillTouchWidth => S(14);
    public static double DashHeight => S(14);
    public static double DashWidth => S(7);
    public static double DashGap => S(5);
    public static double PillPad => S(7);
    public const int MaxDashes = 14;

    // Fan
    public static double TabWidth => S(30);
    public static double TabGap => S(7);
    /// <summary>How far the next tab laps over the one before it.</summary>
    public static double TabLap => S(40);
    public static double PitchMin => S(56);
    public static double PitchMax => S(106);
    /// <summary>The smallest pitch the guard rail may squeeze a tab down to.</summary>
    public static double PitchFloor => S(36);
    /// <summary>
    /// The strip is the label plus this much; the label is drawn inside it with
    /// <see cref="LabelInset"/>. Keeping the two different is what leaves the
    /// last glyph room — sizing the strip to exactly the text width truncates
    /// on rounding.
    /// </summary>
    public static double LabelPad => S(20);
    public static double LabelInset => S(12);
    /// <summary>
    /// Tabs and notes are drawn a little past the screen edge so their lean
    /// cannot open a wedge of background between them and the edge they are
    /// stuck to.
    /// </summary>
    public static double Bleed => S(14);

    /// <summary>
    /// Everything leans the same way — a deck of tabs at matching angles reads
    /// as deliberate, where per-note angles just look scattered.
    /// </summary>
    public const double LeanDegrees = 3.0;

    public static double Lean(bool onRight) => onRight ? -LeanDegrees : LeanDegrees;

    public static double ChipWidth => S(30);
    public static double ChipHeight => S(24);
    public static double ChipGap => S(6);
    public static double FanWidth => S(50);
    public static double PlusSize => S(28);
    public static double PlusGap => S(12);
    // The cog sits under the plus, so it has to grow with it.
    public static double CogSize => S(24);
    public static double CogGap => S(8);

    public static double MoreTabHeight => S(34);

    /// <summary>The deck may claim at most this much of the screen before tabs start shrinking.</summary>
    public const double HeightBudget = 0.68;

    /// <summary>
    /// The open note carries its own tab as a left gutter, so it reads as
    /// growing out of the deck rather than floating beside it. It matches the
    /// tab it grew from, so it scales with one.
    /// </summary>
    public static double GutterWidth => TabWidth;

    // Expanded — the note slides clear of the deck
    public static double EditorWidth(double noteWidth) => noteWidth;
    public static double EditorHeight(double noteHeight) => noteHeight;

    /// <summary>
    /// The open note runs to the screen edge and covers its own tab, exactly
    /// as a pulled sticky would — so there is no gap between note and deck to
    /// tune. A little wider than the note so the lean has somewhere to go, and
    /// it grows with the note when the corner is dragged.
    /// </summary>
    public static double ExpandedWidth(double editorWidth) =>
        Math.Max(FanWidth, editorWidth) + 22;

    public static double PillHeight(int noteCount)
    {
        var shown = Math.Min(noteCount, MaxDashes);
        var n = Math.Max(1, shown + (noteCount > MaxDashes ? 1 : 0));
        return PillPad * 2 + n * DashHeight + (n - 1) * DashGap;
    }

    public static DeckLayout Layout(
        double panelHeight,
        int count,
        bool hasMore,
        DeckStyle style,
        double longestLabel = 0)
    {
        var n = Math.Max(1, count);
        return style switch
        {
            DeckStyle.Compact => new DeckLayout
            {
                ItemHeight = ChipHeight,
                Pitch = ChipHeight + ChipGap,
                MoreGap = ChipGap,
                MoreHeight = 22,
                Count = n,
                HasMore = hasMore,
                PanelHeight = panelHeight,
            },
            DeckStyle.Tabs => LayoutTabs(panelHeight, n, hasMore, longestLabel),
            _ => throw new ArgumentOutOfRangeException(nameof(style)),
        };
    }

    private static DeckLayout LayoutTabs(double panelHeight, int n, bool hasMore, double longestLabel)
    {
        // The uncovered strip of each tab is sized to the longest label on the
        // deck, so titles read in full until they hit the cap and ellipsise.
        var pitch = Math.Min(PitchMax, Math.Max(PitchMin, longestLabel + LabelPad));

        // Guard rail: on a short display, shrink rather than run off-screen.
        var reserved = hasMore ? MoreTabHeight + TabGap : 0;
        var budget = panelHeight * HeightBudget - reserved;
        if (n * pitch + TabLap > budget)
            pitch = Math.Max(PitchFloor, (budget - TabLap) / n);

        return new DeckLayout
        {
            ItemHeight = pitch + TabLap,
            Pitch = pitch,
            MoreGap = TabGap,
            MoreHeight = MoreTabHeight,
            Count = n,
            HasMore = hasMore,
            PanelHeight = panelHeight,
        };
    }
}