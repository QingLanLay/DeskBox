using DeskBox.Helpers;
using DeskBox.Platform;
using System.Runtime.InteropServices;

namespace DeskBox.Services;

/// <summary>
/// Centralizes desktop widget Z-order operations so future layer modes can be
/// implemented without duplicating Win32 calls across each widget window type.
/// </summary>
public static class WidgetLayerService
{
    internal const int StartupDesktopReadyRequiredStableSamples = 5;
    internal const int StartupDesktopReadyMaxProbeAttempts = 48;
    internal static readonly TimeSpan StartupDesktopReadyProbeInterval =
        TimeSpan.FromMilliseconds(250);

    private static readonly object s_desktopLayerLock = new();
    private static readonly Dictionary<IntPtr, DesktopLayerAttachment> s_desktopLayerAttachments = [];
    private static IntPtr s_cachedDesktopIconView;
    private static bool s_startupDesktopLayerAttachmentDeferred;
    private static bool s_sessionRecoverySuspended;
    private static long s_desktopHostGeneration;

    internal static bool IsSessionRecoverySuspended => Volatile.Read(ref s_sessionRecoverySuspended);
    internal static long DesktopHostGeneration => Interlocked.Read(ref s_desktopHostGeneration);

    internal static void SetSessionRecoverySuspended(bool suspended)
    {
        Volatile.Write(ref s_sessionRecoverySuspended, suspended);
        InvalidateDesktopIconViewCache();
    }

    internal static void DetachDesktopOwnersForRecovery()
    {
        lock (s_desktopLayerLock)
        {
            var attachments = s_desktopLayerAttachments.ToArray();
            s_desktopLayerAttachments.Clear();
            s_cachedDesktopIconView = IntPtr.Zero;
            foreach (var (window, attachment) in attachments)
            {
                if (Win32Helper.IsWindow(window))
                {
                    // Do not reposition/show any window during the secure
                    // desktop transition. Only remove our owner association.
                    Win32Helper.SetWindowLongPtr(window, Win32Helper.GWLP_HWNDPARENT,
                        Win32Helper.IsWindow(attachment.OriginalOwner) ? attachment.OriginalOwner : IntPtr.Zero);
                }
            }
        }
    }

    internal static void BeginStartupDesktopLayerAttachmentDeferral()
    {
        Volatile.Write(ref s_startupDesktopLayerAttachmentDeferred, true);
        InvalidateDesktopIconViewCache();
        App.Log("[Startup] Explorer desktop-layer attachment deferred without delaying widget restore");
    }

    internal static void EndStartupDesktopLayerAttachmentDeferral()
    {
        Volatile.Write(ref s_startupDesktopLayerAttachmentDeferred, false);
    }

    /// <summary>
    /// Waits until Explorer's existing desktop icon host has remained stable long
    /// enough for login-time icon restoration to finish. This is intentionally
    /// used only for startup launches; normal launches should remain immediate.
    /// </summary>
    internal static async Task<bool> WaitForDesktopIconViewReadyAsync(
        CancellationToken cancellationToken = default)
    {
        var stabilityTracker = new DesktopIconViewStabilityTracker(
            StartupDesktopReadyRequiredStableSamples);

        for (int attempt = 1; attempt <= StartupDesktopReadyMaxProbeAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsSessionRecoverySuspended)
            {
                return false;
            }

            IntPtr desktopIconView = Win32Helper.IsInteractiveDesktopAvailable()
                ? FindDesktopIconView() : IntPtr.Zero;
            if (stabilityTracker.Observe(desktopIconView))
            {
                App.Log(
                    $"[Startup] Explorer desktop layer stable after {attempt} probes " +
                    $"hwnd=0x{desktopIconView.ToInt64():X}");
                return true;
            }

            if (attempt < StartupDesktopReadyMaxProbeAttempts)
            {
                await Task.Delay(StartupDesktopReadyProbeInterval, cancellationToken);
            }
        }

