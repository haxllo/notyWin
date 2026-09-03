using NotyWin.App.Geometry;
using Xunit;

namespace NotyWin.Geometry.Tests;

/// <summary>
/// Tests the pure deck state machine. Port of the Swift transitions in
/// Sources/DeckController.swift lines 188-270.
/// </summary>
public class DeckStateMachineTests
{
    [Fact]
    public void Rest_PointerEntered_TransitionsToFan()
    {
        var sm = new DeckStateMachine(DeckState.Rest);
        var d = sm.Process(DeckInput.PointerEntered, now: 0);
        Assert.Equal(DeckState.Fan, d.Next);
        Assert.Contains(DeckEffect.StartIdleWatch, d.Effects);
        Assert.Contains(DeckEffect.DeactivateSiblingDecks, d.Effects);
    }

    [Fact]
    public void Fan_PointerEntered_StaysFan()
    {
        var sm = new DeckStateMachine(DeckState.Fan);
        var d = sm.Process(DeckInput.PointerEntered, now: 0);
        Assert.Equal(DeckState.Fan, d.Next);
        Assert.Empty(d.Effects);
    }

    [Fact]
    public void Fan_PointerExited_HotZone_CollapsesToRest()
    {
        var sm = new DeckStateMachine(DeckState.Fan) { RestingState = DeckState.Rest };
        var d = sm.Process(DeckInput.PointerExitedHotZone, now: 0);
        Assert.Equal(DeckState.Rest, d.Next);
        Assert.Contains(DeckEffect.StopIdleWatch, d.Effects);
        Assert.Contains(DeckEffect.Hide, d.Effects);
    }

    [Fact]
    public void Fan_PointerExited_WhileRestingIsFan_StaysFan()
    {
        var sm = new DeckStateMachine(DeckState.Fan) { RestingState = DeckState.Fan };
        var d = sm.Process(DeckInput.PointerExitedHotZone, now: 0);
        Assert.Equal(DeckState.Fan, d.Next);
    }

    [Fact]
    public void Fan_IdleTimeout_CollapsesToRest()
    {
        var sm = new DeckStateMachine(DeckState.Fan) { FanIdleTimeout = 4.0 };
        sm.Process(DeckInput.PointerEntered, now: 0);   // activates idle watch
        // Idle ticks at t=3.9 — no collapse yet.
        Assert.Equal(DeckState.Fan, sm.Process(DeckInput.IdleTick, now: 3.9).Next);
        // Idle ticks at t=4.1 — collapses.
        var d = sm.Process(DeckInput.IdleTick, now: 4.1);
        Assert.Equal(DeckState.Rest, d.Next);
        Assert.Contains(DeckEffect.Hide, d.Effects);
    }

    [Fact]
    public void Expanded_IdleTimeout_Dismisses()
    {
        var sm = new DeckStateMachine(DeckState.Expanded) { NoteIdleTimeout = 60.0 };
        sm.Process(DeckInput.ExpandNote, now: 0);
        var d = sm.Process(DeckInput.IdleTick, now: 61);
        Assert.Equal(DeckState.Rest, d.Next);
        Assert.Contains(DeckEffect.DeactivateApp, d.Effects);
    }

    [Fact]
    public void Expanded_PinnedNote_IdleDoesNotDismiss()
    {
        var sm = new DeckStateMachine(DeckState.Expanded) { NoteIdleTimeout = 60.0, PinnedNoteOpen = true };
        sm.Process(DeckInput.ExpandNote, now: 0);
        var d = sm.Process(DeckInput.IdleTick, now: 999);
        Assert.Equal(DeckState.Expanded, d.Next);
    }

    [Fact]
    public void Expanded_Collapse_StepsToFan_NotRest()
    {
        var sm = new DeckStateMachine(DeckState.Expanded);
        sm.Process(DeckInput.ExpandNote, now: 0);
        var d = sm.Process(DeckInput.Collapse, now: 0);
        Assert.Equal(DeckState.Fan, d.Next);
        Assert.Contains(DeckEffect.DeactivateApp, d.Effects);
        // No Hide: the fan stays visible.
        Assert.DoesNotContain(DeckEffect.Hide, d.Effects);
    }

    [Fact]
    public void Expanded_Dismiss_JumpsToRest()
    {
        var sm = new DeckStateMachine(DeckState.Expanded);
        sm.Process(DeckInput.ExpandNote, now: 0);
        var d = sm.Process(DeckInput.Dismiss, now: 0);
        Assert.Equal(DeckState.Rest, d.Next);
        Assert.Contains(DeckEffect.DeactivateApp, d.Effects);
    }

    [Fact]
    public void Expand_ActivatesApp_AndStartsIdleWatch()
    {
        var sm = new DeckStateMachine(DeckState.Fan);
        var d = sm.Process(DeckInput.ExpandNote, now: 0);
        Assert.Equal(DeckState.Expanded, d.Next);
        Assert.Contains(DeckEffect.ActivateApp, d.Effects);
        Assert.Contains(DeckEffect.ShowExpanded, d.Effects);
        Assert.Contains(DeckEffect.StartIdleWatch, d.Effects);
    }

    [Fact]
    public void DragStarted_FromRest_StaysRest()
    {
        var sm = new DeckStateMachine(DeckState.Rest);
        var d = sm.Process(DeckInput.DragStarted, now: 0);
        Assert.Equal(DeckState.Rest, d.Next);
        Assert.True(sm.IsDragging);
    }

    [Fact]
    public void DragStarted_FromFan_StaysFan_ButHostCanCollapseFirst()
    {
        // Swift's tab drag flips isDragging without changing state; the host
        // decides to fold to rest for a *pill* drag via Collapse.
        var sm = new DeckStateMachine(DeckState.Fan);
        var d = sm.Process(DeckInput.DragStarted, now: 0);
        Assert.Equal(DeckState.Fan, d.Next);
        Assert.True(sm.IsDragging);
    }

    [Fact]
    public void PillDrag_HostFoldsFanToRest_BeforeDragStarted()
    {
        // The pill-drag path: host sends Collapse first (so the pill is the
        // thing on screen), then DragStarted.
        var sm = new DeckStateMachine(DeckState.Fan);
        Assert.Equal(DeckState.Rest, sm.Process(DeckInput.Collapse, now: 0).Next);
        var d = sm.Process(DeckInput.DragStarted, now: 0);
        Assert.Equal(DeckState.Rest, d.Next);
        Assert.True(sm.IsDragging);
    }

    [Fact]
    public void IdleTick_DuringDrag_DoesNotCollapseFan()
    {
        var sm = new DeckStateMachine(DeckState.Fan) { FanIdleTimeout = 4.0 };
        sm.Process(DeckInput.PointerEntered, now: 0);
        sm.Process(DeckInput.DragStarted, now: 5);
        var d = sm.Process(DeckInput.IdleTick, now: 999);
        Assert.Equal(DeckState.Fan, d.Next);
    }

    [Fact]
    public void DetachConfirmed_StepsBackToFan()
    {
        var sm = new DeckStateMachine(DeckState.Expanded) { DetachingId = "abc" };
        sm.Process(DeckInput.ExpandNote, now: 0);
        var d = sm.Process(DeckInput.DetachConfirmed, now: 0);
        Assert.Equal(DeckState.Fan, d.Next);
        Assert.Null(sm.DetachingId);
    }
}