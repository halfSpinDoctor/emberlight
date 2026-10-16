namespace Emberlight.Core;

/// <summary>What a rotate command did: the files it changed and how many it had to leave alone.</summary>
public sealed record RotateResult(IReadOnlyList<string> Updated, int Skipped)
{
    /// <summary>The status bar line for this result.</summary>
    public string Summary => Updated.Count == 0 && Skipped == 0 ? "Nothing selected"
        : $"Updated {Updated.Count} file(s)" + (Skipped > 0 ? $"; {Skipped} skipped (no EXIF orientation tag to change)" : "");
}

/// <summary>
/// The browser's Rotate Left/Right and Reset Orientation commands. They rewrite the file's EXIF orientation
/// value (two bytes; the pixels are untouched, so this is lossless and instant). Only files that already
/// carry an orientation tag can be changed this way; the others are skipped.
/// </summary>
public static class OrientationEditor
{
    /// <summary>
    /// The orientation value to write: the current one turned by <paramref name="degreesCw"/>, or 1 (upright)
    /// when it is null. Null when the file has no orientation tag to change.
    /// </summary>
    public static int? NewOrientation(ExifData? data, int? degreesCw)
    {
        if (data == null || data.Orientation == 0) return null;
        return degreesCw is { } d ? data.ImageOrientation.RotatedBy(d).ToExif() : 1;
    }

    /// <summary>Rotates each file in turn (null = back to upright); files that can't be changed are counted as skipped.</summary>
    public static RotateResult Rotate(IEnumerable<string> paths, int? degreesCw, bool preserveModifiedTime)
    {
        var updated = new List<string>();
        int skipped = 0;
        foreach (string path in paths)
        {
            var data = ImageFileTypes.MayHaveExif(path) ? ExifReader.Read(path) : null;
            if (NewOrientation(data, degreesCw) is { } value && ExifReader.TrySetOrientation(path, value, preserveModifiedTime))
                updated.Add(path);
            else skipped++;
        }
        return new RotateResult(updated, skipped);
    }
}

/// <summary>Moves a file somewhere it can be recovered from (the Windows Recycle Bin, the macOS Trash). Platform code implements it.</summary>
public interface IRecycleBin
{
    bool TryDelete(string path, out string? error);
}

/// <summary>What deleting a selection did.</summary>
public sealed record DeleteResult(IReadOnlyList<string> Deleted, int Failed, string? FirstError)
{
    /// <summary>The status bar line when some files couldn't be deleted; null when all went.</summary>
    public string? FailureText => Failed > 0 ? $"{Failed} file(s) couldn't be deleted: {FirstError}" : null;
}

/// <summary>The browser's Delete command: when to ask first, the deleting itself, and what is selected afterwards.</summary>
public static class FileDeletion
{
    /// <summary>
    /// The question asked before every delete (Yes/No): the file's name when there is one, otherwise how many.
    /// Null when there is nothing to delete.
    /// </summary>
    public static string? ConfirmationText(IReadOnlyList<string> paths) => paths.Count switch
    {
        0 => null,
        1 => $"Are you sure you want to send \"{Path.GetFileName(paths[0])}\" to the Recycle Bin?",
        _ => $"Are you sure you want to send these {paths.Count} files to the Recycle Bin?",
    };

    /// <summary>Deletes each file in turn; <paramref name="deleted"/> runs for each one that went.</summary>
    public static DeleteResult Delete(IEnumerable<string> paths, IRecycleBin bin, Action<string>? deleted = null)
    {
        var done = new List<string>();
        int failed = 0;
        string? firstError = null;
        foreach (string path in paths)
        {
            if (bin.TryDelete(path, out string? error))
            {
                done.Add(path);
                deleted?.Invoke(path);
            }
            else
            {
                failed++;
                firstError ??= error;
            }
        }
        return new DeleteResult(done, failed, firstError);
    }

    /// <summary>
    /// The item to select after deleting: the one now at the old selection's position (the next file),
    /// or the last one if the deleted files were at the end. Null when the list is empty.
    /// </summary>
    public static int? SelectionAfter(int previousIndex, int remainingCount)
        => remainingCount > 0 ? Math.Clamp(previousIndex, 0, remainingCount - 1) : null;
}

/// <summary>What renaming a file did: its new path, or why it couldn't be renamed.</summary>
public sealed record RenameResult(string? NewPath, string? Error)
{
    /// <summary>The status bar line when the rename failed; null when it worked.</summary>
    public string? FailureText => Error != null ? $"Couldn't rename the file: {Error}" : null;
}

