using DeskBox.Models;
using DeskBox.Services;
using Windows.Graphics;

namespace DeskBox.Tests;

/// <summary>
/// Placement-lineage contracts (the 2026-10-20 reveal-jump incident): the
/// user's latest deliberate placement outranks older stored topology
/// entries on the same display, and shelter/fallback geometry is never
/// promoted to authoritative intent. The incident chain: a stale snapshot
/// entry was re-applied over the user's newer placement on every topology
/// activation, and a later commit promoted the result as authoritative —
/// locking the widget to a position the user never chose.
/// </summary>
public sealed class WidgetPlacementLineageContractTests
{
    [Fact]
    public void Reactivation_YieldsToNewerDeliberatePlacement()
    {
        // The widget starts at a stale top spot; the single-panel topology
        // captures it, then a dual topology activates (both entries now
        // hold the stale spot).
        var widget = LineageWidget(x: 911, y: 26, anchor: WidgetPositionAnchors.RightTop);
        var settings = new AppSettings { Widgets = [widget] };
        var service = new WidgetTopologyLayoutService();
        WidgetDisplayTopologySnapshot panel = Snapshot(
            Monitor("panel", @"\\.\DISPLAY1", true, 0, 0, 1920, 1032, 1));
        WidgetDisplayTopologySnapshot dual = Snapshot(
            Monitor("panel", @"\\.\DISPLAY1", true, 0, 0, 1920, 1032, 1),
            Monitor("ext", @"\\.\DISPLAY2", false, 1920, 0, 1920, 1032, 1));

        Assert.True(service.Activate(settings, panel));
        Assert.True(service.Activate(settings, dual));

        // The user deliberately places the widget bottom-right while the
        // dual topology is active — the panel profile's stored entry still
        // holds the stale top spot.
        widget.X = 1336;
        widget.Y = 984;
        widget.PositionAnchor = WidgetPositionAnchors.RightBottom;
        widget.PositionMarginX = 8;
        widget.PositionMarginY = 7;
        widget.LastUserPlacementUtc = DateTimeOffset.UtcNow.AddMinutes(1);

        // The incident's executor: re-activating the panel topology would
        // re-apply its stale stored entry. The deliberate-placement stamp
        // must win instead.
        Assert.True(service.Activate(settings, panel));

        Assert.Equal(1336, widget.X);
        Assert.Equal(984, widget.Y);
        Assert.Equal(WidgetPositionAnchors.RightBottom, widget.PositionAnchor);
        // And the stored entry itself is healed to the deliberate placement.
        WidgetSurfaceLayoutProfile healed = settings.WidgetTopologyLayouts[panel.Key]
            .Surfaces[widget.Id];
        Assert.Equal(1336, healed.X);
        Assert.Equal(984, healed.Y);
    }

    [Fact]
    public void Reactivation_WithoutPlacementStamp_KeepsProfileSemantics()
    {
        // Legacy data carries no deliberate-placement stamp: the stored
        // authoritative entry keeps winning exactly as before — proving
        // the arbitration only changes behavior when the newer intent is
        // actually known.
        var widget = LineageWidget(x: 911, y: 26, anchor: WidgetPositionAnchors.RightTop);
        var settings = new AppSettings { Widgets = [widget] };
        var service = new WidgetTopologyLayoutService();
        WidgetDisplayTopologySnapshot panel = Snapshot(
            Monitor("panel", @"\\.\DISPLAY1", true, 0, 0, 1920, 1032, 1));
        WidgetDisplayTopologySnapshot dual = Snapshot(
            Monitor("panel", @"\\.\DISPLAY1", true, 0, 0, 1920, 1032, 1),
            Monitor("ext", @"\\.\DISPLAY2", false, 1920, 0, 1920, 1032, 1));

        Assert.True(service.Activate(settings, panel));
        Assert.True(service.Activate(settings, dual));

        widget.X = 1336;
        widget.Y = 984;
        widget.PositionAnchor = WidgetPositionAnchors.RightBottom;
        widget.LastUserPlacementUtc = null;

        Assert.True(service.Activate(settings, panel));

        // The stale panel entry re-applies through its old anchor: the
        // widget returns to the TOP strip (RightTop + margins), not the
        // user's bottom-right placement.
        Assert.Equal(WidgetPositionAnchors.RightTop, widget.PositionAnchor);
        Assert.Equal(7, widget.Y);
    }

    /// <summary>
    /// The arbitration is same-display only: an entry describing another
    /// physical monitor keeps its projected intent — the config's stamp
    /// belongs to the display it was placed on. Degenerate identities
    /// cannot prove a foreign display and resolve conservatively.
    /// </summary>
    [Fact]
    public void SameEntryDisplayAsConfig_IsSameDisplayOnly()
    {
        var entry = new WidgetSurfaceLayoutProfile { PositionMonitorStableId = "ext" };
        var config = new WidgetConfig { PositionMonitorStableId = "panel" };
        var profile = new WidgetTopologyLayoutProfile();

        Assert.False(WidgetTopologyLayoutService.SameEntryDisplayAsConfig(
            entry, config, profile));
        Assert.True(WidgetTopologyLayoutService.SameEntryDisplayAsConfig(
            new WidgetSurfaceLayoutProfile { PositionMonitorStableId = "panel" },
            config,
            profile));
        // Degenerate entry identities cannot prove a foreign display.
        Assert.True(WidgetTopologyLayoutService.SameEntryDisplayAsConfig(
            new WidgetSurfaceLayoutProfile { PositionMonitorStableId = null },
            config,
            profile));
    }

