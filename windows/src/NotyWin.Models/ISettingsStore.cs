namespace NotyWin.App.Models;

using NotyWin.App.Geometry;

/// <summary>
/// Snapshot of every preference the app reads. The macOS app exposes these as
/// <c>Settings.*</c> static accessors backed by <c>UserDefaults</c>; here we
/// model the same data as an immutable record, with defaults that match the
/// Swift defaults exactly.
/// </summary>
public sealed record SettingsSnapshot
{
    public bool ShowOverFullScreen { get; init; }
    public bool DeckOnLeftEdge { get; init; }
    public double DeckYRatio { get; init; } = 0.5;
    public string DisplayTarget { get; init; } = "all";
    public double NoteFontSize { get; init; } = 13.5;
    public string NoteFontName { get; init; } = "Noteworthy-Light";
    public double EdgeWidth { get; init; } = 14;
    public int NoteSizeIndex { get; init; } = 1;
    public bool OpenOnHover { get; init; }
    public bool TabPreview { get; init; } = true;
    public bool MarkdownStyling { get; init; } = true;
    public bool DeckAlwaysShown { get; init; }
    public bool DeckPillHidden { get; init; }
    public double DeckScale { get; init; } = 1.0;
    public DeckStyle DeckStyle { get; init; } = DeckStyle.Tabs;
    public double FloatingNoteWidth { get; init; } = 460;
    public double FloatingNoteHeight { get; init; } = 380;
    public bool LaunchAtLogin { get; init; }
    public bool CheckForUpdatesAutomatically { get; init; } = true;

    public Shortcut ScNewNote { get; init; } = new() { Modifiers = KeyModifiers.Alt | KeyModifiers.Meta, KeyCode = 0x4E /* N */ };
    public Shortcut ScAllNotes { get; init; } = new() { Modifiers = KeyModifiers.Alt | KeyModifiers.Meta, KeyCode = 0x41 /* A */ };
    public Shortcut ScCapture { get; init; } = new() { Modifiers = KeyModifiers.Shift | KeyModifiers.Meta, KeyCode = 0x20 /* Space */ };
    public Shortcut ScArchive { get; init; } = new() { Modifiers = KeyModifiers.Alt | KeyModifiers.Meta, KeyCode = 0x4C /* L */ };

    public Shortcut ScArchiveNote { get; init; } = new() { Modifiers = KeyModifiers.Shift | KeyModifiers.Meta, KeyCode = 0x41 /* A */ };
    public Shortcut ScClose { get; init; } = new() { Modifiers = KeyModifiers.None, KeyCode = 0x1B /* Esc */ };
    public Shortcut ScFind { get; init; } = new() { Modifiers = KeyModifiers.Meta, KeyCode = 0x46 /* F */ };
    public Shortcut ScTask { get; init; } = new() { Modifiers = KeyModifiers.Meta, KeyCode = 0x54 /* T */ };
    public Shortcut ScPin { get; init; } = new() { Modifiers = KeyModifiers.Meta, KeyCode = 0x50 /* P */ };
    public Shortcut ScColour { get; init; } = new() { Modifiers = KeyModifiers.Meta, KeyCode = 0xBE /* . */ };
    public Shortcut ScDelete { get; init; } = new() { Modifiers = KeyModifiers.Meta, KeyCode = 0x08 /* Backspace */ };
    public Shortcut ScBigger { get; init; } = new() { Modifiers = KeyModifiers.Control, KeyCode = 0xBB /* = */ };
    public Shortcut ScSmaller { get; init; } = new() { Modifiers = KeyModifiers.Control, KeyCode = 0xBD /* - */ };
}

/// <summary>
/// Persistence boundary. The macOS app reads from <c>UserDefaults</c>;
/// Win32 reads from <c>ApplicationData.Current.LocalSettings</c> (packaged) or
/// a JSON file under <c>%LocalAppData%\Noty\settings.json</c> (unpackaged).
/// The shape of the data is engine-agnostic — only the storage medium changes.
/// </summary>
public interface ISettingsStore
{
    SettingsSnapshot Load();
    void Save(SettingsSnapshot snapshot);
    event Action<SettingsSnapshot>? Changed;
}