/// <summary>
/// The browser's Rename command (one file at a time): checking the new name as it is typed, the question asked when
/// the extension changes, and the rename itself. The name rules are Windows' (the app is Windows-only), so they are
/// the same whichever system runs the tests.
/// </summary>
public static class FileRename
{
    /// <summary>The characters Windows doesn't allow in a file name (control characters aside).</summary>
    public const string InvalidCharacters = "\\/:*?\"<>|";

    /// <summary>The longest name NTFS allows for one file.</summary>
    public const int MaxNameLength = 255;

    /// <summary>The status bar line when Rename is asked for with no file or several files selected.</summary>
    public const string SelectOneFile = "Select one file to rename.";

    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>The name as it will be used: typed spaces at either end are dropped, as Explorer does.</summary>
    public static string Clean(string typed) => typed.Trim();

    /// <summary>
    /// The part of the name the dialog selects at first, so typing replaces the name and keeps the extension:
    /// everything before the last dot, or the whole name when there is no extension (or it starts with its only dot).
    /// </summary>
    public static (int Start, int Length) StemSelection(string fileName)
    {
        int dot = fileName.LastIndexOf('.');
        return (0, dot > 0 ? dot : fileName.Length);
    }

    /// <summary>
    /// Why <paramref name="typed"/> can't be the new name of <paramref name="path"/>, as a line for the dialog; null
    /// when it can. The unchanged name is not a problem (renaming to it does nothing). A change of case only is
    /// allowed: on Windows the "existing" file with that name is the file itself.
    /// </summary>
    public static string? Problem(string path, string typed)
    {
        string name = Clean(typed);
        if (name.Length == 0) return "Type a name for the file.";
        if (name.Any(c => c < ' ' || InvalidCharacters.Contains(c)))
            return $"A file name can't contain any of these characters: {string.Join(" ", InvalidCharacters.ToCharArray())}";
        if (name.EndsWith('.')) return "A file name can't end with a dot.";
        if (name.Length > MaxNameLength) return $"A file name can't be longer than {MaxNameLength} characters.";
        int dot = name.IndexOf('.');
        if (Reserved.Contains((dot < 0 ? name : name[..dot]).TrimEnd()))
            return $"\"{name}\" is a name Windows reserves for devices.";
        string target = Path.Combine(Path.GetDirectoryName(path) ?? "", name);
        if (!PathUtil.SamePath(target, path) && (File.Exists(target) || Directory.Exists(target)))
            return $"There is already a file named \"{name}\" in this folder.";
        return null;
    }

    /// <summary>Whether <paramref name="typed"/> is exactly the current name (nothing to do).</summary>
    public static bool IsUnchanged(string path, string typed) => Clean(typed) == Path.GetFileName(path);

    /// <summary>
    /// The Yes/No question asked before a rename that changes the extension; null when it stays (ignoring case).
    /// <paramref name="stillShown"/>: whether the browser shows files with the new extension; if not, the question
    /// says the file will leave the grid.
    /// </summary>
    public static string? ExtensionChangeQuestion(string path, string typed, bool stillShown)
    {
        string oldExt = Path.GetExtension(path), newExt = Path.GetExtension(Clean(typed));
        if (oldExt.Equals(newExt, StringComparison.OrdinalIgnoreCase)) return null;
        string change = newExt.Length == 0 ? $"If you remove the extension \"{oldExt}\""
            : oldExt.Length == 0 ? $"If you add the extension \"{newExt}\""
            : $"If you change the extension from \"{oldExt}\" to \"{newExt}\"";
        return change + ", the file may not open properly."
            + (stillShown ? "" : " Emberlight doesn't show files of that type, so it will disappear from the browser.")
            + "\n\nAre you sure you want to change it?";
    }

    /// <summary>
    /// Renames the file in its folder (never overwriting). An unchanged name does nothing and returns the same path.
    /// A change of case only goes through a temporary name, so it works however the file system treats case.
    /// </summary>
    public static RenameResult Rename(string path, string typed)
    {
        if (IsUnchanged(path, typed)) return new RenameResult(path, null);
        if (Problem(path, typed) is { } problem) return new RenameResult(null, problem);
        string target = Path.Combine(Path.GetDirectoryName(path) ?? "", Clean(typed));
        try
        {
            if (PathUtil.SamePath(target, path))
            {
                string temp = FileTransfer.GetUniquePath(Path.GetDirectoryName(path) ?? "", Path.GetFileName(path) + ".renaming");
                File.Move(path, temp);
                try { File.Move(temp, target); }
                catch (Exception) { File.Move(temp, path); throw; }   // put it back under its old name
            }
            else File.Move(path, target, overwrite: false);
            return new RenameResult(target, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new RenameResult(null, ex.Message);
        }
    }
}
