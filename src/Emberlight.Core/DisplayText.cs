using System.Text;

namespace Emberlight.Core;

/// <summary>The text the browser and the slideshow show about pictures: status bar, tooltips, info panel and overlays.</summary>
public static class DisplayText
{
    private const string Dot = "  ·  ";
    private const string Times = " × ";

    /// <summary>Browser status bar: how many images, then what is selected.</summary>
    /// <param name="selectedName">The selected file's name when exactly one is selected.</param>
    /// <param name="selectedInfo">Its decoded details, once its thumbnail has loaded.</param>
    public static string BrowserStatus(int imageCount, int selectedCount, string? selectedName, ImageInfo? selectedInfo)
    {
        var sb = new StringBuilder();
        sb.Append(imageCount == 1 ? "1 image" : $"{imageCount} images");
        if (selectedCount > 1) sb.Append(Dot).Append(selectedCount).Append(" selected");
        else if (selectedCount == 1)
        {
            sb.Append(Dot).Append(selectedName);
            if (selectedInfo != null)
                sb.Append(Dot).Append(selectedInfo.PixelWidth).Append(Times).Append(selectedInfo.PixelHeight)
                  .Append(Dot).Append(FileSizeFormatter.Format((ulong)selectedInfo.FileSize));
        }
        return sb.ToString();
    }

    public static string ThumbnailToolTip(string fileName, ImageInfo? info)
        => info == null ? fileName
            : $"{fileName}\n{info.PixelWidth}{Times}{info.PixelHeight}   {FileSizeFormatter.Format((ulong)info.FileSize)}";

    public static string InfoPanelHeader(int selectedCount, string? fileName) => selectedCount switch
    {
        0 => "No selection",
        1 => fileName ?? "",
        _ => $"{selectedCount} files selected",
    };

    /// <summary>
    /// The info panel for one file: folder, size, modified date, the picture's size (if the decoder could read it),
    /// then the EXIF tags after a blank line.
    /// </summary>
    public static string InfoPanelBody(string path, (int Width, int Height)? pixelSize, bool moreExif)
    {
        var sb = new StringBuilder();
        try
        {
            // Read everything before writing anything, so a file that has gone adds no half-written lines.
            var fi = new FileInfo(path);
            string folder = fi.DirectoryName ?? "", size = FileSizeFormatter.Format((ulong)fi.Length), modified = $"{fi.LastWriteTime:g}";
            sb.AppendLine($"Folder: {folder}");
            sb.AppendLine($"Size: {size}");
            sb.AppendLine($"Modified: {modified}");
        }
        catch (IOException) { }
        if (pixelSize is var (w, h)) sb.AppendLine($"Dimensions: {w}{Times}{h}");
        var exif = ExifReader.Read(path);
        if (exif != null)
        {
            string text = exif.ToDisplayString(moreExif);
            if (text.Length > 0) sb.AppendLine().AppendLine(text);
        }
        return sb.ToString();
    }

    /// <summary>
    /// The slideshow's info overlay: position and file name (or path); the picture's size, file size, zoom and any
    /// rotation the user applied; then the random/loop/auto-advance flags.
    /// </summary>
    /// <param name="image">The slide on screen, or null if it couldn't be opened.</param>
    /// <param name="scale">Screen pixels per picture pixel.</param>
    /// <param name="orientation">How the slide is shown, if the user rotated or flipped it.</param>
    public static string SlideshowInfo(SlideshowState state, bool showPath, ImageInfo? image, double scale, ImageOrientation orientation)
    {
        if (state.CurrentFile is not { } path) return "No images";

        var sb = new StringBuilder();
        sb.Append(state.CurrentIndex + 1).Append('/').Append(state.Files.Count).Append("   ")
          .Append(showPath ? path : Path.GetFileName(path)).Append('\n');

        if (image != null)
        {
            sb.Append(image.PixelWidth).Append(Times).Append(image.PixelHeight)
              .Append("   ").Append(FileSizeFormatter.Format((ulong)image.FileSize))
              .Append("   ").Append((scale * 100).ToString("0.#")).Append('%');
            if (orientation != ImageOrientation.Identity)
                sb.Append("   rotated ").Append(orientation.RotationCw).Append('°').Append(orientation.Flipped ? ", flipped" : "");
            sb.Append('\n');
        }

        var flags = new List<string>();
        if (state.RandomMode) flags.Add("random");
        if (state.Loop) flags.Add("loop");
        if (state.AutoAdvanceSeconds > 0)
            flags.Add($"auto {state.AutoAdvanceSeconds:0.#}s" + (state.Paused ? " (paused)" : ""));
        if (flags.Count > 0) sb.Append('[').Append(string.Join("] [", flags)).Append(']');
        return sb.ToString().TrimEnd();
    }

    /// <summary>The slideshow's EXIF overlay for a file.</summary>
    public static string ExifOverlay(string path, bool moreExif)
    {
        string text = ExifReader.Read(path)?.ToDisplayString(moreExif) ?? "";
        return text.Length > 0 ? text : "(no EXIF data)";
    }
}

/// <summary>
/// Which slideshow overlays are showing: <see cref="AppSettings.SlideshowInfoMode"/> (0 none, 1 file info, 2 file info
/// and EXIF), <see cref="AppSettings.SlideshowShowPath"/>, and whether the EXIF view shows the extra tags (this
/// slideshow only).
/// </summary>
public sealed class SlideshowOverlay
{
    private readonly AppSettings _settings;

    public SlideshowOverlay(AppSettings settings) => _settings = settings;

    public bool MoreExif { get; private set; }
    public bool ShowsInfo => _settings.SlideshowInfoMode > 0;
    public bool ShowsExif => _settings.SlideshowInfoMode == 2;

    /// <summary>I: none -> file info -> file info + EXIF -> none.</summary>
    public void Cycle() => _settings.SlideshowInfoMode = (_settings.SlideshowInfoMode + 1) % 3;

    /// <summary>Shift+I: opens the EXIF view with the extra tags, or toggles the extra tags if it is open.</summary>
    public void ToggleMoreExif()
    {
        if (_settings.SlideshowInfoMode != 2) { _settings.SlideshowInfoMode = 2; MoreExif = true; }
        else MoreExif = !MoreExif;
    }

    /// <summary>P: file name or full path, showing the file info if nothing was showing.</summary>
    public void TogglePath()
    {
        _settings.SlideshowShowPath = !_settings.SlideshowShowPath;
        if (_settings.SlideshowInfoMode == 0) _settings.SlideshowInfoMode = 1;
    }
}
