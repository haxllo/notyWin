using System.IO;
using System.Windows;
using NotyWin.App.Deck;
using NotyWin.App.Models;
using NotyWin.Storage;

namespace NotyWin.App;

public partial class App : Application
{
    public static IService Services { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Catch all unhandled exceptions so crashes are logged.
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            try
            {
                var dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Noty");
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "crash.log"),
                    $"[{DateTime.UtcNow:O}] Domain unhandled: {args.ExceptionObject}\n");
            }
            catch { }
        };
        DispatcherUnhandledException += (_, args) =>
        {
            try
            {
                var dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Noty");
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "crash.log"),
                    $"[{DateTime.UtcNow:O}] UI unhandled: {args.Exception}\n");
            }
            catch { }
            args.Handled = true;
        };

        var dataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Noty");
        Directory.CreateDirectory(dataDir);

        var settingsPath = Path.Combine(dataDir, "settings.json");
        var dbPath = Path.Combine(dataDir, "notes.db");
        var keyPath = Path.Combine(dataDir, "note.key.dpapi");

        var settings = new JsonSettingsStore(settingsPath);
        var persistence = new SqliteNotePersistence(dbPath, keyPath);
        var notes = new NoteList(persistence.LoadAll());

        // Seed welcome notes on first run.
        if (notes.Notes.Count == 0)
        {
            notes.Create("Welcome to NotyWin.\n\nMove your cursor to the right edge of the screen to wake the deck.");
            notes.Create("Try the color swatches in the footer.\n- Pick a color\n- Edit the body\n- Watch it autosave");
            notes.Create("Right-click any tab for the menu.\nPin it, archive it, cycle its colour, or delete it.");
            notes.Create("Open Settings (cog button or tray menu) to rebind shortcuts and change the deck style.");
        }

        var manager = new DeckManager(notes, settings);
        Services = new IService(settings, persistence, notes, manager);

        notes.Subscribe(new PersistOnChange(persistence));

        // Build decks for all displays.
        manager.RefreshDisplays();

        // Wire tray icon.
        var tray = new TrayIcon();
        tray.OnNewNote = () =>
        {
            var created = notes.Create();
            var (cx, cy) = DeckWindow.CursorPos();
            var displays = DisplayEnumerator.Snapshot();
            var deck = manager.FocusAt(cx, cy, displays);
            deck?.OnExpand(created.Id);
        };
        tray.OnSettings = () => Dispatcher.BeginInvoke(() =>
            new SettingsWindow(settings, manager).Show());
        tray.OnAllNotes = () => Dispatcher.BeginInvoke(() =>
            new LibraryWindow(notes, manager).Show());
        tray.OnArchive = () => Dispatcher.BeginInvoke(() =>
            new LibraryWindow(notes, manager).Show());
        tray.OnQuit = () =>
        {
            manager.Dispose();
            Shutdown();
        };

        // Global hotkeys.
        var hotkeys = new GlobalHotKeys();
        hotkeys.OnNewNote = () =>
        {
            var created = notes.Create();
            var (cx, cy) = DeckWindow.CursorPos();
            var displays = DisplayEnumerator.Snapshot();
            var deck = manager.FocusAt(cx, cy, displays);
            deck?.OnExpand(created.Id);
        };
        hotkeys.RegisterFromSettings(settings.Load());
        settings.Changed += s => hotkeys.RegisterFromSettings(s);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        base.OnExit(e);
    }
}

public sealed record IService(
    ISettingsStore Settings,
    INotePersistence Persistence,
    NoteList Notes,
    DeckManager Manager);

internal sealed class PersistOnChange : IObserver<NoteList>
{
    private readonly SqliteNotePersistence _store;
    private readonly HashSet<string> _known = new();

    public PersistOnChange(SqliteNotePersistence store) { _store = store; }

    public void OnNext(NoteList value)
    {
        var live = value.Notes;
        var liveIds = new HashSet<string>(live.Count);
        foreach (var n in live)
        {
            liveIds.Add(n.Id);
            _store.Upsert(n);
            _known.Add(n.Id);
        }
        foreach (var gone in _known)
            if (!liveIds.Contains(gone)) _store.Delete(gone);
        _known.IntersectWith(liveIds);
    }

    public void OnCompleted() { }
    public void OnError(Exception error) { }
}
