using System.Runtime.InteropServices;
using DeskBox.Platform;
using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class LifecycleRecoveryGateIntegrationTests
{
    [Theory]
    [InlineData(0x0001, true, "session-unlocked")]
    [InlineData(0x0001, false, "session-locked")]
    [InlineData(0x0003, true, "session-unlocked")]
    [InlineData(0x0003, false, "session-locked")]
    [InlineData(0x0007, false, "session-locked")]
    [InlineData(0x0008, true, "session-unlocked")]
    [InlineData(0x000F, true, "session-unlocked")]
    [InlineData(0x0009, true, null)]
    [InlineData(0x000A, true, null)]
    public void SessionGate_UsesObservedAvailabilityAndCorrectWtsCodes(
        uint sessionEvent, bool available, string? expected)
    {
        Assert.Equal(expected, AppLifecycleRecoverySignalClassifier.ResolveGateEvent(
            AppLifecycleRecoverySignalClassifier.WmWtsSessionChange,
            new UIntPtr(sessionEvent), IntPtr.Zero, available));
    }

    [Theory]
    [InlineData(false, 0, null, "display-power-off")]
    [InlineData(false, 1, null, "display-power-on")]
    [InlineData(true, 0, null, "display-power-off")]
    [InlineData(true, 1, "display-power-on", "display-power-on")]
    public void DisplayGate_IsObservedWhileLockedWithoutRequestingRecovery(
        bool available, byte displayState, string? recovery, string expectedGate)
    {
        var session = new AppLifecycleRecoverySessionState();
        if (!available)
        {
            session.Observe(AppLifecycleRecoverySignalClassifier.WmWtsSessionChange,
                new UIntPtr(0x0007), IntPtr.Zero, 0xC001);
        }

        var setting = new Win32Helper.PowerBroadcastSetting
        {
            PowerSetting = Win32Helper.ConsoleDisplayStatePowerSetting,
            DataLength = 1,
            Data = displayState
        };
        IntPtr payload = Marshal.AllocHGlobal(Marshal.SizeOf<Win32Helper.PowerBroadcastSetting>());
        try
        {
            Marshal.StructureToPtr(setting, payload, false);
            Assert.Equal(recovery, session.Observe(
                AppLifecycleRecoverySignalClassifier.WmPowerBroadcast,
                new UIntPtr(Win32Helper.PbtPowerSettingChange), payload, 0xC001));
            Assert.Equal(expectedGate, AppLifecycleRecoverySignalClassifier.ResolveGateEvent(
                AppLifecycleRecoverySignalClassifier.WmPowerBroadcast,
                new UIntPtr(Win32Helper.PbtPowerSettingChange), payload, session.IsSessionAvailable));
            Assert.Equal(available, session.IsSessionAvailable);
        }
        finally
        {
            Marshal.FreeHGlobal(payload);
        }
    }
}
