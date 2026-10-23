using DeskBox.Models;
using DeskBox.Services;
using Windows.Graphics;

namespace DeskBox.Tests;

public sealed class WidgetPositioningServiceTests
{
    private static RectInt32 ResolveAtScale(
        WidgetConfig config,
        RectInt32 fallbackWorkArea,
        double scale,
        params RectInt32[] availableWorkAreas)
    {
        return WidgetPositioningService.ResolveBoundsForTest(
            config,
            fallbackWorkArea,
            availableWorkAreas.Select(workArea => (workArea, (string?)null)).ToList(),
            _ => scale);
    }

    private static RectInt32 ResolveWithMonitors(
        WidgetConfig config,
        RectInt32 fallbackWorkArea,
        IReadOnlyList<(RectInt32 WorkArea, string? DeviceName)> availableWorkAreas,
        double scale)
    {
        return WidgetPositioningService.ResolveBoundsForTest(
            config,
            fallbackWorkArea,
            availableWorkAreas,
            _ => scale);
    }

    private static RectInt32 ResolveWithPrimaryMonitors(
        WidgetConfig config,
        RectInt32 fallbackWorkArea,
        IReadOnlyList<(RectInt32 WorkArea, string? DeviceName, bool IsPrimary)> availableWorkAreas,
        double scale)
    {
        return WidgetPositioningService.ResolveBoundsForTestWithPrimary(
            config,
            fallbackWorkArea,
            availableWorkAreas,
            _ => scale);
    }

    private static bool EnsureCurrentBoundsCoordinateVersionWithPrimaryMonitors(
        WidgetConfig config,
        RectInt32 fallbackWorkArea,
        IReadOnlyList<(RectInt32 WorkArea, string? DeviceName, bool IsPrimary)> availableWorkAreas,
        double scale)
    {
        return WidgetPositioningService.EnsureCurrentBoundsCoordinateVersionForTestWithPrimary(
            config,
            fallbackWorkArea,
            availableWorkAreas,
            _ => scale);
    }

    [Fact]
    public void CaptureAnchor_UsesNearestCornerMargins()
    {
        var config = new WidgetConfig();
        var workArea = new RectInt32(0, 0, 1920, 1040);
        var bounds = new RectInt32(1580, 80, 300, 400);

        WidgetPositioningService.CaptureAnchor(config, bounds, workArea);

        Assert.Equal(WidgetPositionAnchors.RightTop, config.PositionAnchor);
        Assert.Equal(40, config.PositionMarginX);
        Assert.Equal(80, config.PositionMarginY);
        Assert.Equal("0:0:1920:1040", config.PositionMonitorKey);
    }

    [Fact]
    public void CaptureAnchorPreservingCurrentEdge_DoesNotFlipTopAnchorAfterTallResize()
    {
        var config = new WidgetConfig
        {
            PositionAnchor = WidgetPositionAnchors.LeftTop,
            BoundsCoordinateVersion = WidgetConfig.CurrentBoundsCoordinateVersion
        };
        var workArea = new RectInt32(0, 0, 1920, 1040);
        var resizedBounds = new RectInt32(100, 80, 600, 900);

        WidgetPositioningService.CaptureAnchorPreservingCurrentEdge(
            config,
            resizedBounds,
            workArea);

        Assert.Equal(WidgetPositionAnchors.LeftTop, config.PositionAnchor);
        Assert.Equal(100, config.PositionMarginX);
        Assert.Equal(80, config.PositionMarginY);
    }

    [Fact]
    public void ResolveBounds_KeepsRightTopMarginWhenWorkAreaWidthChanges()
    {
        var config = new WidgetConfig
        {
            X = 1580,
            Y = 80,
            Width = 300,
            Height = 400,
            PositionAnchor = WidgetPositionAnchors.RightTop,
            PositionMarginX = 40,
            PositionMarginY = 80
        };
        var largerWorkArea = new RectInt32(0, 0, 3840, 2080);

        var bounds = ResolveAtScale(config, largerWorkArea, 1.0);

        Assert.Equal(3500, bounds.X);
        Assert.Equal(80, bounds.Y);
        Assert.Equal(300, bounds.Width);
        Assert.Equal(400, bounds.Height);
    }

