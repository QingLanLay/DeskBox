namespace DeskBox.Services;

/// <summary>
/// Display-on and resume messages can arrive before the lock screen is left.
/// Keep shell recovery suspended until this session is interactive again.
/// </summary>
internal sealed class AppLifecycleRecoverySessionState
{
    private bool _isConnected = true;
    private bool _isLocked;
    public bool IsSessionAvailable => _isConnected && !_isLocked;

    public string? Observe(
        uint message, UIntPtr wParam, IntPtr lParam, uint taskbarCreatedMessage)
    {
        if (message == AppLifecycleRecoverySignalClassifier.WmWtsSessionChange)
        {
            uint sessionEvent = unchecked((uint)wParam.ToUInt64());
            switch (sessionEvent)
            {
                case 0x0002 or 0x0004 or 0x0006: // disconnect, logoff
                    _isConnected = false;
                    break;
                case 0x0007: // lock
                    _isLocked = true;
                    break;
                case 0x0001 or 0x0003: // connecting does not unlock the session
                    _isConnected = true;
                    break;
                case 0x0005 or 0x0008 or 0x000F: // logon, unlock, user desktop ready
                    _isConnected = true;
                    _isLocked = false;
                    break;
            }
        }

        if (!IsSessionAvailable)
        {
            return null;
        }

        string? reason = AppLifecycleRecoverySignalClassifier.ResolveRecoveryReason(
            message, wParam, lParam, taskbarCreatedMessage);
        return reason == "display-power-off" ? null : reason;
    }
}

internal static class DesktopLayerRecoveryPolicy
{
    public static bool RequiresStableHost(string reasons) =>
        reasons.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(reason => reason.StartsWith("lifecycle-", StringComparison.Ordinal)
                ? reason["lifecycle-".Length..] : reason)
            .Any(reason => reason is
                "session-unlock" or "session-reconnect" or
                "resume" or "explorer-restart" or
                "display-power-on" or "widget-window-message");
}
