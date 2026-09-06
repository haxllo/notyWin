using NotyWin.App.Geometry;
using NotyWin.App.Models;

namespace NotyWin.App.Deck;

/// <summary>
/// One deck per targeted display. Rebuilt on display changes or display-target
/// preference changes. Port of <c>DeckManager</c> in the macOS app
/// (Sources/DeckController.swift lines 694-770).
/// </summary>
public sealed class DeckManager : IDisposable
{
    private readonly Dictionary<uint, DeckController> _decks = new();
    private readonly NoteList _notes;
    private readonly ISettingsStore _settings;
    public IReadOnlyDictionary<uint, DeckController> Decks => _decks;

    public DeckManager(NoteList notes, ISettingsStore settings)
    {
        _notes = notes;
        _settings = settings;
        _notes.Subscribe(new NoteCountObserver(this));
    }

    public string DisplayTargetRaw => _settings.Load().DisplayTarget;
    public bool ShowOverFullScreen => _settings.Load().ShowOverFullScreen;

    public event Action? DisplaySetChanged;

    public IReadOnlyDictionary<uint, DisplayRect> RefreshDisplays()
    {
        var displays = DisplayEnumerator.Snapshot();
        Rebuild(displays, DisplayEnumerator.MainId());
        DisplaySetChanged?.Invoke();
        return displays;
    }

    public DeckController? Focused { get; private set; }

    public DeckController? FocusAt(double x, double y, IReadOnlyDictionary<uint, DisplayRect> displays)
    {
        var d = DisplayEnumerator.DisplayAtPoint(x, y, displays);
        if (d.HasValue && _decks.TryGetValue(d.Value.DisplayId, out var deck))
        {
            Focused = deck;
            return deck;
        }
        return _decks.Values.FirstOrDefault();
    }

    public void RefreshAll()
    {
        var displays = DisplayEnumerator.Snapshot();
        var s = _settings.Load();
        foreach (var d in _decks.Values)
        {
            d.Model.SyncPreferences(s);
            d.Window.ApplyLevel(s.ShowOverFullScreen);
            if (displays.TryGetValue(d.DisplayId, out var disp))
                d.Relayout(disp);
        }
    }

    private void Rebuild(IReadOnlyDictionary<uint, DisplayRect> displays, uint mainId)
    {
        var s = _settings.Load();
        var target = DisplayTarget.Parse(s.DisplayTarget);
        var keep = DisplaySetResolver.Resolve(target, displays, mainId);

        foreach (var id in _decks.Keys.Where(id => !keep.Contains(id)).ToList())
        {
            _decks[id].Dispose();
            _decks.Remove(id);
        }

        foreach (var id in keep)
        {
            if (!_decks.ContainsKey(id) && displays.TryGetValue(id, out var disp))
            {
                var controller = new DeckController(id, disp, s.ShowOverFullScreen);
                controller.Initialize(_notes, _settings);
                _decks[id] = controller;
            }
            else if (displays.TryGetValue(id, out var existing))
            {
                _decks[id].Relayout(existing);
            }
        }
    }
    /// <summary>Forces a full re-sync after settings change (e.g. scale).</summary>
    public void OnSettingsChanged()
    {
        var s = _settings.Load();
        foreach (var d in _decks.Values)
            d.Model.SyncPreferences(s);
    }

    private void OnNoteListChanged()
    {
        var n = _notes.ActiveCount;
        foreach (var (id, d) in _decks)
        {
            if (d.Model.NoteCount != n)
            {
                d.Model.NoteCount = n;
                d.ForceRelayout();
            }
        }
    }

    public void Dispose()
    {
        foreach (var d in _decks.Values) d.Dispose();
        _decks.Clear();
    }

    private sealed class NoteCountObserver : IObserver<NoteList>
    {
        private readonly DeckManager _m;
        public NoteCountObserver(DeckManager m) { _m = m; }
        public void OnNext(NoteList value) => _m.OnNoteListChanged();
        public void OnCompleted() { }
        public void OnError(Exception error) { }
    }
}
