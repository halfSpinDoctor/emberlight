namespace Emberlight.Core;

/// <summary>What Copy To / Move To did: how many files went, how many didn't, and the first error.</summary>
/// <param name="AlreadyThere">Files a move left alone because they were already in the destination folder.</param>
public sealed record TransferResult(bool Move, int Done, int Failed, string? FirstError, int AlreadyThere = 0)
{
    /// <summary>The status-bar line, e.g. "Moved 3 file(s); 1 already in that folder; 1 failed: ...".</summary>
    public string Summary => $"{(Move ? "Moved" : "Copied")} {Done} file(s)"
        + (AlreadyThere > 0 ? $"; {AlreadyThere} already in that folder" : "")
        + (Failed > 0 ? $"; {Failed} failed: {FirstError}" : "");
}

/// <summary>Copy/move helpers that never overwrite an existing file.</summary>
public static class FileTransfer
{
    /// <summary>
    /// Copies or moves each file into <paramref name="destDirectory"/> (never overwriting). A file that can't be
    /// transferred is counted and skipped; <paramref name="moved"/> is called for each file that was moved away, so
    /// the browser can take it out of the grid. Moving a file into the folder it is already in leaves it where it is
    /// (and in the grid), and is counted as <see cref="TransferResult.AlreadyThere"/>.
    /// </summary>
    public static TransferResult TransferAll(IEnumerable<string> paths, string destDirectory, bool move, Action<string>? moved = null)
    {
        int done = 0, failed = 0, alreadyThere = 0;
        string? firstError = null;
        foreach (string path in paths)
        {
            try
            {
                if (move && IsAlreadyIn(path, destDirectory))
                {
                    alreadyThere++;
                    continue;
                }
                if (move)
                {
                    MoveInto(path, destDirectory);
                    moved?.Invoke(path);
                }
                else CopyInto(path, destDirectory);
                done++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed++;
                firstError ??= ex.Message;
            }
        }
        return new TransferResult(move, done, failed, firstError, alreadyThere);
    }

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
        if (IsAlreadyIn(source, destDirectory)) return source;
        string dest = GetUniquePath(destDirectory, Path.GetFileName(source));
        File.Move(source, dest, overwrite: false);
        return dest;
    }

    /// <summary>Whether <paramref name="path"/> is directly in <paramref name="directory"/> (trailing separators and case ignored).</summary>
    public static bool IsAlreadyIn(string path, string directory)
        => PathUtil.SamePath(Path.GetFullPath(Path.GetDirectoryName(path) ?? ""), Path.GetFullPath(directory));
}
