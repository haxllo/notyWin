namespace NotyWin.App.Geometry;

/// <summary>
/// The strip against the screen edge that wakes the fan. Same idea as
/// <c>DeckController.hotZone</c> in the Swift app.
/// </summary>
public readonly record struct HotZone(double Left, double Top, double Right, double Bottom)
{
    public double Width => Right - Left;
    public double Height => Bottom - Top;

    public bool Contains(double x, double y)
        => x >= Left && x <= Right && y >= Top && y <= Bottom;

    public static HotZone ForPanel(PanelFrame panel, bool onLeftEdge)
    {
        // FanWidth + 20 keeps the strip wide enough that entering the panel
        // never lands outside it.
        var w = DeckGeom.FanWidth + 20;
        return onLeftEdge
            ? new HotZone(panel.X, panel.Y, panel.X + w, panel.Y + panel.Height)
            : new HotZone(panel.X + panel.Width - w, panel.Y,
                          panel.X + panel.Width, panel.Y + panel.Height);
    }
}

public readonly record struct PanelFrame(double X, double Y, double Width, double Height);