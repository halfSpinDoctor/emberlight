namespace Emberlight.Core;

/// <summary>
/// How big to decode pictures: thumbnail sizes, the thumbnail cache size, slide decode sizes, when an embedded EXIF
/// preview will do, and when a zoomed-in slide needs its full-resolution decode. The decoders themselves are
/// platform code; these rules are shared.
/// </summary>
public static class DecodePolicy
{
    /// <summary>
    /// The size thumbnails are decoded at for a grid cell <paramref name="cellWidth"/> device-independent units wide.
    /// A few fixed sizes rather than the exact width, so dragging the size slider doesn't decode everything again.
    /// </summary>
    public static int ThumbnailDecodeSize(double cellWidth, double dpiScale)
    {
        double px = cellWidth * dpiScale;
        return px <= 150 ? 160 : px <= 230 ? 256 : px <= 330 ? 360 : 512;
    }

    /// <summary>How many thumbnails to cache: about 160 MB of 4:3 pictures, whatever the size.</summary>
    public static int ThumbnailCacheCapacity(int decodeSize)
        => Math.Clamp((int)(160_000_000.0 / (decodeSize * (decodeSize * 0.75) * 4)), 100, 4000);

    /// <summary>
    /// The longest side to decode a slide at: the view's longer side (fast, light on memory), 0 (full resolution) when
    /// the slideshow shows pictures at actual size, or 3840 before the view has been laid out.
    /// </summary>
    public static int SlideDecodeTarget(bool actualSize, double viewWidth, double viewHeight)
    {
        if (actualSize) return 0;                    // 100% view needs every pixel
        double d = Math.Max(viewWidth, viewHeight);
        return d > 64 ? (int)Math.Ceiling(d) : 3840;
    }

    /// <summary>Whether a picture should be scaled while decoding. A limit of 0 means full resolution.</summary>
    public static bool ShouldScale(int pixelWidth, int pixelHeight, int maxDimension)
        => maxDimension > 0 && Math.Max(pixelWidth, pixelHeight) > maxDimension;

    /// <summary>The decoded size with the longer side at <paramref name="maxDimension"/>, keeping the aspect ratio.</summary>
    public static (int Width, int Height) ScaledSize(int pixelWidth, int pixelHeight, int maxDimension)
    {
        double scale = (double)maxDimension / Math.Max(pixelWidth, pixelHeight);
        return (Math.Max(1, (int)Math.Round(pixelWidth * scale)), Math.Max(1, (int)Math.Round(pixelHeight * scale)));
    }

    /// <summary>
    /// Whether a JPEG's embedded EXIF preview is big enough to use as its thumbnail (much faster than decoding the
    /// photo): at least 3/4 of the thumbnail size, or of the photo itself if that is smaller.
    /// </summary>
    public static bool EmbeddedPreviewIsBigEnough(int previewWidth, int previewHeight, int maxDimension, int pixelWidth, int pixelHeight)
        => Math.Max(previewWidth, previewHeight) >= 0.75 * Math.Min(maxDimension, Math.Max(pixelWidth, pixelHeight));

    /// <summary>
    /// Whether a slide decoded at <paramref name="bitmapWidth"/> x <paramref name="bitmapHeight"/> from a picture of
    /// <paramref name="pixelWidth"/> x <paramref name="pixelHeight"/> looks soft at <paramref name="scale"/> (screen
    /// pixels per picture pixel), so the full-resolution decode should replace it.
    /// </summary>
    public static bool NeedsFullResolution(int bitmapWidth, int bitmapHeight, int pixelWidth, int pixelHeight, double scale)
    {
        int bitmapSide = Math.Max(bitmapWidth, bitmapHeight), pixelSide = Math.Max(pixelWidth, pixelHeight);
        if (bitmapSide >= pixelSide) return false;                   // already every pixel
        return scale > (double)bitmapSide / pixelSide * 1.001;
    }
}
