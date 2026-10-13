namespace Emberlight.Core;

/// <summary>What to show for a set of command-line paths.</summary>
/// <param name="Files">Image files in display order.</param>
/// <param name="StartIndex">Which file to start on.</param>
/// <param name="Folder">The folder being shown, when the paths all came from one (otherwise null).</param>
public sealed record StartupTarget(IReadOnlyList<string> Files, int StartIndex, string? Folder);

public static class StartupArgs
{
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
                    if (!files.Contains(p, StringComparer.OrdinalIgnoreCase)) files.Add(p);
                    files.Sort(comparer);
                    int idx = files.FindIndex(f => string.Equals(f, p, StringComparison.OrdinalIgnoreCase));
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
            var distinct = all.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
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
