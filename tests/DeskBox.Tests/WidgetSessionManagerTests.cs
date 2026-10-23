using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class WidgetSessionManagerTests
{
    [Fact]
    public void MarkRaisedSession_RecordsRaisedState()
    {
        var manager = new WidgetSessionManager();

        manager.MarkRaisedSession("test");

        Assert.Equal(WidgetSessionState.RaisedSession, manager.State);
        Assert.True(manager.IsRaised);
        Assert.False(manager.IsInteractionActive);
    }

    [Fact]
    public void Interaction_ReturnsToPreviousDesktopState()
    {
        var manager = new WidgetSessionManager();

        manager.BeginInteraction("start");
        manager.EndInteraction("end");

        Assert.Equal(WidgetSessionState.DesktopResting, manager.State);
        Assert.False(manager.IsInteractionActive);
    }

    [Fact]
    public void Interaction_ReturnsToPreviousRaisedState()
    {
        var manager = new WidgetSessionManager();
        manager.MarkRaisedSession("raised");

        manager.BeginInteraction("start");
        manager.EndInteraction("end");

        Assert.Equal(WidgetSessionState.RaisedSession, manager.State);
        Assert.True(manager.IsRaised);
        Assert.False(manager.IsInteractionActive);
    }

    [Fact]
    public void NestedInteraction_RemainsActiveUntilAllInteractionsEnd()
    {
        var manager = new WidgetSessionManager();
        manager.MarkRaisedSession("raised");

        manager.BeginInteraction("first");
        manager.BeginInteraction("second");
        manager.EndInteraction("second-ended");

        Assert.Equal(WidgetSessionState.InteractionActive, manager.State);
        Assert.True(manager.IsInteractionActive);

        manager.EndInteraction("first-ended");

        Assert.Equal(WidgetSessionState.RaisedSession, manager.State);
        Assert.False(manager.IsInteractionActive);
    }

    [Fact]
    public void MarkHidden_ClearsInteractionState()
    {
        var manager = new WidgetSessionManager();
        manager.BeginInteraction("start");

        manager.MarkHidden("hidden");

        Assert.Equal(WidgetSessionState.Hidden, manager.State);
        Assert.False(manager.IsRaised);
        Assert.False(manager.IsInteractionActive);
    }

    [Fact]
    public void EndInteraction_WithoutActiveInteraction_DoesNotChangeState()
    {
        var manager = new WidgetSessionManager();
        manager.MarkRaisedSession("raised");

        manager.EndInteraction("stray-end");

        Assert.Equal(WidgetSessionState.RaisedSession, manager.State);
        Assert.False(manager.IsInteractionActive);
    }

    [Fact]
    public void SwallowedPostResetEnd_LeavesPhantomDepth_ReconcileClearsIt()
    {
        var manager = new WidgetSessionManager();
        manager.BeginInteraction("leaked");
        manager.ForceResetInteractions("watchdog");
        // A short-lived interaction opened and closed inside the grace
        // window: its End is swallowed as stale, parking the depth at one.
        manager.BeginInteraction("new");
        manager.EndInteraction("new-ended");

        Assert.True(manager.IsInteractionActive);

        bool reconciled = manager.ReconcilePostResetDepth(
            DateTime.UtcNow.AddSeconds(5),
            foregroundConsistentWithNoLiveInteraction: true);

        Assert.True(reconciled);
        Assert.False(manager.IsInteractionActive);
        Assert.Equal(WidgetSessionState.DesktopResting, manager.State);
    }

    [Fact]
    public void ReconcilePostResetDepth_BeginAfterSwallow_ProtectsTheDepth()
    {
        var manager = new WidgetSessionManager();
        manager.BeginInteraction("leaked");
        manager.ForceResetInteractions("watchdog");
        manager.BeginInteraction("swallowed");
        manager.EndInteraction("swallowed-ended");
        // A real interaction begins after the swallow — the depth may belong
        // to it, reconciliation must not clear it.
        manager.BeginInteraction("live");

        bool reconciled = manager.ReconcilePostResetDepth(
            DateTime.UtcNow.AddSeconds(5),
            foregroundConsistentWithNoLiveInteraction: true);

        Assert.False(reconciled);
        Assert.True(manager.IsInteractionActive);
    }

    [Fact]
    public void ReconcilePostResetDepth_RequiresDelayAndForeignForeground()
    {
        var manager = new WidgetSessionManager();
        manager.BeginInteraction("leaked");
        manager.ForceResetInteractions("watchdog");
        manager.BeginInteraction("swallowed");
        manager.EndInteraction("swallowed-ended");

        // Within the reconcile delay: no clear.
        Assert.False(manager.ReconcilePostResetDepth(
            DateTime.UtcNow.AddMilliseconds(500),
            foregroundConsistentWithNoLiveInteraction: true));
        // Foreground still on DeskBox-side (an active widget interaction is
        // plausible): no clear.
        Assert.False(manager.ReconcilePostResetDepth(
            DateTime.UtcNow.AddSeconds(5),
            foregroundConsistentWithNoLiveInteraction: false));
        Assert.True(manager.IsInteractionActive);
    }

    [Fact]
    public void SwallowedEnd_WithoutForceReset_DoesNotOccur()
    {
        var manager = new WidgetSessionManager();
        manager.BeginInteraction("normal");

        manager.EndInteraction("normal-ended");

        // No force-reset stamped the grace window, so this End must pass.
        Assert.False(manager.IsInteractionActive);
        Assert.False(manager.ReconcilePostResetDepth(
            DateTime.UtcNow.AddSeconds(5),
            foregroundConsistentWithNoLiveInteraction: true));
    }
}
