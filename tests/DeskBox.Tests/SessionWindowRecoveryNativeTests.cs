using System.Runtime.InteropServices;
using DeskBox.Helpers;

namespace DeskBox.Tests;

public sealed class SessionWindowRecoveryNativeTests
{
    [Fact]
    public void HiddenMessageHost_RejectsShowAndMoveRequests()
    {
        OnStaThread(() =>
        {
            IntPtr window = CreateHiddenWindow();
            try
            {
                using var guard = new HiddenMessageWindowGuard(window);
                Win32Helper.ShowWindow(window, Win32Helper.SW_SHOWNOACTIVATE);
                _ = Win32Helper.SetWindowPos(window, Win32Helper.HWND_TOP,
                    0, 0, 136, 39, Win32Helper.SWP_SHOWWINDOW | Win32Helper.SWP_NOACTIVATE);

                Assert.False(Win32Helper.IsWindowVisible(window));
                Assert.True(Win32Helper.GetWindowRect(window, out var bounds));
                Assert.Equal(-32000, bounds.Left);
                Assert.Equal(-32000, bounds.Top);
                Assert.Equal(0, Win32Helper.GetWindowLong(window, Win32Helper.GWL_STYLE) &
                    (Win32Helper.WS_CAPTION | Win32Helper.WS_THICKFRAME));
            }
            finally
            {
                DestroyWindow(window);
            }
        });
    }

    [Fact]
    public void DropRegistration_RecoversNativeRevocationAndReplacement()
    {
        OnStaThread(() =>
        {
            Assert.True(OleDropTargetNativeMethods.OleInitialize(IntPtr.Zero) >= 0);
            IntPtr window = CreateHiddenWindow();
            try
            {
                using var original = new NativeDropTarget(window);
                original.Register();
                Assert.True(original.IsRegistered);
                IntPtr originalPointer = OleDropTargetNativeMethods.GetPropW(window, "OleDropTargetInterface");

                NativeDropTargetComInterop.Revoke(window);
                Assert.False(original.IsRegistered);
                original.Register();
                Assert.True(original.IsRegistered);

                NativeDropTargetComInterop.Revoke(window);
                using var replacement = new NativeDropTarget(window);
                replacement.Register();
                Assert.True(replacement.IsRegistered);
                Assert.NotEqual(originalPointer,
                    OleDropTargetNativeMethods.GetPropW(window, "OleDropTargetInterface"));

                original.Register(force: true);
                Assert.True(original.IsRegistered);
                Assert.Equal(originalPointer,
                    OleDropTargetNativeMethods.GetPropW(window, "OleDropTargetInterface"));
            }
            finally
            {
                DestroyWindow(window);
                OleDropTargetNativeMethods.OleUninitialize();
            }
        });
    }

    private static void OnStaThread(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)), "Native recovery test timed out.");
        if (failure is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private static IntPtr CreateHiddenWindow()
    {
        IntPtr window = CreateWindowExW(Win32Helper.WS_EX_TOOLWINDOW | Win32Helper.WS_EX_NOACTIVATE,
            "STATIC", "DeskBox recovery regression",
            Win32Helper.WS_POPUP | Win32Helper.WS_CAPTION | Win32Helper.WS_THICKFRAME,
            -32000, -32000, 80, 80, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        Assert.NotEqual(IntPtr.Zero, window);
        return window;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowExW(int extendedStyle, string className,
        string title, int style, int x, int y, int width, int height,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr window);
}
