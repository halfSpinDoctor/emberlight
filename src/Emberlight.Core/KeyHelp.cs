using System.Text;

namespace Emberlight.Core;

/// <summary>One line of key help: the keys (as the platform names them) and what they do.</summary>
public readonly record struct HelpLine(string Keys, string Description);

public static class KeyHelp
{
    /// <summary>
    /// Lays out key help as two columns (the keys padded to the widest, plus two spaces), for a fixed-width font:
    /// the title, then each non-empty section after a blank line, then the footer after a blank line.
    /// </summary>
    public static string Format(string title, IEnumerable<IReadOnlyList<HelpLine>> sections, string? footer = null)
    {
        var nonEmpty = sections.Where(s => s.Count > 0).ToList();
        int width = nonEmpty.SelectMany(s => s).Select(l => l.Keys.Length).DefaultIfEmpty(0).Max() + 2;
        var sb = new StringBuilder(title);
        foreach (var section in nonEmpty)
        {
            sb.Append('\n');
            foreach (var line in section) sb.Append('\n').Append(line.Keys.PadRight(width)).Append(line.Description);
        }
        if (footer != null) sb.Append("\n\n").Append(footer);
        return sb.ToString();
    }
}

/// <summary>The slideshow help screen's topics, in the order shown. Each platform supplies the key names.</summary>
public enum SlideshowHelpTopic
{
    Next, Previous, InOrder, FirstLast, Folders, Pages,
    Timer, StopTimer, QuickTimer, Pause,
    Zoom, ActualSize, ResetView, Rotate, Flip, Pan,
    Info, MoreExif, Path, Delete, Random, Help, Quit,
}

/// <param name="Commands">The commands this line documents (so a test can check that every command is documented).</param>
public sealed record SlideshowHelpEntry(SlideshowHelpTopic Topic, string Description, IReadOnlyList<SlideshowCommand> Commands);

/// <summary>
/// What the slideshow help screen says. The descriptions and their order live here; the key names are the
/// platform's, so the same help can show Windows or Mac keys.
/// </summary>
public static class SlideshowHelp
{
    public const string Title = "Slideshow keys";

    public static IReadOnlyList<IReadOnlyList<SlideshowHelpEntry>> Sections { get; } =
    [
        [
            Entry(SlideshowHelpTopic.Next, "next slide", SlideshowCommand.Next, SlideshowCommand.PauseOrNext),
            Entry(SlideshowHelpTopic.Previous, "previous slide", SlideshowCommand.Previous),
            Entry(SlideshowHelpTopic.InOrder, "next / previous in original order (shuffled show)", SlideshowCommand.Next, SlideshowCommand.Previous),
            Entry(SlideshowHelpTopic.FirstLast, "first / last slide", SlideshowCommand.First, SlideshowCommand.Last),
            Entry(SlideshowHelpTopic.Folders, "next / previous folder (same level; skips empty folders)", SlideshowCommand.NextFolder, SlideshowCommand.PreviousFolder),
            Entry(SlideshowHelpTopic.Pages, $"back / forward {SlideshowCommands.PageSize} slides", SlideshowCommand.BackPage, SlideshowCommand.ForwardPage),
        ],
        [
            Entry(SlideshowHelpTopic.Timer, "auto-advance every N seconds", SlideshowCommand.StartAutoAdvance),
            Entry(SlideshowHelpTopic.StopTimer, "stop auto-advance", SlideshowCommand.SetAutoAdvance),
            Entry(SlideshowHelpTopic.QuickTimer, "auto-advance every 0.5 / 1.5 seconds", SlideshowCommand.SetAutoAdvance),
            Entry(SlideshowHelpTopic.Pause, "pause auto-advance", SlideshowCommand.PauseOrNext),
        ],
        [
            Entry(SlideshowHelpTopic.Zoom, "zoom in / out", SlideshowCommand.ZoomIn, SlideshowCommand.ZoomOut),
            Entry(SlideshowHelpTopic.ActualSize, "actual size", SlideshowCommand.ActualSize),
            Entry(SlideshowHelpTopic.ResetView, "reset zoom and rotation", SlideshowCommand.ResetView),
            Entry(SlideshowHelpTopic.Rotate, "rotate left / right", SlideshowCommand.RotateLeft, SlideshowCommand.RotateRight),
            Entry(SlideshowHelpTopic.Flip, "flip", SlideshowCommand.Flip),
            Entry(SlideshowHelpTopic.Pan, "pan around when zoomed in", SlideshowCommand.KeypadPan),
        ],
        [
            Entry(SlideshowHelpTopic.Info, "cycle file info / EXIF / none", SlideshowCommand.CycleInfo),
            Entry(SlideshowHelpTopic.MoreExif, "more EXIF", SlideshowCommand.MoreExif),
            Entry(SlideshowHelpTopic.Path, "show full path", SlideshowCommand.TogglePath),
            Entry(SlideshowHelpTopic.Delete, "move current file to the Recycle Bin", SlideshowCommand.Delete),
            Entry(SlideshowHelpTopic.Random, "random order on / off (reshuffles when turned on)", SlideshowCommand.ToggleRandom),
            Entry(SlideshowHelpTopic.Help, "this help", SlideshowCommand.Help),
            Entry(SlideshowHelpTopic.Quit, "quit", SlideshowCommand.Quit),
        ],
    ];

    /// <summary>
    /// The help text with the platform's key names. A topic missing from <paramref name="keys"/> shows "?" rather than
    /// failing, so a forgotten entry is visible on screen instead of breaking the slideshow.
    /// </summary>
    /// <param name="footer">Mouse help and the like, after the keys.</param>
    public static string Build(IReadOnlyDictionary<SlideshowHelpTopic, string> keys, string? footer)
        => KeyHelp.Format(Title,
            Sections.Select(section => (IReadOnlyList<HelpLine>)section
                .Select(e => new HelpLine(keys.TryGetValue(e.Topic, out var k) ? k : "?", e.Description)).ToList()),
            footer);

    private static SlideshowHelpEntry Entry(SlideshowHelpTopic topic, string description, params SlideshowCommand[] commands)
        => new(topic, description, commands);
}