    [Fact]
    public void ResolveBounds_KeepsRightBottomMarginWhenWorkAreaChanges()
    {
        var config = new WidgetConfig
        {
            Width = 320,
            Height = 240,
            PositionAnchor = WidgetPositionAnchors.RightBottom,
            PositionMarginX = 24,
            PositionMarginY = 36
        };
        var workArea = new RectInt32(100, 50, 1600, 900);

        var bounds = ResolveAtScale(config, workArea, 1.0);

        Assert.Equal(1356, bounds.X);
        Assert.Equal(674, bounds.Y);
    }

    [Fact]
    public void ResolveBounds_ClampsLegacyAbsoluteCoordinatesIntoFallbackWorkArea()
    {
        var config = new WidgetConfig
        {
            X = 3500,
            Y = 120,
            Width = 300,
            Height = 400
        };
        var laptopWorkArea = new RectInt32(0, 0, 1920, 1040);

        var bounds = ResolveAtScale(config, laptopWorkArea, 1.0);

        Assert.Equal(32, bounds.X);
        Assert.Equal(32, bounds.Y);
    }

    [Fact]
    public void ResolveBounds_PrefersSavedMonitorWhenAvailable()
    {
        var savedMonitor = new RectInt32(1920, 0, 2560, 1400);
        var primaryMonitor = new RectInt32(0, 0, 1920, 1040);
        var config = new WidgetConfig
        {
            Width = 300,
            Height = 400,
            PositionAnchor = WidgetPositionAnchors.RightTop,
            PositionMarginX = 30,
            PositionMarginY = 60,
            PositionMonitorKey = WidgetPositioningService.CreateMonitorKey(savedMonitor)
        };

        var bounds = ResolveAtScale(config, primaryMonitor, 1.0, primaryMonitor, savedMonitor);

        Assert.Equal(4150, bounds.X);
        Assert.Equal(60, bounds.Y);
    }

    [Fact]
    public void ResolveBounds_StaysOnFormerPrimaryWhenStillOnline()
    {
        // Spec 5.1: the runtime smart-follow is gone — a widget captured on a
        // monitor that is still online stays there even when the primary
        // moved elsewhere.
        var formerPrimaryLaptop = new RectInt32(-1536, 0, 1536, 824);
        var currentPrimaryExternal = new RectInt32(0, 0, 2560, 1400);
        var config = new WidgetConfig
        {
            BoundsCoordinateVersion = WidgetConfig.CurrentBoundsCoordinateVersion,
            Width = 300,
            Height = 200,
            PositionAnchor = WidgetPositionAnchors.RightBottom,
            PositionMarginX = 16,
            PositionMarginY = 24,
            PositionMonitorKey = "0:0:1536:824",
            PositionMonitorDeviceName = @"\\.\DISPLAY1",
            PositionMonitorWasPrimary = true
        };

        var bounds = ResolveWithPrimaryMonitors(
            config,
            formerPrimaryLaptop,
            [
                (formerPrimaryLaptop, @"\\.\DISPLAY1", false),
                (currentPrimaryExternal, @"\\.\DISPLAY2", true)
            ],
            1.0);

        Assert.Equal(-316, bounds.X);
        Assert.Equal(600, bounds.Y);
        Assert.Equal(300, bounds.Width);
        Assert.Equal(200, bounds.Height);
    }

    [Fact]
    public void ResolveBounds_KeepsSecondaryMonitorWhenCapturedOffPrimaryMonitor()
    {
        var secondaryLaptop = new RectInt32(-1536, 0, 1536, 824);
        var currentPrimaryExternal = new RectInt32(0, 0, 2560, 1400);
        var config = new WidgetConfig
        {
            BoundsCoordinateVersion = WidgetConfig.CurrentBoundsCoordinateVersion,
            Width = 300,
            Height = 200,
            PositionAnchor = WidgetPositionAnchors.RightBottom,
            PositionMarginX = 16,
            PositionMarginY = 24,
            PositionMonitorKey = "0:0:1536:824",
            PositionMonitorDeviceName = @"\\.\DISPLAY1",
            PositionMonitorWasPrimary = false
        };

        var bounds = ResolveWithPrimaryMonitors(
            config,
            currentPrimaryExternal,
            [
                (secondaryLaptop, @"\\.\DISPLAY1", false),
                (currentPrimaryExternal, @"\\.\DISPLAY2", true)
            ],
            1.0);

        Assert.Equal(-316, bounds.X);
        Assert.Equal(600, bounds.Y);
        Assert.Equal(300, bounds.Width);
        Assert.Equal(200, bounds.Height);
    }

