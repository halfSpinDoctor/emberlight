namespace Emberlight.Core;

public static class PathUtil
{
    /// <summary>
    /// True if <paramref name="path"/> is <paramref name="root"/> itself or lies somewhere beneath it.
    /// Comparison is case-insensitive (Windows semantics) and ignores trailing separators, so
    /// "C:\Pics2" is *not* inside "C:\Pics". Invalid paths are never "inside" anything.
    /// </summary>
    public static bool IsSameOrDescendant(string path, string root)
    {
        try
        {
            string a = Trim(Path.GetFullPath(path)), b = Trim(Path.GetFullPath(root));
            if (a.Equals(b, StringComparison.OrdinalIgnoreCase)) return true;
            return a.Length > b.Length
                && a.StartsWith(b, StringComparison.OrdinalIgnoreCase)
                && (a[b.Length] == Path.DirectorySeparatorChar || a[b.Length] == Path.AltDirectorySeparatorChar);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>
    /// Whether "Include subfolders" stays on when the browser moves to <paramref name="newPath"/>, given the folder
    /// where it was switched on (<paramref name="subfolderRoot"/>) and the user's <see cref="SubfolderMode"/>.
    /// </summary>
    public static bool SubfoldersStayOn(SubfolderMode mode, string newPath, string subfolderRoot)
        => mode == SubfolderMode.Always || IsSameOrDescendant(newPath, subfolderRoot);

    /// <summary>
    /// For a network share root such as <c>\\server\share</c>, the folder-tree label Explorer would use:
    /// <c>share (\\server)</c>. Null for anything that isn't a UNC share (drive paths, device paths like <c>\\?\</c>).
    /// </summary>
    public static string? NetworkShareLabel(string? shareRoot)
    {
        if (string.IsNullOrEmpty(shareRoot) || shareRoot.Length < 2) return null;
        bool unc = shareRoot[0] is '\\' or '/' && shareRoot[1] is '\\' or '/';
        if (!unc || shareRoot.StartsWith(@"\\?\") || shareRoot.StartsWith(@"\\.\")) return null;
        var parts = shareRoot.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 ? $@"{parts[1]} (\\{parts[0]})" : null;
    }

    private static string Trim(string p) => p.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
