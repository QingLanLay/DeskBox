namespace DeskBox.Services;

/// <summary>
/// UI facts the quiescence trim needs. Interaction, in-flight animation, and
/// transient windows block a trim outright; foreground/pointer presence and
/// looping ambient animation only lengthen the required quiet period.
/// </summary>
internal readonly record struct QuiescenceTrimActivitySnapshot(
    bool HasVisibleWidgets,
    bool IsWidgetInteractionActive,
    bool HasActiveVisualWork,
    bool IsTransientUiOpen,
    bool IsDeskBoxForeground,
    bool IsPointerOverDeskBox,
    bool HasAmbientVisualWork = false);

internal enum QuiescenceTrimTier
{
    Hidden,
    VisibleBackground,
    VisibleForeground
}

internal readonly record struct QuiescenceTrimDecision(
    bool ShouldTrim,
    QuiescenceTrimTier Tier,
    string Reason,
    TimeSpan QuietDuration,
    bool EpisodeTrim = false)
{
    internal static QuiescenceTrimDecision Skip(
        QuiescenceTrimTier tier,
        string reason,
        TimeSpan quiet = default) =>
        new(false, tier, reason, quiet);
}

/// <summary>
/// Decides when a process-wide working-set trim is worth running after the
/// UI has gone quiet. Every operation (closing settings, collapsing a
/// capsule, hiding widgets, a drag) ends in the same quiet state, so one
/// tracker replaces per-operation trim hooks. Static background-visible
/// sessions run an idle-EPISODE model (2026-10-19): each transition from
/// activity to idle earns one immediate trim after the tier's quiet period
/// — the "stop interacting and the memory drops seconds later" behavior —
/// while further trims inside the same idle episode are backstop-only (a
/// 5-minute cadence plus genuine growth over the post-rebound plateau
/// baseline), which keeps the trim-refault churn loop (measured ~5.7%
/// single-core at a 20 s cadence with an always-visible system widget)
/// from restarting. The tracker owns no timer so callers can drive it
/// with a fake clock in tests.
/// </summary>
internal sealed class QuiescenceWorkingSetTrimTracker
{
    internal static readonly TimeSpan HiddenQuietDuration = TimeSpan.FromSeconds(3);
    internal static readonly TimeSpan VisibleBackgroundQuietDuration = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan VisibleForegroundQuietDuration = TimeSpan.FromSeconds(15);
    internal static readonly TimeSpan MinimumTrimInterval = TimeSpan.FromSeconds(20);

    // A static background-visible desktop (file grids, the system widget's
    // 1 Hz rows) refaults its touched pages within seconds of a trim, so a
    // 20 s cooldown turns into a trim-refault churn loop that burns far more
    // CPU in soft faults than the sampler itself (measured 2026-10-17:
    // ~5.7% single-core at 20 s cadence). Since 2026-10-19 this cooldown is
    // only the BACKSTOP cadence within one idle episode — the first trim of
    // each idle episode fires on the episode rule below; repeat trims inside
    // the same episode (genuine growth: GC spikes, new content, leaks) pay
    // this interval so the refault loop cannot restart.
    internal static readonly TimeSpan StaticBackgroundMinimumTrimInterval =
        TimeSpan.FromMinutes(5);
    internal const long MinimumWorkingSetGrowthBytes = 32L * 1024 * 1024;
    internal const long MaximumWorkingSetGrowthBytes = 128L * 1024 * 1024;
    internal const double MinimumWorkingSetGrowthRatio = 0.25;

    // The idle-episode rule (2026-10-19): each transition from "user was
    // interacting" to "idle" earns ONE immediate trim once the tier's quiet
    // period has elapsed — the "stop touching widgets and the memory drops
    // seconds later" behavior the pre-widget floor gate used to deliver by
    // accident. It bypasses the 5-minute backstop cooldown and the growth
    // gate (an idle transition is a trim-worthy event by itself) but pays
    // the global 20 s cooldown (burst interactions must not wave-storm) and
    // its own floor: an already-lean process has nothing worth paging out.
    internal const long EpisodeTrimMinimumWorkingSetBytes = 80L * 1024 * 1024;

    // Visible tiers carry an absolute working-set floor on every trim, the
    // first one included: paging a small hot working set out would fault
    // the pages straight back. The floor's scope is tier-aware (see the
    // gate in Observe): the foreground tier always pays it, and a
    // background tier only while looping ambient animation keeps touching
    // pages — a static background-visible desktop skips it and leans on the
    // growth gate alone. The 240 MB value matches the visible-idle
    // working-set threshold in MemoryCleanupPolicy
    // (VisibleIdleWorkingSetThresholdBytes) but is re-declared here so the
    // quiescence path owns its own number rather than reaching into another
    // policy's constants. The hidden tier deliberately has no absolute
    // floor: hidden pages are cheap to re-fault and the immediate-hidden
    // trim already trims unconditionally on hide.
    internal const long VisibleTierMinimumWorkingSetBytes = 240L * 1024 * 1024;