    [Fact]
    public void ResolveBounds_LegacyEntryStaysOnOriginWithoutSmartMode()
    {
        // The "origin-near-primary" heuristic is deleted (spec 5.1); a legacy
        // entry resolves through device-name/work-area identity and stays on
        // its origin display.
        var secondaryLaptop = new RectInt32(-1536, 0, 1536, 824);
        var currentPrimaryExternal = new RectInt32(0, 0, 2560, 1400);
        var config = new WidgetConfig
        {
            BoundsCoordinateVersion = WidgetConfig.CurrentBoundsCoordinateVersion,
            Width = 300,
            Height = 200,
            PositionAnchor = WidgetPositionAnchors.RightBottom,
            PositionMarginX = 16,
            PositionMarginY = 24,
            PositionMonitorKey = "0:0:1536:824",
            PositionMonitorDeviceName = @"\\.\DISPLAY1"
        };

        var bounds = ResolveWithPrimaryMonitors(
            config,
            secondaryLaptop,
            [
                (secondaryLaptop, @"\\.\DISPLAY1", false),
                (currentPrimaryExternal, @"\\.\DISPLAY2", true)
            ],
            1.0);

        Assert.Equal(-316, bounds.X);
        Assert.Equal(600, bounds.Y);
    }

    [Fact]
    public void ResolveBounds_UsesLogicalSizeAndMarginsOnHighDpiMonitor()
    {
        var workArea = new RectInt32(0, 0, 2560, 1400);
        var config = new WidgetConfig
        {
            BoundsCoordinateVersion = WidgetConfig.CurrentBoundsCoordinateVersion,
            Width = 300,
            Height = 200,
            PositionAnchor = WidgetPositionAnchors.RightBottom,
            PositionMarginX = 40,
            PositionMarginY = 20
        };

        var bounds = ResolveAtScale(config, workArea, 1.5);

        Assert.Equal(2050, bounds.X);
        Assert.Equal(1070, bounds.Y);
        Assert.Equal(450, bounds.Width);
        Assert.Equal(300, bounds.Height);
    }

    [Fact]
    public void ResolveBounds_ReflowsHiddenWidgetToFallbackMonitorWhenSavedMonitorIsMissing()
    {
        var savedExternalMonitor = new RectInt32(1920, 0, 2560, 1400);
        var laptopWorkArea = new RectInt32(0, 0, 1536, 824);
        var config = new WidgetConfig
        {
            BoundsCoordinateVersion = WidgetConfig.CurrentBoundsCoordinateVersion,
            X = 4200,
            Y = 900,
            Width = 300,
            Height = 200,
            PositionAnchor = WidgetPositionAnchors.RightBottom,
            PositionMarginX = 16,
            PositionMarginY = 24,
            PositionMonitorKey = WidgetPositioningService.CreateMonitorKey(savedExternalMonitor)
        };

        var bounds = ResolveAtScale(config, laptopWorkArea, 1.25, laptopWorkArea);

        Assert.Equal(1141, bounds.X);
        Assert.Equal(544, bounds.Y);
        Assert.Equal(375, bounds.Width);
        Assert.Equal(250, bounds.Height);
    }

    [Fact]
    public void EnsureCurrentBoundsCoordinateVersion_LegacyPrimaryWidgetStaysOnOriginDisplay()
    {
        var secondaryLaptop = new RectInt32(-1536, 0, 1536, 824);
        var currentPrimaryExternal = new RectInt32(0, 0, 2560, 1400);
        var config = new WidgetConfig
        {
            Width = 300,
            Height = 200,
            PositionAnchor = WidgetPositionAnchors.RightBottom,
            PositionMarginX = 16,
            PositionMarginY = 24,
            PositionMonitorKey = "0:0:1536:824",
            PositionMonitorDeviceName = @"\\.\DISPLAY1"
        };

        bool migrated = EnsureCurrentBoundsCoordinateVersionWithPrimaryMonitors(
            config,
            secondaryLaptop,
            [
                (secondaryLaptop, @"\\.\DISPLAY1", false),
                (currentPrimaryExternal, @"\\.\DISPLAY2", true)
            ],
            1.0);

        Assert.True(migrated);
        Assert.Equal(WidgetConfig.CurrentBoundsCoordinateVersion, config.BoundsCoordinateVersion);
        // Legacy migration now resolves through identity: the origin display
        // is still online, so the widget stays there (spec 5.1).
        Assert.Equal(-316, config.X);
        Assert.Equal(600, config.Y);
        Assert.Equal(@"\\.\DISPLAY1", config.PositionMonitorDeviceName);
        Assert.False(config.PositionMonitorWasPrimary!.Value);
        Assert.Equal(WidgetPositioningService.CreateMonitorKey(secondaryLaptop), config.PositionMonitorKey);
    }
    [Fact]
    public void EnsureCurrentBoundsCoordinateVersion_MigratesLegacyPhysicalSizeToLogicalSize()
    {
        var workArea = new RectInt32(0, 0, 2560, 1400);
        var config = new WidgetConfig
        {
            X = 120,
            Y = 90,
            Width = 450,
            Height = 300
        };

        bool migrated = WidgetPositioningService.EnsureCurrentBoundsCoordinateVersionForTest(
            config,
            workArea,
            [(workArea, null)],
            _ => 1.5);

        Assert.True(migrated);
        Assert.Equal(WidgetConfig.CurrentBoundsCoordinateVersion, config.BoundsCoordinateVersion);
        Assert.Equal(120, config.X);
        Assert.Equal(90, config.Y);
        Assert.Equal(300d, config.Width, precision: 3);
        Assert.Equal(200d, config.Height, precision: 3);
        Assert.Equal(WidgetPositionAnchors.LeftTop, config.PositionAnchor);
        Assert.Equal(80d, config.PositionMarginX, precision: 3);
        Assert.Equal(60d, config.PositionMarginY, precision: 3);
    }

