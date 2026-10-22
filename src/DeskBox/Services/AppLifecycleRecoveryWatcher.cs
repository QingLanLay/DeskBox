using DeskBox.Helpers;
using DeskBox.Platform;
using Microsoft.UI.Dispatching;

namespace DeskBox.Services;

/// <summary>
/// Listens for application-wide lifecycle signals that are otherwise easy to
/// miss when individual widgets are hidden or Explorer is restarted.
/// </summary>
internal sealed class AppLifecycleRecoveryWatcher : IDisposable
{
    private const uint WmQueryEndSession = 0x0011;
    private const uint WmEndSession = 0x0016;
    private const uint WmNcDestroy = 0x0082;
    private const uint NotifyForThisSession = 0;
    private static readonly uint s_taskbarCreatedMessage = Win32Helper.RegisterWindowMessage("TaskbarCreated");
    private static readonly UIntPtr SubclassId = new(0xDBA7);
    private static readonly TimeSpan RecoveryDelay = TimeSpan.FromMilliseconds(420);

    private readonly IntPtr _hWnd;
    private readonly DispatcherQueueTimer _timer;
    private readonly Action<string> _recoveryAction;
    private readonly Action<string>? _endSessionAction;
    private readonly Action<bool>? _sessionStateChangedAction;
    private readonly AppLifecycleRecoverySessionState _sessionState = new();
    private readonly Win32Helper.SubclassProc _subclassProc;
    private bool _isDisposed;
    private bool _isSubclassInstalled;
    private bool _sessionNotificationRegistered;
    private IntPtr _powerNotifyHandle;
    private string _pendingReasons = string.Empty;

    internal bool IsSessionAvailable => _sessionState.IsSessionAvailable;
    internal long SessionGeneration { get; private set; }

    public AppLifecycleRecoveryWatcher(
        IntPtr hWnd,
        DispatcherQueue dispatcherQueue,
        Action<string> recoveryAction,
        Action<string>? endSessionAction = null,
        Action<bool>? sessionStateChangedAction = null)
    {
        _hWnd = hWnd;
        _recoveryAction = recoveryAction;
        _endSessionAction = endSessionAction;
        _sessionStateChangedAction = sessionStateChangedAction;
        _subclassProc = WindowSubclassProc;
        _timer = dispatcherQueue.CreateTimer();
        _timer.Interval = RecoveryDelay;
        _timer.IsRepeating = false;
        _timer.Tick += RecoveryTimer_Tick;

        if (_hWnd == IntPtr.Zero)
        {
            return;
        }

        _isSubclassInstalled = Win32Helper.SetWindowSubclass(
            _hWnd,
            _subclassProc,
            SubclassId,
            UIntPtr.Zero);

        try
        {
            _sessionNotificationRegistered = Win32Helper.WTSRegisterSessionNotification(
                _hWnd,
                NotifyForThisSession);
        }
        catch (DllNotFoundException)
        {
            App.Log("[Lifecycle] Session notifications are unavailable on this system.");
        }
        catch (EntryPointNotFoundException)
        {
            App.Log("[Lifecycle] Session notification API is unavailable on this system.");
        }
        catch (Exception ex)
        {
            App.Log($"[Lifecycle] Session notification registration failed: {ex.Message}");
        }

        try
        {
            Guid displayStateGuid = Win32Helper.ConsoleDisplayStatePowerSetting;
            _powerNotifyHandle = Win32Helper.RegisterPowerSettingNotification(
                _hWnd,
                ref displayStateGuid,
                Win32Helper.DeviceNotifyWindowHandle);
        }
        catch (Exception ex)
        {
            App.Log($"[Lifecycle] Power setting notification registration failed: {ex.Message}");
        }

        App.Log(
            $"[Lifecycle] Recovery watcher attached hwnd=0x{_hWnd.ToInt64():X} " +
            $"subclass={_isSubclassInstalled} session={_sessionNotificationRegistered}");
    }

