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
    private System.Threading.Timer? _exitWork;
    private DisplayRect _display;
    private bool _showOverFullScreen;
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
        if (Notes is not null && Settings is not null)
        {
            View.ViewModel = new DeckViewModel(Notes, () => Settings.Load());
            View.OnRightEdge = !Settings.Load().DeckOnLeftEdge;
        }
        // The XAML tree root is the DeckView itself; setting it as the
        // window content is what wires the per-display HWND to the shared
        // WindowsXamlManager. (Previously this went through
        // DesktopWindowXamlSource, which collided with the main thread's
        // WindowsXamlManager.)
        Window.Window.Content = View;
        View.PointerEntered += () => OnPointerEntered();
        View.PointerExited += () => OnPointerExited();
        View.ItemPressed += (item, x, y) => OnItemPressed(item, x, y);
        View.TabRightClicked += item => OnTabRightClicked(item);

        // Raw Win32 pointer events. WinUI's managed pointer events don't
        // fire on a non-foreground borderless window, so we hook the
        // underlying HWND directly and drive the state machine from the
        // raw messages.
        Window.PointerMoved += (x, y) => OnRawPointerMoved(x, y);
        Window.PointerExited += () => OnPointerExited();
        Window.RightButtonDown += (x, y) => OnRawRightDown(x, y);

        // Size the panel first, then show. Showing before sizing leaves the
        // window at its default 800x600 size and the resize doesn't take.
        Relayout(display);
        Window.Show();
    }

    public void WireViewModel()
    {
        if (Notes is not null && Settings is not null && View is not null)
        {
            View.ViewModel = new DeckViewModel(Notes, () => Settings.Load());
            View.OnRightEdge = !Settings.Load().DeckOnLeftEdge;
            Log($"WireViewModel: notes={Notes.ActiveCount} items");
        }
    }

    private void OnRawPointerMoved(double x, double y)
    {
        // The pointer is over the window. Tell the state machine we entered
        // (idempotent — same as DeckView.PointerEntered).
        OnPointerEntered();

        // Hit-test against the live paint frame to drive hover state.
        if (View?.ViewModel is null) return;
        var w = Window.AppWindow.Size.Width;
        var h = Window.AppWindow.Size.Height;
        if (View is null) return;
        var frame = View.GetOrComputeFrame(w, h);
        var hit = HitTest.Test(x, y, frame, w, !Model.OnLeftEdge);
        if (View.Reveal.HoverTabId != hit?.Item.Note?.Id)
        {
            View.Reveal.HoverTabId = hit?.Item.Note?.Id;
            View.Refresh();
        }
    }

    private void OnRawRightDown(double x, double y)
    {
        // Mirror the WinUI RightTapped path: hit-test, find the tab, show
        // context menu.
        if (View?.ViewModel is null) return;
        var w = Window.AppWindow.Size.Width;
        var h = Window.AppWindow.Size.Height;
        var frame = View.GetOrComputeFrame(w, h);
        var hit = HitTest.Test(x, y, frame, w, !Model.OnLeftEdge);
        if (hit is { Item: { Kind: RenderItemKind.Tab or RenderItemKind.ChipTab } } tabHit)
            OnTabRightClicked(tabHit.Item);
    }

    public void Relayout(DisplayRect display)
    {
        _display = display;
        DeckGeom.Scale = Model.DeckScale;
        StateMachine.RestingState = Model.DeckAlwaysShown ? DeckState.Fan : DeckState.Rest;

        var frame = DeckFrame.Layout(
            StateMachine.State, display, Model.OnLeftEdge,
            Math.Max(1, Model.NoteCount), Model.NoteWidth,
            Model.EdgeWidth, Model.DeckYRatio);
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
        _exitWork?.Dispose();
        _exitWork = new System.Threading.Timer(_ =>
        {
            _dispatcher.TryEnqueue(() =>
            {
                var d = StateMachine.Process(DeckInput.PointerExitedHotZone, Seconds());
                Apply(d);
            });
        }, null, 150, System.Threading.Timeout.Infinite);
    }

    public void OnPointerMoved(double x, double y)
    {
        if (StateMachine.State != DeckState.Fan) return;
        if (StateMachine.IsDragging) return;
        var zone = HotZone.ForPanel(new PanelFrame(_display.FullLeft, _display.VisTop, 0, 0), Model.OnLeftEdge);
        // Real hot zone is for the current panel; just check proximity to the
        // relevant edge strip. The host can refine to a PanelFrame if needed.
        _ = zone;
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
                // Library / Settings — step 6.
                break;
        }
    }

    // MARK: - Effect application

    private void Apply(DeckDecision decision)
    {
        StateMachine.State = decision.Next;
        var effectNames = string.Join(",", decision.Effects.Select(e => e.ToString()));
        Log($"Apply: state={decision.Next} effects=[{effectNames}]");

        if (decision.Next != DeckState.Expanded && View is not null)
            View.Reveal.ExpandedNoteId = null;

        // Cancel any pending deferred work.
        if (Has(decision, DeckEffect.CancelExitWork))
        {
            _exitWork?.Dispose();
            _exitWork = null;
        }

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

        if (Has(decision, DeckEffect.StartIdleWatch)) StartIdleWatch();
        if (Has(decision, DeckEffect.StopIdleWatch)) StopIdleWatch();
        View?.Refresh();
    }

    private static bool Has(DeckDecision d, DeckEffect effect) => d.Effects.Contains(effect);

    private static double Seconds() => Environment.TickCount / 1000.0;

    private static void Log(string msg)
    {
        System.IO.File.AppendAllText(
            System.IO.Path.Combine(System.Environment.GetFolderPath(
                System.Environment.SpecialFolder.LocalApplicationData), "Noty", "wndproc.log"),
            $"[{DateTime.UtcNow:O}] CTRL: {msg}\n");
    }

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

    // MARK: - Disposal

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _exitWork?.Dispose();
        _exitWork = null;
        StopIdleWatch();
        Window.Hide();
        Window.Window.Close();
    }
}