    // Right after a trim the working set is near zero; the pages the render
    // loop and timers actually need fault back over the following minute
    // (measured 2026-10-17: ~86 MB within 50 s). The growth baseline is
    // sampled only after that rebound has SATURATED — an 8 s sample mistook
    // the rebound for growth and fed the trim-refault loop. 90 s sits past
    // the measured rebound tail without making the backstop wait (its own
    // cooldown is 5 minutes anyway).
    internal static readonly TimeSpan BaselineSettleDelay = TimeSpan.FromSeconds(90);

    private DateTimeOffset? _quietSince;
    private QuiescenceTrimTier? _quietTier;
    private DateTimeOffset? _lastTrimAt;
    private long? _settledWorkingSetBaseline;
    // The idle-episode state: available starts armed (the very first idle
    // after launch is an idle transition too); it is consumed when an
    // episode trim commits and re-armed whenever real activity (user
    // interaction, visual work, transient UI, a tier change) breaks the
    // idle episode. CommitTrim alone does NOT re-arm it — a trim is not
    // activity, or every trim would re-arm its own successor (the churn
    // loop the backstop exists to stop).
    private bool _episodeTrimAvailable = true;
    private bool _episodeTrimInFlight;

    internal void NoteActivity()
    {
        BreakIdleEpisode();
    }

    private void BreakIdleEpisode()
    {
        _quietSince = null;
        _quietTier = null;
        _episodeTrimAvailable = true;
    }

    internal QuiescenceTrimDecision Observe(
        DateTimeOffset now,
        QuiescenceTrimActivitySnapshot activity,
        Func<long> readWorkingSetBytes)
    {
        if (_settledWorkingSetBaseline is null &&
            _lastTrimAt is DateTimeOffset trimmedAt &&
            now - trimmedAt >= BaselineSettleDelay)
        {
            _settledWorkingSetBaseline = readWorkingSetBytes();
        }

        QuiescenceTrimTier tier = ResolveTier(activity);
        if (activity.IsWidgetInteractionActive ||
            activity.HasActiveVisualWork ||
            activity.IsTransientUiOpen)
        {
            BreakIdleEpisode();
            return QuiescenceTrimDecision.Skip(tier, DescribeBlocker(activity));
        }

        if (_quietTier != tier)
        {
            // A tier change restarts quiet counting: quiet accumulated under
            // a cheaper tier must not satisfy a stricter one, and a
            // downgrade gives the immediate-hidden trim its grace first.
            // A genuine transition between two observed tiers also starts a
            // new idle episode; the null→tier entry right after a trim is
            // NOT one (CommitTrim clears the tier — re-arming there would
            // hand every trim's successor its own free pass: the churn loop).
            bool hadTier = _quietTier is not null;
            _quietSince = null;
            _quietTier = tier;
            if (hadTier)
            {
                _episodeTrimAvailable = true;
            }
        }

        _quietSince ??= now;
        TimeSpan quiet = now - _quietSince.Value;
        // Looping ambient animation (compact marquee, vinyl) is steady-state
        // decoration — it cannot block outright or the trim would never fire
        // — but it earns the strictest quiet duration.
        TimeSpan requiredQuiet = activity.HasAmbientVisualWork
            ? VisibleForegroundQuietDuration
            : GetRequiredQuietDuration(tier);
        if (quiet < requiredQuiet)
        {
            return QuiescenceTrimDecision.Skip(tier, "waiting-for-quiet", quiet);
        }

        // The episode rule only exists on the static background tier: the
        // hidden tier already trims cheaply, and the floor-bound tiers
        // (foreground, ambient) trim only when big enough to be worth it.
        bool staticBackground = tier == QuiescenceTrimTier.VisibleBackground &&
            !activity.HasAmbientVisualWork;
        bool episodeTrim = staticBackground && _episodeTrimAvailable;
        TimeSpan cooldown = staticBackground && !episodeTrim
            ? StaticBackgroundMinimumTrimInterval
            : MinimumTrimInterval;
        if (_lastTrimAt is DateTimeOffset lastTrimAt &&
            now - lastTrimAt < cooldown)
        {
            return QuiescenceTrimDecision.Skip(tier, "trim-cooldown", quiet);
        }

        if (episodeTrim)
        {
            if (readWorkingSetBytes() < EpisodeTrimMinimumWorkingSetBytes)
            {
                // Already lean — the flag stays armed: a later growth spurt
                // in the same episode still deserves its free trim.
                return QuiescenceTrimDecision.Skip(
                    tier,
                    "below-episode-floor",
                    quiet);
            }

            // The idle transition itself is the justification: no baseline
            // wait, no growth gate. The flag is consumed on CommitTrim (the
            // caller re-observes before trimming — consuming here would
            // make the recheck fail its own cooldown).
            _episodeTrimInFlight = true;
            return new QuiescenceTrimDecision(
                true,
                tier,
                "episode-idle",
                quiet,
                EpisodeTrim: true);
        }

        if (_lastTrimAt is not null && _settledWorkingSetBaseline is null)
        {
            return QuiescenceTrimDecision.Skip(tier, "baseline-settling", quiet);
        }

        if ((tier == QuiescenceTrimTier.VisibleForeground ||
             (tier == QuiescenceTrimTier.VisibleBackground &&
              activity.HasAmbientVisualWork)) &&
            readWorkingSetBytes() < VisibleTierMinimumWorkingSetBytes)
        {
            // The absolute floor protects sessions whose pages the system
            // keeps touching: the foreground tier (the user is working with
            // DeskBox right now) and background sessions with looping
            // ambient animation (the animation reads resident pages every
            // frame, so a trim faults them straight back). A static
            // background-visible desktop — file grids, the system monitor's
            // 1 Hz text rows — only touches a handful of pages per update,
            // so trimming there is effective; those sessions rely on the
            // growth gate alone. (2026-10-17: an always-visible system
            // widget otherwise dead-locked the idle trim below the floor —
            // "idle trim stopped working once the monitor widget was on".)
            return QuiescenceTrimDecision.Skip(tier, "below-visible-floor", quiet);
        }

        if (_settledWorkingSetBaseline is long baseline)
        {
            long growth = readWorkingSetBytes() - baseline;
            long requiredGrowth = Math.Clamp(
                (long)(baseline * MinimumWorkingSetGrowthRatio),
                MinimumWorkingSetGrowthBytes,
                MaximumWorkingSetGrowthBytes);
            if (growth < requiredGrowth)
            {
                return QuiescenceTrimDecision.Skip(tier, "insufficient-growth", quiet);
            }
        }

        return new QuiescenceTrimDecision(true, tier, "quiet", quiet);
    }

