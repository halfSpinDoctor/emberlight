namespace Emberlight.Core;

/// <summary>What to show for a set of command-line paths.</summary>
/// <param name="Files">Image files in display order.</param>
/// <param name="StartIndex">Which file to start on.</param>
/// <param name="Folder">The folder being shown, when the paths all came from one (otherwise null).</param>
public sealed record StartupTarget(IReadOnlyList<string> Files, int StartIndex, string? Folder)
{
    /// <summary>
    /// A slideshow started from the browser. Several files selected: just those, in grid order, from the first.
    /// One (or none): the whole grid, starting on the selected file.
    /// </summary>
    /// <param name="gridPaths">Every file in the grid, in grid order.</param>
    /// <param name="selectedPaths">The selected files, in any order.</param>
    public static StartupTarget FromSelection(IReadOnlyList<string> gridPaths, IReadOnlyCollection<string> selectedPaths, string? folder)
    {
        if (selectedPaths.Count > 1)
        {
            var wanted = selectedPaths.ToHashSet(PathUtil.Comparer);
            return new StartupTarget(gridPaths.Where(wanted.Contains).ToList(), 0, folder);
        }
        int start = 0;
        if (selectedPaths.Count == 1)
        {
            string selected = selectedPaths.First();
            for (int i = 0; i < gridPaths.Count; i++)
                if (PathUtil.Comparer.Equals(gridPaths[i], selected)) { start = i; break; }
        }
        return new StartupTarget(gridPaths.ToList(), start, folder);
    }
}

public static class StartupArgs
{
    /// <summary>
    /// The folder the browser opens at startup: the folder given on the command line, or the folder of a file given
    /// there (which is then selected: <paramref name="fileToSelect"/>); otherwise the last folder or the chosen
    /// pictures folder, per the settings; and if that has gone, the user's Pictures folder, then their home folder.
    /// </summary>
    public static string BrowserFolder(string? startPath, AppSettings settings, out string? fileToSelect)
    {
        fileToSelect = null;
        string? candidate = null;
        if (startPath != null)
        {
            if (Directory.Exists(startPath)) candidate = startPath;
            else if (File.Exists(startPath))
            {
                candidate = Path.GetDirectoryName(Path.GetFullPath(startPath));
                fileToSelect = Path.GetFullPath(startPath);
            }
        }
        candidate ??= settings.StartupOption == StartupFolder.LastFolder ? settings.LastFolderPath : settings.PicturesFolderPath;
        if (string.IsNullOrWhiteSpace(candidate) || !Directory.Exists(candidate))
            candidate = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        if (string.IsNullOrWhiteSpace(candidate) || !Directory.Exists(candidate))
            candidate = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return candidate;
    }

    /// <summary>
    /// Interprets command-line arguments (files and/or folders, e.g. from "Open with" or drag-and-drop).
    /// One file: show its whole folder, starting on that file. One folder: show that folder.
    /// Several paths: show exactly those files (plus the contents of any folders).
    /// Returns null if nothing displayable was given.
    /// </summary>
    public static StartupTarget? Resolve(IEnumerable<string> args, DirectoryScanner scanner,
        IComparer<string> comparer, bool includeSubfolders = false)
    {
        var paths = args.Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a.Trim().Trim('"')).ToList();
        if (paths.Count == 0) return null;

        try
        {
            if (paths.Count == 1)
            {
                string p = Path.GetFullPath(paths[0]);
                if (Directory.Exists(p))
                {
                    var files = scanner.Scan(p, includeSubfolders);
                    files.Sort(comparer);
                    return new StartupTarget(files, 0, p);
                }
                if (File.Exists(p) && scanner.ShouldShowFile(p))
                {
                    string dir = Path.GetDirectoryName(p)!;
                    var files = scanner.Scan(dir, false);
                    if (!files.Contains(p, PathUtil.Comparer)) files.Add(p);
                    files.Sort(comparer);
                    int idx = files.FindIndex(f => string.Equals(f, p, PathUtil.Comparison));
                    return new StartupTarget(files, Math.Max(0, idx), dir);
                }
                return null;
            }

            var all = new List<string>();
            foreach (var raw in paths)
            {
                string p = Path.GetFullPath(raw);
                if (Directory.Exists(p)) all.AddRange(scanner.Scan(p, includeSubfolders));
                else if (File.Exists(p) && scanner.ShouldShowFile(p)) all.Add(p);
            }
            var distinct = all.Distinct(PathUtil.Comparer).ToList();
            if (distinct.Count == 0) return null;
            distinct.Sort(comparer);
            return new StartupTarget(distinct, 0, null);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