        double maximumWaitMilliseconds =
            StartupDesktopReadyProbeInterval.TotalMilliseconds *
            (StartupDesktopReadyMaxProbeAttempts - 1);
        App.Log(
            $"[Startup] Explorer desktop layer was not stable within " +
            $"{maximumWaitMilliseconds:0}ms; restoring widgets with the safe fallback layer");
        return false;
    }

    public static void MoveToDesktopBottom(
        IntPtr windowHandle,
        bool showWindow = true)
    {
        if (IsSessionRecoverySuspended)
        {
            return;
        }

        ApplyDesktopPinnedActivationStyle(windowHandle);

        // Desktop-pinned mode always rests inside Explorer. Dynamic mode uses
        // the same owner only when the user wants widgets to survive Win+D.
        if (ShouldAttachRestingWindowToDesktop() &&
            TryAttachToDesktopIconLayer(
                windowHandle,
                showWindow: showWindow))
        {
            return;
        }

        DetachFromDesktopIconLayerIfNeeded(windowHandle);
        if (UsesDesktopPinnedMode())
        {
            _ = TryBringDesktopPeerBehindApplications(windowHandle, IsDesktopWidgetWindow, showWindow);
        }
        else
        {
            Win32Helper.SetWindowToBottom(windowHandle);
        }
    }

    public static IntPtr ClearTopMostPreservingForeground(IntPtr windowHandle)
    {
        if (IsSessionRecoverySuspended)
        {
            return Win32Helper.GetForegroundWindow();
        }

        ApplyDesktopPinnedActivationStyle(windowHandle);

        if (UsesDesktopPinnedMode())
        {
            if (!TryAttachToDesktopIconLayer(windowHandle))
            {
                MoveToDynamicDesktopBottom(windowHandle);
            }

            return Win32Helper.GetForegroundWindow();
        }

        IntPtr foreground = Win32Helper.GetForegroundWindow();
        IntPtr foregroundRoot = GetForegroundRoot(foreground);
        bool hasForeground =
            foregroundRoot != IntPtr.Zero &&
            Win32Helper.IsWindow(foregroundRoot);
        RelativeLayerRestoreDisposition disposition = RelativeLayerRestorePolicy.Decide(
            hasForeground,
            hasForeground && IsDesktopShellWindow(foregroundRoot),
            foregroundRoot == windowHandle,
            hasForeground && App.Current.IsDeskBoxWindow(foregroundRoot));

        switch (disposition)
        {
            case RelativeLayerRestoreDisposition.DesktopBottom:
                MoveToDesktopBottom(windowHandle);
                break;

            case RelativeLayerRestoreDisposition.PreservePeerOrder:
                if (!TryAttachRestingWindowWithoutChangingLevel(windowHandle))
                {
                    DetachFromDesktopIconLayerIfNeeded(windowHandle);
                }
                Win32Helper.ClearWindowTopMost(windowHandle);
                break;

            case RelativeLayerRestoreDisposition.BehindForeground:
                _ = TryPlaceDynamicWindowBehindForeground(
                    windowHandle,
                    foregroundRoot,
                    "restore");
                break;
        }

        App.LogVerbose(
            $"[ZOrder] Relative restore widget=0x{windowHandle.ToInt64():X} " +
            $"foreground=0x{foregroundRoot.ToInt64():X} disposition={disposition}");

        return foreground;
    }

    /// <summary>
    /// Restores a raised widget group as one Z-order unit. When another
    /// application owns the foreground, the whole group is inserted directly
    /// behind that application instead of being flattened to HWND_BOTTOM.
    /// </summary>
    internal static bool RestoreGroupPreservingForeground(
        IReadOnlyList<IntPtr> windowHandles,
        string reason)
    {
        List<IntPtr> handles = windowHandles
            .Where(handle => handle != IntPtr.Zero && Win32Helper.IsWindow(handle))
            .Distinct()
            .ToList();
        if (handles.Count == 0)
        {
            return true;
        }

        if (UsesDesktopPinnedMode())
        {
            foreach (IntPtr handle in handles)
            {
                MoveToDesktopBottom(handle);
            }

            bool pinnedApplied = ApplyPeerOrderHighestToLowest(handles);
            App.LogVerbose(
                $"[ZOrder] Group restore reason={reason} mode=DesktopPinned " +
                $"count={handles.Count} applied={pinnedApplied}");
            return pinnedApplied;
        }

        IntPtr foreground = Win32Helper.GetForegroundWindow();
        IntPtr foregroundRoot = GetForegroundRoot(foreground);
        bool hasForeground =
            foregroundRoot != IntPtr.Zero &&
            Win32Helper.IsWindow(foregroundRoot);
        RelativeLayerRestoreDisposition disposition = RelativeLayerRestorePolicy.Decide(
            hasForeground,
            hasForeground && IsDesktopShellWindow(foregroundRoot),
            handles.Contains(foregroundRoot),
            hasForeground && App.Current.IsDeskBoxWindow(foregroundRoot));

        bool applied;
        switch (disposition)
        {
            case RelativeLayerRestoreDisposition.DesktopBottom:
                foreach (IntPtr handle in handles)
                {
                    MoveToDesktopBottom(handle);
                }

                applied = ApplyPeerOrderHighestToLowest(handles);
                break;

            case RelativeLayerRestoreDisposition.PreservePeerOrder:
                foreach (IntPtr handle in handles)
                {
                    PrepareRestingWindowForRelativePlacement(handle);
                }

                applied = ApplyPeerOrderHighestToLowest(handles);
                break;

            case RelativeLayerRestoreDisposition.BehindForeground:
                foreach (IntPtr handle in handles)
                {
                    PrepareRestingWindowForRelativePlacement(handle);
                }

                IntPtr boundary = Win32Helper.IsWindowTopMost(foregroundRoot)
                    ? Win32Helper.HWND_TOP
                    : foregroundRoot;
                applied = ApplyWindowOrderHighestToLowest(
                    handles,
                    boundary,
                    $"group-restore-{reason}");
                break;

            default:
                applied = false;
                break;
        }

        App.LogVerbose(
            $"[ZOrder] Group restore reason={reason} count={handles.Count} " +
            $"foreground=0x{foregroundRoot.ToInt64():X} " +
            $"disposition={disposition} applied={applied}");
        return applied;
    }

    public static void ClearTopMost(IntPtr windowHandle)
    {
        ApplyDesktopPinnedActivationStyle(windowHandle);

        if (UsesDesktopPinnedMode())
        {
            if (!TryAttachToDesktopIconLayer(windowHandle))
            {
                MoveToDynamicDesktopBottom(windowHandle);
            }

            return;
        }

        MoveToDesktopBottom(windowHandle);
    }

    public static void HoldTemporaryTopMost(
        IntPtr windowHandle,
        bool showWindow = true)
    {
        ApplyDesktopPinnedActivationStyle(windowHandle);

        if (UsesDesktopPinnedMode())
        {
            if (!TryAttachToDesktopIconLayer(
                    windowHandle,
                    showWindow: showWindow))
            {
                MoveToDynamicDesktopBottom(windowHandle, showWindow);
            }

            return;
        }

        DetachFromDesktopIconLayerIfNeeded(windowHandle);
        Win32Helper.BringWindowTemporarilyToFront(windowHandle, showWindow);
    }

    /// <summary>
    /// Keeps a transient overlay group in the topmost band without activating
    /// any member. Callers must clear the state when the exit animation ends.
    /// The input order is visually highest to lowest.
    /// </summary>
    public static void HoldGroupTopMostWithoutActivation(
        IReadOnlyList<IntPtr> windowHandles)
    {
        List<IntPtr> handles = windowHandles
            .Where(handle => handle != IntPtr.Zero && Win32Helper.IsWindow(handle))
            .Distinct()
            .ToList();

        // Set the lowest peer first so the final call leaves the first input
        // handle visually highest inside the topmost band.
        for (int index = handles.Count - 1; index >= 0; index--)
        {
            IntPtr handle = handles[index];
            ApplyDesktopPinnedActivationStyle(handle);
            DetachFromDesktopIconLayerIfNeeded(handle);
            Win32Helper.SetWindowTopMost(handle);
        }

        App.LogVerbose(
            $"[ZOrder] Topmost overlay group held count={handles.Count}");
    }

    public static void BringToFront(IntPtr windowHandle)
    {
        ApplyDesktopPinnedActivationStyle(windowHandle);

        if (UsesDesktopPinnedMode())
        {
            if (!TryAttachToDesktopIconLayer(windowHandle))
            {
                MoveToDynamicDesktopBottom(windowHandle);
            }

            return;
        }

        DetachFromDesktopIconLayerIfNeeded(windowHandle);
        Win32Helper.BringWindowToFront(windowHandle);
    }

    /// <summary>
    /// Holds a DeskBox-owned auxiliary window (search popup, settings, desktop
    /// organization) in the topmost band above a raised quick-reveal widget
    /// group. The caller owns the release; see <see cref="ReleaseRaisedBandWindow"/>.
    /// </summary>
    internal static void HoldWindowAboveRaisedWidgets(IntPtr windowHandle)
    {
        Win32Helper.SetWindowTopMost(windowHandle);
    }

    /// <summary>
    /// Returns a raised-band guest to the normal Z-order band. When the session
    /// ended because the user activated a foreign application, the guest is
    /// placed directly below that window in a single transaction so it never
    /// covers the app the user just switched to; otherwise it lands at the top
    /// of the normal band as the still-active surface.
    /// </summary>
    internal static void ReleaseRaisedBandWindow(
        IntPtr windowHandle,
        RaisedBandReleasePlacement placement,
        IntPtr foreignForegroundRoot)
    {
        if (placement == RaisedBandReleasePlacement.BelowForeignForeground &&
            foreignForegroundRoot != IntPtr.Zero &&
            Win32Helper.IsWindow(foreignForegroundRoot))
        {
            Win32Helper.SetWindowPos(
                windowHandle,
                foreignForegroundRoot,
                0,
                0,
                0,
                0,
                Win32Helper.SWP_NOMOVE |
                    Win32Helper.SWP_NOSIZE |
                    Win32Helper.SWP_NOACTIVATE |
                    Win32Helper.SWP_NOOWNERZORDER);
            return;
        }

        Win32Helper.ClearWindowTopMost(windowHandle);
    }

    /// <summary>
    /// Raises one widget above its peers without activating it. In desktop-pinned
    /// mode the window remains attached to the desktop icon layer and only its
    /// sibling order changes.
    /// </summary>
    public static void BringAbovePeerWidgets(IntPtr windowHandle)
    {
        if (UsesDesktopPinnedMode())
        {
            MoveToDesktopBottom(windowHandle);
            return;
        }

        DetachFromDesktopIconLayerIfNeeded(windowHandle);
        Win32Helper.BringWindowTemporarilyToFront(windowHandle);
    }

    /// <summary>
    /// Raises a widget only within the Explorer desktop owner group. This is
    /// used by a capsule expanding in place: it must cover sibling widgets,
    /// but it must remain protected from Show Desktop when that preference is
    /// enabled. Tray-raised widgets intentionally use
    /// <see cref="BringAbovePeerWidgets"/> instead so they can appear above
    /// normal application windows.
    /// </summary>
    public static bool TryBringAbovePeerWidgetsAtDesktopLayer(IntPtr windowHandle)
    {
        // Explorer ownership protects the widget from Win+D, but HWND_TOP
        // still raises an owned top-level window above ordinary applications.
        // Keep the peer raise below every visible application window.
        if (!ShouldAttachRestingWindowToDesktop() ||
            !TryAttachToDesktopIconLayer(windowHandle, placeAtBottom: false))
        {
            return false;
        }

        return TryBringDesktopPeerBehindApplications(windowHandle, IsDesktopWidgetWindow);
    }

    internal static bool TryBringDesktopPeerBehindApplications(
        IntPtr windowHandle,
        Func<IntPtr, bool> isWidgetWindow,
        bool showWindow = true)
    {
        uint flags = Win32Helper.WindowLayerPositionFlags;
        if (showWindow)
        {
            flags |= Win32Helper.SWP_SHOWWINDOW;
        }

        return Win32Helper.SetWindowPos(
            windowHandle,
            FindDesktopPeerRaiseBoundary(windowHandle, isWidgetWindow),
            0,
            0,
            0,
            0,
            flags);
    }

    private static IntPtr FindDesktopPeerRaiseBoundary(
        IntPtr windowHandle,
        Func<IntPtr, bool> isWidgetWindow)
    {
        IntPtr boundary = Win32Helper.IsWindowTopMost(windowHandle)
            ? Win32Helper.HWND_NOTOPMOST
            : Win32Helper.HWND_TOP;
        IntPtr current = Win32Helper.GetWindow(windowHandle, Win32Helper.GW_HWNDFIRST);
        while (current != IntPtr.Zero)
        {
            // Windows can leave minimized and cloaked HWNDs behind the desktop.
            // Never use that lower region, or the taskbar's changing Z-order
            // band, as an application boundary for a desktop widget.
            if (IsDesktopShellHost(current) && IsRenderedWindow(current))
            {
                break;
            }

            if (current != windowHandle &&
                IsRenderedWindow(current) &&
                !Win32Helper.IsWindowTopMost(current) &&
                !isWidgetWindow(current) &&
                !HasWindowClass(current, "Shell_TrayWnd") &&
                !HasWindowClass(current, "Shell_SecondaryTrayWnd") &&
                !IsDesktopShellWindow(current))
            {
                boundary = current;
            }

            current = Win32Helper.GetWindow(current, Win32Helper.GW_HWNDNEXT);
        }

        return boundary;
    }

    internal static bool IsRestingAtDesktopLayer(IntPtr windowHandle)
    {
        if (windowHandle == IntPtr.Zero ||
            !Win32Helper.IsWindow(windowHandle) ||
            Win32Helper.IsWindowTopMost(windowHandle))
        {
            return false;
        }

        IntPtr expectedOwner = ShouldAttachRestingWindowToDesktop()
            ? DesktopShellWindowRecovery.GetValidatedOwner(FindDesktopIconView())
            : IntPtr.Zero;
        if (ShouldAttachRestingWindowToDesktop() &&
            (expectedOwner == IntPtr.Zero ||
             Win32Helper.GetWindowLongPtr(windowHandle, Win32Helper.GWLP_HWNDPARENT) != expectedOwner))
        {
            return false;
        }

        IntPtr boundary = FindDesktopPeerRaiseBoundary(windowHandle, IsDesktopWidgetWindow);
        return IsBelowDesktopPeerBoundary(windowHandle, boundary) &&
            IsAboveDesktopShellHosts(windowHandle);
    }

    internal static string DescribeNativeDesktopLayer(IntPtr windowHandle)
    {
        if (windowHandle == IntPtr.Zero || !Win32Helper.IsWindow(windowHandle))
        {
            return $"hwnd=0x{windowHandle.ToInt64():X} exists=False";
        }

        IntPtr owner = Win32Helper.GetWindowLongPtr(windowHandle, Win32Helper.GWLP_HWNDPARENT);
        IntPtr boundary = FindDesktopPeerRaiseBoundary(windowHandle, IsDesktopWidgetWindow);
        int cloakResult = Win32Helper.DwmGetWindowAttribute(windowHandle, 14, out int cloaked, sizeof(int));
        _ = Win32Helper.GetWindowRect(windowHandle, out var bounds);
        return $"hwnd=0x{windowHandle.ToInt64():X} nativeVisible={Win32Helper.IsWindowVisible(windowHandle)} " +
            $"cloaked={(cloakResult == 0 ? cloaked : -1)} topmost={Win32Helper.IsWindowTopMost(windowHandle)} " +
            $"noActivate={IsWindowNoActivate(windowHandle)} owner=0x{owner.ToInt64():X} " +
            $"boundary=0x{boundary.ToInt64():X} belowApplications={IsBelowDesktopPeerBoundary(windowHandle, boundary)} " +
            $"aboveDesktopHosts={IsAboveDesktopShellHosts(windowHandle)} " +
            $"bounds={bounds.Left},{bounds.Top},{bounds.Right},{bounds.Bottom}";
    }

    private static bool IsBelowDesktopPeerBoundary(IntPtr windowHandle, IntPtr boundary)
    {
        if (Win32Helper.IsWindowTopMost(windowHandle))
        {
            return false;
        }

        if (boundary == Win32Helper.HWND_NOTOPMOST || boundary == Win32Helper.HWND_TOP)
        {
            return true;
        }

        IntPtr current = Win32Helper.GetWindow(windowHandle, Win32Helper.GW_HWNDPREV);
        while (current != IntPtr.Zero)
        {
            if (current == boundary)
            {
                return true;
            }

            current = Win32Helper.GetWindow(current, Win32Helper.GW_HWNDPREV);
        }

        return false;
    }

    private static bool IsAboveDesktopShellHosts(IntPtr windowHandle)
    {
        IntPtr current = Win32Helper.GetWindow(windowHandle, Win32Helper.GW_HWNDPREV);
        while (current != IntPtr.Zero)
        {
            if (IsDesktopShellHost(current) && IsRenderedWindow(current))
            {
                return false;
            }

            current = Win32Helper.GetWindow(current, Win32Helper.GW_HWNDPREV);
        }

        return true;
    }

    private static bool IsDesktopShellHost(IntPtr windowHandle) =>
        HasWindowClass(windowHandle, "WorkerW") || HasWindowClass(windowHandle, "Progman");

    private static bool IsRenderedWindow(IntPtr windowHandle)
    {
        return Win32Helper.IsWindowVisible(windowHandle) && !Win32Helper.IsIconic(windowHandle) &&
            (Win32Helper.DwmGetWindowAttribute(windowHandle, 14, out int cloaked, sizeof(int)) != 0 || cloaked == 0) &&
            Win32Helper.GetWindowRect(windowHandle, out var bounds) &&
            bounds.Right > bounds.Left && bounds.Bottom > bounds.Top;
    }

    private static bool IsDesktopWidgetWindow(IntPtr windowHandle) =>
        App.Current?.WidgetManager?.IsWidgetWindow(windowHandle) == true;

    /// <summary>
    /// Applies and verifies an explicit peer order for an expanded capsule.
    /// A successful SetWindowPos call is not sufficient for windows owned by
    /// Explorer because Windows may still preserve an older owner-group order.
    /// </summary>
    public static bool EnsurePeerOrderHighestToLowest(
        IReadOnlyList<IntPtr> windowHandles)
    {
        List<IntPtr> handles = windowHandles
            .Where(handle => handle != IntPtr.Zero && Win32Helper.IsWindow(handle))
            .Distinct()
            .ToList();
        if (handles.Count < 2)
        {
            return handles.Count == 1;
        }

        IntPtr activeWindow = handles[0];
        _ = ApplyPeerOrderHighestToLowest(handles);
        if (IsHighestPeer(activeWindow, handles))
        {
            return true;
        }

        bool raised = TryBringAbovePeerWidgetsAtDesktopLayer(activeWindow) ||
            TryBringAbovePeerWidgetsBehindForeground(activeWindow);
        if (!raised)
        {
            BringAbovePeerWidgets(activeWindow);
        }

        bool reapplied = ApplyPeerOrderHighestToLowest(handles);
        bool verified = IsHighestPeer(activeWindow, handles);
        App.LogVerbose(
            $"[ZOrder] Expanded peer order fallback " +
            $"active=0x{activeWindow.ToInt64():X} " +
            $"raised={raised} reapplied={reapplied} verified={verified}");
        return verified;
    }

    private static bool IsPeerOrderAlreadyApplied(IReadOnlyList<IntPtr> handles)
    {
        var peers = new HashSet<IntPtr>(handles);
        var peersAboveHighest = new List<IntPtr>();
        IntPtr cursor = Win32Helper.GetWindow(handles[0], Win32Helper.GW_HWNDPREV);
        while (cursor != IntPtr.Zero)
        {
            if (peers.Contains(cursor))
            {
                peersAboveHighest.Add(cursor);
            }

            cursor = Win32Helper.GetWindow(cursor, Win32Helper.GW_HWNDPREV);
        }

        var orderFromHighest = new List<IntPtr>();
        cursor = handles[0];
        while (cursor != IntPtr.Zero)
        {
            if (peers.Contains(cursor))
            {
                orderFromHighest.Add(cursor);
            }

            cursor = Win32Helper.GetWindow(cursor, Win32Helper.GW_HWNDNEXT);
        }

        return IdleWidgetZOrderPolicy.MatchesRequestedOrder(
            handles,
            peersAboveHighest,
            orderFromHighest);
    }

    public static bool IsHighestPeer(
        IntPtr windowHandle,
        IReadOnlyCollection<IntPtr> peerWindowHandles)
    {
        if (windowHandle == IntPtr.Zero || !Win32Helper.IsWindow(windowHandle))
        {
            return false;
        }

        HashSet<IntPtr> peers = peerWindowHandles
            .Where(handle => handle != IntPtr.Zero && handle != windowHandle)
            .ToHashSet();
        IntPtr current = Win32Helper.GetWindow(windowHandle, Win32Helper.GW_HWNDPREV);
        while (current != IntPtr.Zero)
        {
            if (peers.Contains(current))
            {
                return false;
            }

            current = Win32Helper.GetWindow(current, Win32Helper.GW_HWNDPREV);
        }

        return true;
    }

    /// <summary>
    /// Moves a dynamically layered widget above its DeskBox peers without
    /// overtaking the current foreground application. This is used when a
    /// compact widget expands from hover after a tray-raised session has
    /// already returned control to another application.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when a foreign foreground window was found and
    /// the widget was positioned behind it; otherwise <see langword="false"/>.
    /// </returns>
    public static bool TryBringAbovePeerWidgetsBehindForeground(IntPtr windowHandle)
    {
        if (UsesDesktopPinnedMode() ||
            windowHandle == IntPtr.Zero ||
            !Win32Helper.IsWindow(windowHandle))
        {
            return false;
        }

        IntPtr foregroundRoot = GetForegroundRoot(Win32Helper.GetForegroundWindow());

        if (foregroundRoot == IntPtr.Zero ||
            foregroundRoot == windowHandle ||
            !Win32Helper.IsWindow(foregroundRoot) ||
            App.Current.IsDeskBoxWindow(foregroundRoot) ||
            IsDesktopShellWindow(foregroundRoot))
        {
            return false;
        }

        return TryPlaceDynamicWindowBehindForeground(
            windowHandle,
            foregroundRoot,
            "peer-raise");
    }

    private static bool TryPlaceDynamicWindowBehindForeground(
        IntPtr windowHandle,
        IntPtr foregroundRoot,
        string reason)
    {
        bool attachedToDesktop = PrepareRestingWindowForRelativePlacement(windowHandle);

        // A topmost foreground window already owns the higher Z-order band, so
        // HWND_TOP safely places this non-topmost widget at the head of the
        // normal band. For a normal foreground window, inserting immediately
        // after it keeps that application visibly above the expanding widget.
        IntPtr insertAfter = Win32Helper.IsWindowTopMost(foregroundRoot)
            ? Win32Helper.HWND_TOP
            : foregroundRoot;
        bool moved = Win32Helper.SetWindowPos(
            windowHandle,
            insertAfter,
            0,
            0,
            0,
            0,
            Win32Helper.SWP_NOMOVE |
                Win32Helper.SWP_NOSIZE |
                Win32Helper.SWP_NOACTIVATE |
                Win32Helper.SWP_NOOWNERZORDER |
                Win32Helper.SWP_SHOWWINDOW);
        App.LogVerbose(
            $"[ZOrder] Place behind foreground reason={reason} " +
            $"widget=0x{windowHandle.ToInt64():X} " +
            $"foreground=0x{foregroundRoot.ToInt64():X} " +
            $"desktopAttached={attachedToDesktop} moved={moved}");
        return moved;
    }

    private static bool PrepareRestingWindowForRelativePlacement(IntPtr windowHandle)
    {
        bool attachedToDesktop =
            TryAttachRestingWindowWithoutChangingLevel(windowHandle);
        if (!attachedToDesktop)
        {
            DetachFromDesktopIconLayerIfNeeded(windowHandle);
        }

        if (Win32Helper.IsWindowTopMost(windowHandle))
        {
            Win32Helper.ClearWindowTopMost(windowHandle);
        }

        return attachedToDesktop;
    }

    internal static IntPtr GetForegroundRoot(IntPtr foreground)
    {
        IntPtr foregroundRoot = Win32Helper.GetAncestor(
            foreground,
            Win32Helper.GA_ROOTOWNER);
        return foregroundRoot == IntPtr.Zero
            ? foreground
            : foregroundRoot;
    }

    public static void BringGroupTemporarilyToFront(
        IReadOnlyList<IntPtr> windowHandles,
        IntPtr activeWindowHandle)
    {
        if (UsesDesktopPinnedMode())
        {
            return;
        }

        var handles = windowHandles
            .Where(handle => handle != IntPtr.Zero && Win32Helper.IsWindow(handle))
            .Distinct()
            .ToList();
        if (handles.Count == 0)
        {
            return;
        }

        foreach (IntPtr handle in handles)
        {
            DetachFromDesktopIconLayerIfNeeded(handle);
            Win32Helper.SetWindowTopMost(handle);
        }

        foreach (IntPtr handle in handles.Where(handle => handle != activeWindowHandle))
        {
            Win32Helper.ClearWindowTopMost(handle);
        }

        IntPtr activeHandle = handles.Contains(activeWindowHandle)
            ? activeWindowHandle
            : handles[^1];
        Win32Helper.ClearWindowTopMost(activeHandle);
        Win32Helper.BringWindowToFront(activeHandle);
        Win32Helper.SetForegroundWindow(activeHandle);
    }

    /// <summary>
    /// Applies a deterministic peer order without activating, moving, or
    /// resizing any widget. The first handle is the visually highest peer.
    /// The current highest DeskBox peer supplies the global Z-order boundary,
    /// so normal desktop widgets do not jump above unrelated applications.
    /// </summary>
    public static bool ApplyPeerOrderHighestToLowest(
        IReadOnlyList<IntPtr> windowHandles)
    {
        List<IntPtr> handles = windowHandles
            .Where(handle => handle != IntPtr.Zero && Win32Helper.IsWindow(handle))
            .Distinct()
            .ToList();
        if (handles.Count < 2)
        {
            return true;
        }

        bool desktopPinned = UsesDesktopPinnedMode();
        IntPtr desktopBoundary = desktopPinned
            ? FindDesktopPeerRaiseBoundary(handles[0], IsDesktopWidgetWindow)
            : IntPtr.Zero;

        // Reordering HWNDs repaints overlapping shadow regions even when the
        // relative peer order is already correct, so a no-op transaction must
        // never reach SetWindowPos.
        if (IsPeerOrderAlreadyApplied(handles) &&
            (!desktopPinned || IsBelowDesktopPeerBoundary(handles[0], desktopBoundary) &&
                IsAboveDesktopShellHosts(handles[0])))
        {
            App.LogVerbose(
                $"[ZOrder] Peer order unchanged count={handles.Count} " +
                $"highest=0x{handles[0].ToInt64():X}");
            return true;
        }

        IntPtr boundary = desktopBoundary;
        if (!desktopPinned)
        {
            IntPtr currentHighest = FindHighestPeer(handles);
            boundary = currentHighest == IntPtr.Zero
                ? IntPtr.Zero
                : Win32Helper.GetWindow(currentHighest, Win32Helper.GW_HWNDPREV);
        }
        return ApplyWindowOrderHighestToLowest(
            handles,
            boundary == IntPtr.Zero ? Win32Helper.HWND_TOP : boundary,
            "idle-peer-order");
    }

    private static bool ApplyWindowOrderHighestToLowest(
        IReadOnlyList<IntPtr> handles,
        IntPtr boundary,
        string reason)
    {
        if (handles.Count == 0)
        {
            return true;
        }

        lock (s_desktopLayerLock)
        {
            IntPtr insertAfter = boundary;
            const uint flags =
                Win32Helper.SWP_NOMOVE |
                Win32Helper.SWP_NOSIZE |
                Win32Helper.SWP_NOACTIVATE |
                Win32Helper.SWP_NOOWNERZORDER;

            IntPtr deferred = Win32Helper.BeginDeferWindowPos(handles.Count);
            if (deferred != IntPtr.Zero)
            {
                foreach (IntPtr handle in handles)
                {
                    deferred = Win32Helper.DeferWindowPos(
                        deferred,
                        handle,
                        insertAfter,
                        0,
                        0,
                        0,
                        0,
                        flags);
                    if (deferred == IntPtr.Zero)
                    {
                        break;
                    }

                    insertAfter = handle;
                }

                if (deferred != IntPtr.Zero && Win32Helper.EndDeferWindowPos(deferred))
                {
                    App.LogVerbose(
                        $"[ZOrder] Window order applied reason={reason} " +
                        $"count={handles.Count} boundary=0x{boundary.ToInt64():X} " +
                        $"highest=0x{handles[0].ToInt64():X}");
                    return true;
                }
            }

            insertAfter = boundary;
            bool succeeded = true;
            foreach (IntPtr handle in handles)
            {
                succeeded &= Win32Helper.SetWindowPos(
                    handle,
                    insertAfter,
                    0,
                    0,
                    0,
                    0,
                    flags);
                insertAfter = handle;
            }

            App.LogVerbose(
                $"[ZOrder] Window order fallback reason={reason} " +
                $"count={handles.Count} boundary=0x{boundary.ToInt64():X} " +
                $"highest=0x{handles[0].ToInt64():X} succeeded={succeeded}");
            return succeeded;
        }
    }

    public static void ReleaseWindow(IntPtr windowHandle)
    {
        DetachFromDesktopIconLayerIfNeeded(windowHandle);
    }

    public static void InvalidateDesktopIconViewCache()
    {
        Interlocked.Increment(ref s_desktopHostGeneration);
        lock (s_desktopLayerLock)
        {
            s_cachedDesktopIconView = IntPtr.Zero;
        }
    }

    public static bool UsesDesktopPinnedMode()
    {
        var settings = App.Current?.SettingsService?.Settings;
        string mode = SettingsService.NormalizeWidgetLayerModeSetting(settings?.WidgetLayerMode);
        return string.Equals(mode, SettingsService.WidgetLayerModeDesktopPinned, StringComparison.Ordinal);
    }

    public static bool UsesQuickRevealMode()
    {
        var settings = App.Current?.SettingsService?.Settings;
        string mode = SettingsService.NormalizeWidgetLayerModeSetting(settings?.WidgetLayerMode);
        return string.Equals(mode, SettingsService.WidgetLayerModeQuickReveal, StringComparison.Ordinal);
    }

    /// <summary>
    /// Determines whether a pointer press must leave the current foreground
    /// application active. Desktop-pinned widgets behave like desktop content:
    /// an exposed part can still receive the mouse message, but it must not
    /// activate and jump above another application. Dynamic mode keeps the
    /// existing interactive raise behavior.
    /// </summary>
    public static bool ShouldSuppressPointerActivation(IntPtr windowHandle)
    {
        return WidgetLayerPointerActivationPolicy.ShouldSuppress(UsesDesktopPinnedMode());
    }

    /// <summary>
    /// Keeps the activating mouse message when a user clicks a widget that was
    /// shown by Quick Reveal. Only one window in the revealed group can be the
    /// foreground window, so every other widget must explicitly opt out of the
    /// default activate-and-eat behavior for its first click.
    /// </summary>
    public static bool ShouldPreserveQuickRevealActivatingClick()
    {
        return WidgetLayerPointerActivationPolicy.ShouldPreserveActivatingClick(
            UsesQuickRevealMode(),
            App.Current?.WidgetManager?.WidgetsRaisedFromTray == true);
    }

    /// <summary>
    /// Ordinary pointer presses never activate a desktop-pinned HWND. Activating
    /// an Explorer-owned popup can reorder the shell owner as well as the widget.
    /// Explicit text entry uses the separate keyboard-input path.
    /// </summary>
    public static bool TryAllowDesktopPinnedPointerActivation(IntPtr windowHandle)
    {
        if (!UsesDesktopPinnedMode())
        {
            SetWindowNoActivate(windowHandle, enabled: false);
            return true;
        }

        SetWindowNoActivate(windowHandle, enabled: true);
        return false;
    }

    /// <summary>
    /// Temporarily removes the desktop-pinned no-activate style for an explicit
    /// keyboard-input interaction. The caller must restore the resting desktop
    /// layer immediately after activating the window.
    /// </summary>
    public static void PrepareForDesktopPinnedKeyboardInput(IntPtr windowHandle)
    {
        // Activate text input independently of Explorer, then restore the desktop
        // owner once the focus request has completed.
        DetachFromDesktopIconLayerIfNeeded(windowHandle);
        SetWindowNoActivate(windowHandle, enabled: false);
    }

    internal static void RestoreAfterDesktopPinnedKeyboardInput(IntPtr windowHandle)
    {
        if (!UsesDesktopPinnedMode() || IsSessionRecoverySuspended)
        {
            return;
        }

        ApplyDesktopPinnedActivationStyle(windowHandle);
        if (TryAttachToDesktopIconLayer(windowHandle, placeAtBottom: false, showWindow: false))
        {
            _ = TryBringDesktopPeerBehindApplications(windowHandle, IsDesktopWidgetWindow, showWindow: false);
        }
        else
        {
            MoveToDesktopBottom(windowHandle, showWindow: false);
        }
    }

    public static bool IsWindowNoActivate(IntPtr windowHandle)
    {
        return windowHandle != IntPtr.Zero &&
            (Win32Helper.GetWindowLong(windowHandle, Win32Helper.GWL_EXSTYLE) &
                Win32Helper.WS_EX_NOACTIVATE) != 0;
    }

    private static void ApplyDesktopPinnedActivationStyle(IntPtr windowHandle)
    {
        SetWindowNoActivate(windowHandle, UsesDesktopPinnedMode());
    }

    private static void SetWindowNoActivate(IntPtr windowHandle, bool enabled)
    {
        if (windowHandle == IntPtr.Zero || !Win32Helper.IsWindow(windowHandle))
        {
            return;
        }

        int extendedStyle = Win32Helper.GetWindowLong(
            windowHandle,
            Win32Helper.GWL_EXSTYLE);
        int updatedStyle = enabled
            ? extendedStyle | Win32Helper.WS_EX_NOACTIVATE
            : extendedStyle & ~Win32Helper.WS_EX_NOACTIVATE;
        if (updatedStyle == extendedStyle)
        {
            return;
        }

        _ = Win32Helper.SetWindowLong(
            windowHandle,
            Win32Helper.GWL_EXSTYLE,
            updatedStyle);
        _ = Win32Helper.SetWindowPos(
            windowHandle,
            IntPtr.Zero,
            0,
            0,
            0,
            0,
            Win32Helper.SWP_NOMOVE |
                Win32Helper.SWP_NOSIZE |
                Win32Helper.SWP_NOZORDER |
                Win32Helper.SWP_NOACTIVATE |
                Win32Helper.SWP_NOOWNERZORDER |
                Win32Helper.SWP_FRAMECHANGED);
        App.LogVerbose(
            $"[WidgetLayer] no-activate style hwnd=0x{windowHandle.ToInt64():X} " +
            $"enabled={enabled}");
    }

    private static bool ShouldAttachRestingWindowToDesktop()
    {
        bool keepVisible = App.Current?.SettingsService?.Settings
            .KeepWidgetsVisibleOnShowDesktop ?? true;
        return RelativeLayerRestorePolicy.ShouldAttachToDesktop(
            UsesDesktopPinnedMode(),
            keepVisible);
    }

    private static bool TryAttachRestingWindowWithoutChangingLevel(
        IntPtr windowHandle)
    {
        return ShouldAttachRestingWindowToDesktop() &&
               TryAttachToDesktopIconLayer(
                   windowHandle,
                   placeAtBottom: false);
    }

    private static bool TryAttachToDesktopIconLayer(
        IntPtr windowHandle,
        bool placeAtBottom = true,
        bool showWindow = true)
    {
        if (windowHandle == IntPtr.Zero || !Win32Helper.IsWindow(windowHandle))
        {
            return false;
        }

        if (IsSessionRecoverySuspended || Volatile.Read(ref s_startupDesktopLayerAttachmentDeferred))
        {
            App.LogVerbose(
                $"[Startup] Desktop-layer attach deferred hwnd=0x{windowHandle.ToInt64():X}");
            return false;
        }

        IntPtr desktopIconView = FindDesktopIconView();
        if (desktopIconView == IntPtr.Zero)
        {
            App.Log("[WidgetLayer] DesktopPinned attach skipped: desktop icon view not found");
            return false;
        }

        // SHELLDLL_DefView is a WS_CHILD window, which cannot own a top-level
        // window. Use its validated Explorer root, never the child HWND.
        IntPtr desktopOwner = DesktopShellWindowRecovery.GetValidatedOwner(desktopIconView);
        if (desktopOwner == IntPtr.Zero) return false;

        lock (s_desktopLayerLock)
        {
            if (!s_desktopLayerAttachments.ContainsKey(windowHandle))
            {
                s_desktopLayerAttachments[windowHandle] = new DesktopLayerAttachment(
                    Win32Helper.GetWindowLongPtr(windowHandle, Win32Helper.GWLP_HWNDPARENT));
            }

            if (Win32Helper.GetWindowLongPtr(windowHandle, Win32Helper.GWLP_HWNDPARENT) != desktopOwner)
            {
                Win32Helper.SetLastError(0);
                _ = Win32Helper.SetWindowLongPtr(
                    windowHandle,
                    Win32Helper.GWLP_HWNDPARENT,
                    desktopOwner);
            }

            IntPtr actualOwner = Win32Helper.GetWindowLongPtr(windowHandle, Win32Helper.GWLP_HWNDPARENT);
            if (actualOwner != desktopOwner)
            {
                int error = Marshal.GetLastWin32Error();
                App.Log($"[WidgetLayer] DesktopPinned owner attach failed hwnd=0x{windowHandle.ToInt64():X} owner=0x{desktopOwner.ToInt64():X} actual=0x{actualOwner.ToInt64():X} error={error}");
                RestoreOriginalOwner(windowHandle);
                s_cachedDesktopIconView = IntPtr.Zero;
                return false;
            }

            if (placeAtBottom)
            {
                uint flags = Win32Helper.WindowLayerPositionFlags;
                if (showWindow)
                {
                    flags |= Win32Helper.SWP_SHOWWINDOW;
                }

                bool moved = UsesDesktopPinnedMode()
                    ? TryBringDesktopPeerBehindApplications(windowHandle, IsDesktopWidgetWindow, showWindow)
                    : Win32Helper.SetWindowPos(
                        windowHandle, Win32Helper.HWND_BOTTOM, 0, 0, 0, 0, flags);
                if (!moved)
                {
                    App.Log($"[WidgetLayer] Desktop placement failed hwnd=0x{windowHandle.ToInt64():X} error={Marshal.GetLastWin32Error()}");
                    RestoreOriginalOwner(windowHandle);
                    return false;
                }
            }

            App.LogVerbose(
                $"[WidgetLayer] Desktop owner attached hwnd=0x{windowHandle.ToInt64():X} " +
                $"owner=0x{desktopOwner.ToInt64():X} defView=0x{desktopIconView.ToInt64():X} bottom={placeAtBottom}");
            return true;
        }
    }

    private static void DetachFromDesktopIconLayerIfNeeded(IntPtr windowHandle)
    {
        lock (s_desktopLayerLock)
        {
            if (!s_desktopLayerAttachments.ContainsKey(windowHandle))
            {
                return;
            }

            RestoreOriginalOwner(windowHandle);
        }
    }

    private static void MoveToDynamicDesktopBottom(
        IntPtr windowHandle,
        bool showWindow = true)
    {
        // Try to attach to desktop icon layer to prevent Win+D from hiding the window
        // while maintaining dynamic layer behavior (can be raised on interaction)
        if (TryAttachToDesktopIconLayer(
                windowHandle,
                showWindow: showWindow))
        {
            return;
        }

        // Fallback: detach and demote directly to the desktop bottom.
        DetachFromDesktopIconLayerIfNeeded(windowHandle);
        if (UsesDesktopPinnedMode())
        {
            _ = TryBringDesktopPeerBehindApplications(windowHandle, IsDesktopWidgetWindow, showWindow);
        }
        else
        {
            Win32Helper.SetWindowToBottom(windowHandle);
        }
    }

    private static void RestoreOriginalOwner(IntPtr windowHandle)
    {
        if (!s_desktopLayerAttachments.TryGetValue(windowHandle, out var attachment))
        {
            return;
        }

        Win32Helper.SetLastError(0);
        _ = Win32Helper.SetWindowLongPtr(
            windowHandle,
            Win32Helper.GWLP_HWNDPARENT,
            Win32Helper.IsWindow(attachment.OriginalOwner) ? attachment.OriginalOwner : IntPtr.Zero);
        Win32Helper.SetWindowPos(
            windowHandle,
            Win32Helper.HWND_NOTOPMOST,
            0,
            0,
            0,
            0,
            Win32Helper.WindowLayerPositionFlags);
        s_desktopLayerAttachments.Remove(windowHandle);
        App.LogVerbose($"[WidgetLayer] DesktopPinned owner detached hwnd=0x{windowHandle.ToInt64():X}");
    }

    internal static IntPtr FindDesktopIconView()
    {
        if (IsSessionRecoverySuspended)
        {
            return IntPtr.Zero;
        }

        if (IsUsableDesktopIconView(s_cachedDesktopIconView))
        {
            return s_cachedDesktopIconView;
        }

        s_cachedDesktopIconView = IntPtr.Zero;

        // Only use a SHELLDLL_DefView that Explorer has already created. Never
        // force WorkerW creation here: doing so during login can race Explorer's
        // icon-layout restoration and leave the user's desktop icons rearranged.
        IntPtr existingDefView = IntPtr.Zero;
        Win32Helper.EnumWindows((hWnd, _) =>
        {
            IntPtr defView = FindDesktopIconViewChild(hWnd);
            if (IsUsableDesktopIconView(defView))
            {
                existingDefView = defView;
                return false; // stop enumeration
            }

            return true;
        }, IntPtr.Zero);

        if (existingDefView != IntPtr.Zero)
        {
            s_cachedDesktopIconView = existingDefView;
            return s_cachedDesktopIconView;
        }

        // The caller already has a bottom-of-desktop fallback and can retry on a
        // later layer operation after Explorer finishes initializing.
        return IntPtr.Zero;
    }

    private static bool IsUsableDesktopIconView(IntPtr view)
    {
        return view != IntPtr.Zero &&
            DesktopShellWindowRecovery.GetValidatedOwner(view) != IntPtr.Zero;
    }

    private static bool HasWindowClass(IntPtr window, string expected)
    {
        var className = new System.Text.StringBuilder(256);
        return window != IntPtr.Zero &&
            Win32Helper.GetClassName(window, className, className.Capacity) > 0 &&
            string.Equals(className.ToString(), expected, StringComparison.Ordinal);
    }

    private static IntPtr FindHighestPeer(IReadOnlyCollection<IntPtr> handles)
    {
        var peers = handles.ToHashSet();
        IntPtr current = Win32Helper.GetWindow(handles.First(), Win32Helper.GW_HWNDFIRST);
        while (current != IntPtr.Zero)
        {
            if (peers.Contains(current))
            {
                return current;
            }

            current = Win32Helper.GetWindow(current, Win32Helper.GW_HWNDNEXT);
        }

        return handles.FirstOrDefault();
    }

    private static IntPtr FindDesktopIconViewChild(IntPtr windowHandle)
    {
        return Win32Helper.FindWindowEx(windowHandle, IntPtr.Zero, "SHELLDLL_DefView", null);
    }

    private static bool IsDesktopShellWindow(IntPtr windowHandle)
    {
        IntPtr current = windowHandle;
        while (current != IntPtr.Zero)
        {
            var className = new System.Text.StringBuilder(256);
            int length = Win32Helper.GetClassName(
                current,
                className,
                className.Capacity);
            if (length > 0 &&
                (string.Equals(className.ToString(), "Progman", StringComparison.Ordinal) ||
                 string.Equals(className.ToString(), "WorkerW", StringComparison.Ordinal) ||
                 string.Equals(className.ToString(), "SHELLDLL_DefView", StringComparison.Ordinal)))
            {
                return true;
            }

            current = Win32Helper.GetParent(current);
        }

        return false;
    }

    private sealed record DesktopLayerAttachment(IntPtr OriginalOwner);
}

