using System.Text;
using System.Runtime.InteropServices;
using DeskBox.Platform;

namespace DeskBox.Services;

/// <summary>Repairs the minimized Explorer icon host left behind by a session transition.</summary>
internal static class DesktopShellWindowRecovery
{
    internal static DesktopShellHealthSnapshot Capture()
    {
        IntPtr shellWindow = Win32Helper.GetShellWindow();
        DesktopShellHealthSnapshot snapshot = default;
        Win32Helper.EnumWindows((host, _) =>
        {
            IntPtr view = Win32Helper.FindWindowEx(host, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (IsValidatedIconHost(host, view, shellWindow))
            {
                Win32Helper.GetWindowRect(host, out var bounds);
                Win32Helper.GetWindowRect(view, out var viewBounds);
                snapshot = new(host, view, Win32Helper.IsIconic(host),
                    Win32Helper.IsWindowVisible(view), bounds, viewBounds);
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return snapshot;
    }

    internal static IntPtr GetValidatedOwner(IntPtr iconView) =>
        GetValidatedOwner(iconView, Win32Helper.GetShellWindow());

    internal static IntPtr GetValidatedOwner(IntPtr iconView, IntPtr shellWindow)
    {
        IntPtr host = Win32Helper.GetAncestor(iconView, Win32Helper.GA_ROOT);
        return IsValidatedIconHost(host, iconView, shellWindow) &&
            !Win32Helper.IsIconic(host) ? host : IntPtr.Zero;
    }

    internal static void Repair(DesktopShellHealthSnapshot snapshot, bool reapplyPlacement)
    {
        // HWNDs may have been destroyed/reused since the background observation.
        if (!Win32Helper.IsInteractiveDesktopAvailable() ||
            !IsValidatedIconHost(snapshot.Host, snapshot.IconView, Win32Helper.GetShellWindow()))
        {
            return;
        }
        RequestRestore(snapshot.Host, snapshot.IconView, reapplyPlacement);
    }

    internal static IntPtr TryRecoverIconHost()
    {
        // Unlock/display-on messages can arrive before Winlogon releases input.
        if (!Win32Helper.IsInteractiveDesktopAvailable())
        {
            return IntPtr.Zero;
        }

        IntPtr shellWindow = Win32Helper.GetShellWindow();
        IntPtr readyIconView = IntPtr.Zero;
        Win32Helper.EnumWindows((host, _) =>
        {
            IntPtr iconView = Win32Helper.FindWindowEx(host, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (iconView != IntPtr.Zero && TryRestoreIconHost(host, iconView, shellWindow))
            {
                readyIconView = iconView;
                return false;
            }

            return true;
        }, IntPtr.Zero);
        return readyIconView;
    }

    internal static bool TryRestoreIconHost(IntPtr host, IntPtr iconView, IntPtr shellWindow)
    {
        if (!IsValidatedIconHost(host, iconView, shellWindow))
        {
            return false;
        }

        if (Win32Helper.IsIconic(host))
        {
            RequestRestore(host, iconView, reapplyPlacement: false);
            return false;
        }
        return Win32Helper.IsWindowVisible(iconView);
    }

    internal static bool IsValidatedIconHost(IntPtr host, IntPtr iconView, IntPtr shellWindow)
    {
        if (shellWindow == IntPtr.Zero || !Win32Helper.IsWindow(shellWindow) ||
            !Win32Helper.IsWindow(host) || !Win32Helper.IsWindow(iconView) ||
            !Win32Helper.IsWindowVisible(host) ||
            Win32Helper.GetParent(iconView) != host ||
            !HasClass(iconView, "SHELLDLL_DefView") ||
            !(HasClass(host, "WorkerW") || HasClass(host, "Progman")))
        {
            return false;
        }

        Win32Helper.GetWindowThreadProcessId(shellWindow, out uint shellProcessId);
        Win32Helper.GetWindowThreadProcessId(host, out uint hostProcessId);
        Win32Helper.GetWindowThreadProcessId(iconView, out uint iconProcessId);
        if (shellProcessId == 0 || hostProcessId != shellProcessId || iconProcessId != shellProcessId)
        {
            return false;
        }

        return true;
    }

    internal static void RequestRestore(IntPtr host, IntPtr iconView, bool reapplyPlacement)
    {
        if (reapplyPlacement)
        {
            var placement = new Win32Helper.WindowPlacement
            {
                Length = (uint)Marshal.SizeOf<Win32Helper.WindowPlacement>()
            };
            if (Win32Helper.GetWindowPlacement(host, ref placement))
            {
                // A second recovery path handles an ignored ShowWindow request
                // or a non-minimized caption-sized host. Never activate Explorer.
                placement.Flags = 0x0004; // WPF_ASYNCWINDOWPLACEMENT
                placement.ShowCommand = Win32Helper.SW_SHOWNOACTIVATE;
                if (!DesktopShellHealthSnapshot.HasDesktopSize(placement.NormalPosition))
                {
                    if (Win32Helper.GetWindowRect(iconView, out var viewBounds) &&
                        DesktopShellHealthSnapshot.HasDesktopSize(viewBounds))
                    {
                        placement.NormalPosition = viewBounds;
                    }
                    else if (Win32Helper.GetWindowRect(Win32Helper.GetShellWindow(), out var shellBounds) &&
                        DesktopShellHealthSnapshot.HasDesktopSize(shellBounds))
                    {
                        placement.NormalPosition = shellBounds;
                    }
                    else
                    {
                        // WM_SIZE may already have shrunk the icon view too.
                        // Obtain current virtual-screen geometry rather than
                        // perpetuating the corrupted saved caption rectangle.
                        int x = Win32Helper.GetSystemMetrics(76); // SM_XVIRTUALSCREEN
                        int y = Win32Helper.GetSystemMetrics(77); // SM_YVIRTUALSCREEN
                        placement.NormalPosition = new Win32Helper.RECT
                        {
                            Left = x, Top = y,
                            Right = x + Win32Helper.GetSystemMetrics(78),
                            Bottom = y + Win32Helper.GetSystemMetrics(79)
                        };
                    }
                }
                bool requested = Win32Helper.SetWindowPlacement(host, in placement);
                App.Log($"[DesktopRecovery] Reapply icon host placement hwnd=0x{host.ToInt64():X} requested={requested}");
                return;
            }
        }
        bool queued = Win32Helper.ShowWindowAsync(host, Win32Helper.SW_SHOWNOACTIVATE);
        App.Log($"[DesktopRecovery] Restore minimized icon host hwnd=0x{host.ToInt64():X} queued={queued}");
    }

    private static bool HasClass(IntPtr window, string expected)
    {
        var name = new StringBuilder(256);
        return Win32Helper.GetClassName(window, name, name.Capacity) > 0 &&
            string.Equals(name.ToString(), expected, StringComparison.Ordinal);
    }
}

internal readonly record struct DesktopShellHealthSnapshot(
    IntPtr Host, IntPtr IconView, bool IsMinimized, bool IsViewVisible,
    Win32Helper.RECT Bounds, Win32Helper.RECT ViewBounds)
{
    internal bool Exists => Host != IntPtr.Zero && IconView != IntPtr.Zero;
    // A user may intentionally hide the desktop icon view. That preference
    // must not trigger repair or application restarts.
    internal bool IsHealthy => Exists && !IsMinimized && HasDesktopSize(Bounds);
    internal static bool HasDesktopSize(Win32Helper.RECT bounds) =>
        bounds.Right - bounds.Left > 160 && bounds.Bottom - bounds.Top > 64;
}
