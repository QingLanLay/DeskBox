namespace DeskBox.Tests;

/// <summary>
/// Contracts for the hosted-content-swap urgent warm-up (2026-10-20 group
/// tab-switch hover delay): a member swap invalidates the expansion
/// warm-up, and the collapse that follows it usually precedes an immediate
/// re-entry. The re-warm must take the urgent lane then — the background
/// lane defers to application idle and to the shared frame clock, where
/// switch-settle animations keep it blocked until long after the user is
/// already hovering (the loss lands on the 320 ms live-layout fallback and
/// visibly stutters the expansion's opening).
/// </summary>
public sealed class HostedContentSwapWarmupContractTests
{
    [Theory]
    [InlineData(0, true)]
    [InlineData(16, true)]
    [InlineData(9_999, true)]
    [InlineData(10_000, true)]
    [InlineData(10_001, false)]
    [InlineData(60_000, false)]
    public void RecentSwap_WarmsUrgently(double elapsedMs, bool expected)
    {
        Assert.Equal(
            expected,
            Services.WidgetCompactWarmupSchedulePolicy
                .ShouldWarmUrgentlyAfterHostedContentSwap(elapsedMs));
    }

    [Fact]
    public void CollapsedSwap_ReWarmTakesTheUrgentLane()
    {
        string source = Read("src/DeskBox/Views/WidgetWindowBase.Collapse.cs");

        // The swap is recorded and both queue sites honor it: the
        // collapsed-swap site queues urgent unconditionally, and the
        // smart-collapse completion keys urgency off the swap recency.
        Assert.Contains("_hostedContentSwapTimestamp = Stopwatch.GetTimestamp();", source, StringComparison.Ordinal);
        Assert.Contains(
            "if (_targetCollapsed && _compactExpansionWarmupCancellation is null)\n" +
            "        {\n" +
            "            // Swapped while collapsed: the user is interacting with the new\n" +
            "            // member right now and the next hover expand is imminent, so the\n" +
            "            // re-warm takes the urgent lane (the pointer-over gate would\n" +
            "            // otherwise pin it to background idle until the pointer leaves).\n" +
            "            QueueCompactExpansionWarmup(urgent: true);",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "QueueCompactExpansionWarmup(\n" +
            "                urgent: WidgetCompactWarmupSchedulePolicy\n" +
            "                    .ShouldWarmUrgentlyAfterHostedContentSwap(",
            source,
            StringComparison.Ordinal);
    }

    private static string Read(string relativePath) =>
        // Normalize EOL: the repository blob is LF but a fresh Windows
        // checkout materializes CRLF, and the multi-line assertions above
        // pin exact "\n"-joined snippets.
        File.ReadAllText(Path.Combine(FindRepositoryRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar)))
            .Replace("\r\n", "\n");

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