internal sealed class DesktopIconViewStabilityTracker
{
    private readonly int _requiredStableSamples;
    private IntPtr _candidate;
    private int _stableSampleCount;

    public DesktopIconViewStabilityTracker(int requiredStableSamples)
    {
        _requiredStableSamples = Math.Max(1, requiredStableSamples);
    }

    public bool Observe(IntPtr desktopIconView)
    {
        if (desktopIconView == IntPtr.Zero)
        {
            _candidate = IntPtr.Zero;
            _stableSampleCount = 0;
            return false;
        }

        if (desktopIconView != _candidate)
        {
            _candidate = desktopIconView;
            _stableSampleCount = 1;
        }
        else
        {
            _stableSampleCount++;
        }

        return _stableSampleCount >= _requiredStableSamples;
    }
}

internal static class WidgetLayerPointerActivationPolicy
{
    public static bool ShouldSuppressFocus(
        bool usesDesktopPinnedMode,
        bool isKeyboardInput,
        bool pointerDriven,
        bool windowIsForeground) =>
        usesDesktopPinnedMode && !isKeyboardInput && pointerDriven && !windowIsForeground;

    public static bool ShouldSuppress(bool usesDesktopPinnedMode)
    {
        return usesDesktopPinnedMode;
    }

    public static bool ShouldPreserveActivatingClick(
        bool usesQuickRevealMode,
        bool widgetsRaisedFromTray)
    {
        return usesQuickRevealMode && widgetsRaisedFromTray;
    }
}
