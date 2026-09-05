using System.Windows;
using System.Windows.Threading;
using NotyWin.App.Geometry;
using NotyWin.App.Models;
using NotyWin.Rendering;
using RenderDeckFrame = NotyWin.Rendering.DeckFrame;

namespace NotyWin.App.Deck;

/// <summary>
/// One-deck-per-display controller. Owns a <see cref="DeckWindow"/> and a
/// <see cref="DeckStateMachine"/>; turns machine decisions into Win32 effects.
/// WPF version — uses <see cref="Dispatcher"/> instead of DispatcherQueue.
/// </summary>
public sealed class DeckController : IDisposable
{
    public uint DisplayId { get; }
    public DeckWindow Window { get; }
    public DeckModel Model { get; } = new();
    public DeckStateMachine StateMachine { get; } = new();
    public NoteList? Notes { get; set; }
    public ISettingsStore? Settings { get; set; }
    public DeckView? View { get; private set; }

    private System.Timers.Timer? _idleTimer;
    private System.Timers.Timer? _pollTimer;
    private System.Threading.Timer? _exitWork;
    private System.Timers.Timer? _animTimer;
    private System.Timers.Timer? _hoverTimer;
    private IDisposable? _notesSub;
    private DisplayRect _display;
    private bool _showOverFullScreen;
    private bool _inside;
    private bool _disposed;
    private readonly Dispatcher _dispatcher;

    public DeckController(uint displayId, DisplayRect display, bool showOverFullScreen)
    {
        DisplayId = displayId;
        _display = display;
        _showOverFullScreen = showOverFullScreen;
        Window = new DeckWindow();
        Window.ApplyLevel(showOverFullScreen);
        _dispatcher = Dispatcher.CurrentDispatcher;
        StateMachine.RestingState = Model.DeckAlwaysShown ? DeckState.Fan : DeckState.Rest;
        StateMachine.State = StateMachine.RestingState;

        View = new DeckView
        {
            OnRightEdge = !Model.OnLeftEdge,
        };
        Window.Window.Content = View;

        Window.InteractiveFilter = (x, y) => View.HitAt(x, y) is not null;
        Window.PointerMoved += (x, y) => OnPointerMoved(x, y);
        Window.LeftButtonDown += (x, y) => OnLeftButtonDown(x, y);
        Window.RightButtonDown += (x, y) => OnRightButtonDown(x, y);
    }

    /// <summary>Called by DeckManager after construction.</summary>
    public void Initialize(NoteList notes, ISettingsStore settings)
    {
        Notes = notes;
        Settings = settings;
        var snapshot = settings.Load();
        Model.SyncPreferences(snapshot);
        Model.NoteCount = notes.ActiveCount;
        View!.ViewModel = new DeckViewModel(notes, () => settings.Load());
        View.OnRightEdge = !snapshot.DeckOnLeftEdge;
        View.Editor.Notes = notes;
        View.Editor.OnRequestCollapse = () => OnCollapse();
        Relayout(_display);
        Window.Show();
        StartPoll();
        _notesSub?.Dispose();
        _notesSub = notes.Subscribe(new NoteChangeObserver(this));
    }

    public Action? OnCogClicked { get; set; }
    public Action? OnMoreClicked { get; set; }

    // MARK: - Pointer

    private void OnPointerMoved(double x, double y)
    {
        try
        {
            var hit = View?.HitAt(x, y);
            var id = hit?.Item.Note?.Id;
            if (View is not null && View.Reveal.HoverTabId != id)
            {
                View.Reveal.HoverTabId = id;
                View.Refresh();
                ScheduleHoverAction(id);
            }
        }
        catch (Exception ex)
        {
            DeckLog.Write("CTRL", $"OnPointerMoved EX: {ex.Message}");
        }
    }

    private void OnLeftButtonDown(double x, double y)
    {
        try
        {
            if (View?.HitAt(x, y) is { } hit)
                OnItemPressed(hit.Item, x, y);
        }
        catch (Exception ex)
        {
            DeckLog.Write("CTRL", $"OnLeftButtonDown EX: {ex.Message}");
        }
    }

    private void OnRightButtonDown(double x, double y)
    {
        try
        {
            if (View?.HitAt(x, y) is { Item: { Kind: RenderItemKind.Tab or RenderItemKind.ChipTab } } hit)
                OnTabRightClicked(hit.Item);
        }
        catch (Exception ex)
        {
            DeckLog.Write("CTRL", $"OnRightButtonDown EX: {ex.Message}");
        }
    }

