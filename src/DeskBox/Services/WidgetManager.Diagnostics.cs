using DeskBox.Controls.WidgetContents;
using DeskBox.Platform;
using Microsoft.UI.Dispatching;

namespace DeskBox.Services;

public sealed partial class WidgetManager
{
    private bool _desktopPinnedClickTracePending;
    private long _lastDesktopPinnedPointerTick = Environment.TickCount64;

    internal void TraceDesktopPinnedInteraction(
        DispatcherQueue dispatcherQueue,
        IntPtr windowHandle,
        string reason,
        bool pointerInput = false)
    {
        if (!App.IsVerboseLoggingEnabled || !WidgetLayerService.UsesDesktopPinnedMode())
        {
            return;
        }

        long now = Environment.TickCount64;
        long pointerGapMilliseconds = now - _lastDesktopPinnedPointerTick;
        if (pointerInput)
        {
            _lastDesktopPinnedPointerTick = now;
        }

        if (_desktopPinnedClickTracePending)
        {
            return;
        }

        _desktopPinnedClickTracePending = true;
        try
        {
            TraceDesktopPinnedNativeWindows($"before-{reason}", windowHandle, pointerGapMilliseconds);
        }
        catch (Exception ex)
        {
            App.LogVerbose($"[DesktopClick] Initial native state trace failed: {ex.Message}");
        }
        // Observe the complete group after native/XAML input and delayed layer
        // restores settle. Diagnostics never show, activate, or reorder HWNDs.
        bool queued = dispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                TraceDesktopPinnedNativeWindows($"dispatched-{reason}", windowHandle, pointerGapMilliseconds);
                await Task.Delay(80);
                TraceDesktopPinnedNativeWindows($"80ms-{reason}", windowHandle, pointerGapMilliseconds);
                await Task.Delay(220);
                TraceDesktopPinnedNativeWindows($"300ms-{reason}", windowHandle, pointerGapMilliseconds);
            }
            catch (Exception ex)
            {
                App.LogVerbose($"[DesktopClick] Native state trace failed: {ex.Message}");
            }
            finally
            {
                _desktopPinnedClickTracePending = false;
            }
        });
        if (!queued)
        {
            _desktopPinnedClickTracePending = false;
        }
    }

    private void TraceDesktopPinnedNativeWindows(string phase, IntPtr inputWindow, long pointerGapMilliseconds)
    {
        IReadOnlyList<IDesktopWidgetWindow> windows = GetLoadedDesktopWindows();
        App.LogVerbose(
            $"[DesktopClick] phase={phase} input=0x{inputWindow.ToInt64():X} " +
            $"pointerGapMs={pointerGapMilliseconds} foreground=0x{Win32Helper.GetForegroundWindow().ToInt64():X} " +
            $"state={SessionState} suspended={WidgetLayerService.IsSessionRecoverySuspended} " +
            $"hostGeneration={WidgetLayerService.DesktopHostGeneration} windows={windows.Count}");
        foreach (IDesktopWidgetWindow window in windows)
        {
            App.LogVerbose(
                $"[DesktopClick] phase={phase} surface={window.Identity.ShortSurfaceId} " +
                $"logicalVisible={window.Visible} raised={window.IsRaisedAboveDesktopLayer} " +
                $"contentOpacity={window.WindowContentRoot?.Opacity} contentVisibility={window.WindowContentRoot?.Visibility} " +
                $"{WidgetLayerService.DescribeNativeDesktopLayer(window.WindowHandle)}");
        }
    }

    internal IReadOnlyList<FolderWatcherHealthSnapshot> GetFolderWatcherHealthSnapshots()
    {
        return _fileWidgets.Values
            .Select(entry => entry.ViewModel)
            .Concat(_contentWidgets.Values
                .Select(window => window.CurrentContent)
                .OfType<FileWidgetContentAdapter>()
                .Select(content => content.ViewModel))
            .Distinct()
            .SelectMany(viewModel => new[]
            {
                viewModel.FolderWatcherHealth,
                viewModel.PublicFolderWatcherHealth
            })
            .ToArray();
    }

    internal DeskBoxWidgetManagerDiagnostic CreateDiagnosticsSnapshot()
    {
        IReadOnlyList<IDesktopWidgetWindow> windows = GetLoadedDesktopWindows();
        TrayToggleQueueSnapshot queue = _trayToggleRequestQueue.GetSnapshot();
        int loadedGroupedFileCount = _contentWidgets.Values
            .DistinctBy(window => window.WindowHandle)
            .Count(window =>
                window.Identity.IsGroupSurface &&
                window.CurrentContent is FileWidgetContentAdapter);
        DeskBoxFileHostDiagnostic fileHosts = _fileWidgetHostDiagnostics.CreateSnapshot(
            _fileWidgets.Values
                .DistinctBy(session => session.Host.WindowHandle)
                .Count(),
            loadedGroupedFileCount);
        DeskBoxWidgetHostDiagnostic[] hosts = windows
            .OrderBy(window => window.Identity.SurfaceId, StringComparer.Ordinal)
            .Select((window, index) =>
            {
                FileWidgetContentAdapter? fileSurface = _contentWidgets.Values
                    .FirstOrDefault(contentWindow =>
                        contentWindow.WindowHandle == window.WindowHandle)
                    ?.CurrentContent as FileWidgetContentAdapter;
                return new DeskBoxWidgetHostDiagnostic(
                    index + 1,
                    window.Identity.WidgetKind,
                    window.Identity.LogKind,
                    window.Identity.IsGroupSurface,
                    window.Visible,
                    window.IsRaisedAboveDesktopLayer,
                    window.IsCompactArrangementActive,
                    fileSurface?.IsImportBusy == true,
                    fileSurface?.ImportBusyElapsedMilliseconds,
                    ToDiagnosticRect(window.AnimationBounds),
                    ToDiagnosticRect(window.RestingAnimationBounds));
            })
            .ToArray();

        return new DeskBoxWidgetManagerDiagnostic(
            WidgetsRaisedFromTray,
            SessionState.ToString(),
            IsWidgetInteractionActive,
            LoadedSurfaceCount,
            windows.Count(window => window.Visible),
            fileHosts,
            new DeskBoxTrayQueueDiagnostic(
                queue.PendingCount,
                queue.WorkerRunning,
                queue.TotalRequests,
                queue.EffectiveToggles,
                queue.FoldedNoOpBatches,
                queue.LastSource,
                !string.IsNullOrWhiteSpace(queue.LastError)),
            hosts);
    }

    private static DeskBoxDiagnosticRect ToDiagnosticRect(Windows.Foundation.Rect bounds)
    {
        return new DeskBoxDiagnosticRect(
            bounds.X,
            bounds.Y,
            bounds.Width,
            bounds.Height);
    }
}
