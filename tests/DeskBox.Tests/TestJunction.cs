using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace DeskBox.Tests;

/// <summary>
/// Creates directory junctions through the native reparse-point path that
/// <c>mklink /J</c> itself takes (FSCTL_SET_REPARSE_POINT), so tests never
/// shell out to cmd.exe with interpolated paths.
/// </summary>
internal static class TestJunction
{
    private const uint GenericWrite = 0x4000_0000;
    private const uint FileShareReadWriteDelete = 0x7;
    private const uint OpenExisting = 3;
    private const int FileFlagBackupSemantics = 0x0200_0000;
    private const int FileFlagOpenReparsePoint = 0x0020_0000;
    private const uint FsctlSetReparsePoint = 0x0009_00A4;
    private const uint IoReparseTagMountPoint = 0xA000_0003;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern SafeFileHandle CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        IntPtr lpInBuffer,
        uint nInBufferSize,
        IntPtr lpOutBuffer,
        uint nOutBufferSize,
        out uint lpBytesReturned,
        IntPtr lpOverlapped);

    internal static bool TryCreate(string junctionPath, string targetPath)
    {
        try
        {
            if (Directory.Exists(junctionPath))
            {
                // mklink parity: an existing entry is never overwritten.
                return false;
            }

            Directory.CreateDirectory(junctionPath);
            using SafeFileHandle handle = CreateFileW(
                junctionPath,
                GenericWrite,
                FileShareReadWriteDelete,
                IntPtr.Zero,
                OpenExisting,
                FileFlagBackupSemantics | FileFlagOpenReparsePoint,
                IntPtr.Zero);
            if (handle.IsInvalid)
            {
                return false;
            }

            // Mount-point reparse buffer: an 8-byte header (tag, data
            // length, reserved) followed by the mount-point block (name
            // offsets and byte lengths) and the two UTF-16 name blobs.
            // Lengths exclude the trailing NUL; both names carry one
            // anyway, matching what mklink writes.
            string substituteName = @"\??\" + Path.GetFullPath(targetPath);
            byte[] substituteBytes = Encoding.Unicode.GetBytes(substituteName + "\0");
            byte[] printBytes = Encoding.Unicode.GetBytes(targetPath + "\0");
            int dataLength = 8 + substituteBytes.Length + printBytes.Length;
            byte[] buffer = new byte[8 + dataLength];
            BitConverter.GetBytes(IoReparseTagMountPoint).CopyTo(buffer, 0);
            BitConverter.GetBytes((ushort)dataLength).CopyTo(buffer, 4);
            BitConverter.GetBytes((ushort)0).CopyTo(buffer, 8);
            BitConverter.GetBytes((ushort)(substituteBytes.Length - 2)).CopyTo(buffer, 10);
            BitConverter.GetBytes((ushort)substituteBytes.Length).CopyTo(buffer, 12);
            BitConverter.GetBytes((ushort)(printBytes.Length - 2)).CopyTo(buffer, 14);
            substituteBytes.CopyTo(buffer, 16);
            printBytes.CopyTo(buffer, 16 + substituteBytes.Length);

            IntPtr nativeBuffer = Marshal.AllocHGlobal(buffer.Length);
            try
            {
                Marshal.Copy(buffer, 0, nativeBuffer, buffer.Length);
                return DeviceIoControl(
                           handle,
                           FsctlSetReparsePoint,
                           nativeBuffer,
                           (uint)buffer.Length,
                           IntPtr.Zero,
                           0,
                           out _,
                           IntPtr.Zero)
                       && Directory.Exists(junctionPath);
            }
            finally
            {
                Marshal.FreeHGlobal(nativeBuffer);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
        {
            return false;
        }
    }
}
