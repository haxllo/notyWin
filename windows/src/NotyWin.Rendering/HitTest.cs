namespace NotyWin.Rendering;

/// <summary>
/// Hit-test: given a panel-local cursor position, returns the kind of element
/// under it. Hit areas follow the shingled tab layout (each tab is full height
/// even though its visible strip is only a fraction of that). Mirrors
/// FanColumn.swift's hit zones.
/// </summary>
public static class HitTest
{
    public static HitItem? Test(double cursorX, double cursorY, DeckFrame frame, double panelWidth, bool onRight)
    {
        // Iterate in reverse — top-most first.
        for (var i = frame.Items.Count - 1; i >= 0; i--)
        {
            var item = frame.Items[i];
            if (cursorX < item.X || cursorX > item.X + item.Width) continue;
            if (cursorY < item.Y || cursorY > item.Y + item.Height) continue;
            return new HitItem(item, i);
        }
        return null;
    }

    public readonly record struct HitItem(RenderItem Item, int Index);
}