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
    /// How often the tree looks for drives that appeared or went away (a network drive reconnecting with a VPN, a USB
    /// stick). Listing the letters is cheap; only new or not-yet-ready letters get the slower readiness check.
    /// </summary>
    public static readonly TimeSpan DrivePollInterval = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Where a drive goes among the tree's top-level entries after the shortcuts: in letter order among the drives,
    /// before any network shares (which come last). Drives answer in any order (an offline network drive can take
    /// many seconds), so each is slotted in as it does. Add the number of shortcuts to get the tree index.
    /// </summary>
    /// <param name="rootsAfterShortcuts">The folders of the entries after the shortcuts, in tree order.</param>
    public static int DriveInsertIndex(IReadOnlyList<string> rootsAfterShortcuts, string driveRoot)
    {
        string key = PathUtil.TrimSeparators(driveRoot);
        int i = 0;
        while (i < rootsAfterShortcuts.Count
               && PathUtil.NetworkShareLabel(rootsAfterShortcuts[i]) == null
               && PathUtil.Comparer.Compare(PathUtil.TrimSeparators(rootsAfterShortcuts[i]), key) < 0)
            i++;
        return i;
    }

    /// <summary>
    /// One pass of the drive check: letters present but not in the tree (and not already being checked) need a
    /// readiness check, which adds them if they answer; a letter that isn't ready yet (a mapped drive while the VPN is
    /// down) comes up again on the next pass. Drives in the tree whose letter has gone are removed.
    /// </summary>
    public static DriveUpdate CompareDrives(IEnumerable<string> inTree, IEnumerable<string> checking, IEnumerable<string> present)
    {
        static HashSet<string> Keys(IEnumerable<string> roots) => roots.Select(PathUtil.TrimSeparators).ToHashSet(PathUtil.Comparer);
        var tree = Keys(inTree);
        var busy = Keys(checking);
        var now = present.ToList();
        var nowKeys = Keys(now);
        return new DriveUpdate(
            now.Where(r => !tree.Contains(PathUtil.TrimSeparators(r)) && !busy.Contains(PathUtil.TrimSeparators(r))).ToList(),
            inTree.Where(r => !nowKeys.Contains(PathUtil.TrimSeparators(r))).ToList());
    }
}

/// <param name="ToCheck">Drive roots to check for readiness (and add if ready).</param>
/// <param name="Removed">Drive roots in the tree that no longer exist.</param>
public sealed record DriveUpdate(IReadOnlyList<string> ToCheck, IReadOnlyList<string> Removed);

/// <summary>What changed in a folder's list of subfolders, as steps that keep the folders already shown in the tree.</summary>
/// <param name="Removed">Subfolders to take out.</param>
/// <param name="Inserted">Subfolders to add, as (index, folder), applied in this order after the removals.</param>
public sealed record ChildrenUpdate(IReadOnlyList<string> Removed, IReadOnlyList<(int Index, string Path)> Inserted)
{
    public bool IsEmpty => Removed.Count == 0 && Inserted.Count == 0;
}

public static class TreeChildren
{
    /// <summary>
    /// Compares the subfolders a tree folder shows with the ones it has now (both in the tree's natural order).
    /// Folders still there keep their tree entries, and so their open/closed state; new ones are inserted where they
    /// belong.
    /// </summary>
    public static ChildrenUpdate Compare(IReadOnlyList<string> shown, IReadOnlyList<string> current)
    {
        var currentSet = current.ToHashSet(PathUtil.Comparer);
        var removed = shown.Where(s => !currentSet.Contains(s)).ToList();
        var kept = shown.Where(currentSet.Contains).ToList();
        var inserted = new List<(int, string)>();
        int k = 0;
        for (int i = 0; i < current.Count; i++)
        {
            if (k < kept.Count && PathUtil.Comparer.Equals(kept[k], current[i])) k++;
            else inserted.Add((i, current[i]));
        }
        return new ChildrenUpdate(removed, inserted);
    }
}
