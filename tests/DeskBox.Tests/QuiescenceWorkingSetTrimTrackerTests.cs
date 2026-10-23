using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class QuiescenceWorkingSetTrimTrackerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private const long MB = 1024 * 1024;

    private static readonly QuiescenceTrimActivitySnapshot VisibleBackground = new(
        HasVisibleWidgets: true,
        IsWidgetInteractionActive: false,
        HasActiveVisualWork: false,
        IsTransientUiOpen: false,
        IsDeskBoxForeground: false,
        IsPointerOverDeskBox: false);

    private static readonly QuiescenceTrimActivitySnapshot Hidden =
        VisibleBackground with { HasVisibleWidgets = false };

    private static readonly QuiescenceTrimActivitySnapshot VisibleForeground =
        VisibleBackground with { IsDeskBoxForeground = true };

    private static readonly QuiescenceTrimActivitySnapshot VisibleBackgroundAmbient =
        VisibleBackground with { HasAmbientVisualWork = true };

    private static QuiescenceTrimDecision Observe(
        QuiescenceWorkingSetTrimTracker tracker,
        DateTimeOffset now,
        QuiescenceTrimActivitySnapshot activity,
        long workingSet = 400 * MB) =>
        tracker.Observe(now, activity, () => workingSet);

    [Fact]
    public void FirstTrim_FiresOnceVisibleBackgroundQuietPeriodElapses()
    {
        var tracker = new QuiescenceWorkingSetTrimTracker();

        var early = Observe(tracker, T0, VisibleBackground);
        Assert.False(early.ShouldTrim);
        Assert.Equal("waiting-for-quiet", early.Reason);
        Assert.Equal(QuiescenceTrimTier.VisibleBackground, early.Tier);

        // The first idle after launch is an idle transition: the episode
        // rule fires, not the backstop cadence.
        var due = Observe(
            tracker,
            T0 + QuiescenceWorkingSetTrimTracker.VisibleBackgroundQuietDuration,
            VisibleBackground);
        Assert.True(due.ShouldTrim);
        Assert.Equal("episode-idle", due.Reason);
        Assert.True(due.EpisodeTrim);
    }

    [Fact]
    public void EpisodeTrim_FiresSecondsAfterInteractionEnds_EvenInsideBackstopCooldown()
    {
        // THE behavior pin (2026-10-19): "stop touching widgets and the
        // memory drops ~5 s later." A trim happened under a minute ago (the
        // backstop cooldown would normally hold it), the user interacted and
        // stopped again — the new idle episode earns its free trim anyway.
        var tracker = new QuiescenceWorkingSetTrimTracker();
        tracker.CommitTrim(T0);
        tracker.NoteActivity();
        Observe(tracker, T0 + TimeSpan.FromSeconds(1), VisibleBackground);

        var due = Observe(
            tracker,
            T0 + TimeSpan.FromSeconds(40),
            VisibleBackground,
            workingSet: 200 * MB);
        Assert.True(due.ShouldTrim);
        Assert.Equal("episode-idle", due.Reason);
    }

    [Fact]
    public void EpisodeTrim_PaysGlobalTwentySecondCooldown()
    {
        // Burst interactions with ~10 s gaps must not wave-storm: the free
        // trim still respects the global minimum interval.
        var tracker = new QuiescenceWorkingSetTrimTracker();
        tracker.CommitTrim(T0);
        tracker.NoteActivity();
        Observe(tracker, T0 + TimeSpan.FromSeconds(1), VisibleBackground);

        var held = Observe(
            tracker,
            T0 + TimeSpan.FromSeconds(19),
            VisibleBackground,
            workingSet: 200 * MB);
        Assert.False(held.ShouldTrim);
        Assert.Equal("trim-cooldown", held.Reason);

        var due = Observe(
            tracker,
            T0 + TimeSpan.FromSeconds(24),
            VisibleBackground,
            workingSet: 200 * MB);
        Assert.True(due.ShouldTrim);
    }

    [Fact]
    public void EpisodeTrim_HeldBelowEpisodeFloor_WithoutConsumingThePass()
    {
        var tracker = new QuiescenceWorkingSetTrimTracker();
        Observe(tracker, T0, VisibleBackground, workingSet: 60 * MB);

        var lean = Observe(
            tracker,
            T0 + QuiescenceWorkingSetTrimTracker.VisibleBackgroundQuietDuration,
            VisibleBackground,
            workingSet: 60 * MB);
        Assert.False(lean.ShouldTrim);
        Assert.Equal("below-episode-floor", lean.Reason);

        // The pass stays armed: growth inside the same episode still gets it.
        var grown = Observe(
            tracker,
            T0 + QuiescenceWorkingSetTrimTracker.VisibleBackgroundQuietDuration +
                TimeSpan.FromSeconds(1),
            VisibleBackground,
            workingSet: 120 * MB);
        Assert.True(grown.ShouldTrim);
        Assert.Equal("episode-idle", grown.Reason);
    }

    [Fact]
    public void EpisodeTrim_ConsumedOncePerIdleEpisode()
    {
        var tracker = new QuiescenceWorkingSetTrimTracker();
        Observe(tracker, T0, VisibleBackground, workingSet: 200 * MB);
        var due = Observe(
            tracker,
            T0 + QuiescenceWorkingSetTrimTracker.VisibleBackgroundQuietDuration,
            VisibleBackground,
            workingSet: 200 * MB);
        Assert.True(due.EpisodeTrim);
        tracker.CommitTrim(
            T0 + QuiescenceWorkingSetTrimTracker.VisibleBackgroundQuietDuration);

        // Same idle episode, rebound far past the episode floor: held by the
        // backstop cooldown — the churn loop stays dead.
        Observe(
            tracker,
            T0 + TimeSpan.FromSeconds(61),
            VisibleBackground,
            workingSet: 180 * MB);
        var churnWindow = Observe(
            tracker,
            T0 + TimeSpan.FromSeconds(67),
            VisibleBackground,
            workingSet: 180 * MB);
        Assert.False(churnWindow.ShouldTrim);
        Assert.Equal("trim-cooldown", churnWindow.Reason);
    }

    [Fact]
    public void EpisodeTrim_RearmedByActivity()
    {
        var tracker = new QuiescenceWorkingSetTrimTracker();
        Observe(tracker, T0, VisibleBackground, workingSet: 200 * MB);
        tracker.CommitTrim(
            T0 + QuiescenceWorkingSetTrimTracker.VisibleBackgroundQuietDuration);

        // Activity (any blocker or NoteActivity) re-arms the pass and ends
        // the old quiet accounting...
        Observe(
            tracker,
            T0 + TimeSpan.FromSeconds(30),
            VisibleBackground with { IsWidgetInteractionActive = true },
            workingSet: 200 * MB);

        // ...the next observation starts the new quiet window...
        Observe(
            tracker,
            T0 + TimeSpan.FromSeconds(31),
            VisibleBackground,
            workingSet: 200 * MB);

        // ...and the idle transition trims again inside the backstop
        // cooldown.
        var due = Observe(
            tracker,
            T0 + TimeSpan.FromSeconds(37),
            VisibleBackground,
            workingSet: 200 * MB);
        Assert.True(due.ShouldTrim);
        Assert.Equal("episode-idle", due.Reason);
    }

    [Theory]
    [InlineData(true, false, false, "blocked:interaction")]
    [InlineData(false, true, false, "blocked:visual-work")]
    [InlineData(false, false, true, "blocked:transient-ui")]
    public void Blockers_ResetQuietPeriod(
        bool interaction,
        bool visualWork,
        bool transientUi,
        string expectedReason)
    {
        var tracker = new QuiescenceWorkingSetTrimTracker();
        Observe(tracker, T0, VisibleBackground);

        var blocked = Observe(
            tracker,
            T0 + TimeSpan.FromSeconds(4),
            VisibleBackground with
            {
                IsWidgetInteractionActive = interaction,
                HasActiveVisualWork = visualWork,
                IsTransientUiOpen = transientUi
            });
        Assert.False(blocked.ShouldTrim);
        Assert.Equal(expectedReason, blocked.Reason);

        // Quiet restarts from the blocker, so 5 s after T0 is not yet due.
        var afterBlock = Observe(tracker, T0 + TimeSpan.FromSeconds(5), VisibleBackground);
        Assert.False(afterBlock.ShouldTrim);
        Assert.Equal("waiting-for-quiet", afterBlock.Reason);
    }

    [Fact]
    public void ForegroundOrPointer_OnlyLengthensQuietPeriod()
    {
        var tracker = new QuiescenceWorkingSetTrimTracker();
        Observe(tracker, T0, VisibleForeground);

        var atBackgroundThreshold = Observe(
            tracker,
            T0 + QuiescenceWorkingSetTrimTracker.VisibleBackgroundQuietDuration,
            VisibleForeground);
        Assert.False(atBackgroundThreshold.ShouldTrim);
        Assert.Equal(QuiescenceTrimTier.VisibleForeground, atBackgroundThreshold.Tier);

        var atForegroundThreshold = Observe(
            tracker,
            T0 + QuiescenceWorkingSetTrimTracker.VisibleForegroundQuietDuration,
            VisibleForeground);
        Assert.True(atForegroundThreshold.ShouldTrim);
    }

    [Fact]
    public void HiddenTier_UsesShortestQuietPeriod()
    {
        var tracker = new QuiescenceWorkingSetTrimTracker();
        Observe(tracker, T0, Hidden);

        var due = Observe(
            tracker,
            T0 + QuiescenceWorkingSetTrimTracker.HiddenQuietDuration,
            Hidden);
        Assert.True(due.ShouldTrim);
        Assert.Equal(QuiescenceTrimTier.Hidden, due.Tier);
    }

    [Fact]
    public void NoteActivity_RestartsQuietPeriod()
    {
        var tracker = new QuiescenceWorkingSetTrimTracker();
        Observe(tracker, T0, VisibleBackground);
        tracker.NoteActivity();

        var notYet = Observe(
            tracker,
            T0 + QuiescenceWorkingSetTrimTracker.VisibleBackgroundQuietDuration,
            VisibleBackground);
        Assert.False(notYet.ShouldTrim);
        Assert.Equal("waiting-for-quiet", notYet.Reason);
    }

    [Fact]
    public void AfterTrim_RequiresCooldownAndGrowthOverSettledBaseline()
    {
        var tracker = new QuiescenceWorkingSetTrimTracker();
        tracker.CommitTrim(T0);
        Observe(tracker, T0 + TimeSpan.FromSeconds(1), VisibleBackgroundAmbient, workingSet: 2 * MB);

        // Ambient work pays the 15 s quiet tier first: at T0+6s the trim
        // is still waiting for quiet (the gate order is quiet → cooldown).
        var early = Observe(
            tracker,
            T0 + TimeSpan.FromSeconds(6),
            VisibleBackgroundAmbient,
            workingSet: 400 * MB);
        Assert.False(early.ShouldTrim);
        Assert.Equal("waiting-for-quiet", early.Reason);

        // The baseline is sampled once the rebound has saturated (the 90 s
        // plateau): pages that fault back after a trim are steady state,
        // not growth, so 300 MB here becomes the reference point. The
        // readings stay above the 240 MB visible floor so the growth gate
        // is what speaks in this test.
        Observe(
            tracker,
            T0 + QuiescenceWorkingSetTrimTracker.BaselineSettleDelay,
            VisibleBackgroundAmbient,
            workingSet: 300 * MB);

        // Ambient backgrounds have no episode pass — the growth gate holds.
        var flat = Observe(
            tracker,
            T0 + TimeSpan.FromSeconds(120),
            VisibleBackgroundAmbient,
            workingSet: 300 * MB + 8 * MB);
        Assert.False(flat.ShouldTrim);
        Assert.Equal("insufficient-growth", flat.Reason);

        // 25% of the 300 MB baseline (75 MB) clears the gate — the gate
        // never dips below the 32 MB floor nor above the 128 MB cap.
        var grown = Observe(
            tracker,
            T0 + TimeSpan.FromSeconds(120),
            VisibleBackgroundAmbient,
            workingSet: 300 * MB + 75 * MB);
        Assert.True(grown.ShouldTrim);
    }

    [Fact]
    public void BeforeBaselineSettles_TrimIsHeldEvenIfCooldownElapsed()
    {
        var tracker = new QuiescenceWorkingSetTrimTracker();
        tracker.CommitTrim(T0);
        // Observations stop before the settle delay, so no baseline exists yet.
        Observe(tracker, T0 + TimeSpan.FromSeconds(1), VisibleBackgroundAmbient, workingSet: 2 * MB);

        var held = Observe(
            tracker,
            T0 + TimeSpan.FromSeconds(30),
            VisibleBackgroundAmbient,
            workingSet: 400 * MB);
        Assert.False(held.ShouldTrim);
        Assert.Equal("baseline-settling", held.Reason);
    }

    [Fact]
    public void GrowthGate_NeverDropsBelowGrowthFloorOnSmallBaseline()
    {
        // Pinned on the hidden tier: below the 240 MB visible floor a
        // visible tier would be caught by the absolute floor before the
        // growth gate could speak.
        var tracker = new QuiescenceWorkingSetTrimTracker();
        tracker.CommitTrim(T0);
        Observe(
            tracker,
            T0 + QuiescenceWorkingSetTrimTracker.BaselineSettleDelay,
            Hidden,
            workingSet: 80 * MB);

        // 25% of 80 MB = 20 MB, but the floor pins the gate at 32 MB so a
        // low-water baseline cannot cause a perpetual trim loop.
        var under = Observe(
            tracker,
            T0 + TimeSpan.FromSeconds(120),
            Hidden,
            workingSet: 100 * MB);
        Assert.False(under.ShouldTrim);
        Assert.Equal("insufficient-growth", under.Reason);

        var grown = Observe(
            tracker,
            T0 + TimeSpan.FromSeconds(120),
            Hidden,
            workingSet: 80 * MB + QuiescenceWorkingSetTrimTracker.MinimumWorkingSetGrowthBytes);
        Assert.True(grown.ShouldTrim);
    }

    [Fact]
    public void FirstTrim_VisibleTierRequiresAbsoluteWorkingSetFloor()
    {
        // 2026-10-17 scope change: the floor binds the foreground tier and
        // ambient-animated backgrounds (pages touched every frame). A static
        // background-visible desktop — file grids, the system monitor's
        // 1 Hz rows — has no floor: trimming is effective there, and the
        // always-visible monitor widget otherwise dead-locked the idle trim.
        var tracker = new QuiescenceWorkingSetTrimTracker();
        Observe(tracker, T0, VisibleBackground, workingSet: 150 * MB);

        var staticBackgroundDue = Observe(
            tracker,
            T0 + QuiescenceWorkingSetTrimTracker.VisibleBackgroundQuietDuration,
            VisibleBackground,
            workingSet: 150 * MB);
        Assert.True(staticBackgroundDue.ShouldTrim);
        Assert.Equal("episode-idle", staticBackgroundDue.Reason);

        var ambientTracker = new QuiescenceWorkingSetTrimTracker();
        Observe(ambientTracker, T0, VisibleBackgroundAmbient, workingSet: 150 * MB);
        var ambientBackground = Observe(
            ambientTracker,
            T0 + QuiescenceWorkingSetTrimTracker.VisibleForegroundQuietDuration,
            VisibleBackgroundAmbient,
            workingSet: 150 * MB);
        Assert.False(ambientBackground.ShouldTrim);
        Assert.Equal("below-visible-floor", ambientBackground.Reason);

        var foregroundTracker = new QuiescenceWorkingSetTrimTracker();
        Observe(foregroundTracker, T0, VisibleForeground, workingSet: 150 * MB);
        var foreground = Observe(
            foregroundTracker,
            T0 + QuiescenceWorkingSetTrimTracker.VisibleForegroundQuietDuration,
            VisibleForeground,
            workingSet: 150 * MB);
        Assert.False(foreground.ShouldTrim);
        Assert.Equal("below-visible-floor", foreground.Reason);

        // The hidden tier has no absolute floor: hidden pages are cheap to
        // re-fault and the immediate-hidden trim already trims on hide.
        var hiddenTracker = new QuiescenceWorkingSetTrimTracker();
        Observe(hiddenTracker, T0, Hidden, workingSet: 150 * MB);
        var hiddenDue = Observe(
            hiddenTracker,
            T0 + QuiescenceWorkingSetTrimTracker.HiddenQuietDuration,
            Hidden,
            workingSet: 150 * MB);
        Assert.True(hiddenDue.ShouldTrim);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HotVisibleTiers_BelowAbsoluteFloor_NeverTrim(bool isForeground)
    {
        var snapshot = isForeground ? VisibleForeground : VisibleBackgroundAmbient;
        var tracker = new QuiescenceWorkingSetTrimTracker();
        Observe(tracker, T0, snapshot, workingSet: 150 * MB);

        // Far past every tier's quiet deadline: still no trim — the process
        // is not big enough to be worth paging out while its pages stay hot.
        var pastDeadline = Observe(
            tracker,
            T0 + QuiescenceWorkingSetTrimTracker.VisibleForegroundQuietDuration +
                TimeSpan.FromSeconds(30),
            snapshot,
            workingSet: 150 * MB);
        Assert.False(pastDeadline.ShouldTrim);
        Assert.Equal("below-visible-floor", pastDeadline.Reason);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void VisibleTier_TrimsAtAbsoluteFloorOnceQuiet(bool isForeground)
    {
        // Both snapshots here are floor-bound (foreground, or ambient-animated
        // background), and ambient work lengthens the required quiet period
        // to the foreground duration — so both pay the 15 s tier.
        var snapshot = isForeground ? VisibleForeground : VisibleBackgroundAmbient;
        TimeSpan requiredQuiet =
            QuiescenceWorkingSetTrimTracker.VisibleForegroundQuietDuration;
        var tracker = new QuiescenceWorkingSetTrimTracker();

        // The floor is inclusive: exactly 240 MB is trimmable (first trim
        // included) once the tier's quiet period has elapsed.
        Observe(tracker, T0, snapshot);
        var due = Observe(
            tracker,
            T0 + requiredQuiet,
            snapshot,
            workingSet: QuiescenceWorkingSetTrimTracker.VisibleTierMinimumWorkingSetBytes);
        Assert.True(due.ShouldTrim);
        Assert.Equal("quiet", due.Reason);
    }

    [Fact]
    public void VisibleTier_ReTrimStillBoundByAbsoluteFloorAndGrowthGate()
    {
        var tracker = new QuiescenceWorkingSetTrimTracker();
        tracker.CommitTrim(T0);
        Observe(
            tracker,
            T0 + QuiescenceWorkingSetTrimTracker.BaselineSettleDelay,
            VisibleBackgroundAmbient,
            workingSet: 80 * MB);

        // Ambient work pays the 15 s quiet tier on top of the cooldown and
        // the settle window before either gate is even reached.
        DateTimeOffset whenChecked = T0 + QuiescenceWorkingSetTrimTracker
            .BaselineSettleDelay +
            QuiescenceWorkingSetTrimTracker.VisibleForegroundQuietDuration +
            TimeSpan.FromSeconds(5);

        // Growth clears the 32 MB minimum over the small baseline, but an
        // ambient-animated visible re-trim must also clear the absolute floor.
        var grownButSmall = Observe(
            tracker,
            whenChecked,
            VisibleBackgroundAmbient,
            workingSet: 80 * MB + QuiescenceWorkingSetTrimTracker.MinimumWorkingSetGrowthBytes);
        Assert.False(grownButSmall.ShouldTrim);
        Assert.Equal("below-visible-floor", grownButSmall.Reason);

        var grown = Observe(
            tracker,
            whenChecked,
            VisibleBackgroundAmbient,
            workingSet: QuiescenceWorkingSetTrimTracker.VisibleTierMinimumWorkingSetBytes);
        Assert.True(grown.ShouldTrim);
    }

    [Fact]
    public void StaticBackgroundVisible_BackstopNeedsGrowthOverPlateauBaseline()
    {
        // The system-monitor regression pin, retold for the episode model
        // (2026-10-19): the free pass trims the idle transition, and within
        // the SAME idle episode the backstop only fires on genuine growth
        // past the 5-minute cadence — the post-trim rebound (absorbed into
        // the 90 s plateau baseline) must not restart the churn loop.
        var tracker = new QuiescenceWorkingSetTrimTracker();
        Observe(tracker, T0, VisibleBackground, workingSet: 200 * MB);

        // The idle transition spends its episode pass...
        DateTimeOffset passAt = T0 +
            QuiescenceWorkingSetTrimTracker.VisibleBackgroundQuietDuration;
        var pass = Observe(
            tracker,
            passAt,
            VisibleBackground,
            workingSet: 200 * MB);
        Assert.True(pass.ShouldTrim);
        Assert.True(pass.EpisodeTrim);
        tracker.CommitTrim(passAt);

        // ...the rebound saturates, and the plateau becomes the baseline.
        Observe(
            tracker,
            passAt + QuiescenceWorkingSetTrimTracker.BaselineSettleDelay +
                TimeSpan.FromSeconds(1),
            VisibleBackground,
            workingSet: 70 * MB);

        DateTimeOffset backstopDue = passAt +
            QuiescenceWorkingSetTrimTracker.StaticBackgroundMinimumTrimInterval;

        // Same episode, regrowth below the growth gate: held.
        var notGrown = Observe(
            tracker,
            backstopDue,
            VisibleBackground,
            workingSet: 75 * MB);
        Assert.False(notGrown.ShouldTrim);
        Assert.Equal("insufficient-growth", notGrown.Reason);

        // Genuine growth (≥32 MB over the plateau) trims at the cadence.
        var grown = Observe(
            tracker,
            backstopDue,
            VisibleBackground,
            workingSet: 70 * MB + QuiescenceWorkingSetTrimTracker.MinimumWorkingSetGrowthBytes);
        Assert.True(grown.ShouldTrim);
        Assert.False(grown.EpisodeTrim);
    }

    [Fact]
    public void TierChange_RestartsQuietPeriod()
    {
        var tracker = new QuiescenceWorkingSetTrimTracker();
        Observe(tracker, T0, Hidden);

        // Quiet accumulated while hidden must not satisfy the foreground
        // tier the moment the user looks at a widget.
        var upgraded = Observe(
            tracker,
            T0 + QuiescenceWorkingSetTrimTracker.HiddenQuietDuration + TimeSpan.FromSeconds(12),
            VisibleForeground);
        Assert.False(upgraded.ShouldTrim);
        Assert.Equal("waiting-for-quiet", upgraded.Reason);

        var due = Observe(
            tracker,
            T0 + QuiescenceWorkingSetTrimTracker.HiddenQuietDuration +
                TimeSpan.FromSeconds(12) +
                QuiescenceWorkingSetTrimTracker.VisibleForegroundQuietDuration,
            VisibleForeground);
        Assert.True(due.ShouldTrim);
    }

    [Fact]
    public void AmbientAnimation_RequiresForegroundQuietDuration()
    {
        var tracker = new QuiescenceWorkingSetTrimTracker();
        var ambient = VisibleBackground with { HasAmbientVisualWork = true };
        Observe(tracker, T0, ambient);

        // Looping decoration does not block outright, but it escalates the
        // required quiet to the strictest tier.
        var atBackgroundThreshold = Observe(
            tracker,
            T0 + QuiescenceWorkingSetTrimTracker.VisibleBackgroundQuietDuration,
            ambient);
        Assert.False(atBackgroundThreshold.ShouldTrim);
        Assert.Equal("waiting-for-quiet", atBackgroundThreshold.Reason);

        var atForegroundThreshold = Observe(
            tracker,
            T0 + QuiescenceWorkingSetTrimTracker.VisibleForegroundQuietDuration,
            ambient);
        Assert.True(atForegroundThreshold.ShouldTrim);
    }

    [Fact]
    public void CommitTrim_FromAnotherPath_ResetsQuietAccounting()
    {
        var tracker = new QuiescenceWorkingSetTrimTracker();
        Observe(tracker, T0, VisibleBackground);

        // The immediate-hidden path trimmed independently.
        tracker.CommitTrim(T0 + TimeSpan.FromSeconds(4));

        var afterExternalTrim = Observe(
            tracker,
            T0 + TimeSpan.FromSeconds(5),
            VisibleBackground,
            workingSet: 400 * MB);
        Assert.False(afterExternalTrim.ShouldTrim);
        Assert.Equal("waiting-for-quiet", afterExternalTrim.Reason);
    }
}
