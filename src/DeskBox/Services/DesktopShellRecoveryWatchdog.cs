namespace DeskBox.Services;

/// <summary>
/// Continuously verifies the native desktop, independently of the XAML thread
/// and of session messages. A successful API request never ends verification.
/// </summary>
internal sealed class DesktopShellRecoveryWatchdog : IDisposable
{
    internal static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);
    private readonly Func<bool> _interactive;
    private readonly Func<DesktopShellHealthSnapshot> _capture;
    private readonly Action<DesktopShellHealthSnapshot, bool> _repair;
    private readonly Action<bool> _suspendLayers;
    private readonly Action _desktopReady;
    private readonly Action _restartRequested;
    private readonly Func<long> _clock;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task? _loop;
    private int _sessionAvailable = 1;
    private long _sessionHintChangedAt;
    private int _verificationRequested;
    private int _isReady;
    private bool _sawSecureDesktop;
    private bool _suspended;
    private bool _needsRebind;
    private volatile bool _disposed;
    private DesktopShellHealthSnapshot _previous;
    private int _stableSamples;
    private long? _faultSince;
    private long? _lastRepair;
    private bool _lastRepairWasPlacement;
    private long? _lastRestartRequest;

    internal bool IsReady => Volatile.Read(ref _isReady) != 0 && Volatile.Read(ref _sessionAvailable) != 0;

    internal DesktopShellRecoveryWatchdog(
        Func<bool> interactive,
        Func<DesktopShellHealthSnapshot> capture,
        Action<DesktopShellHealthSnapshot, bool> repair,
        Action<bool> suspendLayers,
        Action desktopReady,
        Action restartRequested,
        Func<long>? clock = null,
        bool startLoop = true)
    {
        _interactive = interactive;
        _capture = capture;
        _repair = repair;
        _suspendLayers = suspendLayers;
        _desktopReady = desktopReady;
        _restartRequested = restartRequested;
        _clock = clock ?? (() => Environment.TickCount64);
        if (startLoop) _loop = Task.Run(LoopAsync);
    }

    internal void SetSessionAvailable(bool available)
    {
        Interlocked.Exchange(ref _sessionHintChangedAt, _clock());
        Volatile.Write(ref _sessionAvailable, available ? 1 : 0);
        RequestVerification();
    }

    internal void RequestVerification() => Interlocked.Exchange(ref _verificationRequested, 1);

    internal void RunCycleOnce()
    {
        if (_disposed) return;
        if (Interlocked.Exchange(ref _verificationRequested, 0) != 0)
        {
            Suspend();
        }

        if (!_interactive())
        {
            _sawSecureDesktop = true;
            Suspend();
            _faultSince = null;
            return;
        }

        // If WTS missed unlock, observing the actual input desktop return is
        // sufficient. An old "locked" flag must never disable recovery forever.
        if (_sawSecureDesktop)
        {
            _sawSecureDesktop = false;
            Volatile.Write(ref _sessionAvailable, 1);
        }
        if (Volatile.Read(ref _sessionAvailable) == 0)
        {
            if (_clock() - Interlocked.Read(ref _sessionHintChangedAt) < 2_000)
            {
                Suspend();
                return;
            }
            // The process may have been suspended throughout the secure
            // desktop interval. After a short transition grace period the
            // actual readable input desktop overrides a stale WTS flag.
            Volatile.Write(ref _sessionAvailable, 1);
        }

        DesktopShellHealthSnapshot snapshot = _capture();
        long now = _clock();
        if (!snapshot.IsHealthy)
        {
            Suspend();
            if (!snapshot.Exists)
            {
                // Explorer can be restarting. There is no confirmed broken
                // window to repair or reason to restart DeskBox yet.
                _faultSince = null;
                _lastRepair = null;
                return;
            }
            if (_previous.Host != snapshot.Host || _previous.IconView != snapshot.IconView)
            {
                _faultSince = null;
                _lastRepair = null;
            }
            _previous = snapshot;
            _faultSince ??= now;
            bool forcePlacement = now - _faultSince.Value >= 2_000;
            long retryInterval = forcePlacement ? 2_000 : 500;
            if (_lastRepair is null || now - _lastRepair.Value >= retryInterval ||
                forcePlacement && !_lastRepairWasPlacement)
            {
                _lastRepair = now;
                _lastRepairWasPlacement = forcePlacement;
                _repair(snapshot, forcePlacement);
            }
            if (now - _faultSince.Value >= 15_000 &&
                (_lastRestartRequest is null || now - _lastRestartRequest.Value >= 30_000))
            {
                _lastRestartRequest = now;
                _restartRequested();
            }
            return;
        }

        bool sameHost = _previous.Host == snapshot.Host && _previous.IconView == snapshot.IconView &&
            _previous.Bounds.Equals(snapshot.Bounds);
        if (_previous.Exists && !sameHost) Suspend();
        _stableSamples = sameHost ? _stableSamples + 1 : 1;
        _previous = snapshot;
        if (_stableSamples < 3) return;
        // A one-frame restore is not recovery. Keep the escalation deadline
        // until the desktop actually passes the whole stable sequence.
        _faultSince = null;
        _lastRepair = null;
        Volatile.Write(ref _isReady, 1);
        if (_suspended)
        {
            _suspended = false;
            _suspendLayers(false);
        }
        if (_needsRebind)
        {
            _needsRebind = false;
            _desktopReady();
        }
        // Keep polling: a delayed minimize, missed event, or later Explorer
        // replacement must be repaired even hours after the original unlock.
    }

    private void Suspend()
    {
        Volatile.Write(ref _isReady, 0);
        _stableSamples = 0;
        _needsRebind = true;
        if (_suspended) return;
        _suspended = true;
        _suspendLayers(true);
    }

    private async Task LoopAsync()
    {
        CancellationToken token = _stop.Token;
        using var timer = new PeriodicTimer(PollInterval);
        try
        {
            do
            {
                try { RunCycleOnce(); }
                catch (Exception ex)
                {
                    Suspend();
                    App.Log($"[DesktopRecovery] Watchdog cycle failed: {ex}");
                }
            }
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false));
        }
        catch (OperationCanceledException) { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Volatile.Write(ref _isReady, 0);
        _stop.Cancel();
        if (_loop is null) _stop.Dispose();
        else _ = _loop.ContinueWith(_ => _stop.Dispose(), TaskScheduler.Default);
    }
}
