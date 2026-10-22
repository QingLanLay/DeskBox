using System.Runtime.InteropServices;
using DeskBox.Platform;

namespace DeskBox.Helpers;

/// <summary>Prevents a message-only UI host from becoming a visible desktop window.</summary>
internal sealed class HiddenMessageWindowGuard : IDisposable
{
    private static readonly UIntPtr SubclassId = new(0xDBA8);
    private const uint WmWindowPosChanging = 0x0046;
    private const uint WmNcDestroy = 0x0082;
    private readonly IntPtr _window;
    private readonly Win32Helper.SubclassProc _callback;
    private bool _installed;

    internal HiddenMessageWindowGuard(IntPtr window)
    {
        _window = window;
        _callback = WindowProc;
        _installed = Win32Helper.SetWindowSubclass(window, _callback, SubclassId, UIntPtr.Zero);
        int style = Win32Helper.GetWindowLong(window, Win32Helper.GWL_STYLE);
        _ = Win32Helper.SetWindowLong(window, Win32Helper.GWL_STYLE,
            style & ~(Win32Helper.WS_CAPTION | Win32Helper.WS_BORDER |
                Win32Helper.WS_DLGFRAME | Win32Helper.WS_THICKFRAME));
        int extendedStyle = Win32Helper.GetWindowLong(window, Win32Helper.GWL_EXSTYLE);
        _ = Win32Helper.SetWindowLong(window, Win32Helper.GWL_EXSTYLE,
            extendedStyle | Win32Helper.WS_EX_TOOLWINDOW | Win32Helper.WS_EX_NOACTIVATE);
        EnsureHidden();
    }

    internal void EnsureHidden()
    {
        Win32Helper.ShowWindow(_window, Win32Helper.SW_HIDE);
        _ = Win32Helper.SetWindowPos(_window, IntPtr.Zero, -32000, -32000, 1, 1,
            Win32Helper.SWP_NOZORDER | Win32Helper.SWP_NOACTIVATE |
            Win32Helper.SWP_NOOWNERZORDER | Win32Helper.SWP_FRAMECHANGED);
    }

    private IntPtr WindowProc(IntPtr window, uint message, UIntPtr wParam,
        IntPtr lParam, UIntPtr subclassId, UIntPtr referenceData)
    {
        if (message == WmWindowPosChanging && lParam != IntPtr.Zero)
        {
            var position = Marshal.PtrToStructure<WindowPosition>(lParam);
            position.Flags &= ~Win32Helper.SWP_SHOWWINDOW;
            position.X = position.Y = -32000;
            position.Width = position.Height = 1;
            position.Flags &= ~(Win32Helper.SWP_NOMOVE | Win32Helper.SWP_NOSIZE);
            Marshal.StructureToPtr(position, lParam, false);
        }
        else if (message == WmNcDestroy)
        {
            Dispose();
        }
        return Win32Helper.DefSubclassProc(window, message, wParam, lParam);
    }

    public void Dispose()
    {
        if (_installed)
        {
            Win32Helper.RemoveWindowSubclass(_window, _callback, SubclassId);
            _installed = false;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPosition
    {
        public IntPtr Window;
        public IntPtr InsertAfter;
        public int X;
        public int Y;
        public int Width;
        public int Height;
        public uint Flags;
    }
}
