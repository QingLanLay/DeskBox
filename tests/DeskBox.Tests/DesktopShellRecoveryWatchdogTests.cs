using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class DesktopShellRecoveryWatchdogTests
{
    [Fact]
    public void HealthyDesktop_IsObservedWithoutRepeatedRebindingOrRestarts()
    {
        using var scenario = new Scenario();
        scenario.Step(100);
        Assert.True(scenario.Watchdog.IsReady);
        Assert.Empty(scenario.Repairs);
        Assert.Empty(scenario.Suspensions);
        Assert.Equal(0, scenario.ReadyCount);
        Assert.Equal(0, scenario.RestartCount);
    }

    [Fact]
    public void LateRecurrence_AfterSuccessfulRecovery_IsDetectedWithoutAnyNewSessionMessage()
    {
        using var scenario = new Scenario();
        for (int cycle = 0; cycle < 8; cycle++)
        {
            scenario.Snapshot = Healthy with { IsMinimized = true };
            scenario.Step();
            Assert.False(scenario.Watchdog.IsReady);
            Assert.True(scenario.Suspensions[^1]);
            scenario.Snapshot = Healthy;
            scenario.Step(3);
            Assert.True(scenario.Watchdog.IsReady);
            Assert.False(scenario.Suspensions[^1]);
            scenario.Now += 3_600_000; // Fail again an hour after "success".
        }
        Assert.Equal(8, scenario.Repairs.Count);
        Assert.Equal(8, scenario.ReadyCount);
        Assert.Equal(0, scenario.RestartCount);
    }

    [Fact]
    public void SuccessfulRestoreRequest_IsNotTreatedAsAHealthyWindow()
    {
        using var scenario = new Scenario { Snapshot = Healthy with { IsMinimized = true } };
        scenario.Step(6); // The repair delegate returns normally, but the HWND remains minimized.
        Assert.False(scenario.Watchdog.IsReady);
        Assert.Equal(0, scenario.ReadyCount);
        Assert.Contains(scenario.Repairs, forcePlacement => forcePlacement);
        Assert.Equal(0, scenario.RestartCount);
    }

    [Fact]
    public void PersistentFailure_EscalatesToPlacementThenRestart_WhileContinuingNativeChecks()
    {
        using var scenario = new Scenario { Snapshot = Healthy with { IsMinimized = true } };
        scenario.Step(32);
        Assert.Contains(false, scenario.Repairs);
        Assert.Contains(true, scenario.Repairs);
        Assert.Equal(1, scenario.RestartCount);
        int repairsAtRestart = scenario.Repairs.Count;
        scenario.Step(20);
        Assert.True(scenario.Repairs.Count > repairsAtRestart);
        Assert.Equal(1, scenario.RestartCount); // No restart request storm.
        scenario.Snapshot = Healthy;
        scenario.Step(3);
        Assert.True(scenario.Watchdog.IsReady);
    }

    [Fact]
    public void BriefHealthyFrames_DoNotContinuouslyResetTheEscalationDeadline()
    {
        using var scenario = new Scenario();
        for (int i = 0; i < 20; i++)
        {
            scenario.Snapshot = Healthy with { IsMinimized = true };
            scenario.Step();
            scenario.Snapshot = Healthy;
            scenario.Step();
        }
        Assert.False(scenario.Watchdog.IsReady);
        Assert.Equal(1, scenario.RestartCount);
    }

    [Fact]
    public void MissingUnlockNotification_RecoversWhenTheActualInputDesktopReturns()
    {
        using var scenario = new Scenario();
        scenario.Step(3);
        scenario.Watchdog.SetSessionAvailable(false);
        scenario.Interactive = false;
        scenario.Step(10);
        Assert.False(scenario.Watchdog.IsReady);
        Assert.Empty(scenario.Repairs);
        // Deliberately do not send SetSessionAvailable(true).
        scenario.Interactive = true;
        scenario.Snapshot = Healthy with { IsMinimized = true };
        scenario.Step();
        Assert.Single(scenario.Repairs);
        scenario.Snapshot = Healthy;
        scenario.Step(3);
        Assert.True(scenario.Watchdog.IsReady);
        Assert.Equal(1, scenario.ReadyCount);
    }

    [Fact]
    public void ProcessSuspendedAcrossTheWholeLockInterval_DoesNotLeaveAStaleLockFlagForever()
    {
        using var scenario = new Scenario();
        scenario.Step(3);
        scenario.Watchdog.SetSessionAvailable(false);
        // No polling happened while locked, and no unlock notification arrived.
        scenario.Now += 60_000;
        scenario.Snapshot = Healthy with { IsMinimized = true };
        scenario.Step();
        Assert.Single(scenario.Repairs);
        scenario.Snapshot = Healthy;
        scenario.Step(3);
        Assert.True(scenario.Watchdog.IsReady);
    }

    [Fact]
    public void LockAndRapidRelock_CancelAnUnfinishedStableSequence()
    {
        using var scenario = new Scenario();
        scenario.Step(3);
        scenario.Watchdog.SetSessionAvailable(false);
        scenario.Step();
        scenario.Watchdog.SetSessionAvailable(true);
        scenario.Step(2);
        Assert.False(scenario.Watchdog.IsReady);
        scenario.Watchdog.SetSessionAvailable(false);
        scenario.Step();
        scenario.Watchdog.SetSessionAvailable(true);
        scenario.Step(2);
        Assert.False(scenario.Watchdog.IsReady);
        scenario.Step();
        Assert.True(scenario.Watchdog.IsReady);
        Assert.Equal(1, scenario.ReadyCount);
    }

    [Fact]
    public void ExplorerMissingOrRestarting_DoesNotCauseDeskBoxRestartLoops()
    {
        using var scenario = new Scenario { Snapshot = default };
        scenario.Step(240);
        Assert.Empty(scenario.Repairs);
        Assert.Equal(0, scenario.RestartCount);
        scenario.Snapshot = Healthy;
        scenario.Step(3);
        Assert.True(scenario.Watchdog.IsReady);
    }

    [Fact]
    public void ExplorerHostReplacement_IsVerifiedBeforeWidgetRebinding()
    {
        using var scenario = new Scenario();
        scenario.Step(3);
        scenario.Snapshot = Healthy with { Host = new IntPtr(30), IconView = new IntPtr(40) };
        scenario.Step(2);
        Assert.False(scenario.Watchdog.IsReady);
        Assert.Equal(0, scenario.ReadyCount);
        scenario.Step();
        Assert.Equal(1, scenario.ReadyCount);
        scenario.Step(20);
        Assert.Equal(1, scenario.ReadyCount);
    }

    [Fact]
    public void CaptionSizedWindow_IsBrokenEvenWhenTheMinimizedFlagIsMissing()
    {
        using var scenario = new Scenario
        {
            Snapshot = Healthy with { Bounds = new Win32Helper.RECT { Right = 160, Bottom = 28 } }
        };
        scenario.Step(6);
        Assert.Contains(true, scenario.Repairs);
        Assert.False(scenario.Watchdog.IsReady);
    }

    [Fact]
    public void IntentionallyHiddenDesktopIcons_DoNotTriggerRepairOrRestart()
    {
        using var scenario = new Scenario { Snapshot = Healthy with { IsViewVisible = false } };
        scenario.Step(60);
        Assert.True(scenario.Watchdog.IsReady);
        Assert.Empty(scenario.Repairs);
        Assert.Equal(0, scenario.RestartCount);
    }

    [Fact]
    public async Task BackgroundLoop_ContinuesAfterOneNativeObservationThrows()
    {
        int calls = 0;
        var repaired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var watchdog = new DesktopShellRecoveryWatchdog(
            () => true,
            () => Interlocked.Increment(ref calls) == 1
                ? throw new InvalidOperationException("Transient native observation failure")
                : Healthy with { IsMinimized = true },
            (_, _) => repaired.TrySetResult(), _ => { }, () => { }, () => { });

        Assert.Same(repaired.Task, await Task.WhenAny(repaired.Task, Task.Delay(TimeSpan.FromSeconds(5))));
        Assert.True(calls >= 2);
    }

    [Fact]
    public void Dispose_StopsFurtherRepairsAndRebinding()
    {
        using var scenario = new Scenario();
        scenario.Watchdog.Dispose();
        scenario.Snapshot = Healthy with { IsMinimized = true };
        scenario.Step(100);
        Assert.Empty(scenario.Repairs);
        Assert.Equal(0, scenario.ReadyCount);
    }

    private static DesktopShellHealthSnapshot Healthy => new(
        new IntPtr(10), new IntPtr(20), false, true,
        new Win32Helper.RECT { Right = 1920, Bottom = 1080 },
        new Win32Helper.RECT { Right = 1920, Bottom = 1080 });

    private sealed class Scenario : IDisposable
    {
        internal bool Interactive = true;
        internal DesktopShellHealthSnapshot Snapshot = Healthy;
        internal long Now;
        internal readonly List<bool> Repairs = [];
        internal readonly List<bool> Suspensions = [];
        internal int ReadyCount;
        internal int RestartCount;
        internal readonly DesktopShellRecoveryWatchdog Watchdog;

        internal Scenario()
        {
            Watchdog = new DesktopShellRecoveryWatchdog(
                () => Interactive, () => Snapshot,
                (_, forcePlacement) => Repairs.Add(forcePlacement),
                Suspensions.Add, () => ReadyCount++, () => RestartCount++,
                () => Now, startLoop: false);
        }

        internal void Step(int count = 1)
        {
            for (int i = 0; i < count; i++)
            {
                Now += 500;
                Watchdog.RunCycleOnce();
            }
        }

        public void Dispose() => Watchdog.Dispose();
    }
}
