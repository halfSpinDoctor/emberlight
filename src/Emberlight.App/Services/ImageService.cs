using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Emberlight.Core;

namespace Emberlight.App.Services;

/// <summary>A decoded picture plus the facts the UI needs about the file it came from.</summary>
/// <param name="Bitmap">Frozen bitmap. May be a reduced-size decode; see <paramref name="IsFullResolution"/>.</param>
/// <param name="PixelWidth">Pixel width of the *file's* image (not necessarily of <paramref name="Bitmap"/>).</param>
public sealed record LoadedImage(
    BitmapSource Bitmap, int PixelWidth, int PixelHeight,
    ImageOrientation ExifOrientation, bool IsFullResolution, long FileSize, DateTime ModifiedUtc);

/// <summary>
/// Image decoding via Windows' built-in imaging stack (WIC, through WPF's BitmapDecoder).
/// Replaces the Image I/O calls in DYImageCache. Handles whatever formats Windows has codecs for
/// (JPEG, PNG, TIFF, BMP and GIF out of the box). All results are frozen, so they can be created on
/// a worker thread and handed to the UI thread.
/// </summary>
public static class ImageService
{
    public static BitmapSource? BrokenPlaceholder { get; private set; }
    public static BitmapSource? LoadingPlaceholder { get; private set; }

    /// <summary>Must be called once on the UI thread (it renders with a DrawingVisual).</summary>
    public static void InitPlaceholders()
    {
        BrokenPlaceholder = MakePlaceholder("\u2715");
        LoadingPlaceholder = MakeIconPlaceholder() ?? MakePlaceholder("\u2026");
    }

