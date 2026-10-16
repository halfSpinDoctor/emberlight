namespace Emberlight.Core;

/// <summary>
/// Puts an animated GIF together from a decoder's frames and metadata: keeps the frames' memory within a limit (a GIF
/// too big to hold is shown as a still picture instead), maps the disposal codes, and falls back to the area the
/// frames cover when the file has no canvas size. The decoder itself is platform code (WIC on Windows).
/// </summary>
public sealed class GifAnimationBuilder
{
    /// <summary>Animated GIFs whose frames would need more memory than this are shown still.</summary>
    public const long DefaultMaxBytes = 256L * 1024 * 1024;

    private readonly long _maxBytes;
    private readonly List<GifFrame> _frames = new();
    private int _right, _bottom;

    public GifAnimationBuilder(long maxBytes = DefaultMaxBytes) => _maxBytes = maxBytes;

    /// <summary>Bytes of pixel data reserved so far.</summary>
    public long ReservedBytes { get; private set; }

    /// <summary>
    /// Reserves room for a frame's 32-bit pixels. Call it before decoding the frame: false means the animation is too
    /// big, so give up (and show the GIF still) without allocating anything.
    /// </summary>
    public bool TryReserve(int width, int height)
    {
        long bytes = (long)width * height * 4;
        if (ReservedBytes + bytes > _maxBytes) return false;
        ReservedBytes += bytes;
        return true;
    }

    /// <param name="disposalCode">The GIF's disposal method (0-3); anything else counts as unspecified.</param>
    /// <param name="bgra">The frame's pixels, 32-bit BGRA, transparent pixels with alpha 0.</param>
    public void Add(int left, int top, int width, int height, int delayCentiseconds, int disposalCode, byte[] bgra)
    {
        _frames.Add(new GifFrame(left, top, width, height, delayCentiseconds, DisposalFromCode(disposalCode), bgra));
        _right = Math.Max(_right, left + width);
        _bottom = Math.Max(_bottom, top + height);
    }

    /// <summary>
    /// The animation, on the file's canvas ("logical screen") size, or on the frames' extent if that size is missing.
    /// Null for fewer than two frames: a single-frame GIF is shown like any other picture.
    /// </summary>
    public GifAnimation? Build(int canvasWidth, int canvasHeight)
    {
        if (_frames.Count < 2) return null;
        if (canvasWidth <= 0 || canvasHeight <= 0) (canvasWidth, canvasHeight) = (_right, _bottom);
        return new GifAnimation(canvasWidth, canvasHeight, _frames.ToList());
    }

    public static GifDisposal DisposalFromCode(int code) => code is >= 0 and <= 3 ? (GifDisposal)code : GifDisposal.Unspecified;
}
