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
    }

    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        // Windows App SDK 1.7+ requires an explicit version pin when running
        // unpackaged. 1.7 matches our referenced Microsoft.WindowsAppSDK 2.4.0
        // (which exposes runtime 1.7). See
        // https://learn.microsoft.com/windows/apps/windows-app-sdk/stable-channel
        const uint WarVersion = 0x00010007;   // 1.7
        try
        {
            Microsoft.Windows.ApplicationModel.DynamicDependency.Bootstrap.Initialize(WarVersion);
        }
        catch (Exception ex)
        {
            // Surface the failure rather than silently exiting.
            System.Diagnostics.Debug.WriteLine($"NotyWin: WAR bootstrap failed ({ex.HResult}): {ex.Message}");
            throw;
        }

        DispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();

        // Build the service graph. Settings + persistence live on disk;
        // NoteList is the in-memory model the rest of the app reads from.
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
        var manager = new DeckManager(notes, settings);
        Services = new IService(settings, persistence, notes, manager);

        Window = new MainWindow();
        Window.Activate();
        Window.SetStatus(BuildDisplaysText(manager), notes.ActiveCount);

        // Watch for hot-plug.
        var watcher = new DisplayChangeWatcher();
        watcher.Changed += () =>
        {
            var displays = manager.RefreshDisplays();
            Window.SetStatus(BuildDisplaysText(manager), notes.ActiveCount);
        };

        // Build per-display decks on the first refresh.
        manager.RefreshDisplays();

        // Persist notes on every change. The NoteList is observable, so we
        // subscribe once and write through. The debounce on the in-app
        // editor isn't wired yet (step 7); for now every mutation hits
        // SQLite immediately, which is fine for a 0.1s button feedback.
        notes.Subscribe(new PersistOnChange(persistence));

        // Show per-display decks.
        foreach (var d in manager.Decks.Values)
        {
            d.Window.Show();
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
    public PersistOnChange(SqliteNotePersistence store) { _store = store; }
    public void OnNext(NoteList value)
    {
        foreach (var n in value.Notes) _store.Upsert(n);
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