    /// <summary>
    /// The app icon as a square tile, shown while a thumbnail loads. The icon's own rounded corners are transparent,
    /// so the tile paints the icon's background gradient edge to edge first: the corners then come out square.
    /// Null if the icon can't be read (the caller falls back to the drawn placeholder).
    /// </summary>
    /// <summary>
    /// The largest image in the app icon (256 px), or null if it can't be read. Use this rather than pointing an
    /// Image at app.ico: WPF then shows the icon's first frame, the 16 px one, scaled up and blocky.
    /// </summary>
    public static BitmapSource? LoadLargestAppIcon()
    {
        try
        {
            var decoder = BitmapDecoder.Create(new Uri("pack://application:,,,/Emberlight;component/Assets/app.ico"),
                BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            var frame = decoder.Frames.OrderByDescending(f => f.PixelWidth).First();
            frame.Freeze();
            return frame;
        }
        catch (Exception) { return null; }
    }

    private static BitmapSource? MakeIconPlaceholder()
    {
        var icon = LoadLargestAppIcon();
        if (icon is not { PixelWidth: 256 }) return null;    // the geometry below is for the 256 px frame

        // In the 256 px frame the rounded square spans pixels 6..250; its vertical gradient runs between these
        // colours (sampled from the icon, identical at the left and right edges).
        const int inset = 6, side = 256 - 2 * inset;
        var gradient = new LinearGradientBrush(Color.FromRgb(0xFE, 0xAC, 0x2D), Color.FromRgb(0xD6, 0x32, 0x26), 90);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(gradient, null, new Rect(0, 0, side, side));
            // The icon's rounded edge has a 1-2 px lighter rim that would show as faint arcs at the new square
            // corners, so only its interior is drawn: a circular clip 3 px in, which stays inside its squircle corners.
            dc.PushClip(new RectangleGeometry(new Rect(3, 3, side - 6, side - 6), 44, 44));
            dc.DrawImage(icon, new Rect(-inset, -inset, 256, 256));
            dc.Pop();
        }
        var rtb = new RenderTargetBitmap(side, side, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();
        return rtb;
    }

    // ---- sizes ----------------------------------------------------------------------------

    /// <summary>Reads the pixel size from the file header without decoding the picture.</summary>
    public static bool TryReadSize(string path, out int width, out int height)
    {
        width = height = 0;
        try
        {
            using var fs = OpenRead(path);
            var decoder = BitmapDecoder.Create(fs,
                BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None);
            if (decoder.Frames.Count == 0) return false;
            var frame = decoder.Frames[0];
            width = frame.PixelWidth;
            height = frame.PixelHeight;
            return width > 0 && height > 0;
        }
        catch (Exception)
        {
            // Any decoder failure just means "not a readable image".
            return false;
        }
    }

    // ---- full-screen viewing --------------------------------------------------------------

    /// <summary>
    /// Loads a picture for viewing. With <paramref name="maxDecodeDimension"/> &gt; 0 and a bigger source, the
    /// decoder scales while reading (fast, and far less memory than a full 24-megapixel bitmap); pass 0 for
    /// full resolution. <paramref name="isCancelled"/> is polled before the expensive work so queued
    /// requests for slides the user has already skipped past cost almost nothing.
    /// </summary>
    public static LoadedImage? LoadDisplay(string path, int maxDecodeDimension, Func<bool>? isCancelled = null)
    {
        try
        {
            if (isCancelled?.Invoke() == true) return null;
            var fi = new FileInfo(path);
            if (!fi.Exists) return null;
            if (!TryReadSize(path, out int pw, out int ph)) return null;
            var exif = ImageOrientation.FromExif(ExifReader.GetOrientation(path));
            if (isCancelled?.Invoke() == true) return null;

            bool scaled = maxDecodeDimension > 0 && Math.Max(pw, ph) > maxDecodeDimension;
            BitmapSource bmp = scaled ? DecodeScaled(path, pw, ph, maxDecodeDimension) : DecodeFull(path);
            return new LoadedImage(bmp, pw, ph, exif, !scaled, fi.Length, fi.LastWriteTimeUtc);
        }
        catch (Exception)
        {
            return null;
        }
    }

    // ---- thumbnails -----------------------------------------------------------------------

    /// <summary>
    /// Makes a thumbnail whose longer side is about <paramref name="maxDimension"/> pixels. JPEGs with an
    /// embedded EXIF preview that is big enough use it (no need to decode the whole photo); otherwise the
    /// decoder scales on the fly.
    /// </summary>
    public static LoadedImage? LoadThumbnail(string path, int maxDimension, bool preferEmbedded)
    {
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists) return null;
            if (!TryReadSize(path, out int pw, out int ph)) return null;

            bool jpeg = ImageFileTypes.IsJpeg(path);
            ExifData? exif = ImageFileTypes.MayHaveExif(path) ? ExifReader.Read(path, includeThumbnail: preferEmbedded && jpeg) : null;
            var orientation = ImageOrientation.FromExif(exif?.Orientation ?? 0);

            if (preferEmbedded && exif?.ThumbnailJpeg is { } bytes)
            {
                var embedded = DecodeBytes(bytes);
                if (embedded != null && Math.Max(embedded.PixelWidth, embedded.PixelHeight) >= 0.75 * Math.Min(maxDimension, Math.Max(pw, ph)))
                    return new LoadedImage(embedded, pw, ph, orientation, false, fi.Length, fi.LastWriteTimeUtc);
            }

            bool scaled = Math.Max(pw, ph) > maxDimension;
            BitmapSource bmp = scaled ? DecodeScaled(path, pw, ph, maxDimension) : DecodeFull(path);
            return new LoadedImage(bmp, pw, ph, orientation, !scaled, fi.Length, fi.LastWriteTimeUtc);
        }
        catch (Exception)
        {
            return null;
        }
    }

    // ---- decoding helpers -----------------------------------------------------------------

    private static FileStream OpenRead(string path)
        => new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan);

    private static BitmapSource DecodeFull(string path)
    {
        using var fs = OpenRead(path);
        // OnLoad pulls everything into memory so the file isn't left locked after we return.
        var decoder = BitmapDecoder.Create(fs, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];
        frame.Freeze();
        return frame;
    }

    private static BitmapSource DecodeScaled(string path, int pw, int ph, int maxDimension)
    {
        double scale = (double)maxDimension / Math.Max(pw, ph);
        int w = Math.Max(1, (int)Math.Round(pw * scale));
        int h = Math.Max(1, (int)Math.Round(ph * scale));

        using var fs = OpenRead(path);
        var bi = new BitmapImage();
        bi.BeginInit();
        bi.CacheOption = BitmapCacheOption.OnLoad;
        bi.StreamSource = fs;
        // Setting only one dimension lets WIC preserve the aspect ratio.
        if (pw >= ph) bi.DecodePixelWidth = w; else bi.DecodePixelHeight = h;
        bi.EndInit();
        bi.Freeze();
        return bi;
    }

    private static BitmapSource? DecodeBytes(byte[] bytes)
    {
        try
        {
            using var ms = new MemoryStream(bytes);
            var decoder = BitmapDecoder.Create(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var frame = decoder.Frames[0];
            frame.Freeze();
            return frame;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static BitmapSource MakePlaceholder(string glyph)
    {
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(
                new SolidColorBrush(Color.FromRgb(0xEC, 0xEC, 0xEC)),
                new Pen(new SolidColorBrush(Color.FromRgb(0xBC, 0xBC, 0xBC)), 2),
                new Rect(6, 6, 148, 108));
            var text = new FormattedText(glyph, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), 44, Brushes.Gray, 1.0);
            dc.DrawText(text, new Point(80 - text.Width / 2, 60 - text.Height / 2));
        }
        var rtb = new RenderTargetBitmap(160, 120, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();
        return rtb;
    }
}
