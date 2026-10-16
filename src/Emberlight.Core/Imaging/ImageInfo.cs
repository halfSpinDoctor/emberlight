namespace Emberlight.Core;

/// <summary>
/// What is known about a decoded picture apart from its pixels: the file's picture size, EXIF orientation, whether
/// the decode is full size, and the file's size and modified time (to tell whether a cached decode is still current).
/// Each platform pairs this with its own bitmap type.
/// </summary>
/// <param name="PixelWidth">Pixel width of the file's picture (not necessarily of the decoded bitmap).</param>
/// <param name="Animation">For an animated GIF, its frames.</param>
public sealed record ImageInfo(
    int PixelWidth, int PixelHeight, ImageOrientation ExifOrientation, bool IsFullResolution,
    long FileSize, DateTime ModifiedUtc, GifAnimation? Animation = null)
{
    public static ImageInfo ForFile(FileInfo file, int pixelWidth, int pixelHeight, ImageOrientation exifOrientation,
        bool isFullResolution, GifAnimation? animation = null)
        => new(pixelWidth, pixelHeight, exifOrientation, isFullResolution, file.Length, file.LastWriteTimeUtc, animation);

    /// <summary>Whether the file at <paramref name="path"/> is still the one decoded (same size and modified time).</summary>
    public bool MatchesFile(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            return fi.Exists && fi.Length == FileSize && fi.LastWriteTimeUtc == ModifiedUtc;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }
}
