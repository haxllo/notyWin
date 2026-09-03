using NotyWin.App.Geometry;

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

    public void SyncPreferences()
    {
        DeckGeom.Scale = DeckScale;
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

    private System.Timers.Timer? _idleTimer;
    private System.Threading.Timer? _exitWork;
    private DisplayRect _display;
    private bool _showOverFullScreen;
    private bool _disposed;

    public DeckController(uint displayId, DisplayRect display, bool showOverFullScreen)
    {
        DisplayId = displayId;
        _display = display;
        _showOverFullScreen = showOverFullScreen;
        Window = new DeckWindow();
        Window.ApplyLevel(showOverFullScreen);
        StateMachine.RestingState = Model.DeckAlwaysShown ? DeckState.Fan : DeckState.Rest;
        StateMachine.State = StateMachine.RestingState;
        Relayout(display);
        Window.Show();
    }

    public void Relayout(DisplayRect display)
    {
        _display = display;
        Model.SyncPreferences();
        StateMachine.RestingState = Model.DeckAlwaysShown ? DeckState.Fan : DeckState.Rest;

        var frame = DeckFrame.Layout(
            StateMachine.State, display, Model.OnLeftEdge,
            Math.Max(1, Model.NoteCount), Model.NoteWidth,
            Model.EdgeWidth, Model.DeckYRatio);
        Window.SetFrame(frame.X, frame.Y, frame.Width, frame.Height);

        if (StateMachine.State == DeckState.Rest)
            Window.Hide();
        else
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
            var d = StateMachine.Process(DeckInput.PointerExitedHotZone, Seconds());
            Apply(d);
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
        var d = StateMachine.Process(DeckInput.ExpandNote, Seconds());
        Apply(d);
    }

    public void OnCollapse() => Apply(StateMachine.Process(DeckInput.Collapse, Seconds()));
    public void OnDismiss() => Apply(StateMachine.Process(DeckInput.Dismiss, Seconds()));
    public void OnDetachConfirmed() => Apply(StateMachine.Process(DeckInput.DetachConfirmed, Seconds()));

    public void OnDragStarted() => Apply(StateMachine.Process(DeckInput.DragStarted, Seconds()));
    public void OnDragEnded() => Apply(StateMachine.Process(DeckInput.DragEnded, Seconds()));

    // MARK: - Effect application

    private void Apply(DeckDecision decision)
    {
        StateMachine.State = decision.Next;
        Model.SyncPreferences();

        // Cancel any pending deferred work.
        if (Has(decision, DeckEffect.CancelExitWork))
        {
            _exitWork?.Dispose();
            _exitWork = null;
        }

        if (Has(decision, DeckEffect.Hide))
        {
            Window.Hide();
            StopIdleWatch();
        }
        else if (Has(decision, DeckEffect.ShowPill) ||
                 Has(decision, DeckEffect.ShowFan) ||
                 Has(decision, DeckEffect.ShowExpanded))
        {
            Relayout(_display);
        }

        if (Has(decision, DeckEffect.StartIdleWatch)) StartIdleWatch();
        if (Has(decision, DeckEffect.StopIdleWatch)) StopIdleWatch();
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
            var d = StateMachine.Process(DeckInput.IdleTick, Seconds());
            Apply(d);
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
        Window.Dispose();
    }
}