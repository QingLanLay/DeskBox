using System.Runtime.InteropServices;
using DeskBox.Helpers;
using DeskBox.Models;

namespace DeskBox.Tests;

/// <summary>
/// Namespace-shortcut menu contracts: the pure parsing-name → kind mapping
/// behind the target-aware item menu, plus the entry-point probes for the
/// new shell32 recycle-bin P/Invoke family (resolution happens on first
/// call, which plain unit tests can never see — the 1.5.9 lesson).
/// </summary>
public sealed class NamespaceShortcutMenuTests
{
    [Theory]
    [InlineData("::{20D04FE0-3AEA-1069-A2D8-08002B30309D}",
        NamespaceShortcutKind.ThisPC)]
    [InlineData("::{645FF040-5081-101B-9F08-00AA002F954E}",
        NamespaceShortcutKind.RecycleBin)]
    [InlineData("::{26EE0668-A00A-44D7-9371-BEB064C98683}",
        NamespaceShortcutKind.ControlPanel)]
    // Control-panel category pages extend past the root CLSID.
    [InlineData("::{26EE0668-A00A-44D7-9371-BEB064C98683}\\::{D20EA4E1-3957-11D2-A40B-0C5020524153}",
        NamespaceShortcutKind.ControlPanel)]
    // The legacy control-panel CLSID still resolves in modern Windows.
    [InlineData("::{21EC2020-3AEA-1069-A2DD-08002B30309D}",
        NamespaceShortcutKind.ControlPanel)]
    [InlineData("::{F02C1AA0-4AE4-4BD1-9BF0-8BAA2DBF44C5}",
        NamespaceShortcutKind.OtherNamespace)]
    [InlineData("C:\\Program Files\\app.exe", NamespaceShortcutKind.None)]
    [InlineData("shell:ControlPanelFolder", NamespaceShortcutKind.None)]
    [InlineData("", NamespaceShortcutKind.None)]
    [InlineData("   ", NamespaceShortcutKind.None)]
    public void ClassifyParsingName_MapsCuratedNamespaces(
        string parsingName,
        NamespaceShortcutKind expected)
    {
        Assert.Equal(
            expected,
            NamespaceShortcutHelper.ClassifyParsingName(parsingName));
    }

    [Fact]
    public void ClassifyParsingName_MatchesGuidCaseInsensitively()
    {
        Assert.Equal(
            NamespaceShortcutKind.RecycleBin,
            NamespaceShortcutHelper.ClassifyParsingName(
                "::{645ff040-5081-101b-9f08-00aa002f954e}"));
    }

    // ---- entry-point probes for the new shell32 P/Invoke family ----

    // Classic DllImport on purpose: the test project is not compiled with
    // unsafe blocks for LibraryImport source generation, and the Platform
    // interop ratchet only scans src/DeskBox.
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibrary(string fileName);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern IntPtr GetProcAddress(IntPtr module, string procName);

    [StructLayout(LayoutKind.Sequential)]
    private struct ShQueryRbInfo
    {
        public uint cbSize;
        public long totalSize;
        public long totalItems;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true,
        EntryPoint = "SHQueryRecycleBinW")]
    private static extern int SHQueryRecycleBin(
        string? rootPath,
        ref ShQueryRbInfo queryInfo);

    [Fact]
    public void RecycleBinExports_ResolveThroughGetProcAddress()
    {
        IntPtr shell32 = LoadLibrary("shell32.dll");
        Assert.NotEqual(IntPtr.Zero, shell32);
        Assert.NotEqual(
            IntPtr.Zero,
            GetProcAddress(shell32, "SHQueryRecycleBinW"));
        Assert.NotEqual(
            IntPtr.Zero,
            GetProcAddress(shell32, "SHEmptyRecycleBinW"));
    }

    [Fact]
    public void RecycleBinQuery_MarshalsOnARealCall()
    {
        // x64 pin: cbSize + padding + two 64-bit counters.
        Assert.Equal(24, Marshal.SizeOf<ShQueryRbInfo>());

        var info = new ShQueryRbInfo
        {
            cbSize = (uint)Marshal.SizeOf<ShQueryRbInfo>()
        };
        int hr = SHQueryRecycleBin(null, ref info);
        Assert.True(hr >= 0, $"aggregate query failed hr=0x{hr:X8}");
    }

    [Fact]
    public void LinkVtableReader_PinsSlotsAndInterfaceIds()
    {
        // The vtable reader's slot and IID constants must be re-verified
        // against the SDK whenever the file changes; pin the literals so a
        // careless edit fails here first.
        string source = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Platform/ShellLinkVtableInterop.cs"));
        Assert.Contains(
            "new(\"00021401-0000-0000-C000-000000000046\")",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "new(\"000214F9-0000-0000-C000-000000000046\")",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "new(\"0000010B-0000-0000-C000-000000000046\")",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "new(\"43826d1e-e718-42ee-bc55-a1e261c37bfe\")",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "private const int PersistFileLoadSlot = 5;",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "private const int ShellLinkGetIdListSlot = 4;",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "private const int ShellItemGetDisplayNameSlot = 5;",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "private const int SigdnDesktopAbsoluteParsing = unchecked((int)0x80028000);",
            source,
            StringComparison.Ordinal);
    }
}
