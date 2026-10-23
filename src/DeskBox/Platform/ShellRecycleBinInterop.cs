using System.Runtime.InteropServices;

namespace DeskBox.Platform;

/// <summary>
/// Recycle-bin verbs for the native-aligned namespace shortcut menu.
/// Entry points are pinned with ExactSpelling + EntryPoint so a resolver
/// mismatch surfaces as a compile-time constant here rather than as a
/// first-call EntryPointNotFound that unit tests can never see (the
/// entry-point probe test in DeskBox.Tests guards the export side).
/// </summary>
internal static class ShellRecycleBinInterop
{
    [StructLayout(LayoutKind.Sequential)]
    private struct SHQUERYRBINFO
    {
        public uint cbSize;
        public long totalSize;
        public long totalItems;
    }

    // A null root aggregates every drive's bin — what the desktop icon's
    // native menu reports and empties.
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true,
        EntryPoint = "SHQueryRecycleBinW")]
    private static extern int SHQueryRecycleBin(
        string? rootPath,
        ref SHQUERYRBINFO queryInfo);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true,
        EntryPoint = "SHEmptyRecycleBinW")]
    private static extern int SHEmptyRecycleBin(
        IntPtr ownerWindow,
        string? rootPath,
        uint flags);

    /// <summary>
    /// Total item count across all bins; false when the aggregate query
    /// itself fails (callers then keep the menu row enabled rather than
    /// graying a working verb).
    /// </summary>
    internal static bool TryQueryTotalItemCount(out long totalItems)
    {
        totalItems = 0;
        try
        {
            var info = new SHQUERYRBINFO
            {
                cbSize = (uint)Marshal.SizeOf<SHQUERYRBINFO>()
            };
            if (SHQueryRecycleBin(null, ref info) < 0)
            {
                return false;
            }

            totalItems = info.totalItems;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Empties every bin with flags 0, so the system shows its own
    /// confirmation and progress dialogs — the same behavior as the native
    /// desktop verb.
    /// </summary>
    internal static bool TryEmptyAllBins(IntPtr ownerWindow)
    {
        try
        {
            return SHEmptyRecycleBin(ownerWindow, null, 0) >= 0;
        }
        catch
        {
            return false;
        }
    }
}
