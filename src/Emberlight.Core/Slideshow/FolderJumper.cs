namespace Emberlight.Core;

public enum FolderJumpOutcome
{
    /// <summary>A jump is already under way (key repeat): ignored, silently.</summary>
    Busy,
    /// <summary>There is no folder to go by (nothing shown yet).</summary>
    NoFolder,
    /// <summary>No next or previous folder with images: the end of the row.</summary>
    NotFound,
    /// <summary>The folders couldn't be read.</summary>
    Failed,
    /// <summary>Found one; <see cref="FolderJumpResult.Target"/> has its sorted files.</summary>
    Jumped,
}

/// <param name="Message">What to tell the user (a short on-screen note); null when there is nothing to say.</param>
public sealed record FolderJumpResult(FolderJumpOutcome Outcome, string? Message, SiblingFolder? Target)
{
    /// <summary>Whether to beep, as the original does when a jump can't happen.</summary>
    public bool ShouldBeep => Outcome is FolderJumpOutcome.NoFolder or FolderJumpOutcome.NotFound or FolderJumpOutcome.Failed;
}

/// <summary>
/// The slideshow's next/previous folder jump (Ctrl+Right / Ctrl+Left on Windows). Remembers the folder jumps are
/// relative to (<see cref="Anchor"/>: where the slideshow started, or the folder last jumped to, while the slide on
/// screen is inside it; see <see cref="FolderNavigator.ResolveReferenceFolder"/>), runs the search on a worker thread,
/// ignores key repeat while one is under way, and words the messages.
/// </summary>
public sealed class FolderJumper
{
    private readonly Func<string, int, SiblingFolder?> _find;

    /// <param name="startFolder">The folder the slideshow was started from, if there was one.</param>
    /// <param name="order">How to sort the files of a folder jumped to.</param>
    /// <param name="includeSubfolders">Whether folders include their subfolders' images.</param>
    public FolderJumper(string? startFolder, DirectoryScanner scanner, SortOrder order, bool includeSubfolders)
        // The comparer is created for each search (with its own metadata cache), not shared with the browser.
        : this(startFolder, (folder, direction) =>
            FolderNavigator.FindSibling(folder, direction, scanner, FileComparers.For(order), includeSubfolders))
    {
    }

    /// <param name="find">Finds the sibling folder (tests pass their own).</param>
    internal FolderJumper(string? startFolder, Func<string, int, SiblingFolder?> find)
    {
        _find = find;
        // Whether the start folder still exists is checked at the first jump, on a worker thread: the constructor
        // runs on the UI thread as the slideshow opens, and the folder may be on a share that has dropped.
        Anchor = string.IsNullOrWhiteSpace(startFolder) ? null : startFolder;
        _anchorUnchecked = Anchor != null;
    }

    private bool _anchorUnchecked;

    /// <summary>The folder jumps are currently relative to, if any (the start folder counts until the first jump
    /// finds it missing).</summary>
    public string? Anchor { get; private set; }

    /// <summary>A search is under way.</summary>
    public bool IsBusy { get; private set; }

    public static string SearchingText(int direction) => direction > 0 ? "Next folder…" : "Previous folder…";

    /// <summary>
    /// Looks for the next (<paramref name="direction"/> &gt; 0) or previous folder with images, relative to the anchor
    /// or to <paramref name="currentSlide"/>'s folder. <paramref name="searching"/> is called with a "Next folder…" note
    /// once the search starts (it can take a moment on a big or slow folder). Continues on the caller's thread.
    /// </summary>
    public async Task<FolderJumpResult> JumpAsync(int direction, string? currentSlide, Action<string>? searching = null)
    {
        if (IsBusy) return new FolderJumpResult(FolderJumpOutcome.Busy, null, null);
        IsBusy = true;
        SiblingFolder? target;
        string? folder;
        try
        {
            if (_anchorUnchecked)
            {
                _anchorUnchecked = false;
                string? start = Anchor;
                if (start != null && !await Task.Run(() => Directory.Exists(start))) Anchor = null;
            }
            folder = FolderNavigator.ResolveReferenceFolder(Anchor, currentSlide, out var anchor);
            Anchor = anchor;
            if (folder == null) return new FolderJumpResult(FolderJumpOutcome.NoFolder, null, null);

            searching?.Invoke(SearchingText(direction));
            target = await Task.Run(() => _find(folder, direction));
        }
        catch (Exception)
        {
            return new FolderJumpResult(FolderJumpOutcome.Failed, "Couldn't read that folder", null);
        }
        finally
        {
            IsBusy = false;
        }

        if (target == null)
            return new FolderJumpResult(FolderJumpOutcome.NotFound, direction > 0 ? "No next folder" : "No previous folder", null);
        Anchor = target.Path;
        return new FolderJumpResult(FolderJumpOutcome.Jumped,
            $"{System.IO.Path.GetFileName(target.Path)}   ({target.Position} of {target.Total})", target);
    }
}
