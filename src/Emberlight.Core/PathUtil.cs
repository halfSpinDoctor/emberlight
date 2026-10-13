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

    private static string Trim(string p) => p.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
