namespace NotyWin.App.Geometry;

/// <summary>
/// Inputs the state machine consumes (pointer events, timers, user commands).
/// All times in seconds; all coordinates in screen space (Y down).
/// </summary>
public enum DeckInput
{
    PointerEntered,
    PointerExited,
    PointerMoved,
    ExpandNote,
    Collapse,
    Dismiss,
    DetachConfirmed,
    PointerExitedHotZone,
    IdleTick,
    DragStarted,
    DragEnded,
}

/// <summary>
/// Side effects the state machine asks the host to perform. The state machine
/// itself does not touch the window manager, timers, or settings — only the
/// host does.
/// </summary>
public enum DeckEffect
{
    /// <summary>Hide the deck panel.</summary>
    Hide,
    /// <summary>Show the deck panel at the resting-pill frame.</summary>
    ShowPill,
    /// <summary>Show the deck panel at the fan/expanded frame and start the fan stagger.</summary>
    ShowFan,
    /// <summary>Show the open note.</summary>
    ShowExpanded,
    /// <summary>Start the 0.12s idle poll.</summary>
    StartIdleWatch,
    /// <summary>Stop the idle poll.</summary>
    StopIdleWatch,
    /// <summary>Cancel the pending 0.15s pointer-exit confirmation.</summary>
    CancelExitWork,
    /// <summary>Cancel the pending shrink-after-collapse work.</summary>
    CancelShrinkWork,
    /// <summary>Deactivate the app (no longer key).</summary>
    DeactivateApp,
    /// <summary>Activate the app (frontmost).</summary>
    ActivateApp,
    /// <summary>Pull sibling decks back to rest (one open at a time).</summary>
    DeactivateSiblingDecks,
}

/// <summary>
/// One transition the host should commit: a state change, possibly with effects.
/// </summary>
public sealed class DeckDecision
{
    public required DeckState Next { get; set; }
    public required DeckEffect[] Effects { get; set; }
}

/// <summary>
/// Pure deck state machine. Port of <c>DeckController.setState()</c> +
/// <c>pointerEntered/Exited()</c> + idle watch in
/// <c>Sources/DeckController.swift</c>. Takes inputs, returns a transition.
/// </summary>
public sealed class DeckStateMachine
{
    // Configuration (mirrors Settings.* in the Swift app).
    public DeckState RestingState { get; set; } = DeckState.Rest;
    public bool PinnedNoteOpen { get; set; }
    public bool IsDragging { get; set; }
    public double FanIdleTimeout { get; set; } = 4.0;
    public double NoteIdleTimeout { get; set; } = 60.0;

    // Live state (mirrors DeckModel).
    public DeckState State { get; set; } = DeckState.Rest;
    public double LastActivity { get; private set; } = 0;
    public string? DetachingId { get; set; }
    public string? ShowAll { get; set; }
    public string? FindQuery { get; set; }

    public DeckStateMachine(DeckState initial = DeckState.Rest)
    {
        State = initial;
    }

    public DeckDecision Process(DeckInput input, double now)
    {
        return input switch
        {
            DeckInput.PointerEntered => OnPointerEntered(now),
            DeckInput.PointerExited => OnPointerExited(),
            DeckInput.IdleTick => OnIdleTick(now),
            DeckInput.ExpandNote => OnExpand(now),
            DeckInput.Collapse => OnCollapse(),
            DeckInput.Dismiss => OnDismiss(),
            DeckInput.DetachConfirmed => OnDetachConfirmed(),
            DeckInput.DragStarted => OnDragStarted(now),
            DeckInput.DragEnded => OnDragEnded(),
            DeckInput.PointerExitedHotZone => OnPointerExitedHotZone(),
            _ => Stay(),
        };
    }

    public DeckDecision Stay() => new()
    {
        Next = State,
        Effects = Array.Empty<DeckEffect>(),
    };

    // MARK: - Pointer

