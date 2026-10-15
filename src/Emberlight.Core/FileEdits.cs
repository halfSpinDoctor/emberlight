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
    /// <summary>Deleting more files than this asks for confirmation first.</summary>
    public const int ConfirmAbove = 10;

    public static bool NeedsConfirmation(int count) => count > ConfirmAbove;

    public static string ConfirmationText(int count) => $"Move {count} files to the Recycle Bin?";

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
