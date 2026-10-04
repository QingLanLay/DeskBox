namespace DeskBox.Platform;

/// <summary>Clips frameless windows on systems without native DWM corners.</summary>
internal sealed class WindowCornerRegion
{
    private (int Width, int Height, int Diameter)? _appliedGeometry;
    private bool _isApplying;

    public bool Apply(IntPtr hwnd, double logicalRadius, bool preserveAntialiasing = false)
    {
        // SetWindowRgn sends window-position messages synchronously.
        if (_isApplying)
        {
            return true;
        }

        if (hwnd == IntPtr.Zero || !Win32Helper.GetWindowRect(hwnd, out var bounds))
        {
            return false;
        }

        int width = bounds.Right - bounds.Left;
        int height = bounds.Bottom - bounds.Top;
        if (width <= 0 || height <= 0)
        {
            return false;
        }

        double scale = Win32Helper.GetDpiScaleForWindow(hwnd, null);
        // A transparent host draws the visible edge with XAML antialiasing.
        // Keep the binary input region outside that one-pixel coverage fringe.
        double physicalRadius = Math.Max(0, logicalRadius * scale - (preserveAntialiasing ? 1 : 0));
        int diameter = (int)Math.Clamp(
            Math.Round(physicalRadius * 2),
            0,
            Math.Min(width, height));
        var geometry = (width, height, diameter);
        if (_appliedGeometry == geometry || (diameter == 0 && _appliedGeometry is null))
        {
            return true;
        }

        IntPtr region = diameter == 0
            ? IntPtr.Zero
            : Win32Helper.CreateRoundRectRgn(0, 0, width + 1, height + 1, diameter, diameter);
        if (diameter > 0 && region == IntPtr.Zero)
        {
            return false;
        }

        _isApplying = true;
        try
        {
            if (Win32Helper.SetWindowRgn(hwnd, region, redraw: true) == 0)
            {
                if (region != IntPtr.Zero)
                {
                    Win32Helper.DeleteObject(region);
                }
                return false;
            }

            // Windows owns the HRGN after a successful SetWindowRgn call.
            _appliedGeometry = diameter == 0 ? null : geometry;
            return true;
        }
        finally
        {
            _isApplying = false;
        }
    }
}
