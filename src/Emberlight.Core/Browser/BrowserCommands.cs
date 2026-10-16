namespace Emberlight.Core;

/// <summary>
/// What the browser window can be told to do from the keyboard, independent of which key asks for it. Each platform
/// maps its own keys to these (see MainWindow.MapWindowKey / MapGridKey on Windows).
/// </summary>
public enum BrowserCommand
{
    None,
    Rescan,
    StartSlideshow,
    OpenFolder,
    ToggleInfoPanel,
    ToggleSubfolders,
    ToggleRandom,
    LargerThumbnails,
    SmallerThumbnails,
    /// <summary>Move the selected files to the Recycle Bin.</summary>
    Delete,
    /// <summary>Rename the selected file (one file only).</summary>
    Rename,
    /// <summary>Move the selection in the grid by <see cref="BrowserInput.Move"/>.</summary>
    MoveSelection,
}

/// <param name="Move">For <see cref="BrowserCommand.MoveSelection"/>: which way.</param>
public readonly record struct BrowserInput(BrowserCommand Command, GridMove Move = GridMove.Left);

public static class BrowserCommands
{
    /// <summary>
    /// Whether a command applies now. Moving in the grid needs something in the grid; otherwise the key is left to the
    /// list itself. Everything else always runs (starting a slideshow from an empty grid just does nothing).
    /// </summary>
    public static bool CanRun(BrowserInput input, int itemCount) => input.Command switch
    {
        BrowserCommand.None => false,
        BrowserCommand.MoveSelection => itemCount > 0,
        _ => true,
    };
}
