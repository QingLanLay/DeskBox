using System.Diagnostics;
using System.Runtime.InteropServices;
using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class DesktopShellWindowRecoveryTests
{
    [Theory]
    [InlineData("WorkerW")]
    [InlineData("Progman")]
    public void MinimizedIconHost_RestoresSavedBoundsWithoutTakingFocus(string hostClass)
    {
        OnStaThread(() =>
        {
            using var shell = new TestWindow("STATIC");
            using var host = new TestWindow(hostClass);
            using var icons = new TestWindow("SHELLDLL_DefView", host.Handle);
            Win32Helper.ShowWindow(host.Handle, Win32Helper.SW_SHOWNOACTIVATE);
            Assert.True(Win32Helper.GetWindowRect(host.Handle, out var normal));
            IntPtr foreground = Win32Helper.GetForegroundWindow();

            Win32Helper.ShowWindow(host.Handle, 7); // SW_SHOWMINNOACTIVE
            Assert.True(Win32Helper.IsIconic(host.Handle));
            Assert.False(DesktopShellWindowRecovery.TryRestoreIconHost(host.Handle, icons.Handle, shell.Handle));
            PumpUntil(() => !Win32Helper.IsIconic(host.Handle));

            Assert.True(DesktopShellWindowRecovery.TryRestoreIconHost(host.Handle, icons.Handle, shell.Handle));
            Assert.Equal(foreground, Win32Helper.GetForegroundWindow());
            Assert.True(Win32Helper.GetWindowRect(host.Handle, out var restored));
            Assert.Equal(normal.Left, restored.Left);
            Assert.Equal(normal.Top, restored.Top);
            Assert.Equal(normal.Right, restored.Right);
            Assert.Equal(normal.Bottom, restored.Bottom);
        });
    }

    [Fact]
    public void PlacementFallback_RestoresAMinimizedHostWithoutTakingFocus()
    {
        OnStaThread(() =>
        {
            using var host = new TestWindow("WorkerW");
            using var icons = new TestWindow("SHELLDLL_DefView", host.Handle);
            Win32Helper.ShowWindow(host.Handle, Win32Helper.SW_SHOWNOACTIVATE);
            Assert.True(Win32Helper.GetWindowRect(host.Handle, out var normal));
            IntPtr foreground = Win32Helper.GetForegroundWindow();
            Win32Helper.ShowWindow(host.Handle, 7);

            DesktopShellWindowRecovery.RequestRestore(host.Handle, icons.Handle, reapplyPlacement: true);
            PumpUntil(() => !Win32Helper.IsIconic(host.Handle));

            Assert.Equal(foreground, Win32Helper.GetForegroundWindow());
            Assert.True(Win32Helper.GetWindowRect(host.Handle, out var restored));
            Assert.Equal(normal, restored);
        });
    }

    [Fact]
    public void CaptionSizedNonMinimizedHost_UsesTheIconViewsFullBounds()
    {
        OnStaThread(() =>
        {
            using var host = new TestWindow("WorkerW");
            using var icons = new TestWindow("SHELLDLL_DefView", host.Handle);
            Win32Helper.ShowWindow(host.Handle, Win32Helper.SW_SHOWNOACTIVATE);
            Assert.True(Win32Helper.SetWindowPos(host.Handle, IntPtr.Zero, 0, 0, 160, 28,
                Win32Helper.SWP_NOMOVE | Win32Helper.SWP_NOZORDER | Win32Helper.SWP_NOACTIVATE));
            IntPtr foreground = Win32Helper.GetForegroundWindow();
            Assert.False(Win32Helper.IsIconic(host.Handle));

            DesktopShellWindowRecovery.RequestRestore(host.Handle, icons.Handle, reapplyPlacement: true);
            PumpUntil(() => Win32Helper.GetWindowRect(host.Handle, out var bounds) &&
                bounds.Right - bounds.Left == 800 && bounds.Bottom - bounds.Top == 600);

            Assert.Equal(foreground, Win32Helper.GetForegroundWindow());
        });
    }

    [Fact]
    public void DesktopDiscovery_NeverCachesAForeignProcessIconView()
    {
        OnStaThread(() =>
        {
            using var foreignHost = new TestWindow("WorkerW");
            using var foreignIcons = new TestWindow("SHELLDLL_DefView", foreignHost.Handle);
            Win32Helper.ShowWindow(foreignHost.Handle, Win32Helper.SW_SHOWNOACTIVATE);
            WidgetLayerService.InvalidateDesktopIconViewCache();
            Assert.NotEqual(foreignIcons.Handle, WidgetLayerService.FindDesktopIconView());
            Assert.NotEqual(foreignIcons.Handle, WidgetLayerService.FindDesktopIconView());
            WidgetLayerService.InvalidateDesktopIconViewCache();
        });
    }

    [Fact]
    public void DesktopOwner_IsTheValidatedTopLevelHostInsteadOfItsChildView()
    {
        OnStaThread(() =>
        {
            using var shell = new TestWindow("STATIC");
            using var host = new TestWindow("WorkerW");
            using var icons = new TestWindow("SHELLDLL_DefView", host.Handle);
            Win32Helper.ShowWindow(host.Handle, Win32Helper.SW_SHOWNOACTIVATE);
            Assert.Equal(host.Handle, DesktopShellWindowRecovery.GetValidatedOwner(icons.Handle, shell.Handle));
            Win32Helper.ShowWindow(host.Handle, 7);
            Assert.Equal(IntPtr.Zero, DesktopShellWindowRecovery.GetValidatedOwner(icons.Handle, shell.Handle));
        });
    }

    [Fact]
    public void CorruptedHostAndIconViewBounds_FallBackToCurrentDesktopGeometry()
    {
        OnStaThread(() =>
        {
            using var host = new TestWindow("WorkerW");
            using var icons = new TestWindow("SHELLDLL_DefView", host.Handle);
            Win32Helper.ShowWindow(host.Handle, Win32Helper.SW_SHOWNOACTIVATE);
            uint flags = Win32Helper.SWP_NOMOVE | Win32Helper.SWP_NOZORDER | Win32Helper.SWP_NOACTIVATE;
            Assert.True(Win32Helper.SetWindowPos(host.Handle, IntPtr.Zero, 0, 0, 160, 28, flags));
            Assert.True(Win32Helper.SetWindowPos(icons.Handle, IntPtr.Zero, 0, 0, 160, 28, flags));
            IntPtr foreground = Win32Helper.GetForegroundWindow();

            DesktopShellWindowRecovery.RequestRestore(host.Handle, icons.Handle, reapplyPlacement: true);
            PumpUntil(() => Win32Helper.GetWindowRect(host.Handle, out var bounds) &&
                DesktopShellHealthSnapshot.HasDesktopSize(bounds));

            Assert.Equal(foreground, Win32Helper.GetForegroundWindow());
        });
    }

    [Fact]
    public void NormalIconHost_LeavesPlacementAndForegroundUnchanged()
    {
        OnStaThread(() =>
        {
            using var shell = new TestWindow("STATIC");
            using var host = new TestWindow("WorkerW");
            using var icons = new TestWindow("SHELLDLL_DefView", host.Handle);
            Win32Helper.ShowWindow(host.Handle, Win32Helper.SW_SHOWNOACTIVATE);
            Assert.True(Win32Helper.GetWindowRect(host.Handle, out var before));
            IntPtr foreground = Win32Helper.GetForegroundWindow();

            Assert.True(DesktopShellWindowRecovery.TryRestoreIconHost(host.Handle, icons.Handle, shell.Handle));
            Assert.True(DesktopShellWindowRecovery.TryRestoreIconHost(host.Handle, icons.Handle, shell.Handle));

            Assert.True(Win32Helper.GetWindowRect(host.Handle, out var after));
            Assert.Equal(before, after);
            Assert.Equal(foreground, Win32Helper.GetForegroundWindow());
        });
    }

    [Theory]
    [InlineData("hidden")]
    [InlineData("wrong-host-class")]
    [InlineData("wrong-view-class")]
    [InlineData("wrong-parent")]
    [InlineData("missing-shell")]
    [InlineData("foreign-process")]
    public void UnrelatedOrUnavailableWindow_IsNeverRestored(string scenario)
    {
        OnStaThread(() =>
        {
            using var shell = new TestWindow("STATIC");
            using var host = new TestWindow(scenario == "wrong-host-class" ? "STATIC" : "WorkerW");
            using var icons = new TestWindow(
                scenario == "wrong-view-class" ? "STATIC" : "SHELLDLL_DefView",
                scenario == "wrong-parent" ? shell.Handle : host.Handle);
            Win32Helper.ShowWindow(host.Handle, 7);
            Assert.True(Win32Helper.IsIconic(host.Handle));
            if (scenario == "hidden")
            {
                Win32Helper.ShowWindow(host.Handle, Win32Helper.SW_HIDE);
            }

            IntPtr shellHandle = scenario switch
            {
                "missing-shell" => IntPtr.Zero,
                "foreign-process" => Win32Helper.GetShellWindow(),
                _ => shell.Handle
            };
            Assert.False(DesktopShellWindowRecovery.TryRestoreIconHost(host.Handle, icons.Handle, shellHandle));
            Assert.True(Win32Helper.IsIconic(host.Handle));
            if (scenario == "hidden")
            {
                Assert.False(Win32Helper.IsWindowVisible(host.Handle));
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
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "Native desktop recovery test timed out.");
        if (failure is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private static void PumpUntil(Func<bool> completed)
    {
        var timeout = Stopwatch.StartNew();
        while (!completed() && timeout.Elapsed < TimeSpan.FromSeconds(2))
        {
            while (PeekMessageW(out var message, IntPtr.Zero, 0, 0, 1))
            {
                DispatchMessageW(ref message);
            }
            Thread.Sleep(1);
        }
        Assert.True(completed(), "Explorer-style window did not process the restore request.");
    }

    private sealed class TestWindow : IDisposable
    {
        internal TestWindow(string className, IntPtr parent = default)
        {
            IntPtr instance = Win32Helper.GetModuleHandle(null);
            var windowClass = new WindowClass
            {
                Procedure = DefaultProcedure,
                Instance = instance,
                ClassName = className
            };
            RegisterClassW(ref windowClass);
            bool child = parent != IntPtr.Zero;
            Handle = CreateWindowExW(
                Win32Helper.WS_EX_TOOLWINDOW | Win32Helper.WS_EX_NOACTIVATE | Win32Helper.WS_EX_LAYERED,
                className, "DeskBox desktop recovery regression",
                child ? Win32Helper.WS_CHILD | Win32Helper.WS_VISIBLE : Win32Helper.WS_POPUP,
                child ? 0 : -32000, child ? 0 : -32000, 800, 600,
                parent, IntPtr.Zero, instance, IntPtr.Zero);
            Assert.NotEqual(IntPtr.Zero, Handle);
            // These are real native HWNDs, kept transparent and offscreen.
            Assert.True(Win32Helper.SetLayeredWindowAttributes(Handle, 0, 0, Win32Helper.LWA_ALPHA));
        }

        internal IntPtr Handle { get; }
        public void Dispose() => DestroyWindow(Handle);
    }

    private delegate IntPtr WindowProcedure(IntPtr window, uint message, UIntPtr wParam, IntPtr lParam);
    private static readonly WindowProcedure DefaultProcedure =
        (window, message, wParam, lParam) => DefWindowProcW(window, message, wParam, lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClass
    {
        public uint Style;
        public WindowProcedure Procedure;
        public int ClassExtraBytes, WindowExtraBytes;
        public IntPtr Instance, Icon, Cursor, BackgroundBrush;
        public string? MenuName;
        public string ClassName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    {
        public IntPtr Window;
        public uint Id;
        public UIntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public Win32Helper.POINT Point;
        public uint Private;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(int extendedStyle, string className, string title,
        int style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassW(ref WindowClass windowClass);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProcW(IntPtr window, uint message, UIntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool PeekMessageW(out Message message, IntPtr window, uint min, uint max, uint remove);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessageW(ref Message message);
}
