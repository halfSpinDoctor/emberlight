namespace Emberlight.Core;

/// <summary>Copy/move helpers that never overwrite an existing file.</summary>
public static class FileTransfer
{
    /// <summary>Returns <paramref name="fileName"/> in <paramref name="directory"/>, or "name (2).ext", "name (3).ext"... if taken.</summary>
    public static string GetUniquePath(string directory, string fileName)
    {
        string candidate = Path.Combine(directory, fileName);
        if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        string stem = Path.GetFileNameWithoutExtension(fileName), ext = Path.GetExtension(fileName);
        for (int i = 2; i < 100_000; i++)
        {
            candidate = Path.Combine(directory, $"{stem} ({i}){ext}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        }
        throw new IOException("Could not find an unused file name.");
    }

    /// <summary>Copies a file into a folder under a non-colliding name. Returns the destination path.</summary>
    public static string CopyInto(string source, string destDirectory)
    {
        Directory.CreateDirectory(destDirectory);
        string dest = GetUniquePath(destDirectory, Path.GetFileName(source));
        File.Copy(source, dest, overwrite: false);
        return dest;
    }

    /// <summary>
    /// Moves a file into a folder under a non-colliding name. Returns the destination path.
    /// Moving into the folder the file is already in is a no-op that returns the original path.
    /// </summary>
    public static string MoveInto(string source, string destDirectory)
    {
        Directory.CreateDirectory(destDirectory);
        if (string.Equals(Path.GetFullPath(Path.GetDirectoryName(source) ?? ""),
                          Path.GetFullPath(destDirectory), PathUtil.Comparison))
            return source;
        string dest = GetUniquePath(destDirectory, Path.GetFileName(source));
        File.Move(source, dest, overwrite: false);
        return dest;
    }
}
