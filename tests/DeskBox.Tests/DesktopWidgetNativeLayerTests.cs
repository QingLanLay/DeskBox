using System.Runtime.InteropServices;
using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class DesktopWidgetNativeLayerTests
{
    [Fact]
    public void BottomDemotion_NeverPassesThroughTheFrontOfTheApplicationBand()
    {
        using var application = new TestWindow();
        using var widget = new TestWindow();
        widget.PlaceAfter(Win32Helper.HWND_TOPMOST);
        IntPtr foreground = Win32Helper.GetForegroundWindow();
        var observedAboveApplication = new List<bool>();
        Win32Helper.SubclassProc callback = (window, message, wParam, lParam, id, data) =>
        {
            if (message == 0x0047) // WM_WINDOWPOSCHANGED
            {
                observedAboveApplication.Add(IsAbove(window, application.Handle));
            }

            return Win32Helper.DefSubclassProc(window, message, wParam, lParam);
        };
        Assert.True(Win32Helper.SetWindowSubclass(widget.Handle, callback, new UIntPtr(1), UIntPtr.Zero));
        try
        {
            Win32Helper.SetWindowToBottom(widget.Handle);

            Assert.NotEmpty(observedAboveApplication);
            Assert.All(observedAboveApplication, above => Assert.False(above));
            Assert.False(Win32Helper.IsWindowTopMost(widget.Handle));
            Assert.True(Win32Helper.IsWindowVisible(widget.Handle));
            Assert.Equal(foreground, Win32Helper.GetForegroundWindow());
        }
        finally
        {
            Win32Helper.RemoveWindowSubclass(widget.Handle, callback, new UIntPtr(1));
            GC.KeepAlive(callback);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DesktopPeerExpansion_RemainsBelowEveryApplicationWhileCoveringItsPeer(bool desktopOwned)
    {
        IntPtr owner = desktopOwned ? Win32Helper.GetShellWindow() : IntPtr.Zero;
        using var firstApplication = new TestWindow();
        using var secondApplication = new TestWindow();
        using var peer = new TestWindow(owner);
        using var expanded = new TestWindow(owner);
        firstApplication.PlaceAfter(Win32Helper.HWND_TOP);
        secondApplication.PlaceAfter(firstApplication.Handle);
        peer.PlaceAfter(Win32Helper.HWND_BOTTOM);
        expanded.PlaceAfter(peer.Handle);
        IntPtr foreground = Win32Helper.GetForegroundWindow();

        Assert.True(WidgetLayerService.TryBringDesktopPeerBehindApplications(
            expanded.Handle,
            window => window == peer.Handle || window == expanded.Handle));

        Assert.True(IsAbove(firstApplication.Handle, expanded.Handle));
        Assert.True(IsAbove(secondApplication.Handle, expanded.Handle));
        Assert.True(IsAbove(expanded.Handle, peer.Handle));
        Assert.True(IsAbove(expanded.Handle, FindHighestDesktopHost()));
        Assert.False(Win32Helper.IsWindowTopMost(expanded.Handle));
        Assert.Equal(foreground, Win32Helper.GetForegroundWindow());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DesktopPeerExpansion_RepairsAWidgetAlreadyFloatingAboveAnApplication(bool topMost)
    {
        using var application = new TestWindow();
        using var peer = new TestWindow();
        using var expanded = new TestWindow();
        application.PlaceAfter(Win32Helper.HWND_TOP);
        peer.PlaceAfter(Win32Helper.HWND_BOTTOM);
        expanded.PlaceAfter(topMost ? Win32Helper.HWND_TOPMOST : Win32Helper.HWND_TOP);
        Assert.True(IsAbove(expanded.Handle, application.Handle));

        Assert.True(WidgetLayerService.TryBringDesktopPeerBehindApplications(
            expanded.Handle,
            window => window == peer.Handle || window == expanded.Handle));

        Assert.True(IsAbove(application.Handle, expanded.Handle));
        Assert.True(IsAbove(expanded.Handle, peer.Handle));
        Assert.False(Win32Helper.IsWindowTopMost(expanded.Handle));
    }

    [Fact]
    public void DesktopPeerPlacement_WithNoApplications_RaisesAnAlreadyBuriedWindowAboveTheWallpaper()
    {
        using var widget = new TestWindow();
        IntPtr desktopHost = FindHighestDesktopHost();
        Assert.NotEqual(IntPtr.Zero, desktopHost);
        widget.PlaceAfter(Win32Helper.HWND_BOTTOM);
        Assert.True(IsAbove(desktopHost, widget.Handle));
        IntPtr foreground = Win32Helper.GetForegroundWindow();

        Assert.True(WidgetLayerService.TryBringDesktopPeerBehindApplications(
            widget.Handle, _ => true));

        Assert.True(IsAbove(widget.Handle, desktopHost));
        Assert.False(Win32Helper.IsWindowTopMost(widget.Handle));
        Assert.Equal(foreground, Win32Helper.GetForegroundWindow());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-2)]
    public void NativePositionGuard_PreventsApplicationOvertakeBeforeItIsPainted(int requestedBand)
    {
        using var application = new TestWindow();
        using var peer = new TestWindow();
        using var widget = new TestWindow();
        application.PlaceAfter(Win32Helper.HWND_TOP);
        peer.PlaceAfter(application.Handle);
        widget.PlaceAfter(peer.Handle);
        IntPtr foreground = Win32Helper.GetForegroundWindow();
        var observedAboveApplication = new List<bool>();
        int guardedRequests = 0;
        Win32Helper.SubclassProc guard = (window, message, wParam, lParam, id, data) =>
        {
            if (message == 0x0046)
            {
                guardedRequests++;
                WidgetLayerService.ConstrainDesktopPinnedWindowPosition(window, lParam,
                    handle => handle == peer.Handle || handle == widget.Handle);
            }
            if (message == 0x0047)
            {
                observedAboveApplication.Add(IsAbove(window, application.Handle));
            }
            return Win32Helper.DefSubclassProc(window, message, wParam, lParam);
        };
        Assert.True(Win32Helper.SetWindowSubclass(widget.Handle, guard, new UIntPtr(2), UIntPtr.Zero));
        try
        {
            widget.PlaceAfter(new IntPtr(requestedBand));

            Assert.True(guardedRequests > 0);
            Assert.All(observedAboveApplication, above => Assert.False(above));
            Assert.False(Win32Helper.IsWindowTopMost(widget.Handle));
            Assert.True(IsAbove(application.Handle, widget.Handle));
            Assert.True(IsAbove(widget.Handle, FindHighestDesktopHost()));
            Assert.Equal(foreground, Win32Helper.GetForegroundWindow());

            widget.PlaceAfter(peer.Handle);
            Assert.True(IsAbove(peer.Handle, widget.Handle));
            Assert.True(IsAbove(application.Handle, peer.Handle));
        }
        finally
        {
            Win32Helper.RemoveWindowSubclass(widget.Handle, guard, new UIntPtr(2));
            GC.KeepAlive(guard);
        }
    }

    [Fact]
    public void DesktopLayerRepair_RestoresDriftedPeersWithoutShowingHiddenWindowsOrReorderingAgain()
    {
        using var application = new TestWindow();
        using var expanded = new TestWindow();
        using var peer = new TestWindow();
        using var hidden = new TestWindow();
        Win32Helper.ShowWindow(hidden.Handle, Win32Helper.SW_HIDE);
        application.PlaceAfter(Win32Helper.HWND_TOP);
        peer.PlaceAfter(Win32Helper.HWND_TOPMOST);
        expanded.PlaceAfter(Win32Helper.HWND_TOPMOST);
        peer.PlaceAfter(expanded.Handle);
        Assert.True(IsAbove(expanded.Handle, peer.Handle));
        IntPtr foreground = Win32Helper.GetForegroundWindow();
        IntPtr[] handles = [peer.Handle, expanded.Handle, hidden.Handle];

        Assert.True(WidgetLayerService.RepairDesktopPinnedWindowOrder(handles));

        Assert.True(IsAbove(application.Handle, expanded.Handle));
        Assert.True(IsAbove(expanded.Handle, peer.Handle));
        Assert.True(IsAbove(peer.Handle, FindHighestDesktopHost()));
        Assert.False(Win32Helper.IsWindowTopMost(expanded.Handle));
        Assert.False(Win32Helper.IsWindowTopMost(peer.Handle));
        Assert.False(Win32Helper.IsWindowVisible(hidden.Handle));
        Assert.Equal(foreground, Win32Helper.GetForegroundWindow());
        Assert.False(WidgetLayerService.RepairDesktopPinnedWindowOrder(handles));
    }

    private static IntPtr FindHighestDesktopHost()
    {
        IntPtr host = IntPtr.Zero;
        Win32Helper.EnumWindows((window, _) =>
        {
            var className = new System.Text.StringBuilder(256);
            Win32Helper.GetClassName(window, className, className.Capacity);
            if (Win32Helper.IsWindowVisible(window) && !Win32Helper.IsWindowTopMost(window) &&
                className.ToString() is "WorkerW" or "Progman")
            {
                host = window;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return host;
    }

    private static bool IsAbove(IntPtr higher, IntPtr lower)
    {
        IntPtr current = Win32Helper.GetWindow(lower, Win32Helper.GW_HWNDPREV);
        while (current != IntPtr.Zero)
        {
            if (current == higher)
            {
                return true;
            }

            current = Win32Helper.GetWindow(current, Win32Helper.GW_HWNDPREV);
        }

        return false;
    }

    private sealed class TestWindow : IDisposable
    {
        public TestWindow(IntPtr owner = default)
        {
            // Offscreen, non-activating native windows exercise real Z-order
            // without covering the user's desktop or taking keyboard focus.
            Handle = CreateWindowExW(
                Win32Helper.WS_EX_NOACTIVATE | Win32Helper.WS_EX_TOOLWINDOW,
                "STATIC", "DeskBox layer regression", unchecked((int)0x80000000),
                -32000, -32000, 80, 80, owner, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            Assert.NotEqual(IntPtr.Zero, Handle);
            PlaceAfter(Win32Helper.HWND_BOTTOM);
        }

        public IntPtr Handle { get; }

        public void PlaceAfter(IntPtr boundary) => Assert.True(Win32Helper.SetWindowPos(
            Handle, boundary, 0, 0, 0, 0,
            Win32Helper.SWP_NOMOVE | Win32Helper.SWP_NOSIZE |
                Win32Helper.SWP_NOACTIVATE | Win32Helper.SWP_NOOWNERZORDER |
                Win32Helper.SWP_SHOWWINDOW));

        public void Dispose() => DestroyWindow(Handle);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(
        int extendedStyle, string className, string windowName, int style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu,
        IntPtr instance, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr window);
}
