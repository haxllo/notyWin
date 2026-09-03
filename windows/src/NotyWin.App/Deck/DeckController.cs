using NotyWin.App.Geometry;
using NotyWin.App.Models;
using NotyWin.Rendering;
using DeckFrame = NotyWin.App.Geometry.DeckFrame;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace NotyWin.App.Deck;

public sealed class DeckModel
{
    // Mirrored from Settings so observers re-render when a preference flips.
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

/// <summary>
/// One-deck-per-display controller. Owns a <see cref="DeckStateMachine"/> and a
/// <see cref="DeckWindow"/>; turns machine decisions into Win32 effects.
///
/// Pointer enter/exit comes from polling <see cref="DeckWindow.CursorPos"/>
/// against the panel rect — the macOS app polls <c>NSEvent.mouseLocation</c>
/// for the same reason: the panel's hit region changes shape on every state
/// change, so event-driven enter/exit fires spuriously during resize.
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
    private readonly DispatcherQueue _dispatcher;

    public DeckController(uint displayId, DisplayRect display, bool showOverFullScreen)
    {
        DisplayId = displayId;
        _display = display;
        _showOverFullScreen = showOverFullScreen;
        Window = new DeckWindow();
        Window.ApplyLevel(showOverFullScreen);
        _dispatcher = Window.Window.DispatcherQueue;
        StateMachine.RestingState = Model.DeckAlwaysShown ? DeckState.Fan : DeckState.Rest;
        StateMachine.State = StateMachine.RestingState;

        View = new DeckView
        {
            OnRightEdge = !Model.OnLeftEdge,
        };
        Window.Window.Content = View;
        Window.Window.Closed += (_, _) => DeckLog.Write("CTRL", $"deck window CLOSED display={DisplayId}");

        // Blank panel regions are click-through (HTTRANSPARENT); anything
        // over a drawn item takes the click.
        Window.InteractiveFilter = (x, y) => View.HitAt(x, y) is not null;
        Window.PointerMoved += (x, y) => OnPointerMoved(x, y);
        Window.LeftButtonDown += (x, y) => OnLeftButtonDown(x, y);
        Window.RightButtonDown += (x, y) => OnRightButtonDown(x, y);

        // Size the panel first, then show. Showing before sizing leaves the
        // window at its default 800x600 size and the resize doesn't take.
        Relayout(display);
        Window.Show();
        StartPoll();
    }

