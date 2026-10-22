using DeskBox.Platform;
using Microsoft.UI.Dispatching;

namespace DeskBox.Services;

public sealed partial class WidgetManager
{
    private DispatcherQueueTimer? _desktopPinnedLayerTimer;

    private void UpdateDesktopPinnedLayerMonitor()
    {
        StopDesktopPinnedLayerMonitor();
        if (!WidgetLayerService.UsesDesktopPinnedMode() || App.UiDispatcherQueue is null)
        {
            return;
        }

        _desktopPinnedLayerTimer = App.UiDispatcherQueue.CreateTimer();
        _desktopPinnedLayerTimer.Interval = TimeSpan.FromSeconds(1);
        _desktopPinnedLayerTimer.IsRepeating = true;
        _desktopPinnedLayerTimer.Tick += DesktopPinnedLayerTimer_Tick;
        _desktopPinnedLayerTimer.Start();
    }

    private void StopDesktopPinnedLayerMonitor()
    {
        if (_desktopPinnedLayerTimer is null) return;
        _desktopPinnedLayerTimer.Stop();
        _desktopPinnedLayerTimer.Tick -= DesktopPinnedLayerTimer_Tick;
        _desktopPinnedLayerTimer = null;
    }

    private void DesktopPinnedLayerTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        if (!WidgetLayerService.UsesDesktopPinnedMode() ||
            WidgetLayerService.IsSessionRecoverySuspended ||
            _isTogglingWidgetsDesktopLayer || _sessionManager.IsInteractionActive ||
            !Win32Helper.IsInteractiveDesktopAvailable())
        {
            return;
        }

        try
        {
            var windows = GetLoadedDesktopWindows().Where(window => window.Visible).ToList();
            if (windows.Any(window => window.IsBoundsTransitionActive)) return;
            bool repaired = WidgetLayerService.RepairDesktopPinnedWindowOrder(
                windows.Select(window => window.WindowHandle).ToList());
            if (repaired)
            {
                App.Log("[ZOrder] Repaired desktop-pinned widgets behind application windows.");
            }
        }
        catch (Exception ex)
        {
            App.Log($"[ZOrder] Desktop-pinned layer verification failed: {ex}");
        }
    }
}
