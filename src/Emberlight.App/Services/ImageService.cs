using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Emberlight.Core;

namespace Emberlight.App.Services;

/// <summary>A decoded picture: the WPF bitmap plus what Core knows about it (<see cref="ImageInfo"/>).</summary>
/// <param name="Bitmap">Frozen bitmap. May be a reduced-size decode; see <see cref="ImageInfo.IsFullResolution"/>.
/// For an animated GIF it is the first frame, composed.</param>
public sealed record LoadedImage(BitmapSource Bitmap, ImageInfo Info);

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
    /// requests for slides the user has already skipped past cost almost nothing. With <paramref name="animateGifs"/>,
    /// an animated GIF comes back with all its frames so it can be played; otherwise just its first frame.
    /// </summary>
    public static LoadedImage? LoadDisplay(string path, int maxDecodeDimension, Func<bool>? isCancelled = null,
        bool animateGifs = true)
    {
        try
        {
            if (isCancelled?.Invoke() == true) return null;
            var fi = new FileInfo(path);
            if (!fi.Exists) return null;
            if (!TryReadSize(path, out int pw, out int ph)) return null;

            // Animated GIFs are kept at full size with all their frames; the slideshow plays them.
            if (animateGifs && ImageFileTypes.IsGif(path) && LoadGifAnimation(path) is { } anim)
            {
                var first = new GifCompositor(anim);
                var bitmap = BitmapSource.Create(anim.Width, anim.Height, 96, 96, PixelFormats.Bgra32, null, first.Canvas, first.Stride);
                bitmap.Freeze();
                return new LoadedImage(bitmap, ImageInfo.ForFile(fi, anim.Width, anim.Height, ImageOrientation.Identity, true, anim));
            }

            var exif = ImageOrientation.FromExif(ExifReader.GetOrientation(path));
            if (isCancelled?.Invoke() == true) return null;

            bool scaled = DecodePolicy.ShouldScale(pw, ph, maxDecodeDimension);
            BitmapSource bmp = scaled ? DecodeScaled(path, pw, ph, maxDecodeDimension) : DecodeFull(path);
            return new LoadedImage(bmp, ImageInfo.ForFile(fi, pw, ph, exif, !scaled));
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Animated GIFs whose frames would need more memory than this are shown as still pictures.</summary>
    private const long MaxGifFrameBytes = 256L * 1024 * 1024;

    /// <summary>
    /// Reads an animated GIF's frames and the metadata needed to play them (position, delay, disposal; canvas size).
    /// Null for a GIF with a single frame (shown like any other picture), one too big to hold in memory, or one the
    /// decoder can't read.
    /// </summary>
    public static GifAnimation? LoadGifAnimation(string path)
    {
        try
        {
            using var fs = OpenRead(path);
            var decoder = new GifBitmapDecoder(fs, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count < 2) return null;

            var frames = new List<GifFrame>(decoder.Frames.Count);
            long bytes = 0;
            int right = 0, bottom = 0;
            foreach (var f in decoder.Frames)
            {
                var md = f.Metadata as BitmapMetadata;
                int left = QueryInt(md, "/imgdesc/Left"), top = QueryInt(md, "/imgdesc/Top");
                int w = f.PixelWidth, h = f.PixelHeight;
                bytes += (long)w * h * 4;
                if (bytes > MaxGifFrameBytes) return null;

                var px = new byte[w * h * 4];
                new FormatConvertedBitmap(f, PixelFormats.Bgra32, null, 0).CopyPixels(px, w * 4, 0);   // transparent -> alpha 0
                int disposal = QueryInt(md, "/grctlext/Disposal");
                frames.Add(new GifFrame(left, top, w, h, QueryInt(md, "/grctlext/Delay"),
                    disposal is >= 0 and <= 3 ? (GifDisposal)disposal : GifDisposal.Unspecified, px));
                right = Math.Max(right, left + w);
                bottom = Math.Max(bottom, top + h);
            }

            // The canvas ("logical screen") size is in the file header; fall back to the frames' extent if it's missing.
            var header = decoder.Metadata;
            int width = QueryInt(header, "/logscrdesc/Width"), height = QueryInt(header, "/logscrdesc/Height");
            if (width <= 0 || height <= 0) (width, height) = (right, bottom);
            return new GifAnimation(width, height, frames);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static int QueryInt(BitmapMetadata? metadata, string query)
    {
        try { return metadata?.GetQuery(query) is { } value ? Convert.ToInt32(value, CultureInfo.InvariantCulture) : 0; }
        catch (Exception) { return 0; }          // a missing or unreadable field
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
                if (embedded != null && DecodePolicy.EmbeddedPreviewIsBigEnough(embedded.PixelWidth, embedded.PixelHeight, maxDimension, pw, ph))
                    return new LoadedImage(embedded, ImageInfo.ForFile(fi, pw, ph, orientation, false));
            }

            bool scaled = DecodePolicy.ShouldScale(pw, ph, maxDimension);
            BitmapSource bmp = scaled ? DecodeScaled(path, pw, ph, maxDimension) : DecodeFull(path);
            return new LoadedImage(bmp, ImageInfo.ForFile(fi, pw, ph, orientation, !scaled));
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
        var (w, h) = DecodePolicy.ScaledSize(pw, ph, maxDimension);

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
