namespace Emberlight.Core;

/// <summary>One displayable EXIF entry. Extended entries are shown only in the "more info" view.</summary>
public readonly record struct ExifTag(string Name, string Value, bool Extended);

/// <summary>The subset of EXIF metadata the app uses.</summary>
public sealed class ExifData
{
    /// <summary>EXIF orientation 1-8, or 0 when absent/invalid.</summary>
    public ushort Orientation { get; internal set; }

    public DateTime? DateTimeOriginal { get; internal set; }
    public DateTime? DateTimeDigitized { get; internal set; }
    public DateTime? DateTime { get; internal set; }

    /// <summary>The best "when was this taken" value: original, then digitized, then modified.</summary>
    public DateTime? DateTaken => DateTimeOriginal ?? DateTimeDigitized ?? DateTime;

    public IReadOnlyList<ExifTag> Tags => _tags;

    /// <summary>Embedded JPEG preview from IFD1, if requested and present.</summary>
    public byte[]? ThumbnailJpeg { get; internal set; }

    /// <summary>Absolute file offset of the 2-byte orientation value, or -1 when there is none.</summary>
    internal long OrientationFileOffset { get; set; } = -1;
    internal bool BigEndian { get; set; }

    internal readonly List<ExifTag> _tags = new();

    public ImageOrientation ImageOrientation => ImageOrientation.FromExif(Orientation);

    /// <summary>Formats the tags for the info panel, one "Name: value" line each.</summary>
    public string ToDisplayString(bool includeExtended)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var t in _tags)
        {
            if (t.Extended && !includeExtended) continue;
            sb.Append(t.Name).Append(": ").Append(t.Value).Append('\n');
        }
        return sb.ToString().TrimEnd('\n');
    }
}
