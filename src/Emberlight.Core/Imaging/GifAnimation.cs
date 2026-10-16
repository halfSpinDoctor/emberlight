namespace Emberlight.Core;

/// <summary>What a GIF frame asks to happen to its area before the next frame is drawn.</summary>
public enum GifDisposal
{
    /// <summary>No preference; treated like <see cref="Keep"/>.</summary>
    Unspecified = 0,
    /// <summary>Leave the frame in place; the next frame is drawn over it.</summary>
    Keep = 1,
    /// <summary>Clear the frame's rectangle to transparent.</summary>
    RestoreBackground = 2,
    /// <summary>Put back what was there before this frame was drawn.</summary>
    RestorePrevious = 3,
}

/// <summary>
/// One frame of an animated GIF: a rectangle of pixels (32-bit BGRA, top-down rows, alpha 0 where the frame is
/// transparent) placed at <paramref name="Left"/>, <paramref name="Top"/> on the animation's canvas.
/// </summary>
public sealed record GifFrame(int Left, int Top, int Width, int Height, int DelayCentiseconds, GifDisposal Disposal, byte[] Bgra)
{
    /// <summary>
    /// How long the frame stays on screen. GIF delays are in hundredths of a second; like web browsers, delays of
    /// 0 or 1 (which would make many GIFs race) become 100 ms.
    /// </summary>
    public TimeSpan Delay => TimeSpan.FromMilliseconds(DelayCentiseconds <= 1 ? 100 : DelayCentiseconds * 10);
}

/// <summary>An animated GIF: its canvas size and frames. Built by the app from the decoder's frames and metadata.</summary>
public sealed class GifAnimation
{
    public int Width { get; }
    public int Height { get; }
    public IReadOnlyList<GifFrame> Frames { get; }

    public GifAnimation(int width, int height, IReadOnlyList<GifFrame> frames)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width), "The canvas must not be empty.");
        if (frames.Count == 0) throw new ArgumentException("An animation needs at least one frame.", nameof(frames));
        foreach (var f in frames)
            if (f.Width < 0 || f.Height < 0 || f.Bgra.Length != f.Width * f.Height * 4)
                throw new ArgumentException("A frame's pixel data doesn't match its size.", nameof(frames));
        Width = width;
        Height = height;
        Frames = frames;
    }

    /// <summary>
    /// How long one play-through takes. The slideshow's auto-advance waits at least this long on an animated GIF,
    /// so a short auto-advance time doesn't keep restarting it from the first frame.
    /// </summary>
    public TimeSpan Duration => TimeSpan.FromTicks(Frames.Sum(f => f.Delay.Ticks));

    /// <summary>Bytes of pixel data held by the frames (used to refuse animations too big to keep in memory).</summary>
    public long FrameBytes => Frames.Sum(f => (long)f.Bgra.Length);
}

/// <summary>
/// Plays a <see cref="GifAnimation"/>: paints its frames, one at a time, onto a canvas the size of the animation,
/// following each frame's <see cref="GifDisposal"/>. The canvas is 32-bit BGRA, top-down. It loops forever: after the
/// last frame it starts again from a clear canvas.
/// </summary>
public sealed class GifCompositor
{
    private readonly GifAnimation _anim;
    private byte[]? _beforeCurrent;     // canvas as it was before the current frame, when that frame restores it

    /// <summary>The composed picture for <see cref="FrameIndex"/>.</summary>
    public byte[] Canvas { get; }
    public int FrameIndex { get; private set; }
    public int Stride => _anim.Width * 4;
    public GifFrame CurrentFrame => _anim.Frames[FrameIndex];

    public GifCompositor(GifAnimation animation)
    {
        _anim = animation;
        Canvas = new byte[animation.Width * animation.Height * 4];
        Draw(0);
    }

    /// <summary>Disposes of the current frame as it asks, then draws the next one (wrapping round to the first).</summary>
    public void Advance()
    {
        var current = CurrentFrame;
        switch (current.Disposal)
        {
            case GifDisposal.RestoreBackground:
                ClearRect(current.Left, current.Top, current.Width, current.Height);
                break;
            case GifDisposal.RestorePrevious when _beforeCurrent != null:
                Buffer.BlockCopy(_beforeCurrent, 0, Canvas, 0, Canvas.Length);
                break;
        }

        int next = FrameIndex + 1;
        if (next == _anim.Frames.Count)
        {
            next = 0;
            Array.Clear(Canvas);     // each loop starts from an empty canvas, as browsers do
        }
        Draw(next);
    }

    private void Draw(int index)
    {
        FrameIndex = index;
        var f = _anim.Frames[index];
        if (f.Disposal == GifDisposal.RestorePrevious)
        {
            _beforeCurrent ??= new byte[Canvas.Length];
            Buffer.BlockCopy(Canvas, 0, _beforeCurrent, 0, Canvas.Length);
        }

        // Clip the frame to the canvas (frames may hang off the edge), then copy every non-transparent pixel.
        int x0 = Math.Max(0, f.Left), y0 = Math.Max(0, f.Top);
        int x1 = Math.Min(_anim.Width, f.Left + f.Width), y1 = Math.Min(_anim.Height, f.Top + f.Height);
        for (int y = y0; y < y1; y++)
        {
            int src = ((y - f.Top) * f.Width + (x0 - f.Left)) * 4;
            int dst = (y * _anim.Width + x0) * 4;
            for (int x = x0; x < x1; x++, src += 4, dst += 4)
            {
                if (f.Bgra[src + 3] == 0) continue;     // transparent: what's underneath shows through
                Canvas[dst] = f.Bgra[src];
                Canvas[dst + 1] = f.Bgra[src + 1];
                Canvas[dst + 2] = f.Bgra[src + 2];
                Canvas[dst + 3] = f.Bgra[src + 3];
            }
        }
    }

    private void ClearRect(int left, int top, int width, int height)
    {
        int x0 = Math.Max(0, left), y0 = Math.Max(0, top);
        int x1 = Math.Min(_anim.Width, left + width), y1 = Math.Min(_anim.Height, top + height);
        if (x1 <= x0) return;
        for (int y = y0; y < y1; y++) Array.Clear(Canvas, (y * _anim.Width + x0) * 4, (x1 - x0) * 4);
    }
}
