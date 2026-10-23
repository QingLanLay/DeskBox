using DeskBox.Helpers;

namespace DeskBox.Tests;

/// <summary>
/// Live interop smoke for the Open With association path. The 2026-10-16
/// crash (0xc0000005 / CLR internal error at MngdSafeArrayMarshaler) came
/// from the IEnumAssocHandlers.Next array parameter defaulting to SAFEARRAY
/// marshaling: a regression here kills the test host outright, which is
/// exactly the alarm this test exists to raise.
/// </summary>
public sealed class ShellOpenWithInteropTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "deskbox-openwith-tests-" +
            Guid.NewGuid().ToString("N"));

    public ShellOpenWithInteropTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void EnumerateRecommendedApps_OnStaThread_CompletesWithoutCrashing()
    {
        string target = Path.Combine(_root, "sample.txt");
        File.WriteAllText(target, "open-with smoke");

        Exception? captured = null;
        IReadOnlyList<OpenWithHandlerEntry>? result = null;
        var thread = new Thread(() =>
        {
            try
            {
                result = ShellOpenWithHelper.GetRecommendedApps(target);
            }
            catch (Exception ex)
            {
                captured = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        Assert.Null(captured);
        Assert.NotNull(result);
        foreach (OpenWithHandlerEntry entry in result!)
        {
            Assert.False(string.IsNullOrWhiteSpace(entry.UiName));
        }
    }
}
