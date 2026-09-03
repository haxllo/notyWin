namespace NotyWin.App.Models;

/// <summary>
/// Observable, in-memory list of notes with the same mutation API as
/// <c>NoteStore</c> in Sources/NoteStore.swift. Storage backend (SQLite) plugs
/// in behind the interface; the list itself has no I/O.
/// </summary>
public sealed class NoteList : IObservable<NoteList>
{
    private readonly List<Note> _notes;
    private readonly List<IObserver<NoteList>> _observers = new();

    public PendingDelete? PendingUndo { get; private set; }
    public IReadOnlyList<Note> Notes => _notes;

    public NoteList(IEnumerable<Note> seed)
    {
        _notes = seed.ToList();
    }

    // MARK: Derived

    public IEnumerable<Note> Active => _notes
        .Where(n => !n.Archived)
        .OrderBy(n => n.Order);

    public IEnumerable<Note> Archived => _notes
        .Where(n => n.Archived)
        .OrderByDescending(n => n.Modified);

    public int ActiveCount => _notes.Count(n => !n.Archived);
    public Note? ById(string id) => _notes.FirstOrDefault(n => n.Id == id);

    // MARK: Mutations

    public Note Create(string body = "", int? color = null)
    {
        var n = new Note
        {
            Order = (_notes.Where(x => !x.Archived).Select(x => x.Order).DefaultIfEmpty(0).Min()) - 1,
            Color = color ?? _notes.Count % NoteColor.All.Length,
            Body = body,
            Title = Note.DerivedTitle(body),
        };
        _notes.Add(n);
        Publish();
        return n;
    }

    public void UpdateBody(string id, string body)
    {
        var n = ById(id);
        if (n is null) return;
        if (n.Body == body) return;
        n.Body = body;
        n.Title = Note.DerivedTitle(body);
        n.Modified = DateTime.UtcNow;
        Publish();
    }

    public void TogglePin(string id)
    {
        var n = ById(id);
        if (n is null) return;
        n.Pinned = !n.Pinned;
        Publish();
    }

    public void CycleColor(string id)
    {
        var n = ById(id);
        if (n is null) return;
        n.Color = (n.Color + 1) % NoteColor.All.Length;
        n.Modified = DateTime.UtcNow;
        Publish();
    }

    public void SetColor(string id, int color)
    {
        var n = ById(id);
        if (n is null) return;
        n.Color = color;
        n.Modified = DateTime.UtcNow;
        Publish();
    }

    public void SetTextDirection(string id, NoteTextDirection direction)
    {
        var n = ById(id);
        if (n is null) return;
        if (n.TextDirection == direction) return;
        n.TextDirection = direction;
        n.Modified = DateTime.UtcNow;
        Publish();
    }

    public void SetArchived(string id, bool archived)
    {
        var n = ById(id);
        if (n is null) return;
        n.Archived = archived;
        n.Modified = DateTime.UtcNow;
        if (!archived)
            n.Order = (Active.Select(x => x.Order).DefaultIfEmpty(0).Min()) - 1;
        Publish();
    }

    /// <summary>Removes the note but keeps it recoverable for ten seconds.</summary>
    public void Delete(string id, TimeSpan undoWindow)
    {
        var n = ById(id);
        if (n is null) return;
        _notes.Remove(n);
        PendingUndo = new PendingDelete { Note = n, Deadline = DateTime.UtcNow + undoWindow };
        Publish();
    }

    public void UndoDelete()
    {
        if (PendingUndo is not { } p) return;
        _notes.Add(p.Note);
        PendingUndo = null;
        Publish();
    }

    public void ClearPendingUndo()
    {
        if (PendingUndo is null) return;
        PendingUndo = null;
        Publish();
    }

    /// <summary>Move a note <paramref name="slots"/> positions up or down the deck.</summary>
    public void Reorder(string id, int slots)
    {
        if (slots == 0) return;
        var list = Active.ToList();
        var from = list.FindIndex(n => n.Id == id);
        if (from < 0) return;
        var to = Math.Clamp(from + slots, 0, list.Count - 1);
        if (to == from) return;
        var moved = list[from];
        list.RemoveAt(from);
        list.Insert(to, moved);
        for (var rank = 0; rank < list.Count; rank++)
        {
            var n = list[rank];
            if (n.Order != rank)
            {
                n.Order = rank;
            }
        }
        Publish();
    }

    public void Move(string id, string? beforeId)
    {
        var n = ById(id);
        if (n is null) return;
        var list = Active.ToList();
        double newOrder;
        if (beforeId is not null)
        {
            var target = list.FindIndex(x => x.Id == beforeId);
            if (target < 0) return;
            var upper = list[target].Order;
            var lower = target > 0 ? list[target - 1].Order : upper - 2;
            newOrder = (upper + lower) / 2.0;
        }
        else
        {
            newOrder = (list.Count == 0 ? 0 : list.Max(x => x.Order)) + 1;
        }
        n.Order = newOrder;
        Publish();
    }

    public int Ingest(IEnumerable<Note> incoming)
    {
        var added = 0;
        var ids = new HashSet<string>(_notes.Select(n => n.Id));
        var baseOrder = (_notes.Count == 0 ? 0 : _notes.Min(n => n.Order)) - 1;
        foreach (var n in incoming)
        {
            var copy = Clone(n);
            if (ids.Contains(copy.Id)) copy.Id = Guid.NewGuid().ToString();
            copy.Order = baseOrder--;
            _notes.Add(copy);
            added++;
        }
        Publish();
        return added;
    }

    /// <summary>Bulk apply: used by the storage layer to seed from disk.</summary>
    public void ReplaceAll(IEnumerable<Note> notes)
    {
        _notes.Clear();
        _notes.AddRange(notes);
        Publish();
    }

    private static Note Clone(Note n) => new()
    {
        Id = n.Id, Title = n.Title, Body = n.Body, Color = n.Color,
        Created = n.Created, Modified = n.Modified, Archived = n.Archived,
        Pinned = n.Pinned, TextDirection = n.TextDirection, Order = n.Order,
    };

    // MARK: Observers

    public IDisposable Subscribe(IObserver<NoteList> observer)
    {
        _observers.Add(observer);
        observer.OnNext(this);
        return new Unsubscriber(_observers, observer);
    }

    private void Publish()
    {
        foreach (var o in _observers.ToList()) o.OnNext(this);
    }

    private sealed class Unsubscriber : IDisposable
    {
        private readonly List<IObserver<NoteList>> _observers;
        private readonly IObserver<NoteList> _observer;
        public Unsubscriber(List<IObserver<NoteList>> observers, IObserver<NoteList> observer)
        { _observers = observers; _observer = observer; }
        public void Dispose() => _observers.Remove(_observer);
    }
}