    [Fact]
    public void ResolveBounds_PrefersMonitorDeviceNameOverLegacyWorkAreaKey()
    {
        var primaryMonitor = new RectInt32(0, 0, 1920, 1040);
        var externalMonitor = new RectInt32(1920, 0, 2560, 1400);
        var config = new WidgetConfig
        {
            BoundsCoordinateVersion = WidgetConfig.CurrentBoundsCoordinateVersion,
            Width = 300,
            Height = 200,
            PositionAnchor = WidgetPositionAnchors.LeftTop,
            PositionMarginX = 10,
            PositionMarginY = 20,
            PositionMonitorKey = WidgetPositioningService.CreateMonitorKey(primaryMonitor),
            PositionMonitorDeviceName = @"\\.\DISPLAY2",
            PositionMonitorWasPrimary = false
        };

        var bounds = ResolveWithMonitors(
            config,
            primaryMonitor,
            [
                (primaryMonitor, @"\\.\DISPLAY1"),
                (externalMonitor, @"\\.\DISPLAY2")
            ],
            1.0);

        Assert.Equal(1930, bounds.X);
        Assert.Equal(20, bounds.Y);
        Assert.Equal(300, bounds.Width);
        Assert.Equal(200, bounds.Height);
    }

    [Fact]
    public void UpdateConfigFromPhysicalBounds_StoresLogicalSizeForCurrentDpi()
    {
        var workArea = new RectInt32(0, 0, 2560, 1400);
        var config = new WidgetConfig();
        var physicalBounds = new RectInt32(180, 120, 450, 300);

        WidgetPositioningService.UpdateConfigFromPhysicalBoundsForTest(
            config,
            physicalBounds,
            workArea,
            _ => 1.5);

        Assert.Equal(WidgetConfig.CurrentBoundsCoordinateVersion, config.BoundsCoordinateVersion);
        Assert.Equal(180, config.X);
        Assert.Equal(120, config.Y);
        Assert.Equal(300d, config.Width, precision: 3);
        Assert.Equal(200d, config.Height, precision: 3);
    }

    [Fact]
    public void ResolveBounds_MixedDpiMonitorRoundTrip_PreservesLogicalSizeAndVisibility()
    {
        var primary100 = new RectInt32(0, 0, 1920, 1040);
        var secondary150 = new RectInt32(-2560, 0, 2560, 1400);
        var config = new WidgetConfig
        {
            BoundsCoordinateVersion = WidgetConfig.CurrentBoundsCoordinateVersion,
            Width = 300,
            Height = 200,
            PositionAnchor = WidgetPositionAnchors.RightBottom,
            PositionMarginX = 20,
            PositionMarginY = 24,
            PositionMonitorDeviceName = @"\\.\DISPLAY2",
            PositionMonitorWasPrimary = false
        };
        var monitors = new[]
        {
            (primary100, (string?)@"\\.\DISPLAY1"),
            (secondary150, (string?)@"\\.\DISPLAY2")
        };

        RectInt32 highDpiBounds = WidgetPositioningService.ResolveBoundsForTest(
            config,
            primary100,
            monitors,
            area => area.X < 0 ? 1.5 : 1.0);

        Assert.Equal(450, highDpiBounds.Width);
        Assert.Equal(300, highDpiBounds.Height);
        Assert.Equal(-480, highDpiBounds.X);
        Assert.Equal(1064, highDpiBounds.Y);

        RectInt32 fallbackBounds = WidgetPositioningService.ResolveBoundsForTest(
            config,
            primary100,
            [(primary100, (string?)@"\\.\DISPLAY1")],
            _ => 1.0);

        Assert.Equal(300, fallbackBounds.Width);
        Assert.Equal(200, fallbackBounds.Height);
        Assert.InRange(fallbackBounds.X, primary100.X, primary100.X + primary100.Width - fallbackBounds.Width);
        Assert.InRange(fallbackBounds.Y, primary100.Y, primary100.Y + primary100.Height - fallbackBounds.Height);
    }

