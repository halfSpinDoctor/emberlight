namespace Emberlight.Core;

/// <summary>What the browser selects when a slideshow it started closes.</summary>
public enum CloseSelection
{
    None,
    /// <summary>Select the slideshow's last slide (it is in the grid).</summary>
    Select,
    /// <summary>The folder is still loading: remember the slide and select it once it arrives.</summary>
    SelectWhenLoaded,
}

/// <summary>
/// The browser following a slideshow that jumps between folders (Ctrl+Right / Ctrl+Left). The browser waits a
/// moment (<see cref="Delay"/>) after each jump, so holding the key down doesn't reload it for every folder passed;
/// the platform's timer calls <see cref="TakeFolderToOpen"/> when that pause is over, and again when the slideshow closes.
/// </summary>
public sealed class SlideshowFollow
{
    public static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(400);

    private string? _pending;

    /// <summary>The slideshow jumped to <paramref name="folder"/>; restart the pause.</summary>
    public void Jumped(string folder) => _pending = folder;

    /// <summary>
    /// The folder the browser should open now, or null: nothing is pending, the browser already shows it, or it no
    /// longer exists. Either way the pending folder is forgotten.
    /// </summary>
    public string? TakeFolderToOpen(string? currentFolder, Func<string, bool> folderExists)
    {
        string? folder = _pending;
        _pending = null;
        if (folder == null || string.Equals(folder, currentFolder, PathUtil.Comparison) || !folderExists(folder)) return null;
        return folder;
    }

    /// <param name="lastShown">The slide on screen when the slideshow closed.</param>
    /// <param name="isListed">Whether that file is in the browser's grid.</param>
    /// <param name="isLoading">Whether the browser is still loading a folder.</param>
    public static CloseSelection AfterClose(string? lastShown, bool isListed, bool isLoading)
        => lastShown == null ? CloseSelection.None
            : isListed ? CloseSelection.Select
            : isLoading ? CloseSelection.SelectWhenLoaded
            : CloseSelection.None;
}
