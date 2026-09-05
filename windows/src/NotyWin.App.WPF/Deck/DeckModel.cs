using NotyWin.App.Geometry;
using NotyWin.App.Models;

namespace NotyWin.App.Deck;

public sealed class DeckModel
{
    public DeckStyle Style { get; set; } = DeckStyle.Tabs;
    public bool DeckAlwaysShown { get; set; }
    public bool PillHidden { get; set; }
    public double DeckScale { get; set; } = 1.0;
    public bool OnLeftEdge { get; set; }
    public double NoteFontSize { get; set; } = 14;
    public bool Markdown { get; set; } = true;
    public double NoteWidth { get; set; } = 360;
    public double NoteHeight { get; set; } = 380;
    public bool OpenOnHover { get; set; }
    public bool TabPreview { get; set; } = true;
    public bool ShowOverFullScreen { get; set; }
    public double EdgeWidth { get; set; } = 14;
    public double DeckYRatio { get; set; } = 0.5;
    public int NoteCount { get; set; } = 0;

    public void SyncPreferences(SettingsSnapshot s)
    {
        Style = s.DeckStyle;
        DeckAlwaysShown = s.DeckAlwaysShown;
        PillHidden = s.DeckPillHidden;
        DeckScale = s.DeckScale;
        OnLeftEdge = s.DeckOnLeftEdge;
        NoteFontSize = s.NoteFontSize;
        Markdown = s.MarkdownStyling;
        NoteWidth = s.FloatingNoteWidth;
        NoteHeight = s.FloatingNoteHeight;
        OpenOnHover = s.OpenOnHover;
        TabPreview = s.TabPreview;
        ShowOverFullScreen = s.ShowOverFullScreen;
        EdgeWidth = s.EdgeWidth;
        DeckYRatio = s.DeckYRatio;
        DeckGeom.Scale = s.DeckScale;
    }
}