    [Fact]
    public void ConfigPlacementOutranks_OnlyWhenStampIsNewer()
    {
        var config = new WidgetConfig { LastUserPlacementUtc = DateTimeOffset.UtcNow };
        var older = new WidgetSurfaceLayoutProfile
        {
            IsAuthoritative = true,
            AuthoredAtUtc = DateTimeOffset.UtcNow.AddMinutes(-5)
        };
        var never = new WidgetSurfaceLayoutProfile
        {
            IsAuthoritative = true,
            AuthoredAtUtc = null
        };
        var newer = new WidgetSurfaceLayoutProfile
        {
            IsAuthoritative = true,
            AuthoredAtUtc = DateTimeOffset.UtcNow.AddMinutes(5)
        };

        Assert.True(WidgetTopologyLayoutService.ConfigPlacementOutranksEntry(config, older));
        Assert.True(WidgetTopologyLayoutService.ConfigPlacementOutranksEntry(config, never));
        Assert.False(WidgetTopologyLayoutService.ConfigPlacementOutranksEntry(config, newer));
        Assert.False(WidgetTopologyLayoutService.ConfigPlacementOutranksEntry(
            new WidgetConfig { LastUserPlacementUtc = null }, older));
    }

    [Fact]
    public void MapToTopology_FallbackPlacement_IsNeverAuthoritative()
    {
        // A source with neither a valid anchor nor a resolvable source
        // monitor (empty source monitor list) maps to shelter geometry
        // (work area + 32). It must never carry authority, or the fallback
        // outranks every later deliberate placement.
        var source = new WidgetSurfaceLayoutProfile
        {
            PositionAnchor = null,
            PositionMonitorStableId = null,
            PositionMonitorDeviceName = null,
            BoundsCoordinateVersion = WidgetConfig.CurrentBoundsCoordinateVersion,
            X = -4000,
            Y = -4000,
            Width = 576,
            Height = 41,
            IsAuthoritative = true,
            AuthoredAtUtc = DateTimeOffset.UtcNow
        };
        WidgetTopologyMonitorProfile replacement =
            Monitor("ext", @"\\.\DISPLAY2", true, 0, 0, 1920, 1032, 1);

        WidgetSurfaceLayoutProfile mapped = WidgetTopologyLayoutService.MapToTopology(
            source,
            [],
            [replacement],
            out bool usedFallbackPlacement);

        Assert.True(usedFallbackPlacement);
        Assert.NotEqual(mapped.IsAuthoritative, true);
        Assert.Null(mapped.AuthoredAtUtc);
    }

    [Fact]
    public void EnsureVisible_FallbackFlag_MarksShelterGeometry()
    {
        var workArea = new RectInt32(0, 0, 1920, 1032);

        RectInt32 sheltered = WidgetPositioningService.EnsureVisible(
            new RectInt32(1320, 1100, 576, 41), workArea, out bool fellBack);
        Assert.True(fellBack);
        Assert.Equal(32, sheltered.X);

        WidgetPositioningService.EnsureVisible(
            new RectInt32(1320, 984, 576, 41), workArea, out bool dockedFellBack);
        Assert.False(dockedFellBack);
    }

    /// <summary>
    /// Source contract: every deliberate placement commit advances the
    /// arbitration clock — without the stamp the lineage arbitration is
    /// vacuous.
    /// </summary>
    [Fact]
    public void CommitUserPlacement_StampsTheDeliberateClock()
    {
        string source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "DeskBox",
            "Services",
            "WidgetManager.ScreenHome.cs"));
        Assert.Contains(
            "config.LastUserPlacementUtc = DateTimeOffset.UtcNow;",
            source,
            StringComparison.Ordinal);
    }

    private static WidgetConfig LineageWidget(int x, int y, string anchor) => new()
    {
        Id = "lineage-1",
        X = x,
        Y = y,
        Width = 576,
        Height = 41,
        BoundsCoordinateVersion = WidgetConfig.CurrentBoundsCoordinateVersion,
        PositionAnchor = anchor,
        PositionMarginX = 8,
        PositionMarginY = 7,
        PositionMonitorStableId = "panel",
        PositionMonitorDeviceName = @"\\.\DISPLAY1",
        PositionMonitorWasPrimary = true,
        PositionMonitorKey = "0:0:1920:1032",
        ScreenBindingMode = WidgetScreenBindingMode.Pinned,
        BoundScreenId = "panel"
    };

    private static WidgetDisplayTopologySnapshot Snapshot(
        params WidgetTopologyMonitorProfile[] monitors) =>
        WidgetTopologyLayoutService.CreateSnapshotForTest(monitors);

    private static WidgetTopologyMonitorProfile Monitor(
        string stableId,
        string deviceName,
        bool primary,
        int x,
        int y,
        int width,
        int height,
        double scale) => new()
    {
        StableId = stableId,
        DeviceName = deviceName,
        IsPrimary = primary,
        MonitorX = x,
        MonitorY = y,
        MonitorWidth = width,
        MonitorHeight = height,
        WorkAreaX = x,
        WorkAreaY = y,
        WorkAreaWidth = width,
        WorkAreaHeight = height,
        DpiScale = scale
    };

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
