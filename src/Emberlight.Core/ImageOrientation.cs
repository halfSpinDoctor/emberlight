namespace Emberlight.Core;

/// <summary>
/// An orientation expressed as "mirror horizontally (optional), then rotate clockwise".
/// This is the standard decomposition of the eight EXIF orientation values, and it is the
/// same model used by the original app's image view (rotation + flip flag).
///
/// Note the original stored counter-clockwise degrees (Cocoa convention); this type uses
/// clockwise degrees (WPF's RotateTransform convention), so +90 here is a 90° CW turn.
/// </summary>
public readonly record struct ImageOrientation
{
    public int RotationCw { get; }   // 0, 90, 180 or 270
    public bool Flipped { get; }     // mirror left-right, applied before the rotation

    public ImageOrientation(int rotationCw, bool flipped)
    {
        RotationCw = Normalize(rotationCw);
        Flipped = flipped;
    }

    public static ImageOrientation Identity => new(0, false);

    /// <summary>True when width and height swap (90°/270°).</summary>
    public bool SwapsAxes => RotationCw is 90 or 270;

    /// <summary>Converts an EXIF Orientation value (1-8). Anything else is treated as 1.</summary>
    public static ImageOrientation FromExif(int exif) => exif switch
    {
        2 => new(0, true),
        3 => new(180, false),
        4 => new(180, true),
        5 => new(270, true),
        6 => new(90, false),
        7 => new(90, true),
        8 => new(270, false),
        _ => Identity,
    };

    public int ToExif() => (RotationCw, Flipped) switch
    {
        (0, false) => 1,
        (0, true) => 2,
        (180, false) => 3,
        (180, true) => 4,
        (270, true) => 5,
        (90, false) => 6,
        (90, true) => 7,
        (270, false) => 8,
        _ => 1,
    };

    /// <summary>Adds a further clockwise rotation of the displayed image (negative = counter-clockwise).</summary>
    public ImageOrientation RotatedBy(int degreesCw) => new(RotationCw + degreesCw, Flipped);

    /// <summary>
    /// Mirrors the *displayed* image left-right. Because a mirror applied after a rotation equals the
    /// opposite rotation applied after the mirror, a 90°/270° rotation reverses direction.
    /// (Matches DYImageView toggleFlip.)
    /// </summary>
    public ImageOrientation FlippedDisplay() => new(-RotationCw, !Flipped);

    /// <summary>Size of the displayed image for a source of the given size.</summary>
    public (int Width, int Height) ApplyToSize(int width, int height)
        => SwapsAxes ? (height, width) : (width, height);

    /// <summary>
    /// Maps a source pixel to its position in the displayed image. Mainly for tests and for
    /// anything that needs exact pixel geometry (e.g. mapping a click back to the source).
    /// </summary>
    public (int X, int Y) MapPoint(int x, int y, int width, int height)
    {
        if (Flipped) x = width - 1 - x;
        return RotationCw switch
        {
            90 => (height - 1 - y, x),
            180 => (width - 1 - x, height - 1 - y),
            270 => (y, width - 1 - x),
            _ => (x, y),
        };
    }

    private static int Normalize(int deg)
    {
        deg %= 360;
        if (deg < 0) deg += 360;
        // Snap to the nearest quarter turn; callers only ever pass multiples of 90.
        return (deg / 90 * 90) % 360;
    }
}
