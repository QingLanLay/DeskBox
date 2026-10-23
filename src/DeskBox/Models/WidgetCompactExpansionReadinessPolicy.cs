namespace DeskBox.Models;

public enum WidgetCompactExpansionReadinessDecision
{
    ExpandNow,
    WaitForWarmup,
    ExpandWithLiveLayoutFallback
}

/// <summary>
/// Readiness may improve first-frame smoothness, but can never become a hard
/// functional gate. Every deferred request therefore has a fixed deadline.
/// </summary>
public static class WidgetCompactExpansionReadinessPolicy
{
    public const int DefaultDeadlineMilliseconds = 96;

    /// <summary>
    /// Hard ceiling for the total hold when the warm-up is still actively
    /// running at the default deadline. The capsule has not moved yet, so a
    /// bounded extra wait is invisible next to a live-layout fallback that
    /// stutters the expansion's opening frames.
    /// </summary>
    public const int ExtendedDeadlineMilliseconds = 320;

    public static WidgetCompactExpansionReadinessDecision Decide(
        bool isReady,
        bool deadlineElapsed)
    {
        if (isReady)
        {
            return WidgetCompactExpansionReadinessDecision.ExpandNow;
        }

        return deadlineElapsed
            ? WidgetCompactExpansionReadinessDecision.ExpandWithLiveLayoutFallback
            : WidgetCompactExpansionReadinessDecision.WaitForWarmup;
    }

    /// <summary>
    /// Extra milliseconds the deferral may wait past the default deadline.
    /// Only a warm-up that is both alive and currently allowed to run can
    /// finish; a blocked one must fall back immediately instead of stalling
    /// the capsule for the full extension.
    /// </summary>
    public static int ResolveDeadlineExtensionMilliseconds(
        bool warmupActive,
        bool warmupCanRunNow)
    {
        return warmupActive && warmupCanRunNow
            ? ExtendedDeadlineMilliseconds - DefaultDeadlineMilliseconds
            : 0;
    }

    /// <summary>
    /// The extension with the total hold enforced. The 2026-10-20 perf trail
    /// showed the deadline handler re-extending on every expiry for as long
    /// as the warm-up was runnable — and the pointer dwelling on the capsule
    /// re-issuing the expand request every dwell cycle, each with a fresh
    /// deadline — so the "hard ceiling" never actually engaged. The caller
    /// supplies the total time the current deferral lineage has already
    /// held; the extension is capped so the cumulative hold can never exceed
    /// <see cref="ExtendedDeadlineMilliseconds"/>.
    /// </summary>
    public static int ResolveDeadlineExtensionMilliseconds(
        bool warmupActive,
        bool warmupCanRunNow,
        double totalHeldMilliseconds)
    {
        if (!warmupActive || !warmupCanRunNow)
        {
            return 0;
        }

        double remaining =
            ExtendedDeadlineMilliseconds - Math.Max(0, totalHeldMilliseconds);
        if (remaining <= 0)
        {
            return 0;
        }

        double nominal =
            ExtendedDeadlineMilliseconds - DefaultDeadlineMilliseconds;
        return (int)Math.Ceiling(Math.Min(nominal, remaining));
    }
}
