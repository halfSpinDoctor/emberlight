namespace Emberlight.Core;

/// <summary>A sibling folder that contains images, with its files already sorted.</summary>
/// <param name="Position">1-based position of the folder among *all* its sibling folders.</param>
/// <param name="Total">Number of sibling folders (including the current one and any without images).</param>
public sealed record SiblingFolder(string Path, IReadOnlyList<string> Files, int Position, int Total);

/// <summary>
/// "Next folder / previous folder" at the same level of the directory tree: the siblings of a folder are
/// the other folders in its parent, in natural name order (the same order as the folder tree). Folders
/// without displayable images are skipped; there is no wrap-around.
/// </summary>
public static class FolderNavigator
{
    /// <summary>
    /// Decides which folder "next/previous folder" is relative to.
    /// <paramref name="anchor"/> is the folder the slideshow was started from (or last jumped to). While the slide
    /// on screen is inside it, the anchor wins; this matters with "include subfolders", where the first image
    /// of an empty root folder lives in a subfolder but you still mean "the folder I started in". If the slide
    /// is outside the anchor (or there is no anchor), the slide's own folder is used and the anchor is dropped.
    /// </summary>
    public static string? ResolveReferenceFolder(string? anchor, string? currentImage, out string? updatedAnchor)
    {
        string? dir = currentImage == null ? null : System.IO.Path.GetDirectoryName(currentImage);
        if (dir == null)
        {
            updatedAnchor = anchor;
            return anchor;
        }
        if (anchor != null && PathUtil.IsSameOrDescendant(dir, anchor))
        {
            updatedAnchor = anchor;
            return anchor;
        }
        updatedAnchor = null;
        return dir;
    }

    /// <summary>Sub-folders of <paramref name="folder"/>'s parent (including <paramref name="folder"/> itself), in natural name order. Empty for a drive root.</summary>
    public static IReadOnlyList<string> GetSiblingFolders(string folder)
    {
        string full = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(folder));
        string? parent = System.IO.Path.GetDirectoryName(full);
        if (parent == null || !Directory.Exists(parent)) return Array.Empty<string>();
        return GetSubfolders(parent);
    }

    /// <summary>
    /// The visible sub-folders of <paramref name="folder"/> in natural name order (hidden and system folders are
    /// skipped). Empty if the folder is missing or can't be read. Can be slow on network shares, so the UI calls it
    /// off the UI thread.
    /// </summary>
    public static List<string> GetSubfolders(string folder)
    {
        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
            ReturnSpecialDirectories = false,
        };
        List<string> dirs;
        try { dirs = Directory.EnumerateDirectories(folder, "*", options).ToList(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return new(); }
        dirs.Sort((a, b) => NaturalStringComparer.Instance.Compare(
            System.IO.Path.GetFileName(a), System.IO.Path.GetFileName(b)));
        return dirs;
    }

    /// <summary>
    /// Finds the next (<paramref name="direction"/> &gt; 0) or previous (&lt; 0) sibling folder that has at least
    /// one displayable image, and scans and sorts its files. Returns null when there is no such folder
    /// (you are at the end), when <paramref name="currentFolder"/> is a drive root, or when it no longer exists.
    /// </summary>
    public static SiblingFolder? FindSibling(string currentFolder, int direction, DirectoryScanner scanner,
        IComparer<string> comparer, bool includeSubfolders, CancellationToken ct = default)
    {
        if (direction == 0) return null;
        int step = direction > 0 ? 1 : -1;

        var siblings = GetSiblingFolders(currentFolder);
        string current = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(currentFolder));
        int index = -1;
        for (int i = 0; i < siblings.Count; i++)
            if (string.Equals(siblings[i], current, PathUtil.Comparison)) { index = i; break; }
        if (index < 0) return null;

        for (int i = index + step; i >= 0 && i < siblings.Count; i += step)
        {
            ct.ThrowIfCancellationRequested();
            // Cheap test first: stop at the first image rather than listing a possibly huge folder just to skip it.
            if (!scanner.HasAnyImage(siblings[i], includeSubfolders, ct)) continue;

            var files = scanner.Scan(siblings[i], includeSubfolders, ct);
            if (files.Count == 0) continue;     // everything vanished between the two calls
            files.Sort(comparer);
            return new SiblingFolder(siblings[i], files, i + 1, siblings.Count);
        }
        return null;
    }
}
