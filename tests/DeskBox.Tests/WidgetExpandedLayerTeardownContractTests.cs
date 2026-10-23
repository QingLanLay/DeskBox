namespace DeskBox.Tests;

/// <summary>
/// Pins the expanded-lease dual-state fix (2026-10-17): manager teardown must
/// hand the lease back (direction 1 — EndExpandedWidgetLayerLease notifies the
/// manager), and a re-show must reconcile the residue a hide-while-expanded
/// leaves behind (direction 2 — the manager voids its slot once the owner is
/// invisible, but the window-local generation survives the hidden period and
/// would otherwise keep the window refusing to bed forever).
/// </summary>
public sealed class WidgetExpandedLayerTeardownContractTests
{
    [Fact]
    public void ForceRestoreDesktopLayerFromManager_EndsLeaseBeforeForcedRestore()
    {
        string source = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Views/ContentWidgetWindow.xaml.cs"));
        string method = SliceMethod(
            source,
            "public void ForceRestoreDesktopLayerFromManager()",
            "public void RestoreDesktopLayerFromManager()");

        // The single wiring point of the teardown fix: without this call the
        // manager-side lease survives the session teardown and gates every
        // restore funnel until the window disappears. It must run before the
        // forced restore and unconditionally.
        int end = method.IndexOf("EndExpandedWidgetLayerLease();", StringComparison.Ordinal);
        int restore = method.IndexOf("RestoreDesktopLayer(force: true);", StringComparison.Ordinal);
        Assert.True(end >= 0 && restore > end,
            "lease teardown must precede the forced layer restore");
        Assert.DoesNotContain("HasExpandedWidgetLayerLease", method, StringComparison.Ordinal);
    }

    [Fact]
    public void EndExpandedWidgetLayerLease_NotifiesManagerTearDownWithItsGeneration()
    {
        string source = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Views/WidgetWindowBase.Collapse.cs"));
        string method = SliceMethod(
            source,
            "protected void EndExpandedWidgetLayerLease()",
            "private long _compactExpansionRequestGeneration;");

        Assert.Contains("TearDownExpandedWidgetLayer", method, StringComparison.Ordinal);
        Assert.Contains("if (generation > 0)", method, StringComparison.Ordinal);
        // The captured local generation (not the already-zeroed field) must
        // be handed back — argument-order pin.
        int call = method.IndexOf("TearDownExpandedWidgetLayer(", StringComparison.Ordinal);
        int hwnd = method.IndexOf("HWnd,", StringComparison.Ordinal);
        int generation = method.IndexOf("generation,", StringComparison.Ordinal);
        Assert.True(call >= 0 && hwnd > call && generation > hwnd,
            "teardown must pass HWnd then the captured generation");
        // The side-effectful release (temporary-raise restore + cohesion-exit
        // normalization) must not run from manager teardown.
        Assert.DoesNotContain("ReleaseExpandedWidgetLayerLease(", method, StringComparison.Ordinal);
    }

    [Fact]
    public void TearDownExpandedWidgetLayer_IsGenerationGuardedAndSideEffectFree()
    {
        string source = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Services/WidgetManager.ZOrder.cs"));
        string method = SliceMethod(
            source,
            "internal bool TearDownExpandedWidgetLayer(",
            "private bool HasActiveExpandedWidgetLayerLease()");

        // A preempted owner's stale teardown must not release the live lease
        // (polarity pin: the guard rejects non-owners).
        Assert.Contains(
            "if (!WidgetExpandedLayerLeasePolicy.Owns(",
            method,
            StringComparison.Ordinal);
        // Teardown callers own the group's layer transition; the release-time
        // restore and the forced cohesion-exit normalization must not re-enter.
        Assert.DoesNotContain(
            "RestoreTemporarilyRaisedWidgetsToDesktopLayer", method, StringComparison.Ordinal);
        Assert.DoesNotContain("QueueIdleWidgetZOrderNormalization", method, StringComparison.Ordinal);
    }

    [Fact]
    public void ShowPaths_ReconcileStaleExpandedLeaseResidue()
    {
        string window = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Views/ContentWidgetWindow.xaml.cs"));
        string collapse = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Views/WidgetWindowBase.Collapse.cs"));

        string atDesktopLayer = SliceMethod(
            window,
            "public void ShowPreparedAtDesktopLayer(",
            "public void ShowPreparedRaisedFromTray(");
        string raisedFromTray = SliceMethod(
            window,
            "public void ShowPreparedRaisedFromTray(",
            "public void PlayTrayShowAnimation()");
        string reconcile = SliceMethod(
            collapse,
            "protected void ReconcileStaleExpandedWidgetLayerLease()",
            "private void HoldExpandedPeerLayerLease(");

        Assert.Contains(
            "ReconcileStaleExpandedWidgetLayerLease();",
            atDesktopLayer,
            StringComparison.Ordinal);
        Assert.Contains(
            "ReconcileStaleExpandedWidgetLayerLease();",
            raisedFromTray,
            StringComparison.Ordinal);
        // The residue is kept while the manager still attributes the lease
        // here; only an unowned generation is cleared on re-show
        // (polarity pin: a null manager counts as not owning).
        Assert.Contains(
            "OwnsExpandedWidgetLayer(HWnd, generation) == true",
            reconcile,
            StringComparison.Ordinal);
        Assert.Contains("generation == 0", reconcile, StringComparison.Ordinal);
    }

    private static string SliceMethod(string source, string startMarker, string endMarker)
    {
        int start = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Missing source marker: {startMarker}");
        int end = source.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.True(end > start, $"Missing source marker: {endMarker}");
        return source[start..end];
    }
}
