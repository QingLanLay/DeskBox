using System.Runtime.InteropServices;

namespace DeskBox.Platform;

/// <summary>
/// Direct COM vtable slot invocation. Classic ComImport RCW marshaling does
/// not exist on NativeAOT — the call dies at runtime with "COM Interop
/// requires ComWrapper instance registered for marshalling" (proven by the
/// 1.5.9 retail acceptance on 2026-10-20: the Open With submenu enumerated
/// nothing on every machine that ran the AOT installer, while Debug builds
/// stayed green because JIT ships the RCW machinery). Resolving the slot's
/// function pointer and invoking it through an
/// <see cref="UnmanagedFunctionPointerAttribute"/> delegate behaves
/// identically on JIT and AOT, so callers keep one code path instead of a
/// runtime fork. Slot indices count from the interface's first method after
/// IUnknown (slots 0-2) and must be verified against the Windows SDK header
/// at every call site.
/// </summary>
internal static class ComVtable
{
    /// <summary>
    /// Resolves the delegate for one vtable slot. The returned delegate
    /// carries the interface pointer as its first argument.
    /// </summary>
    internal static T Slot<T>(IntPtr comObject, int slot) where T : class
    {
        IntPtr vtable = Marshal.ReadIntPtr(comObject);
        IntPtr proc = Marshal.ReadIntPtr(vtable, slot * IntPtr.Size);
        return Marshal.GetDelegateForFunctionPointer<T>(proc);
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int QueryInterfaceInvoker(
        IntPtr self, ref Guid iid, out IntPtr ppv);

    /// <summary>
    /// IUnknown::QueryInterface (slot 0) through a raw pointer — the
    /// interface-hop counterpart of <see cref="Slot{T}"/> for callers that
    /// hold a bare COM pointer without any ComImport declaration.
    /// Release the returned pointer with <see cref="Marshal.Release"/>.
    /// </summary>
    internal static int QueryInterface(
        IntPtr comObject, ref Guid iid, out IntPtr ppv)
    {
        return Slot<QueryInterfaceInvoker>(comObject, 0)(comObject, ref iid, out ppv);
    }

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(
        ref Guid clsid,
        IntPtr punkOuter,
        uint dwClsContext,
        ref Guid riid,
        out IntPtr ppv);

    /// <summary>
    /// CoCreateInstance with an in-process server, returning the raw
    /// interface pointer. Release it with <see cref="Marshal.Release"/>.
    /// </summary>
    internal static bool TryCreateInProcessInstance(
        ref Guid clsid,
        ref Guid iid,
        out IntPtr instance)
    {
        int hr = CoCreateInstance(
            ref clsid,
            IntPtr.Zero,
            ClsctxInprocServer,
            ref iid,
            out instance);
        return hr >= 0 && instance != IntPtr.Zero;
    }

    private const uint ClsctxInprocServer = 0x1;
}