    /// <summary>
    /// The wild-offscreen gate of EnsureVisible must measure the window's
    /// work-area overlap, not the window's near-edge coordinate. The
    /// previous edge form treated any window shorter than 48px docked to an
    /// edge as fully offscreen — a 41px-tall expanded strip glued to the
    /// bottom jumped to the (32,32) fallback spot on every tray-reveal
    /// topology re-resolve (2026-10-20).
    /// </summary>
    [Fact]
    public void EnsureVisible_ShortStripDockedToBottomEdge_StaysPut()
    {
        var workArea = new RectInt32(0, 0, 1920, 1032);

        // The exact repro geometry: top edge at 984, 41px tall, fully inside
        // the work area's last 48px band.
        RectInt32 kept = WidgetPositioningService.EnsureVisible(
            new RectInt32(1320, 984, 576, 41), workArea);

        Assert.Equal(1320, kept.X);
        Assert.Equal(984, kept.Y);
    }

    [Fact]
    public void EnsureVisible_NarrowStripDockedToRightEdge_StaysPut()
    {
        var workArea = new RectInt32(0, 0, 1920, 1032);

        RectInt32 kept = WidgetPositioningService.EnsureVisible(
            new RectInt32(1879, 100, 41, 300), workArea);

        Assert.Equal(1879, kept.X);
        Assert.Equal(100, kept.Y);
    }

    [Fact]
    public void EnsureVisible_FullyOutside_FallsBackToWorkAreaOffset()
    {
        var workArea = new RectInt32(0, 0, 1920, 1032);

        // Fully below the work area (e.g. parked by a slide-out animation).
        RectInt32 below = WidgetPositioningService.EnsureVisible(
            new RectInt32(1320, 1100, 576, 41), workArea);
        Assert.Equal(32, below.X);
        Assert.Equal(32, below.Y);

        // Fully above it.
        RectInt32 above = WidgetPositioningService.EnsureVisible(
            new RectInt32(1320, -60, 576, 41), workArea);
        Assert.Equal(32, above.X);
        Assert.Equal(32, above.Y);

        // Fully to the right of it.
        RectInt32 right = WidgetPositioningService.EnsureVisible(
            new RectInt32(2000, 100, 576, 41), workArea);
        Assert.Equal(32, right.X);
        Assert.Equal(32, right.Y);
    }

    [Fact]
    public void EnsureVisible_BarelyPeekingSliver_FallsBack()
    {
        var workArea = new RectInt32(0, 0, 1920, 1032);

        // A 300px-tall window with only 20px still inside the bottom edge:
        // the overlap is a minority sliver, not the window's whole body.
        RectInt32 sliver = WidgetPositioningService.EnsureVisible(
            new RectInt32(1320, 1012, 576, 300), workArea);
        Assert.Equal(32, sliver.X);
        Assert.Equal(32, sliver.Y);

        // Same for a wide window peeking 20px from the right edge.
        RectInt32 sideSliver = WidgetPositioningService.EnsureVisible(
            new RectInt32(1900, 100, 600, 300), workArea);
        Assert.Equal(32, sideSliver.X);
        Assert.Equal(32, sideSliver.Y);
    }

    [Fact]
    public void EnsureVisible_TallWindowStraddlingBottom_ClampsNotFallsBack()
    {
        var workArea = new RectInt32(0, 0, 1920, 1032);

        // A normal window overlapping the bottom band but mostly inside
        // must take the gentle clamp path, never the fallback.
        RectInt32 clamped = WidgetPositioningService.EnsureVisible(
            new RectInt32(1320, 900, 576, 200), workArea);

        Assert.Equal(1320, clamped.X);
        Assert.Equal(832, clamped.Y); // 1032 - 200
    }
}
