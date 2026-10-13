namespace Emberlight.Core;

/// <summary>Finds the image files to display for a folder.</summary>
public sealed class DirectoryScanner
{
    private readonly ImageFileTypes _types;

    public DirectoryScanner(ImageFileTypes types) => _types = types;

    /// <summary>Hidden/system files and macOS "._" companion files are skipped.</summary>
    public bool ShouldShowFile(string path)
    {
        string name = Path.GetFileName(path);
        if (name.Length == 0 || name.StartsWith("._", StringComparison.Ordinal)) return false;
        if (!_types.IsSupportedFile(path)) return false;
        try
        {
            var attrs = File.GetAttributes(path);
            return (attrs & (FileAttributes.Hidden | FileAttributes.System | FileAttributes.Directory)) == 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>How often (in files found) <see cref="Scan"/> reports progress.</summary>
    public const int ProgressInterval = 250;

    /// <param name="progress">Optional; receives the running count of matching files every <see cref="ProgressInterval"/> files.</param>
    public List<string> Scan(string directory, bool includeSubfolders, CancellationToken ct = default, IProgress<int>? progress = null)
    {
        var results = new List<string>();
        if (!Directory.Exists(directory)) return results;
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = includeSubfolders,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
            ReturnSpecialDirectories = false,
        };
        foreach (var path in Directory.EnumerateFiles(directory, "*", options))
        {
            ct.ThrowIfCancellationRequested();
            string name = Path.GetFileName(path);
            if (name.StartsWith("._", StringComparison.Ordinal)) continue;
            if (_types.IsSupportedFile(path))
            {
                results.Add(path);
                if (progress != null && results.Count % ProgressInterval == 0) progress.Report(results.Count);
            }
        }
        return results;
    }

    /// <summary>True as soon as one displayable image is found; stops enumerating immediately.</summary>
    public bool HasAnyImage(string directory, bool includeSubfolders, CancellationToken ct = default)
    {
        if (!Directory.Exists(directory)) return false;
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = includeSubfolders,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
            ReturnSpecialDirectories = false,
        };
        foreach (var path in Directory.EnumerateFiles(directory, "*", options))
        {
            ct.ThrowIfCancellationRequested();
            if (Path.GetFileName(path).StartsWith("._", StringComparison.Ordinal)) continue;
            if (_types.IsSupportedFile(path)) return true;
        }
        return false;
    }

    public List<string> ScanSorted(string directory, bool includeSubfolders, SortOrder order,
        FileMetadataCache? cache = null, CancellationToken ct = default, IProgress<int>? progress = null)
    {
        var list = Scan(directory, includeSubfolders, ct, progress);
        list.Sort(FileComparers.For(order, cache));
        return list;
    }
}
