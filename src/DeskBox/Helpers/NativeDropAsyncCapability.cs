using System.Runtime.InteropServices;

namespace DeskBox.Helpers;

/// <summary>
/// Raw vtable accessor for the shell's asynchronous-drop negotiation
/// interface (shldisp.h, the renamed IAsyncOperation). The drop target
/// queries it on the drag source's data object, asks whether the source
/// supports async extraction, and — before returning from
/// IDropTarget::Drop — calls StartOperation so the source knows the target
/// will keep extracting on a background thread after DoDragDrop returns.
/// Built with explicit function pointers like <see cref="NativeOleDataObject"/>
/// so no runtime callable wrapper is ever created (NativeAOT safe).
/// Vtable order verified against Windows SDK 10.0.26100.0 um/shldisp.h:8482
/// (MIDL declaration order): SetAsyncMode, GetAsyncMode, StartOperation,
/// InOperation, EndOperation. The IID and slot numbers are pinned by
/// CallbackDeferralContractTests.
/// </summary>
internal readonly unsafe struct NativeDropAsyncCapability
{
    public static readonly Guid InterfaceId = new(
        "3D8B0590-F691-11d2-8EA9-006097DF5BD4");

    private const int QueryInterfaceVtableSlot = 0;
    private const int ReleaseVtableSlot = 2;
    private const int GetAsyncModeVtableSlot = 4;
    private const int StartOperationVtableSlot = 5;
    private const int EndOperationVtableSlot = 7;

    private readonly nint _pointer;

    private NativeDropAsyncCapability(nint pointer)
    {
        _pointer = pointer;
    }

    /// <summary>
    /// Queries the async capability off a live data-object pointer. The
    /// returned capability holds a reference of its own; callers must
    /// <see cref="Release"/> it.
    /// </summary>
    public static bool TryQuery(nint dataObject, out NativeDropAsyncCapability capability)
    {
        capability = default;
        if (dataObject == 0)
        {
            return false;
        }

        nint* vtable = *(nint**)dataObject;
        if (vtable == null || vtable[QueryInterfaceVtableSlot] == 0)
        {
            return false;
        }

        var queryInterface = (delegate* unmanaged[Stdcall]<
            nint,
            Guid*,
            void**,
            int>)vtable[QueryInterfaceVtableSlot];

        Guid iid = InterfaceId;
        nint resolved = 0;
        int hr;
        {
            Guid* iidPointer = &iid;
            hr = queryInterface(dataObject, iidPointer, (void**)&resolved);
        }

        if (hr < 0 || resolved == 0)
        {
            return false;
        }

        capability = new NativeDropAsyncCapability(resolved);
        return true;
    }

    public bool GetAsyncMode()
    {
        var getAsyncMode = (delegate* unmanaged[Stdcall]<nint, int*, int>)
            GetVtableEntry(GetAsyncModeVtableSlot);

        int isAsync = 0;
        int hr = getAsyncMode(_pointer, &isAsync);
        return hr >= 0 && isAsync != 0;
    }

    public int StartOperation()
    {
        var startOperation = (delegate* unmanaged[Stdcall]<nint, nint, int>)
            GetVtableEntry(StartOperationVtableSlot);

        return startOperation(_pointer, 0);
    }

    /// <summary>Signals the end of the asynchronous extraction to the source.
    /// The final effect is reported here and through the performed-effect
    /// receipt written on the same worker thread.</summary>
    public int EndOperation(int operationResult, uint finalEffect)
    {
        var endOperation = (delegate* unmanaged[Stdcall]<nint, int, nint, uint, int>)
            GetVtableEntry(EndOperationVtableSlot);

        return endOperation(_pointer, operationResult, 0, finalEffect);
    }

    public uint Release()
    {
        var release = (delegate* unmanaged[Stdcall]<nint, uint>)
            GetVtableEntry(ReleaseVtableSlot);

        return release(_pointer);
    }

    private nint GetVtableEntry(int slot)
    {
        nint* vtable = *(nint**)_pointer;
        if (vtable == null || vtable[slot] == 0)
        {
            throw new InvalidOperationException(
                $"The async-capability vtable does not contain slot {slot}.");
        }

        return vtable[slot];
    }
}