    // MARK: - Enter/exit poll

    private void StartPoll()
    {
        _pollTimer = new System.Timers.Timer(40) { AutoReset = true };
        _pollTimer.Elapsed += (_, _) => _dispatcher.BeginInvoke(() => PollTick());
        _pollTimer.Start();
    }

    private void PollTick()
    {
        if (_disposed) return;
        try { PollTickCore(); }
        catch (Exception ex) { DeckLog.Write("CTRL", $"PollTick EX {ex}"); }
    }

    private void PollTickCore()
    {
        var (cx, cy) = DeckWindow.CursorPos();
        var r = Window.ScreenRect();
        var inside = cx >= r.Left && cx < r.Right && cy >= r.Top && cy < r.Bottom;

        if (inside)
        {
            CancelExitWork();
            if (!_inside) { _inside = true; OnPointerEntered(); }
            return;
        }

        if (!_inside) return;
        if (_exitWork is not null) return;
        _exitWork = new System.Threading.Timer(_ => _dispatcher.BeginInvoke(() =>
        {
            try
            {
                if (_disposed) return;
                CancelExitWork();
                var (px, py) = DeckWindow.CursorPos();
                var rect = Window.ScreenRect();
                var zone = HotZone.ForPanel(
                    new PanelFrame(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top),
                    Model.OnLeftEdge);
                if (!zone.Contains(px, py))
                {
                    _inside = false;
                    OnPointerExited();
                }
            }
            catch (Exception ex)
            {
                DeckLog.Write("CTRL", $"ExitWork EX: {ex.Message}");
            }
        }), null, 150, System.Threading.Timeout.Infinite);
    }

    private void CancelExitWork()
    {
        _exitWork?.Dispose();
        _exitWork = null;
    }

    // MARK: - Layout

    public void Relayout(DisplayRect display)
    {
        try { RelayoutCore(display); }
        catch (Exception ex) { DeckLog.Write("CTRL", $"Relayout EX {ex}"); }
    }

    private void RelayoutCore(DisplayRect display)
    {
        _display = display;
        DeckGeom.Scale = Model.DeckScale;
        StateMachine.RestingState = Model.DeckAlwaysShown ? DeckState.Fan : DeckState.Rest;

        var dpi = Window.DpiScale;
        var displayDips = display.ToDips(dpi);
        var frame = Geometry.DeckFrame.Layout(
            StateMachine.State, displayDips, Model.OnLeftEdge,
            Math.Max(1, Model.NoteCount), Model.NoteWidth,
            Model.EdgeWidth, Model.DeckYRatio);
        DeckLog.Write("CTRL", $"Relayout state={StateMachine.State} frame=({frame.X:F0},{frame.Y:F0}) {frame.Width:F0}x{frame.Height:F0} noteCount={Model.NoteCount}");

        Window.SetFrame(frame.X, frame.Y, frame.Width, frame.Height);
        _dispatcher.BeginInvoke(() =>
        {
            if (_disposed || View is null) return;
            View.Resize(frame.Width, frame.Height);
        });

        if (StateMachine.State == DeckState.Rest && Model.PillHidden)
            Window.Hide();
        else
            Window.Show();
    }

    public void ApplyLevel(bool overFullScreen)
    {
        _showOverFullScreen = overFullScreen;
        Window.ApplyLevel(overFullScreen);
    }

    public void ForceRelayout() => Relayout(_display);

    // MARK: - Input handlers

    public void OnPointerEntered()
    {
        var d = StateMachine.Process(DeckInput.PointerEntered, Seconds());
        Apply(d);
    }

    public void OnPointerExited()
    {
        var d = StateMachine.Process(DeckInput.PointerExitedHotZone, Seconds());
        Apply(d);
    }

    public void OnExpand(string noteId)
    {
        if (View is not null) View.Reveal.ExpandedNoteId = noteId;
        var d = StateMachine.Process(DeckInput.ExpandNote, Seconds());
        Apply(d);
    }

    public void OnCollapse() => Apply(StateMachine.Process(DeckInput.Collapse, Seconds()));
    public void OnDismiss() => Apply(StateMachine.Process(DeckInput.Dismiss, Seconds()));
    public void OnDetachConfirmed() => Apply(StateMachine.Process(DeckInput.DetachConfirmed, Seconds()));
    public void OnDragStarted() => Apply(StateMachine.Process(DeckInput.DragStarted, Seconds()));
    public void OnDragEnded() => Apply(StateMachine.Process(DeckInput.DragEnded, Seconds()));

