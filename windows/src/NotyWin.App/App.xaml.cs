using Microsoft.UI.Xaml;
using NotyWin.App.Deck;
using NotyWin.App.Models;
using NotyWin.Storage;

namespace NotyWin.App;

/// <summary>
/// Application entry point. Boots the Windows App Runtime, constructs the
/// service graph (settings + persistence + notes + deck manager) and shows
/// the per-display deck HWNDs.
/// </summary>
public partial class App : Application
{
    public static MainWindow Window { get; private set; } = null!;
    public static Microsoft.UI.Dispatching.DispatcherQueue DispatcherQueue { get; private set; } = null!;

    public static IService Services { get; private set; } = null!;

    public App()
    {
        InitializeComponent();
        UnhandledException += OnUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
    }

    private static void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        System.Diagnostics.Debug.WriteLine($"NotyWin UnhandledException: {e.Exception}");
        System.IO.File.AppendAllText(
            System.IO.Path.Combine(
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
                "Noty", "crash.log"),
            $"[{DateTime.UtcNow:O}] UI unhandled: {e.Exception}\n");
        e.Handled = true;
    }

    private static void OnDomainUnhandledException(object sender, System.UnhandledExceptionEventArgs e)
    {
        System.Diagnostics.Debug.WriteLine($"NotyWin Domain UnhandledException: {e.ExceptionObject}");
        try
        {
            System.IO.File.AppendAllText(
                System.IO.Path.Combine(
                    System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
                    "Noty", "crash.log"),
                $"[{DateTime.UtcNow:O}] domain unhandled: {e.ExceptionObject}\n");
        }
        catch { }
    }

    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        // Log early so silent failures are visible.
        var logPath = System.IO.Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
            "Noty", "startup.log");
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(logPath)!);
        var sw = new System.IO.StreamWriter(logPath, append: true) { AutoFlush = true };
        void Log(string s) { sw.WriteLine($"[{DateTime.UtcNow:O}] {s}"); }

        try
        {
            Log("OnLaunched entered");
            // WAR auto-initializer (from Microsoft.WindowsAppSDK NuGet) runs at
            // module load and pinned to the runtime version that ships with
            // the SDK (2.4 in our case). No explicit Bootstrap.Initialize
            // needed for self-contained deployment.

            DispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
            Log("DispatcherQueue acquired");

            var dataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Noty");
            Directory.CreateDirectory(dataDir);
            var settingsPath = Path.Combine(dataDir, "settings.json");
            var dbPath = Path.Combine(dataDir, "notes.db");
            var keyPath = Path.Combine(dataDir, "note.key.dpapi");

            var settings = new JsonSettingsStore(settingsPath);
            Log($"Settings store OK: {settingsPath}");
            var persistence = new SqliteNotePersistence(dbPath, keyPath);
            Log("Persistence OK");
            var notes = new NoteList(persistence.LoadAll());
            Log($"Loaded {notes.Notes.Count} notes");
            var manager = new DeckManager(notes, settings);
            Services = new IService(settings, persistence, notes, manager);
            Log("DeckManager constructed");

            // Defer the part that touches XAML islands until after OnLaunched
            // returns. The XAML host attaches to the process during the
            // OnLaunched -> MainWindow.Activate() handshake; until that
            // finishes, DesktopWindowXamlSource cannot be initialized.
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal, () =>
            {
                try
                {
                    Log("Deferred phase: constructing MainWindow");
                    Window = new MainWindow();
                    Log("MainWindow constructed");
                    Window.Closed += (_, _) => Log("MainWindow CLOSED");
                    Window.Activate();
                    // Send the MainWindow to the back so it doesn't sit on top
                    // of the deck. We still want it visible as a status panel.
                    Window.AppWindow.MoveInZOrderAtBottom();
                    Log("MainWindow activated");
                    Window.SetStatus(BuildDisplaysText(manager), notes.ActiveCount);

                    var watcher = new DisplayChangeWatcher();
                    watcher.Changed += () =>
                    {
                        var displays = manager.RefreshDisplays();
                        Window.SetStatus(BuildDisplaysText(manager), notes.ActiveCount);
                    };
                    Log("DisplayChangeWatcher set up");

                    manager.RefreshDisplays();
                    Log($"RefreshDisplays: {manager.Decks.Count} decks");

                    notes.Subscribe(new PersistOnChange(persistence));

                    var undoToast = new UndoToast(notes, DispatcherQueue);
                    Log("UndoToast created");

                    var hotkeys = new GlobalHotKeys();
                    hotkeys.OnNewNote = () =>
                    {
                        var created = notes.Create();
                        // Expand on the focused deck (where the mouse is).
                        var (cx, cy) = DeckWindow.CursorPos();
                        var displays = DisplayEnumerator.Snapshot();
                        var deck = manager.FocusAt(cx, cy, displays);
                        deck?.OnExpand(created.Id);
                    };
                    hotkeys.RegisterFromSettings(settings.Load());
                    settings.Changed += s => hotkeys.RegisterFromSettings(s);
                    Log("GlobalHotKeys registered");

                    var tray = new TrayIcon();
                    tray.OnNewNote = () =>
                    {
                        var created = notes.Create();
                        var (cx, cy) = DeckWindow.CursorPos();
                        var displays = DisplayEnumerator.Snapshot();
                        var deck = manager.FocusAt(cx, cy, displays);
                        deck?.OnExpand(created.Id);
                    };
                    tray.OnQuit = () =>
                    {
                        manager.Dispose();
                        Microsoft.UI.Xaml.Application.Current.Exit();
                    };
                    Log("TrayIcon created");

                    foreach (var d in manager.Decks.Values)
                    {
                        d.Window.Show();
                    }
                    Log("All deck windows shown");
                }
                catch (Exception ex)
                {
                    Log($"Deferred phase FAILED: {ex}");
                }
            });
        }
        catch (Exception ex)
        {
            Log($"OnLaunched FAILED: {ex}");
            sw.Close();
            throw;
        }
    }

    private static string BuildDisplaysText(DeckManager m)
    {
        if (m.Decks.Count == 0) return "none";
        return string.Join(", ", m.Decks.Keys.Select(k => "0x" + k.ToString("X")));
    }
}

/// <summary>Persist every note-list mutation to SQLite.</summary>
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
        // A note missing from the list was deleted (archived notes stay in it),
        // so drop it from the database too. Undo re-adds and re-upserts it.
        foreach (var gone in _known)
            if (!liveIds.Contains(gone)) _store.Delete(gone);
        _known.IntersectWith(liveIds);
    }

    public void OnCompleted() { }
    public void OnError(Exception error) { }
}

/// <summary>Top-level service graph the rest of the app reads from.</summary>
public sealed record IService(
    ISettingsStore Settings,
    INotePersistence Persistence,
    NoteList Notes,
    DeckManager Manager);