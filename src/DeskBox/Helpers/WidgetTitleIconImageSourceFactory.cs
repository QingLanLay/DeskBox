using DeskBox.Services;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace DeskBox.Helpers;

/// <summary>
/// Builds the XAML image source for a custom widget title icon. Raster
/// formats decode bounded to <see cref="WidgetTitleIconCustomization.MaxDecodedIconPixels"/>
/// so a huge source photo never lands in the decoded-bitmap budget; SVG
/// rasterizes at the same bound. Content streams in through
/// <see cref="WidgetImageStreamLoader"/>: the asset file is rewritten
/// under one fixed name on every re-pick, and a URI source would keep
/// serving the framework cache's first decode for the whole session.
/// </summary>
public static class WidgetTitleIconImageSourceFactory
{
    /// <summary>
    /// Creates the source with decode bounds set but no content loaded —
    /// the caller owns streaming the bytes.
    /// </summary>
    public static ImageSource? CreateSource(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return null;
        }

        try
        {
            if (!File.Exists(filePath))
            {
                return null;
            }

            if (string.Equals(
                    Path.GetExtension(filePath),
                    ".svg",
                    StringComparison.OrdinalIgnoreCase))
            {
                return new SvgImageSource
                {
                    RasterizePixelWidth = WidgetTitleIconCustomization.MaxDecodedIconPixels
                };
            }

            return new BitmapImage
            {
                DecodePixelType = DecodePixelType.Physical,
                DecodePixelWidth = WidgetTitleIconCustomization.MaxDecodedIconPixels
            };
        }
        catch (Exception ex)
        {
            App.Log($"[TitleIconAsset] Failed to create image source for '{filePath}': {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Creates the source and streams the file into it.
    /// <paramref name="loadFailed"/> runs when the streamed decode fails
    /// so callers can degrade to their fallback layer.
    /// </summary>
    public static ImageSource? TryCreate(string? filePath, Action? loadFailed = null)
    {
        ImageSource? source = CreateSource(filePath);
        if (source is null)
        {
            return null;
        }

        WidgetImageStreamLoader.Fill(
            source,
            filePath!,
            settled =>
            {
                if (!settled)
                {
                    loadFailed?.Invoke();
                }
            });
        return source;
    }
}