    public string? ExpandedNoteId
    {
        get
        {
            if (StateMachine.State != DeckState.Expanded) return null;
            return View?.Reveal.ExpandedNoteId;
        }
    }

    // MARK: - Tab interactions

    public void OnTabRightClicked(RenderItem item)
    {
        if (item.Note is not { } n) return;
        try
        {
            var menu = new System.Windows.Controls.ContextMenu();
            var pin = new System.Windows.Controls.MenuItem { Header = n.Pinned ? "Unpin" : "Pin" };
            pin.Click += (_, _) => Notes?.TogglePin(n.Id);
            var archive = new System.Windows.Controls.MenuItem { Header = "Archive" };
            archive.Click += (_, _) => { if (Notes is not null) { Notes.SetArchived(n.Id, true); OnCollapse(); } };
            var cycle = new System.Windows.Controls.MenuItem { Header = "Cycle color" };
            cycle.Click += (_, _) => Notes?.CycleColor(n.Id);
            var del = new System.Windows.Controls.MenuItem { Header = "Delete" };
            del.Click += (_, _) =>
            {
                if (Notes is null) return;
                Notes.Delete(n.Id, TimeSpan.FromSeconds(10));
                if (ExpandedNoteId == n.Id) OnCollapse();
            };
            menu.Items.Add(pin);
            menu.Items.Add(archive);
            menu.Items.Add(cycle);
            menu.Items.Add(new System.Windows.Controls.Separator());
            menu.Items.Add(del);
            menu.IsOpen = true;
        }
        catch (Exception ex)
        {
            DeckLog.Write("CTRL", $"OnTabRightClicked EX: {ex.Message}");
        }
    }

    public void OnItemPressed(RenderItem item, double x, double y)
    {
        switch (item.Kind)
        {
            case RenderItemKind.Tab:
            case RenderItemKind.ChipTab:
                if (item.Note is not { } n) return;
                if (ExpandedNoteId == n.Id) OnCollapse();
                else OnExpand(n.Id);
                break;
            case RenderItemKind.EmptyTab:
            case RenderItemKind.PlusButton:
                if (Notes is null) return;
                var created = Notes.Create();
                OnExpand(created.Id);
                break;
            case RenderItemKind.MoreTab:
                OnMoreClicked?.Invoke();
                break;
            case RenderItemKind.CogButton:
                OnCogClicked?.Invoke();
                break;
        }
    }

    // MARK: - Effect application

    private void Apply(DeckDecision decision)
    {
        try { ApplyCore(decision); }
        catch (Exception ex) { DeckLog.Write("CTRL", $"Apply EX state={decision.Next}: {ex.Message}"); }
    }

    private void ApplyCore(DeckDecision decision)
    {
        var prev = StateMachine.State;
        StateMachine.State = decision.Next;
        DeckLog.Write("CTRL", $"Apply state={decision.Next} effects=[{string.Join(",", decision.Effects)}]");

        var expanded = decision.Next == DeckState.Expanded;
        if (!expanded && View is not null)
        {
            // CRITICAL: Hide the editor BEFORE resizing the window.
            // Resizing with the XAML editor still visible can crash WPF's
            // rendering thread on a transparent overlay window.
            View.Reveal.ExpandedNoteId = null;
            View.SyncEditor(null, !Model.OnLeftEdge, Model.NoteFontSize);
        }

        if (Has(decision, DeckEffect.CancelExitWork)) CancelExitWork();

        if (Has(decision, DeckEffect.ShowPill) ||
            Has(decision, DeckEffect.ShowFan) ||
            Has(decision, DeckEffect.ShowExpanded))
        {
            Relayout(_display);
        }

        if (Has(decision, DeckEffect.ShowFan) && View is not null)
        {
            View.Reveal.RevealStart = Seconds();
            StartAnimTimer();
        }

        if (Has(decision, DeckEffect.ShowPill) && View is not null)
        {
            View.Reveal.RevealStart = -1;
            StopAnimTimer();
            CancelHoverPreview();
        }

        if (Has(decision, DeckEffect.StartIdleWatch)) StartIdleWatch();
        if (Has(decision, DeckEffect.StopIdleWatch)) StopIdleWatch();

        Window.SetAcceptsActivation(expanded);
        if (expanded && prev != DeckState.Expanded)
            Window.ActivateForInput();

        if (decision.Next == DeckState.Rest && Model.PillHidden)
            Window.Hide();
        else
            Window.Show();

        View?.Refresh();
        if (expanded)
            SyncEditorIfExpanded();
    }

