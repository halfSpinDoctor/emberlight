using System.Text.Json;
using System.Text.Json.Serialization;

namespace Emberlight.Core;

public enum StartupFolder
{
    LastFolder = 0,
    PicturesFolder = 1,
}

/// <summary>What happens to "Include subfolders" when the browser moves to another folder.</summary>
public enum SubfolderMode
{
    /// <summary>Switch it off on leaving the folder tree where it was switched on (moving deeper keeps it on).</summary>
    ResetOnNewTree = 0,
    /// <summary>Leave it on until the user switches it off.</summary>
    Always = 1,
}

/// <summary>User preferences. Defaults mirror the original app's registered NSUserDefaults.</summary>
public sealed class AppSettings
{
    public const double MinThumbCellWidth = 40;     // MIN_CELL_WIDTH in the original
    public const double MaxThumbCellWidth = 400;
    public const double MinInfoPanelWidth = 160;

    public string PicturesFolderPath { get; set; } = "";
    public string LastFolderPath { get; set; } = "";
    public StartupFolder StartupOption { get; set; } = StartupFolder.LastFolder;

    public double ThumbCellWidth { get; set; } = 120;
    public bool ShowFilenames { get; set; } = true;
    public int SortBy { get; set; } = 1;                        // legacy signed encoding: 1 = name ascending
    public int MaxThumbsToLoad { get; set; } = 100;
    public bool AutoRotateByOrientationTag { get; set; } = true;
    public bool InfoPanelVisible { get; set; }
    public bool PreserveModifiedDateOnEdit { get; set; }

    public bool SlideshowAutoAdvance { get; set; }
    public double SlideshowAutoAdvanceSeconds { get; set; } = 5.5;
    public bool SlideshowLoop { get; set; }
    public bool SlideshowRandom { get; set; }
    public bool SlideshowScaleUp { get; set; }
    public bool SlideshowActualSize { get; set; }
    public bool SlideshowWindowFitToImage { get; set; }
    public bool RerandomizeOnLoop { get; set; } = true;
    public bool SlideshowPlayGifs { get; set; } = true;          // animate GIFs in the slideshow (else first frame)
    public bool SlideshowGifsFinish { get; set; } = true;        // auto-advance waits for one full play of a GIF
    public bool SuppressLoopIndicator { get; set; }
    public string SlideshowBackgroundColor { get; set; } = "#000000";
    public string TransparentImageBackgroundColor { get; set; } = "#FFFFFF";   // white behind transparent areas

    public bool OpenFilesDoSlideshow { get; set; } = true;
    public bool OpenFilesIgnoreAutoAdvance { get; set; }
    public bool StartupSlideshowFromFolder { get; set; }
    public bool StartupSlideshowSubfolders { get; set; }

    public bool SlideshowDefaultFullScreen { get; set; } = true;
    /// <summary>0 = no overlay, 1 = file info, 2 = file info + EXIF (cycled with the "i" key).</summary>
    public int SlideshowInfoMode { get; set; }
    public bool SlideshowShowPath { get; set; }

    public double MainWindowWidth { get; set; } = 1100;
    public double MainWindowHeight { get; set; } = 720;
    public double FolderTreeWidth { get; set; } = 240;
    public double InfoPanelWidth { get; set; } = 280;
    public bool IncludeSubfolders { get; set; }
    public SubfolderMode IncludeSubfoldersMode { get; set; } = SubfolderMode.ResetOnNewTree;
    public bool InfoPanelMoreExif { get; set; }

    public List<string> IgnoredFileTypes { get; set; } = new();

    [JsonIgnore]
    public SortOrder SortOrder
    {
        get => SortOrder.FromLegacy(SortBy);
        set => SortBy = value.ToLegacy();
    }

    /// <summary>Clamps values that a hand-edited or corrupt settings file could make unusable.</summary>
    public void Sanitize()
    {
        ThumbCellWidth = double.IsFinite(ThumbCellWidth)
            ? Math.Clamp(ThumbCellWidth, MinThumbCellWidth, MaxThumbCellWidth) : 120;
        MaxThumbsToLoad = Math.Clamp(MaxThumbsToLoad, 1, 100_000);
        if (!double.IsFinite(SlideshowAutoAdvanceSeconds) || SlideshowAutoAdvanceSeconds <= 0)
            SlideshowAutoAdvanceSeconds = 5.5;
        if (SortBy == 0 || Math.Abs(SortBy) > 7) SortBy = 1;
        if (!double.IsFinite(MainWindowWidth) || MainWindowWidth < 400) MainWindowWidth = 1100;
        if (!double.IsFinite(MainWindowHeight) || MainWindowHeight < 300) MainWindowHeight = 720;
        if (!double.IsFinite(FolderTreeWidth)) FolderTreeWidth = 240;
        FolderTreeWidth = Math.Clamp(FolderTreeWidth, 120, 600);
        InfoPanelWidth = double.IsFinite(InfoPanelWidth) ? Math.Clamp(InfoPanelWidth, MinInfoPanelWidth, 800) : 280;
        SlideshowInfoMode = Math.Clamp(SlideshowInfoMode, 0, 2);
        if (!Enum.IsDefined(IncludeSubfoldersMode)) IncludeSubfoldersMode = SubfolderMode.ResetOnNewTree;
        IgnoredFileTypes ??= new();
        PicturesFolderPath ??= "";
        LastFolderPath ??= "";
        SlideshowBackgroundColor ??= "#000000";
        TransparentImageBackgroundColor ??= "#FFFFFF";
    }
}

public static class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Emberlight", "settings.json");

    /// <summary>Loads settings; a missing or unreadable file silently yields defaults.</summary>
    public static AppSettings Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (File.Exists(path))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Options);
                if (s != null) { s.Sanitize(); return s; }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            // fall through to defaults
        }
        var d = new AppSettings();
        d.Sanitize();
        return d;
    }

    /// <summary>Saves atomically (write temp file, then replace) so a crash can't leave a half-written file.</summary>
    public static bool Save(AppSettings settings, string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(settings, Options));
            File.Move(tmp, path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