    /// <summary>
    /// Records a completed trim from any path (quiescence, immediate hidden,
    /// or deep cleanup) so cooldown and growth accounting stay unified. The
    /// growth baseline is re-sampled once the working set has settled. A
    /// committed episode trim consumes the idle episode's free pass; a trim
    /// from any other path does not re-arm it (a trim is not activity).
    /// </summary>
    internal void CommitTrim(DateTimeOffset now)
    {
        _lastTrimAt = now;
        _settledWorkingSetBaseline = null;
        _quietSince = null;
        _quietTier = null;
        if (_episodeTrimInFlight)
        {
            _episodeTrimInFlight = false;
            _episodeTrimAvailable = false;
        }
    }

    /// <summary>
    /// Whether any trim path committed within <paramref name="interval"/>.
    /// Other trim callers use this to skip redundant back-to-back trims.
    /// </summary>
    internal bool LastTrimWithin(DateTimeOffset now, TimeSpan interval) =>
        _lastTrimAt is DateTimeOffset trimmedAt && now - trimmedAt < interval;

    internal static TimeSpan GetRequiredQuietDuration(QuiescenceTrimTier tier) => tier switch
    {
        QuiescenceTrimTier.Hidden => HiddenQuietDuration,
        QuiescenceTrimTier.VisibleForeground => VisibleForegroundQuietDuration,
        _ => VisibleBackgroundQuietDuration
    };

    private static QuiescenceTrimTier ResolveTier(QuiescenceTrimActivitySnapshot activity)
    {
        if (!activity.HasVisibleWidgets)
        {
            return QuiescenceTrimTier.Hidden;
        }

        return activity.IsDeskBoxForeground || activity.IsPointerOverDeskBox
            ? QuiescenceTrimTier.VisibleForeground
            : QuiescenceTrimTier.VisibleBackground;
    }

    private static string DescribeBlocker(QuiescenceTrimActivitySnapshot activity)
    {
        if (activity.IsWidgetInteractionActive)
        {
            return "blocked:interaction";
        }

        return activity.HasActiveVisualWork
            ? "blocked:visual-work"
            : "blocked:transient-ui";
    }
}
