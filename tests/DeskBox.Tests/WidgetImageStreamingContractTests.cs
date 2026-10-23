namespace DeskBox.Tests;

/// <summary>
/// Pins the widget asset streaming contract: per-widget backgrounds,
/// title icons, and the shared unified/panorama backgrounds load through
/// streams so nothing enters the framework's un-evictable URI-keyed
/// image cache — the fixed asset file names are rewritten in place on
/// every re-pick, so a URI source would keep serving the session's
/// first decode (the "cannot switch the custom background" bug family).
/// The shared files additionally dedupe through one process-wide cache,
/// replacing the cross-window dedupe the URI cache used to provide.
/// </summary>
public sealed class WidgetImageStreamingContractTests
{
    [Fact]
    public void MutableAssetFactories_NeverLoadByUri()
    {
        string background = ReadRepositoryFile(
            "src/DeskBox/Helpers/WidgetBackgroundImageSourceFactory.cs");
        string icon = ReadRepositoryFile(
            "src/DeskBox/Helpers/WidgetTitleIconImageSourceFactory.cs");

        Assert.Contains(
            "WidgetImageStreamLoader.Fill(",
            background,
            StringComparison.Ordinal);
        Assert.Contains(
            "WidgetImageStreamLoader.Fill(",
            icon,
            StringComparison.Ordinal);
        Assert.DoesNotContain("UriSource", background, StringComparison.Ordinal);
        Assert.DoesNotContain("UriSource", icon, StringComparison.Ordinal);
    }

    [Fact]
    public void StreamLoader_HasNoUriFallbackForMutableAssets()
    {
        // Glance may fall back to URI (content-hashed immutable files);
        // widget assets rewrite one fixed name, so a URI fallback would
        // resurrect exactly the stale decode this loader exists to avoid.
        string loader = ReadRepositoryFile(
            "src/DeskBox/Services/WidgetImageStreamLoader.cs");

        Assert.Contains("SetSourceAsync(stream)", loader, StringComparison.Ordinal);
        Assert.DoesNotContain("UriSource", loader, StringComparison.Ordinal);
        // Failures must be reported, not swallowed: callers degrade to
        // their fallback layer instead of keeping a blank surface.
        Assert.Contains("settled?.Invoke(succeeded)", loader, StringComparison.Ordinal);
    }

    [Fact]
    public void SharedBackgrounds_DedupeAcrossWindows_AndPinFailuresUntilContentChanges()
    {
        string cache = ReadRepositoryFile(
            "src/DeskBox/Services/WidgetSharedImageSourceCache.cs");

        // Content-change detection: the cache must not hand out the
        // previous pick's decode after the file was rewritten.
        Assert.Contains("LastWriteTimeUtc", cache, StringComparison.Ordinal);
        // A re-pick racing an in-flight decode must not have the older
        // fill's failure evict the newer entry.
        Assert.Contains(
            "ReferenceEquals(current, entry)",
            cache,
            StringComparison.Ordinal);
    }

    [Fact]
    public void WindowResolvers_DegradeOnLoadFailureInsteadOfLooping()
    {
        string window = ReadRepositoryFile(
            "src/DeskBox/Views/WidgetWindowBase.TitleIconCustomization.cs");

        Assert.Contains("_customBackgroundLoadFailedPath", window, StringComparison.Ordinal);
        Assert.Contains("_customTitleIconLoadFailedPath", window, StringComparison.Ordinal);
        // The unified background routes through the shared cache; the
        // panorama positions every window's slice off one shared decode
        // through the cache's settle callbacks, never an event
        // subscription on the shared instance.
        Assert.Contains("WidgetSharedImageSourceCache.TryGet", window, StringComparison.Ordinal);
        Assert.Contains(
            "loadSucceeded: () => DispatcherQueue.TryEnqueue(UpdateCustomPanoramaViewport)",
            window,
            StringComparison.Ordinal);
    }

    [Fact]
    public void AssetCopy_SwapsAtomicallyForConcurrentSnapshots()
    {
        string store = ReadRepositoryFile(
            "src/DeskBox/Services/WidgetTitleIconAssetStore.cs");

        Assert.Contains(
            "File.Move(tempPath, targetPath, overwrite: true)",
            store,
            StringComparison.Ordinal);
        // The temp must stay .tmp-suffixed: the backup filter excludes
        // exactly that suffix, so a raced snapshot never captures it.
        Assert.Contains("targetPath + \".tmp\"", store, StringComparison.Ordinal);
    }

    private static string ReadRepositoryFile(string relativePath) =>
        File.ReadAllText(TestPaths.FromRepository(relativePath));
}