    private void SyncEditorIfExpanded()
    {
        if (View is null) return;
        var onRight = !Model.OnLeftEdge;
        if (StateMachine.State == DeckState.Expanded &&
            ExpandedNoteId is { } id && Notes?.ById(id) is { } note)
        {
            View.SyncEditor(note, onRight, Model.NoteFontSize);
        }
        else
        {
            View.SyncEditor(null, onRight, Model.NoteFontSize);
        }
    }

    private static bool Has(DeckDecision d, DeckEffect effect) => d.Effects.Contains(effect);
    private static double Seconds() => Environment.TickCount / 1000.0;

    // MARK: - Idle watch

    private void StartIdleWatch()
    {
        if (_idleTimer != null) return;
        _idleTimer = new System.Timers.Timer(120) { AutoReset = true };
        _idleTimer.Elapsed += (_, _) =>
        {
            _dispatcher.BeginInvoke(() =>
            {
                if (_disposed) return;
                var d = StateMachine.Process(DeckInput.IdleTick, Seconds());
                Apply(d);
            });
        };
        _idleTimer.Start();
    }

    private void StopIdleWatch()
    {
        _idleTimer?.Stop();
        _idleTimer?.Dispose();
        _idleTimer = null;
    }

    // MARK: - Stagger animation timer

    private void StartAnimTimer()
    {
        StopAnimTimer();
        _animTimer = new System.Timers.Timer(16) { AutoReset = true };
        _animTimer.Elapsed += (_, _) =>
        {
            _dispatcher.BeginInvoke(() =>
            {
                if (_disposed || View is null) { StopAnimTimer(); return; }
                View.Refresh();
                var now = Seconds();
                var start = View.Reveal.RevealStart;
                if (start < 0) { StopAnimTimer(); return; }
                var elapsed = now - start;
                if (elapsed > 0.6)
                {
                    View.Reveal.RevealStart = -1;
                    StopAnimTimer();
                }
            });
        };
        _animTimer.Start();
    }

    private void StopAnimTimer()
    {
        _animTimer?.Stop();
        _animTimer?.Dispose();
        _animTimer = null;
    }

    // MARK: - Hover preview / OpenOnHover

    private void ScheduleHoverAction(string? noteId)
    {
        CancelHoverPreview();
        if (noteId is null || View is null) return;
        if (StateMachine.State != DeckState.Fan) return;

        var delay = Model.OpenOnHover ? 400 : (Model.TabPreview ? 180 : 0);
        if (delay <= 0) return;

        _hoverTimer = new System.Timers.Timer(delay) { AutoReset = false };
        _hoverTimer.Elapsed += (_, _) =>
        {
            _dispatcher.BeginInvoke(() =>
            {
                _hoverTimer = null;
                if (_disposed || View is null) return;
                if (StateMachine.State != DeckState.Fan) return;
                if (View.Reveal.HoverTabId != noteId) return;

                if (Model.OpenOnHover) OnExpand(noteId);
                else if (Model.TabPreview)
                {
                    View.Reveal.PreviewNoteId = noteId;
                    View.Refresh();
                }
            });
        };
        _hoverTimer.Start();
    }

    private void CancelHoverPreview()
    {
        _hoverTimer?.Stop();
        _hoverTimer?.Dispose();
        _hoverTimer = null;
        if (View is not null && View.Reveal.PreviewNoteId is not null)
        {
            View.Reveal.PreviewNoteId = null;
            View.Refresh();
        }
    }

    // MARK: - Disposal

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CancelExitWork();
        StopIdleWatch();
        StopAnimTimer();
        CancelHoverPreview();
        _notesSub?.Dispose();
        _notesSub = null;
        _pollTimer?.Stop();
        _pollTimer?.Dispose();
        _pollTimer = null;
        Window.Hide();
        Window.Dispose();
    }

    private void OnNotesChanged()
    {
        if (_disposed || View is null) return;
        View.Refresh();
        SyncEditorIfExpanded();
    }

    private sealed class NoteChangeObserver : IObserver<NoteList>
    {
        private readonly DeckController _c;
        public NoteChangeObserver(DeckController c) { _c = c; }
        public void OnNext(NoteList value) => _c.OnNotesChanged();
        public void OnCompleted() { }
        public void OnError(Exception error) { }
    }
}
