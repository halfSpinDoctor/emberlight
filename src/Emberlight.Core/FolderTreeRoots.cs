namespace Emberlight.Core;

/// <summary>The top level of the browser's folder tree: starred shortcuts first, then the drives.</summary>
public static class FolderTreeRoots
{
    /// <summary>Starts every shortcut's label, to tell shortcuts from drives that may name the same folder.</summary>
    public const string ShortcutMark = "★ ";

    public static bool IsShortcut(string label) => label.StartsWith(ShortcutMark, StringComparison.Ordinal);

    /// <summary>
    /// The shortcuts, in order: Pictures, Desktop, Documents and Downloads, for those that exist. Checks the disk, so
    /// call it off the UI thread.
    /// </summary>
    /// <param name="folderExists">The existence check (tests pass their own).</param>
    public static List<(string Path, string Label)> Shortcuts(Func<string, bool>? folderExists = null)
    {
        folderExists ??= Directory.Exists;
        var candidates = new (string? Path, string Name)[]
        {
            (Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Pictures"),
            (Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Desktop"),
            (Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Documents"),
            (Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"), "Downloads"),
        };
        var found = new List<(string Path, string Label)>();
        foreach (var (path, name) in candidates)
            if (!string.IsNullOrWhiteSpace(path) && folderExists(path)) found.Add((path, ShortcutMark + name));
        return found;
    }

    /// <summary>
    /// Where a drive goes in the tree once it reports ready: after the shortcuts (from
    /// <paramref name="firstDriveIndex"/>) and after the drives before it in letter order that are already in.
    /// Drives answer in any order (an offline network drive can take many seconds), so each is slotted in as it does.
    /// </summary>
    /// <param name="ready">Which drives (in letter order) are in the tree, including this one.</param>
    public static int DriveSlot(IReadOnlyList<bool> ready, int index, int firstDriveIndex)
        => firstDriveIndex + ready.Take(index).Count(r => r);
}
