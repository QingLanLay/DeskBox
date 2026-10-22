using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class DesktopSessionRecoveryTests
{
    private const uint TaskbarCreated = 0xC123;

    [Theory]
    [InlineData(0x0007, 0x0008, "session-unlock")]
    [InlineData(0x0004, 0x0003, "session-reconnect")]
    [InlineData(0x0002, 0x0001, "session-reconnect")]
    [InlineData(0x0006, 0x0005, "session-reconnect")]
    public void NonInteractiveSession_IgnoresEarlyRecoveryUntilReturn(
        uint leaveEvent, uint returnEvent, string expectedReason)
    {
        var state = new AppLifecycleRecoverySessionState();
        Assert.Null(SessionChange(state, leaveEvent));
        Assert.False(state.IsSessionAvailable);

        Assert.Null(state.Observe(AppLifecycleRecoverySignalClassifier.WmPowerBroadcast,
            new UIntPtr(0x0012), IntPtr.Zero, TaskbarCreated));
        Assert.Null(state.Observe(AppLifecycleRecoverySignalClassifier.WmDisplayChange,
            UIntPtr.Zero, IntPtr.Zero, TaskbarCreated));
        Assert.Null(state.Observe(TaskbarCreated, UIntPtr.Zero, IntPtr.Zero, TaskbarCreated));

        Assert.Equal(expectedReason, SessionChange(state, returnEvent));
        Assert.True(state.IsSessionAvailable);
        Assert.Equal("explorer-restart",
            state.Observe(TaskbarCreated, UIntPtr.Zero, IntPtr.Zero, TaskbarCreated));
    }

    [Fact]
    public void RemoteControlChange_DoesNotUnlockLockedSession()
    {
        var state = new AppLifecycleRecoverySessionState();
        SessionChange(state, 0x0007);
        Assert.Null(SessionChange(state, 0x0009));
        Assert.False(state.IsSessionAvailable);
        Assert.Equal("session-unlock", SessionChange(state, 0x0008));
    }

    [Fact]
    public void RepeatedLockUnlockCycles_DoNotLeaveRecoveryEnabledDuringLock()
    {
        var state = new AppLifecycleRecoverySessionState();
        for (int cycle = 0; cycle < 3; cycle++)
        {
            Assert.Null(SessionChange(state, 0x0007));
            Assert.Null(state.Observe(TaskbarCreated, UIntPtr.Zero, IntPtr.Zero, TaskbarCreated));
            Assert.Equal("session-unlock", SessionChange(state, 0x0008));
        }
    }

    [Theory]
    [InlineData(0x0001)]
    [InlineData(0x0003)]
    public void ReconnectingLockedSession_WaitsForUnlock(uint connectEvent)
    {
        var state = new AppLifecycleRecoverySessionState();
        SessionChange(state, 0x0007);
        SessionChange(state, 0x0004);
        Assert.Null(SessionChange(state, connectEvent));
        Assert.False(state.IsSessionAvailable);
        Assert.Equal("session-unlock", SessionChange(state, 0x0008));
    }

    [Fact]
    public void DesktopReadySignal_RecoversIfUnlockNotificationWasMissed()
    {
        var state = new AppLifecycleRecoverySessionState();
        SessionChange(state, 0x0007);
        Assert.Equal("session-reconnect", SessionChange(state, 0x000F));
        Assert.True(state.IsSessionAvailable);
    }

    [Theory]
    [InlineData("lifecycle-session-unlock", true)]
    [InlineData("display-area-watcher,lifecycle-explorer-restart", true)]
    [InlineData("lifecycle-display-message,session-unlock", true)]
    [InlineData("lifecycle-display-message,display-power-on", true)]
    [InlineData("display-message,session-reconnect", true)]
    [InlineData("lifecycle-display-power-on", true)]
    [InlineData("lifecycle-resume", true)]
    [InlineData("widget-window-message", true)]
    [InlineData("display-area-watcher", false)]
    public void Recovery_SeparatesShellTransitionsFromOrdinaryLayoutChanges(string reasons, bool expected)
    {
        Assert.Equal(expected, DesktopLayerRecoveryPolicy.RequiresStableHost(reasons));
    }

    private static string? SessionChange(AppLifecycleRecoverySessionState state, uint sessionEvent) =>
        state.Observe(AppLifecycleRecoverySignalClassifier.WmWtsSessionChange,
            new UIntPtr(sessionEvent), IntPtr.Zero, TaskbarCreated);
}
