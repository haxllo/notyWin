namespace NotyWin.App.Geometry;

/// <summary>
/// Resolved metrics for one fan.
///
/// Tabs *shingle*: each is full height but sits <see cref="Pitch"/> below the one
/// before, so it laps over it like a roof tile. That keeps every tab tall
/// enough to carry a label while the deck as a whole stays well short of the
/// screen.
/// </summary>
public sealed class DeckLayout
{
    public double ItemHeight { get; init; }
    public double Pitch { get; init; }
    public double MoreGap { get; init; }
    public double MoreHeight { get; init; }
    public int Count { get; init; }
    public bool HasMore { get; init; }
    public double PanelHeight { get; init; }

    /// <summary>Negative for shingled tabs — VStack spacing that produces the overlap.</summary>
    public double Spacing => Pitch - ItemHeight;

    public double StackHeight
    {
        get
        {
            if (Count <= 0) return 0;
            var h = (Count - 1) * Pitch + ItemHeight;
            if (HasMore) h += MoreGap + MoreHeight;
            h += DeckGeom.PlusGap + DeckGeom.PlusSize;   // new note
            h += DeckGeom.CogGap + DeckGeom.CogSize;     // settings
            return h;
        }
    }

    public double Top => Math.Max(12, (PanelHeight - StackHeight) / 2.0);

    /// <summary>Centre of the strip of item <paramref name="index"/> that is actually visible.</summary>
    public double Center(int index)
    {
        var strip = index == Count - 1 ? ItemHeight : Pitch;
        return Top + index * Pitch + strip / 2.0;
    }

    public double Cap => Math.Max(140, PanelHeight - 76);
    public bool Overflows => StackHeight > Cap;
}