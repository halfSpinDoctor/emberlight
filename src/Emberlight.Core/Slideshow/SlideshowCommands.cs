namespace Emberlight.Core;

/// <summary>
/// Everything the slideshow can be told to do, independent of which key or gesture asks for it. Each platform maps
/// its own keys to these (see SlideshowWindow.MapKey on Windows).
/// </summary>
public enum SlideshowCommand
{
    None,
    Next,
    Previous,
    /// <summary>Space: pauses a running auto-advance, otherwise moves on.</summary>
    PauseOrNext,
    First,
    Last,
    BackPage,
    ForwardPage,
    NextFolder,
    PreviousFolder,
    /// <summary>Starts auto-advance every <see cref="SlideshowInput.Value"/> seconds, moving on once if it was off.</summary>
    StartAutoAdvance,
    /// <summary>Sets auto-advance to <see cref="SlideshowInput.Value"/> seconds (0 = off) without moving.</summary>
    SetAutoAdvance,
    /// <summary>Pans a zoomed-in picture towards keypad digit <see cref="SlideshowInput.Value"/> (1-9, 5 = centre).</summary>
    KeypadPan,
    ZoomIn,
    ZoomOut,
    ActualSize,
    ResetView,
    RotateLeft,
    RotateRight,
    Flip,
    CycleInfo,
    MoreExif,
    TogglePath,
    Help,
    Delete,
    ToggleRandom,
    Quit,
}

/// <param name="Value">Seconds for the auto-advance commands, the digit for <see cref="SlideshowCommand.KeypadPan"/>.</param>
/// <param name="InOrder">For Next, Previous and PauseOrNext in a shuffled show: step in the original (sorted) order.</param>
public readonly record struct SlideshowInput(SlideshowCommand Command, double Value = 0, bool InOrder = false);

/// <summary>What a command does once the show has run past its last slide (every remaining file was deleted).</summary>
public enum PastEndRule
{
    /// <summary>Works as usual.</summary>
    Run,
    /// <summary>Does nothing, but counts as handled (the key isn't passed on).</summary>
    Swallow,
    /// <summary>Does nothing and isn't handled.</summary>
    Ignore,
}

public static class SlideshowCommands
{
    /// <summary>Slides moved by <see cref="SlideshowCommand.BackPage"/> and <see cref="SlideshowCommand.ForwardPage"/>.</summary>
    public const int PageSize = 10;

    /// <summary>
    /// After the last slide (as in the original) only going back, first/last, the folder jumps, help and quit still
    /// work. Space is swallowed rather than moving on (Shift+Space, which goes back, is <see cref="SlideshowCommand.Previous"/>).
    /// </summary>
    public static PastEndRule WhenPastEnd(SlideshowCommand command) => command switch
    {
        SlideshowCommand.Previous or SlideshowCommand.First or SlideshowCommand.Last or SlideshowCommand.BackPage
            or SlideshowCommand.NextFolder or SlideshowCommand.PreviousFolder
            or SlideshowCommand.Help or SlideshowCommand.Quit => PastEndRule.Run,
        SlideshowCommand.PauseOrNext => PastEndRule.Swallow,
        _ => PastEndRule.Ignore,
    };
}
