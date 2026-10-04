using System.Runtime.InteropServices;
using DeskBox.Platform;

namespace DeskBox.Tests;

public sealed class WindowCornerRegionTests
{
    [Theory]
    [InlineData(4)]
    [InlineData(8)]
    public void RoundedRegion_ClipsCornersAndFollowsResize(double radius)
    {
        IntPtr hwnd = CreateTestWindow();
        try
        {
            var corners = new WindowCornerRegion();
            Assert.True(corners.Apply(hwnd, radius));
            AssertRoundedRegion(hwnd, 200, 120);
            Assert.True(corners.Apply(hwnd, radius));
            AssertRoundedRegion(hwnd, 200, 120);

            Assert.True(Win32Helper.SetWindowPos(
                hwnd, IntPtr.Zero, 0, 0, 300, 180,
                Win32Helper.SWP_NOMOVE | Win32Helper.SWP_NOZORDER | Win32Helper.SWP_NOACTIVATE));
            Assert.True(corners.Apply(hwnd, radius));
            AssertRoundedRegion(hwnd, 300, 180);

            Assert.True(Win32Helper.SetWindowPos(
                hwnd, IntPtr.Zero, 0, 0, 120, 80,
                Win32Helper.SWP_NOMOVE | Win32Helper.SWP_NOZORDER | Win32Helper.SWP_NOACTIVATE));
            Assert.True(corners.Apply(hwnd, radius));
            AssertRoundedRegion(hwnd, 120, 80);
        }
        finally
        {
            DestroyWindow(hwnd);
        }
    }

    [Fact]
    public void SquarePreference_RemovesRegionAndCanBeRoundedAgain()
    {
        IntPtr hwnd = CreateTestWindow();
        IntPtr snapshot = CreateRectRgn(0, 0, 0, 0);
        try
        {
            var corners = new WindowCornerRegion();
            Assert.True(corners.Apply(hwnd, 8));
            AssertRoundedRegion(hwnd, 200, 120);
            Assert.True(corners.Apply(hwnd, 0));
            Assert.Equal(0, GetWindowRgn(hwnd, snapshot));
            Assert.True(corners.Apply(hwnd, 0));
            Assert.True(corners.Apply(hwnd, 4));
            AssertRoundedRegion(hwnd, 200, 120);
        }
        finally
        {
            Win32Helper.DeleteObject(snapshot);
            DestroyWindow(hwnd);
        }
    }

    private static IntPtr CreateTestWindow()
    {
        IntPtr hwnd = CreateWindowEx(
            0, "STATIC", "DeskBox corner test", 0x80000000,
            -32000, -32000, 200, 120,
            IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        Assert.NotEqual(IntPtr.Zero, hwnd);
        return hwnd;
    }

    [Fact]
    public void TransparentHostRegion_PreservesTheAntialiasedEdgeCoverage()
    {
        IntPtr hwnd = CreateTestWindow();
        IntPtr nominal = CreateRectRgn(0, 0, 0, 0);
        IntPtr expanded = CreateRectRgn(0, 0, 0, 0);
        try
        {
            var corners = new WindowCornerRegion();
            Assert.True(corners.Apply(hwnd, 8));
            Assert.Equal(3, GetWindowRgn(hwnd, nominal));
            Assert.True(corners.Apply(hwnd, 8, preserveAntialiasing: true));
            Assert.Equal(3, GetWindowRgn(hwnd, expanded));
            bool includesFringe = false;
            for (int y = 0; y < 120; y++)
            for (int x = 0; x < 200; x++)
            {
                bool inNominal = PtInRegion(nominal, x, y);
                bool inExpanded = PtInRegion(expanded, x, y);
                if (inNominal) Assert.True(inExpanded);
                includesFringe |= inExpanded && !inNominal;
            }
            Assert.True(includesFringe);
        }
        finally
        {
            Win32Helper.DeleteObject(nominal);
            Win32Helper.DeleteObject(expanded);
            DestroyWindow(hwnd);
        }
    }

    private static void AssertRoundedRegion(IntPtr hwnd, int width, int height)
    {
        IntPtr snapshot = CreateRectRgn(0, 0, 0, 0);
        Assert.NotEqual(IntPtr.Zero, snapshot);
        try
        {
            Assert.Equal(3, GetWindowRgn(hwnd, snapshot));
            Assert.False(PtInRegion(snapshot, 0, 0));
            Assert.False(PtInRegion(snapshot, width - 1, 0));
            Assert.False(PtInRegion(snapshot, 0, height - 1));
            Assert.False(PtInRegion(snapshot, width - 1, height - 1));
            Assert.True(PtInRegion(snapshot, width / 2, height / 2));
            Assert.True(PtInRegion(snapshot, width - 1, height / 2));
            Assert.True(PtInRegion(snapshot, width / 2, height - 1));
            Assert.False(PtInRegion(snapshot, width + 1, height / 2));
        }
        finally
        {
            Win32Helper.DeleteObject(snapshot);
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(
        uint exStyle, string className, string windowName, uint style,
        int x, int y, int width, int height,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int GetWindowRgn(IntPtr hwnd, IntPtr region);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PtInRegion(IntPtr region, int x, int y);
}
