using DeskBox.Contracts;
using DeskBox.Models;
using DeskBox.Services;
using DeskBox.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace DeskBox.Controls.WidgetContents;

public sealed class GlanceWidgetContentAdapter :
    WidgetContentAdapterBase,
    IWidgetResponsiveLayoutContent
{
    private string? _compactBackgroundPath;
    private int _compactBackgroundDecodeWidth;
    private ImageSource? _compactBackground;
    private long _compactBackgroundEstimatedBytes;

    public GlanceWidgetContentAdapter(
        WidgetConfig config,
        LocalizationService localizationService,
        GlanceWidgetStore? store = null,
        GlanceImageService? imageService = null,
        ICalendarPresentationSource? calendarSource = null,
        Func<GlanceWidgetViewModel, FrameworkElement>? viewFactory = null,
        SettingsService? settingsService = null)
        : this(
            config,
            new GlanceWidgetViewModel(
                config,
                localizationService,
                store,
                imageService,
                calendarSource,
                settingsService: settingsService),
            viewFactory)
    {
    }

    private GlanceWidgetContentAdapter(
        WidgetConfig config,
        GlanceWidgetViewModel viewModel,
        Func<GlanceWidgetViewModel, FrameworkElement>? viewFactory)
        : base(
            config,
            () => (viewFactory ?? (vm => new GlanceWidgetContent(vm)))(viewModel))
    {
        ViewModel = viewModel;
    }

    public GlanceWidgetViewModel ViewModel { get; }

    public override Task InitializeAsync() => ViewModel.InitializeAsync();

    public override Task RefreshAsync() => ViewModel.RefreshAsync();

    public override void ApplyAppearance() => ViewModel.ApplyAppearance();

    public override void OnActivated() => ViewModel.OnActivated();

    public override void OnDeactivated() => ViewModel.OnDeactivated();

    public override void OnWindowVisibilityChanged(bool visible) => ViewModel.OnWindowVisibilityChanged(visible);

    public override void OnWindowRevealCompleted() => ViewModel.OnWindowRevealCompleted();

    public override void OnCompactStateChanged(bool collapsed) => ViewModel.OnCompactStateChanged(collapsed);

    public void BeginResponsiveLayoutTransition(double targetContentWidth, double targetContentHeight, bool isCollapsing)
        => ViewModel.UpdateAvailableSize(targetContentWidth, targetContentHeight);

    public void CompleteResponsiveLayoutTransition(double finalContentWidth, double finalContentHeight)
        => ViewModel.UpdateAvailableSize(finalContentWidth, finalContentHeight);

    public void CancelResponsiveLayoutTransition()
    {
    }

    public ImageSource? GetCompactBackgroundImage(int decodePixelWidth)
    {
        string? path = ViewModel.CurrentImagePath;
        if (string.Equals(path, _compactBackgroundPath, StringComparison.OrdinalIgnoreCase) &&
            _compactBackgroundDecodeWidth == decodePixelWidth)
        {
            return _compactBackground;
        }

        _compactBackgroundPath = path;
        _compactBackgroundDecodeWidth = decodePixelWidth;
        _compactBackground = null;
        SetCompactBackgroundEstimatedBytes(0);
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        try
        {
            var bitmap = new BitmapImage
            {
                DecodePixelType = DecodePixelType.Physical,
                DecodePixelWidth = Math.Max(1, decodePixelWidth)
            };
            // Streamed like the expanded surface: URI loads would accumulate
            // one un-evictable framework cache entry per rotation step the
            // capsule has shown. The bitmap fills in when the decode
            // completes, matching the URI path's own async first-load.
            _ = GlanceImageStreamLoader.LoadAsync(bitmap, path);
            _compactBackground = bitmap;
            PerformanceLogger.RecordGlanceCompactBackgroundDecode();
            SetCompactBackgroundEstimatedBytes((long)decodePixelWidth * decodePixelWidth * 4);
        }
        catch (Exception ex)
        {
            App.LogVerbose($"[GlanceWidget] Compact background failed for '{path}': {ex.Message}");
        }

        return _compactBackground;
    }

    private void SetCompactBackgroundEstimatedBytes(long estimatedBytes)
    {
        long normalizedBytes = Math.Max(0, estimatedBytes);
        long delta = normalizedBytes - _compactBackgroundEstimatedBytes;
        _compactBackgroundEstimatedBytes = normalizedBytes;
        if (delta != 0)
        {
            PerformanceLogger.AdjustGlanceCompactBackgroundEstimatedBytes(
                delta);
        }
    }

    protected override void Dispose(bool disposing)
    {
        _compactBackground = null;
        _compactBackgroundPath = null;
        SetCompactBackgroundEstimatedBytes(0);
        ViewModel.Dispose();
    }
}