    private DeckDecision OnPointerEntered(double now)
    {
        LastActivity = now;
        if (State == DeckState.Rest)
            return TransitionTo(DeckState.Fan,
                DeckEffect.CancelExitWork,
                DeckEffect.CancelShrinkWork,
                DeckEffect.DeactivateSiblingDecks,
                DeckEffect.StartIdleWatch);
        // Already in fan or expanded: keep open, refresh layout.
        return Stay();
    }

    private DeckDecision OnPointerExited()
    {
        // The Swift code debounces this with a 0.15s delayed check; the host
        // owns that timer. The state machine just answers "is leaving a reason
        // to step down?" synchronously.
        if (State == DeckState.Fan && RestingState != DeckState.Fan)
            return Stay();   // host will confirm in 150ms and re-emit PointerExitedHotZone
        return Stay();
    }

    /// <summary>The deferred pointer-exit fired and the cursor is truly outside the hot zone.</summary>
    private DeckDecision OnPointerExitedHotZone()
    {
        if (State != DeckState.Fan || RestingState == DeckState.Fan) return Stay();
        return TransitionTo(RestingState, DeckEffect.StopIdleWatch, DeckEffect.Hide);
    }

    // MARK: - Commands

    private DeckDecision OnExpand(double now)
    {
        LastActivity = now;
        FindQuery = null;
        return TransitionTo(DeckState.Expanded,
            DeckEffect.ShowExpanded,
            DeckEffect.ActivateApp,
            DeckEffect.DeactivateSiblingDecks,
            DeckEffect.StartIdleWatch);
    }

    private DeckDecision OnCollapse()
    {
        FindQuery = null;
        if (State == DeckState.Expanded)
            return TransitionTo(DeckState.Fan,
                DeckEffect.DeactivateApp);
        // From fan or rest: collapse to resting.
        return TransitionTo(RestingState,
            DeckEffect.StopIdleWatch,
            DeckEffect.Hide);
    }

    private DeckDecision OnDismiss()
    {
        var wasExpanded = State == DeckState.Expanded;
        FindQuery = null;
        var dec = TransitionTo(RestingState,
            DeckEffect.StopIdleWatch,
            DeckEffect.Hide);
        if (wasExpanded)
        {
            dec.Effects = dec.Effects.Append(DeckEffect.DeactivateApp).ToArray();
        }
        return dec;
    }

    private DeckDecision OnDetachConfirmed()
    {
        DetachingId = null;
        return TransitionTo(DeckState.Fan, DeckEffect.StopIdleWatch);
    }

    private DeckDecision OnDragStarted(double now)
    {
        // Tab drags (DeckViews.swift) just flip isDragging without touching
        // state. Pill drags (DeckController.beginPillDrag) fold Fan→Rest before
        // setting isDragging — that's the host's call, not the state machine's.
        IsDragging = true;
        return Stay();
    }

    private DeckDecision OnDragEnded()
    {
        IsDragging = false;
        return Stay();
    }

    // MARK: - Idle

    private DeckDecision OnIdleTick(double now)
    {
        // During a drag, the Swift timer resets lastActivity every tick so the
        // deck never collapses under the cursor. The state machine mirrors that
        // here so a single IsDragging flag is enough on the host side.
        if (IsDragging)
        {
            LastActivity = now;
            return Stay();
        }
        if (State == DeckState.Fan)
        {
            // Host checks hot-zone containment separately (cursor coords aren't
            // pure-state). The state machine only owns time-based collapses.
            if (now - LastActivity > FanIdleTimeout)
                return TransitionTo(RestingState,
                    DeckEffect.StopIdleWatch,
                    DeckEffect.Hide);
        }
        if (State == DeckState.Expanded && !PinnedNoteOpen)
        {
            if (now - LastActivity > NoteIdleTimeout)
                return TransitionTo(RestingState,
                    DeckEffect.StopIdleWatch,
                    DeckEffect.Hide,
                    DeckEffect.DeactivateApp);
        }
        return Stay();
    }

    // MARK: - Helper

    private DeckDecision TransitionTo(DeckState next, params DeckEffect[] effects)
    {
        if (next == DeckState.Rest) ShowAll = null;
        if (next == DeckState.Rest) FindQuery = null;
        State = next;
        return new DeckDecision { Next = next, Effects = effects };
    }
}