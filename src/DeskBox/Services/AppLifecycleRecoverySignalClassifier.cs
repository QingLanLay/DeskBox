using System.Runtime.InteropServices;
using DeskBox.Platform;

namespace DeskBox.Services;

/// <summary>
/// Converts native lifecycle messages into stable recovery reasons. Keeping
/// this mapping independent from the Win32 window subclass makes the sleep,
/// display/DPI, session, and Explorer-restart recovery contract testable.
/// </summary>
internal static class AppLifecycleRecoverySignalClassifier
{
    internal const uint WmPowerBroadcast = 0x0218;
    internal const uint WmWtsSessionChange = 0x02B1;
    internal const uint WmDisplayChange = 0x007E;
    internal const uint WmDpiChanged = 0x02E0;

    private const uint PbtResumeAutomatic = 0x0012;
    private const uint PbtResumeSuspend = 0x0007;
    private const uint PbtResumeCritical = 0x0006;
    private const uint WtsSessionUnlock = 0x0008;
    private const uint WtsSessionLogon = 0x0005;
    private const uint WtsSessionConsoleConnect = 0x0001;
    private const uint WtsSessionRemoteConnect = 0x0003;
    private const uint WtsSessionDesktopReady = 0x000F;

    internal static string? ResolveRecoveryReason(
        uint message,
        UIntPtr wParam,
        uint taskbarCreatedMessage) =>
        ResolveRecoveryReason(message, wParam, IntPtr.Zero, taskbarCreatedMessage);

    internal static string? ResolveRecoveryReason(
        uint message,
        UIntPtr wParam,
        IntPtr lParam,
        uint taskbarCreatedMessage)
    {
        uint eventValue = unchecked((uint)wParam.ToUInt64());
        if (message == WmPowerBroadcast &&
            eventValue is PbtResumeAutomatic or PbtResumeSuspend or PbtResumeCritical)
        {
            return "resume";
        }

        // Idle background apps get their working sets trimmed and hooks
        // starved while the display is off; "display on" is the earliest
        // reliable signal that the user is back and input should work again.
        if (message == WmPowerBroadcast &&
            eventValue == Win32Helper.PbtPowerSettingChange &&
            IsConsoleDisplayOn(lParam))
        {
            return "display-power-on";
        }

        if (message == WmWtsSessionChange &&
            eventValue is WtsSessionUnlock or WtsSessionLogon or WtsSessionConsoleConnect or
                WtsSessionRemoteConnect or WtsSessionDesktopReady)
        {
            return eventValue == WtsSessionUnlock
                ? "session-unlock"
                : "session-reconnect";
        }

        if (message is WmDisplayChange or WmDpiChanged)
        {
            return "display-message";
        }

        return message == taskbarCreatedMessage
            ? "explorer-restart"
            : null;
    }

    private static bool IsConsoleDisplayOn(IntPtr lParam)
    {
        if (lParam == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            var setting = Marshal.PtrToStructure<Win32Helper.PowerBroadcastSetting>(lParam);
            return setting.PowerSetting == Win32Helper.ConsoleDisplayStatePowerSetting &&
                   setting.DataLength >= 1 &&
                   setting.Data == 1;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
