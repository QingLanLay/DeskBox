namespace DeskBox.Tests;

/// <summary>
/// Contracts for the compact-expansion hold ceiling (2026-10-20 group
/// tab-switch hover delay). The perf trail showed a switched-in member's
/// visual-tree prime queue draining at 48 nodes per slice (~930 ms total)
/// while every deadline expiry re-extended the readiness hold by 224 ms and
/// the dwelling pointer re-issued the expand request with a fresh deadline
/// every dwell cycle — together defeating the documented 320 ms "hard
/// ceiling". These contracts pin the two restorations: the cumulative hold
/// (including inherited clocks across re-requests) can never exceed
/// ExtendedDeadlineMilliseconds, and the urgent warm-up lane primes the
/// tree with a full-frame budget.
/// </summary>
public sealed class CompactExpansionHoldCeilingContractTests
{
    [Theory]
    [InlineData(true, true, 0, 224)]      // fresh hold: full nominal extension
    [InlineData(true, true, 96, 224)]     // one default deadline in: still nominal
    [InlineData(true, true, 200, 120)]    // partial: capped to the remainder
    [InlineData(true, true, 319.5, 1)]    // ceiling boundary: half measure
    [InlineData(true, true, 320, 0)]      // at the ceiling: no more holding
    [InlineData(true, true, 5000, 0)]     // long-overdue: no more holding
    [InlineData(false, true, 0, 0)]       // warm-up not running: fall back now
    [InlineData(true, false, 0, 0)]       // warm-up blocked: fall back now
    public void Extension_RespectsCumulativeCeiling(
        bool warmupActive,
        bool warmupCanRunNow,
        double totalHeldMs,
        int expected)
    {
        Assert.Equal(
            expected,
            Models.WidgetCompactExpansionReadinessPolicy
                .ResolveDeadlineExtensionMilliseconds(
                    warmupActive,
                    warmupCanRunNow,
                    totalHeldMilliseconds: totalHeldMs));
    }

    [Fact]
    public void UrgentWarmup_PrimesWithAFullFrameBudget()
    {
        string source = Read("src/DeskBox/Views/WidgetWindowBase.Collapse.cs");

        Assert.Contains(
            "private const int CompactExpansionPrimeNodeBudgetUrgent = 192;",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "PrimeCompactExpansionVisualTree(reason, urgent);",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "int nodeBudget = urgent",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void RepeatExpandRequests_InheritTheParkedHoldClock()
    {
        string source = Read("src/DeskBox/Views/WidgetWindowBase.Collapse.cs");

        // The parked deferral's clock survives re-requests ...
        Assert.Contains(
            "long requestedAt = _pendingCompactExpansion is { } parked &&",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "? parked.RequestedAtTimestamp",
            source,
            StringComparison.Ordinal);
        // ... and the deadline handler bills against the cumulative hold.
        Assert.Contains(
            "totalHeldMilliseconds: totalHeldMs",
            source,
            StringComparison.Ordinal);
    }

    private static string Read(string relativePath) =>
        File.ReadAllText(Path.Combine(FindRepositoryRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar)));

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "src", "DeskBox", "DeskBox.csproj")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("DeskBox repository root was not found.");
    }
}
