using NotyWin.App.Geometry;

namespace NotyWin.App.Deck;

/// <summary>
/// One deck per targeted display. Rebuilt on display changes or display-target
/// preference changes. Port of <c>DeckManager</c> in the macOS app
/// (Sources/DeckController.swift lines 694-770).
/// </summary>
public sealed class DeckManager : IDisposable
{
    private readonly Dictionary<uint, DeckController> _decks = new();
    public IReadOnlyDictionary<uint, DeckController> Decks => _decks;

    public string DisplayTargetRaw { get; set; } = "all";
    public bool ShowOverFullScreen { get; set; } = true;

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
        foreach (var d in _decks.Values)
        {
            d.Model.SyncPreferences();
            d.Window.ApplyLevel(ShowOverFullScreen);
            if (displays.TryGetValue(d.DisplayId, out var disp))
                d.Relayout(disp);
        }
    }

    private void Rebuild(IReadOnlyDictionary<uint, DisplayRect> displays, uint mainId)
    {
        var target = DisplayTarget.Parse(DisplayTargetRaw);
        var keep = DisplaySetResolver.Resolve(target, displays, mainId);

        // Drop decks whose display is no longer targeted.
        foreach (var id in _decks.Keys.Where(id => !keep.Contains(id)).ToList())
        {
            _decks[id].Dispose();
            _decks.Remove(id);
        }

        // Create decks for newly targeted displays.
        foreach (var id in keep)
        {
            if (!_decks.ContainsKey(id) && displays.TryGetValue(id, out var disp))
            {
                var controller = new DeckController(id, disp, ShowOverFullScreen);
                _decks[id] = controller;
            }
            else if (displays.TryGetValue(id, out var existing))
            {
                _decks[id].Relayout(existing);
            }
        }
    }

    public void Dispose()
    {
        foreach (var d in _decks.Values) d.Dispose();
        _decks.Clear();
    }
}