    public void WireViewModel()
    {
        if (Notes is not null && Settings is not null && View is not null)
        {
            View.ViewModel = new DeckViewModel(Notes, () => Settings.Load());
            View.OnRightEdge = !Settings.Load().DeckOnLeftEdge;

            // The editor mutates the shared NoteList directly (like the macOS
            // view calls NoteStore.shared) and asks us to collapse on close.
            View.Editor.Notes = Notes;
            View.Editor.OnRequestCollapse = () => OnCollapse();

            // Repaint tabs and re-sync the editor whenever a note changes.
            _notesSub?.Dispose();
            _notesSub = Notes.Subscribe(new NoteChangeObserver(this));

            DeckLog.Write("CTRL", $"WireViewModel notes={Notes.ActiveCount}");
        }
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

    // MARK: - Pointer

    private void OnPointerMoved(double x, double y)
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

    private void OnLeftButtonDown(double x, double y)
    {
        if (View?.HitAt(x, y) is { } hit)
            OnItemPressed(hit.Item, x, y);
    }

    private void OnRightButtonDown(double x, double y)
    {
        if (View?.HitAt(x, y) is { Item: { Kind: RenderItemKind.Tab or RenderItemKind.ChipTab } } hit)
            OnTabRightClicked(hit.Item);
    }

    // MARK: - Enter/exit poll

    private void StartPoll()
    {
        _pollTimer = new System.Timers.Timer(40) { AutoReset = true };
        _pollTimer.Elapsed += (_, _) => _dispatcher.TryEnqueue(PollTick);
        _pollTimer.Start();
    }

    private void PollTick()
    {
        if (_disposed) return;
        try
        {
            PollTickCore();
        }
        catch (Exception ex)
        {
            DeckLog.Write("CTRL", $"PollTick EX {ex}");
        }
    }

    private void PollTickCore()
    {
        var (cx, cy) = DeckWindow.CursorPos();
        var r = Window.ScreenRect();
        var inside = cx >= r.Left && cx < r.Right && cy >= r.Top && cy < r.Bottom;

        if (inside)
        {
            CancelExitWork();
            if (!_inside)
            {
                _inside = true;
                OnPointerEntered();
            }
            return;
        }

        if (!_inside) return;

        // Left the panel: debounce, then confirm against the hot zone before
        // folding — the same 0.15 s re-check the macOS app does.
        if (_exitWork is not null) return;
        _exitWork = new System.Threading.Timer(_ => _dispatcher.TryEnqueue(() =>
        {
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
        try
        {
            RelayoutCore(display);
        }
        catch (Exception ex)
        {
            DeckLog.Write("CTRL", $"Relayout EX {ex}");
        }
    }

    private void RelayoutCore(DisplayRect display)
    {
        _display = display;
        DeckGeom.Scale = Model.DeckScale;
        StateMachine.RestingState = Model.DeckAlwaysShown ? DeckState.Fan : DeckState.Rest;

        // Convert physical-pixel display rect to DIPs so the frame and the
        // panel agree on units.
        var dpi = Window.DpiScale;
        var displayDips = display.ToDips(dpi);
        var frame = DeckFrame.Layout(
            StateMachine.State, displayDips, Model.OnLeftEdge,
            Math.Max(1, Model.NoteCount), Model.NoteWidth,
            Model.EdgeWidth, Model.DeckYRatio);
        DeckLog.Write("CTRL", $"Relayout state={StateMachine.State} frame=({frame.X:F0},{frame.Y:F0}) {frame.Width:F0}x{frame.Height:F0} noteCount={Model.NoteCount} dpi={dpi:F2}");
        Window.SetFrame(frame.X, frame.Y, frame.Width, frame.Height);
        View?.Resize(frame.Width, frame.Height);

        // Rest = pill IS visible. The window is the detection strip.
        if (StateMachine.State == DeckState.Rest)
            Window.Show();
    }

    public void ApplyLevel(bool overFullScreen)
    {
        _showOverFullScreen = overFullScreen;
        Window.ApplyLevel(overFullScreen);
    }

    // MARK: - Input handlers — drive the state machine

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

    /// <summary>Id of the note currently open, or null.</summary>
    public string? ExpandedNoteId
    {
        get
        {
            if (StateMachine.State != DeckState.Expanded) return null;
            if (View?.Reveal.ExpandedNoteId is { } id) return id;
            return null;
        }
    }

    /// <summary>Right-click on a tab. Shows a context menu with the same actions
    /// as the macOS <c>noteContextMenu</c> — pin, archive, cycle colour, delete.</summary>
    public void OnTabRightClicked(RenderItem item)
    {
        if (item.Note is not { } n) return;
        var flyout = new MenuFlyout();
        var pin = new MenuFlyoutItem { Text = n.Pinned ? "Unpin" : "Pin" };
        pin.Click += (_, _) => Notes?.TogglePin(n.Id);
        var archive = new MenuFlyoutItem { Text = "Archive" };
        archive.Click += (_, _) => { if (Notes is not null) { Notes.SetArchived(n.Id, true); OnCollapse(); } };
        var cycle = new MenuFlyoutItem { Text = "Cycle color" };
        cycle.Click += (_, _) => Notes?.CycleColor(n.Id);
        var del = new MenuFlyoutItem { Text = "Delete" };
        del.Click += (_, _) =>
        {
            if (Notes is null) return;
            Notes.Delete(n.Id, TimeSpan.FromSeconds(10));
            if (ExpandedNoteId == n.Id) OnCollapse();
        };
        flyout.Items.Add(pin);
        flyout.Items.Add(archive);
        flyout.Items.Add(cycle);
        flyout.Items.Add(new MenuFlyoutSeparator());
        flyout.Items.Add(del);
        flyout.ShowAt(View!, new FlyoutShowOptions { Position = new Windows.Foundation.Point(0, 0) });
    }

    /// <summary>Translate a paint-time hit into a state-machine command.</summary>
    public void OnItemPressed(RenderItem item, double x, double y)
    {
        switch (item.Kind)
        {
            case RenderItemKind.Tab:
            case RenderItemKind.ChipTab:
                if (item.Note is not { } n) return;
                if (ExpandedNoteId == n.Id)
                    OnCollapse();
                else
                    OnExpand(n.Id);
                break;
            case RenderItemKind.EmptyTab:
            case RenderItemKind.PlusButton:
                if (Notes is null) return;
                var created = Notes.Create();
                OnExpand(created.Id);
                break;
            case RenderItemKind.MoreTab:
            case RenderItemKind.CogButton:
                // Library / Settings — step 9.
                break;
        }
    }

    // MARK: - Effect application

    private void Apply(DeckDecision decision)
    {
        var prev = StateMachine.State;
        StateMachine.State = decision.Next;
        DeckLog.Write("CTRL", $"Apply state={decision.Next} effects=[{string.Join(",", decision.Effects)}]");

        var expanded = decision.Next == DeckState.Expanded;
        if (!expanded && View is not null)
            View.Reveal.ExpandedNoteId = null;

        if (Has(decision, DeckEffect.CancelExitWork))
            CancelExitWork();

        // The "Hide" effect from the state machine is ignored on Windows.
        // macOS hides the panel because the user can re-trigger it from the
        // global menu bar; Windows has no equivalent surface, so the panel
        // must always be visible.

        if (Has(decision, DeckEffect.ShowPill) ||
            Has(decision, DeckEffect.ShowFan) ||
            Has(decision, DeckEffect.ShowExpanded))
        {
            Relayout(_display);
        }

        // Start stagger animation when entering Fan state.
        if (Has(decision, DeckEffect.ShowFan) && View is not null)
        {
            View.Reveal.RevealStart = Seconds();
            StartAnimTimer();
        }

        // Reset reveal when collapsing to pill.
        if (Has(decision, DeckEffect.ShowPill) && View is not null)
        {
            View.Reveal.RevealStart = -1;
            StopAnimTimer();
            CancelHoverPreview();
        }

        if (Has(decision, DeckEffect.StartIdleWatch)) StartIdleWatch();
        if (Has(decision, DeckEffect.StopIdleWatch)) StopIdleWatch();

        // Only an open note takes the keyboard; the deck is non-activating the
        // rest of the time so hovering or clicking it never steals focus. On the
        // transition into Expanded, make the just-clicked panel key so the
        // editor's TextBox can receive input.
        Window.SetAcceptsActivation(expanded);
        if (expanded && prev != DeckState.Expanded)
            Window.ActivateForInput();

        // DeckPillHidden: when set, hide the pill window at rest.
        if (decision.Next == DeckState.Rest && Model.PillHidden)
            Window.Hide();
        else
            Window.Show();

        View?.Refresh();
        SyncEditorIfExpanded();
    }

    /// <summary>Show the XAML editor over the expanded note's rect, or hide it
    /// (flushing pending edits) when no note is open.</summary>
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
            _dispatcher.TryEnqueue(() =>
            {
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

    /// <summary>Drives the fan-stagger reveal at ~60 fps until all tabs are
    /// fully revealed (StageProgress returns 1.0 for every item).</summary>
    private void StartAnimTimer()
    {
        StopAnimTimer();
        _animTimer = new System.Timers.Timer(16) { AutoReset = true };
        _animTimer.Elapsed += (_, _) =>
        {
            _dispatcher.TryEnqueue(() =>
            {
                if (_disposed || View is null) { StopAnimTimer(); return; }
                View.Refresh();
                // Stop once the last tab's reveal is complete.
                var now = Seconds();
                var start = View.Reveal.RevealStart;
                if (start < 0) { StopAnimTimer(); return; }
                var elapsed = now - start;
                // Max delay = FanLimit * 0.042 + 0.34 spring ≈ 0.55s
                if (elapsed > 0.6)
                {
                    View.Reveal.RevealStart = -1; // settled
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

    /// <summary>Schedule a hover preview or open-on-hover after the configured
    /// delay. Called from <see cref="OnPointerMoved"/> when a tab is hovered.</summary>
    private void ScheduleHoverAction(string? noteId)
    {
        CancelHoverPreview();
        if (noteId is null || View is null) return;
        if (StateMachine.State != DeckState.Fan) return;

        // OpenOnHover takes priority over preview (same as macOS).
        var delay = Model.OpenOnHover ? 400 : (Model.TabPreview ? 180 : 0);
        if (delay <= 0) return;

        _hoverTimer = new System.Timers.Timer(delay) { AutoReset = false };
        _hoverTimer.Elapsed += (_, _) =>
        {
            _dispatcher.TryEnqueue(() =>
            {
                _hoverTimer = null;
                if (_disposed || View is null) return;
                if (StateMachine.State != DeckState.Fan) return;
                if (View.Reveal.HoverTabId != noteId) return;

                if (Model.OpenOnHover)
                {
                    OnExpand(noteId);
                }
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

    // MARK: - Escape dismiss

    /// <summary>Handle Escape key: close find bar → collapse note → dismiss
    /// deck. Mirrors the macOS Esc chain.</summary>
    public void HandleEscape()
    {
        if (StateMachine.State == DeckState.Expanded)
        {
            // If find bar is open, close it first (handled by NoteEditorControl).
            OnCollapse();
        }
        else if (StateMachine.State == DeckState.Fan)
        {
            OnDismiss();
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
}
