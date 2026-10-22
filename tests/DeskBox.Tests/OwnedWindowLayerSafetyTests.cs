using System.ComponentModel;
using System.Runtime.InteropServices;
using DeskBox.Platform;

namespace DeskBox.Tests;

public sealed class OwnedWindowLayerSafetyTests
{
    [Theory]
    [InlineData(false, "bottom")]
    [InlineData(true, "bottom")]
    [InlineData(false, "clear-topmost")]
    [InlineData(true, "clear-topmost")]
    [InlineData(false, "geometry")]
    [InlineData(true, "geometry")]
    public void UpdatingOwnedWidget_LeavesOwnerAboveItsOriginalBoundary(bool childOwner, string operation)
    {
        // These are hidden test-owned windows; the real desktop is never moved.
        IntPtr boundary = CreateHiddenWindow(IntPtr.Zero);
        IntPtr owner = CreateHiddenWindow(IntPtr.Zero);
        IntPtr widget = CreateHiddenWindow(owner);
        IntPtr desktopView = IntPtr.Zero;
        try
        {
            if (childOwner)
            {
                // Match DeskBox's ownership by a desktop view inside a shell host.
                desktopView = CreateWindowExW(0, "STATIC", "DeskBox test desktop view",
                    0x40000000, 0, 0, 1, 1, owner, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                Assert.NotEqual(IntPtr.Zero, desktopView);
                _ = Win32Helper.SetWindowLongPtr(widget, Win32Helper.GWLP_HWNDPARENT, desktopView);
            }
            Assert.True(Win32Helper.SetWindowPos(boundary, Win32Helper.HWND_TOP,
                0, 0, 0, 0, Win32Helper.WindowLayerPositionFlags));
            Assert.True(Win32Helper.SetWindowPos(owner, Win32Helper.HWND_TOP,
                0, 0, 0, 0, Win32Helper.WindowLayerPositionFlags));
            Assert.True(IsAbove(owner, boundary));

            switch (operation)
            {
                case "bottom":
                    Win32Helper.SetWindowToBottom(widget);
                    break;
                case "clear-topmost":
                    Win32Helper.ClearWindowTopMost(widget);
                    break;
                case "geometry":
                    Assert.True(Win32Helper.SetWindowPos(widget, IntPtr.Zero,
                        2, 3, 20, 30, Win32Helper.SWP_NOZORDER |
                        Win32Helper.SWP_NOACTIVATE | Win32Helper.SWP_NOOWNERZORDER));
                    break;
            }

            Assert.True(IsAbove(owner, boundary));
            Assert.Equal(childOwner ? desktopView : owner,
                Win32Helper.GetWindowLongPtr(widget, Win32Helper.GWLP_HWNDPARENT));
        }
        finally
        {
            DestroyWindow(widget);
            if (desktopView != IntPtr.Zero) DestroyWindow(desktopView);
            DestroyWindow(owner);
            DestroyWindow(boundary);
        }
    }

    private static bool IsAbove(IntPtr first, IntPtr second)
    {
        IntPtr cursor = Win32Helper.GetWindow(first, Win32Helper.GW_HWNDNEXT);
        for (int i = 0; cursor != IntPtr.Zero && i < 10000; i++)
        {
            if (cursor == second) return true;
            cursor = Win32Helper.GetWindow(cursor, Win32Helper.GW_HWNDNEXT);
        }
        return false;
    }

    private static IntPtr CreateHiddenWindow(IntPtr owner)
    {
        IntPtr window = CreateWindowExW(0x00000080, "STATIC", "DeskBox layer regression",
            0x80000000, 0, 0, 1, 1, owner, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (window == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        return window;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(uint exStyle, string className, string name,
        uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu,
        IntPtr instance, IntPtr parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr window);
}