    private IntPtr WindowSubclassProc(
        IntPtr hWnd,
        uint message,
        UIntPtr wParam,
        IntPtr lParam,
        UIntPtr subclassId,
        UIntPtr refData)
    {
        bool sessionWasAvailable = _sessionState.IsSessionAvailable;
        string? recoveryReason =
            _sessionState.Observe(
                message,
                wParam,
                lParam,
                s_taskbarCreatedMessage);
        if (sessionWasAvailable != _sessionState.IsSessionAvailable)
        {
            SessionGeneration++;
            _timer.Stop();
            _pendingReasons = string.Empty;
            NotifySessionAvailability(_sessionState.IsSessionAvailable);
            App.Log($"[Lifecycle] Session available={_sessionState.IsSessionAvailable}; pending recovery reset.");
        }

        if (recoveryReason is not null)
        {
            QueueRecovery(recoveryReason);
        }
        else if (message == WmQueryEndSession)
        {
            InvokeEndSessionAction("query-end-session");
            return new IntPtr(1);
        }
        else if (message == WmEndSession)
        {
            if (wParam != UIntPtr.Zero)
            {
                InvokeEndSessionAction("end-session");
            }
        }
        else if (message == WmNcDestroy)
        {
            Dispose();
        }

        return Win32Helper.DefSubclassProc(hWnd, message, wParam, lParam);
    }

    private void InvokeEndSessionAction(string reason)
    {
        try
        {
            _endSessionAction?.Invoke(reason);
        }
        catch (Exception ex)
        {
            App.Log($"[Lifecycle] End-session callback failed: {ex.Message}");
        }
    }

    private void NotifySessionAvailability(bool available)
    {
        try { _sessionStateChangedAction?.Invoke(available); }
        catch (Exception ex) { App.Log($"[Lifecycle] Session availability callback failed: {ex}"); }
    }

    private void QueueRecovery(string reason)
    {
        if (_isDisposed || !_sessionState.IsSessionAvailable)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(_pendingReasons))
        {
            _pendingReasons = reason;
        }
        else if (!_pendingReasons.Contains(reason, StringComparison.Ordinal))
        {
            _pendingReasons += "," + reason;
        }

        _timer.Stop();
        _timer.Interval = RecoveryDelay;
        _timer.Start();
    }

    private void RecoveryTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        _timer.Stop();
        if (_isDisposed || !_sessionState.IsSessionAvailable || string.IsNullOrWhiteSpace(_pendingReasons))
        {
            return;
        }

        if (!Win32Helper.IsInteractiveDesktopAvailable())
        {
            // Display-on/unlock can precede the switch away from Winlogon.
            // Keep the reasons pending until the user's desktop takes input.
            _timer.Start();
            return;
        }

        string reasons = _pendingReasons;
        _pendingReasons = string.Empty;
        try
        {
            _recoveryAction(reasons);
        }
        catch (Exception ex)
        {
            App.Log($"[Lifecycle] Recovery callback failed: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _timer.Stop();
        _timer.Tick -= RecoveryTimer_Tick;

        if (_powerNotifyHandle != IntPtr.Zero)
        {
            try
            {
                Win32Helper.UnregisterPowerSettingNotification(_powerNotifyHandle);
            }
            catch
            {
                // Best effort during teardown; the window is going away.
            }

            _powerNotifyHandle = IntPtr.Zero;
        }

        if (_sessionNotificationRegistered)
        {
            try
            {
                Win32Helper.WTSUnRegisterSessionNotification(_hWnd);
            }
            catch
            {
                // Best effort during teardown; the window is going away.
            }

            _sessionNotificationRegistered = false;
        }

        if (_isSubclassInstalled)
        {
            Win32Helper.RemoveWindowSubclass(_hWnd, _subclassProc, SubclassId);
            _isSubclassInstalled = false;
        }
    }
}
