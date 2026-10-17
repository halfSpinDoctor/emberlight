using System.Text.RegularExpressions;

namespace Emberlight.Core;

public static class PathUtil
{
    /// <summary>
    /// How file paths are compared everywhere in the app: ignoring case, as Windows (and macOS by default) does.
    /// One place to change if a front end ever needs case-sensitive paths.
    /// </summary>
    public static StringComparer Comparer => StringComparer.OrdinalIgnoreCase;

    /// <inheritdoc cref="Comparer"/>
    public const StringComparison Comparison = StringComparison.OrdinalIgnoreCase;

    /// <summary>Whether two paths name the same folder, ignoring case and trailing separators (no other normalising).</summary>
    public static bool SamePath(string? a, string? b)
        => a == null || b == null ? a == b : string.Equals(TrimSeparators(a), TrimSeparators(b), Comparison);

    public static string TrimSeparators(string path) => path.TrimEnd('\\', '/');

    /// <summary>
    /// The root of <paramref name="path"/> (a drive, share or "/") followed by each folder down to the path itself,
    /// e.g. C:\, C:\Pics, C:\Pics\2020. Empty for an invalid path.
    /// </summary>
    public static IReadOnlyList<string> AncestorChain(string path)
    {
        string full;
        try { full = Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return []; }

        string root = Path.GetPathRoot(full) ?? "";
        var chain = new List<string> { root };
        string acc = root;
        foreach (string segment in full[root.Length..].Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries))
        {
            acc = Path.Combine(acc, segment);
            chain.Add(acc);
        }
        return chain;
    }

    /// <summary>The path itself if it is an existing folder, else its nearest existing parent; null if there is none.</summary>
    public static string? NearestExistingFolder(string? path)
    {
        string? p = path;
        while (!string.IsNullOrEmpty(p) && !Directory.Exists(p)) p = Path.GetDirectoryName(p);
        return string.IsNullOrEmpty(p) ? null : p;
    }

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
            if (a.Equals(b, Comparison)) return true;
            return a.Length > b.Length
                && a.StartsWith(b, Comparison)
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

    /// <summary>
    /// Whether <paramref name="path"/> matches <paramref name="pattern"/>: '*' matches any run of characters
    /// (including path separators, so "/System/*" matches everything under /System, not just one level down);
    /// everything else is matched literally, using the same case rule as <see cref="Comparison"/>.
    /// </summary>
    public static bool MatchesPattern(string path, string pattern)
    {
        string regex = "^" + string.Join(".*", TrimSeparators(pattern).Split('*').Select(Regex.Escape)) + "$";
        return Regex.IsMatch(TrimSeparators(path), regex, RegexOptions.IgnoreCase);
    }

    /// <summary>Whether <paramref name="path"/> matches any of <paramref name="patterns"/> (see <see cref="MatchesPattern"/>).</summary>
    public static bool MatchesAnyPattern(string path, IEnumerable<string> patterns) => patterns.Any(p => MatchesPattern(path, p));
}
