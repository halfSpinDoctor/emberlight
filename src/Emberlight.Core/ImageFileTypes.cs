namespace Emberlight.Core;

/// <summary>
/// Which file extensions the browser treats as images. Replaces the original's
/// NSImage.imageUnfilteredTypes lookup, which has no Windows equivalent.
/// Formats are limited to what Windows' built-in imaging codecs (WIC) decode out of the box.
/// </summary>
public sealed class ImageFileTypes
{
    /// <summary>JPEG, PNG, TIFF, plus BMP and (static) GIF, which WIC decodes for free.</summary>
    public static readonly IReadOnlyList<string> BuiltIn = new[]
    {
        ".jpg", ".jpeg", ".jpe", ".jfif", ".png", ".tif", ".tiff", ".bmp", ".gif",
    };

    private readonly HashSet<string> _all;
    private readonly HashSet<string> _enabled;

    public ImageFileTypes() : this(BuiltIn) { }

    public ImageFileTypes(IEnumerable<string> extensions)
    {
        _all = new HashSet<string>(extensions.Select(Normalize), StringComparer.OrdinalIgnoreCase);
        _enabled = new HashSet<string>(_all, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>All known extensions (lower-case, with leading dot), sorted.</summary>
    public IReadOnlyList<string> AllExtensions => _all.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();

    public bool IsEnabled(string ext) => _enabled.Contains(Normalize(ext));

    public void SetEnabled(string ext, bool enabled)
    {
        ext = Normalize(ext);
        if (!_all.Contains(ext)) return;
        if (enabled) _enabled.Add(ext); else _enabled.Remove(ext);
    }

    /// <summary>Adds support for another extension (e.g. ".jp2" once a decoder is registered).</summary>
    public void Register(string ext)
    {
        ext = Normalize(ext);
        _all.Add(ext);
        _enabled.Add(ext);
    }

    /// <summary>Applies the persisted "ignored types" list from settings.</summary>
    public void ApplyIgnored(IEnumerable<string> ignored)
    {
        foreach (var e in ignored) SetEnabled(e, false);
    }

    public IReadOnlyList<string> DisabledExtensions => _all.Where(e => !_enabled.Contains(e)).OrderBy(x => x).ToList();

    public bool IsSupportedFile(string path) => _enabled.Contains(Path.GetExtension(path).ToLowerInvariant());

    public static bool IsJpeg(string path)
        => Path.GetExtension(path).ToLowerInvariant() is ".jpg" or ".jpeg" or ".jpe" or ".jfif";

    /// <summary>Files whose EXIF can be read by <see cref="ExifReader"/>.</summary>
    public static bool MayHaveExif(string path)
        => Path.GetExtension(path).ToLowerInvariant() is ".jpg" or ".jpeg" or ".jpe" or ".jfif" or ".tif" or ".tiff" or ".png";

    private static string Normalize(string ext)
    {
        ext = ext.Trim().ToLowerInvariant();
        return ext.StartsWith('.') ? ext : "." + ext;
    }
}
