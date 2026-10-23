
using DeskBox.Platform;
namespace DeskBox.Helpers;

/// <summary>
/// Keeps a native modal dialog that was explicitly opened from a desktop-layer
/// widget visible without changing the widget's normal resting Z-order.
/// </summary>
internal static class ShellUiForegroundMonitor
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(40);
    private static readonly TimeSpan DiscoveryWindow = TimeSpan.FromSeconds(10);

    public static IDisposable Start(IntPtr ownerHwnd)
    {
        if (ownerHwnd == IntPtr.Zero || !Win32Helper.IsWindow(ownerHwnd))
        {
            return EmptyScope.Instance;
        }

        var cancellation = new CancellationTokenSource();
        Task monitorTask = Task.Run(() => MonitorOwnedDialogAsync(
            ownerHwnd,
            cancellation.Token));
        return new MonitorScope(cancellation, monitorTask);
    }

    private static async Task MonitorOwnedDialogAsync(
        IntPtr ownerHwnd,
        CancellationToken cancellationToken)
    {
        long deadline = Environment.TickCount64 +
            (long)DiscoveryWindow.TotalMilliseconds;

        try
        {
            while (!cancellationToken.IsCancellationRequested &&
                   Environment.TickCount64 < deadline)
            {
                IReadOnlyList<IntPtr> dialogs =
                    Win32Helper.FindVisibleDialogWindowsForCurrentProcess(
                        excludeHwnd: ownerHwnd,
                        requiredOwnerHwnd: ownerHwnd);
                if (dialogs.Count > 0)
                {
                    IntPtr dialogHwnd = dialogs[0];
                    // Discovery stays on this background thread (the UI thread
                    // is blocked inside the native resolve modal — that is why
                    // this monitor exists); only the promotion hops to the UI
                    // thread, so the window operations run on the owning
                    // thread instead of injecting cross-thread synchronous
                    // sends into it at an arbitrary moment (drag-freeze
                    // family). The landing re-validates liveness and process
                    // ownership because the dialog may already be gone.
                    uint ownerProcessId = (uint)Environment.ProcessId;
                    UiDispatch.PostAfterCallback(() =>
                    {
                        if (!Win32Helper.IsWindow(dialogHwnd) ||
                            !Win32Helper.IsWindowVisible(dialogHwnd))
                        {
                            return;
                        }

                        Win32Helper.GetWindowThreadProcessId(
                            dialogHwnd,
                            out uint dialogProcessId);
                        if (dialogProcessId != ownerProcessId)
                        {
                            return;
                        }

                        Win32Helper.SetWindowTopMost(dialogHwnd);
                        bool foreground = Win32Helper.SetForegroundWindow(dialogHwnd);
                        App.Log(
                            $"[ShellActivation] Promoted owned native dialog " +
                            $"dialog=0x{dialogHwnd.ToInt64():X} " +
                            $"topMost={Win32Helper.IsWindowTopMost(dialogHwnd)} " +
                            $"foreground={foreground}");
                    });
                    return;
                }

                await Task.Delay(PollInterval, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            App.Log(
                $"[ShellActivation] Owned dialog monitor failed " +
                $"owner=0x{ownerHwnd.ToInt64():X}: {ex.Message}");
        }
    }

    private sealed class MonitorScope(
        CancellationTokenSource cancellation,
        Task monitorTask) : IDisposable
    {
        private CancellationTokenSource? _cancellation = cancellation;
        private Task? _monitorTask = monitorTask;

        public void Dispose()
        {
            CancellationTokenSource? cancellationSource =
                Interlocked.Exchange(ref _cancellation, null);
            Task? task = Interlocked.Exchange(ref _monitorTask, null);
            if (cancellationSource is null || task is null)
            {
                return;
            }

            cancellationSource.Cancel();
            _ = DisposeCancellationAfterMonitorAsync(task, cancellationSource);
        }

        private static async Task DisposeCancellationAfterMonitorAsync(
            Task monitorTask,
            CancellationTokenSource cancellationSource)
        {
            try
            {
                await monitorTask.ConfigureAwait(false);
            }
            finally
            {
                cancellationSource.Dispose();
            }
        }
    }

    private sealed class EmptyScope : IDisposable
    {
        public static EmptyScope Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
