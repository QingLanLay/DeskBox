namespace DeskBox.Tests;

/// <summary>
/// Mirror guard for the 2026-10-20 retail incident (1.5.9 acceptance): the
/// Open With submenu enumerated nothing on every NativeAOT install because
/// classic ComImport RCW marshaling does not exist on NativeAOT —
/// "COM Interop requires ComWrapper instance registered for marshalling" —
/// while Debug builds stayed green because JIT ships the RCW machinery, and
/// the IL2050 suppression had silenced the only analyzer that objected.
/// Shell COM surfaces must call vtable slots through
/// <see cref="DeskBox.Platform.ComVtable"/> instead. These pins keep that
/// contract visible: the banned call stays out of the app assembly, and the
/// vtable helper stays in.
/// </summary>
public sealed class AotComInteropContractTests
{
    [Fact]
    public void AppAssembly_NeverCreatesRcwFromRawComPointers()
    {
        string root = TestPaths.FromRepository("src/DeskBox");
        string[] offenders = Directory.EnumerateFiles(
            root,
            "*.cs",
            SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Where(path => File.ReadAllText(path).Contains(
                "Marshal.GetObjectForIUnknown",
                StringComparison.Ordinal))
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            "Marshal.GetObjectForIUnknown creates a classic RCW, which " +
            "NativeAOT rejects at runtime. Use DeskBox.Platform.ComVtable " +
            "slot delegates instead. Offenders: " +
            string.Join(", ", offenders.Select(Path.GetFileName)));
    }

    [Fact]
    public void ComVtable_HelperStaysInTheAssembly()
    {
        string helper = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Platform/ComVtable.cs"));

        Assert.Contains(
            "Marshal.GetDelegateForFunctionPointer",
            helper,
            StringComparison.Ordinal);
        Assert.Contains(
            "slot * IntPtr.Size",
            helper,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ShellOpenWithInterop_CallsSlotsNotMarshaling()
    {
        string interop = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Platform/ShellOpenWithInterop.cs"));

        Assert.Contains(
            "ComVtable.Slot",
            interop,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "UnconditionalSuppressMessage",
            interop,
            StringComparison.Ordinal);
    }
}
