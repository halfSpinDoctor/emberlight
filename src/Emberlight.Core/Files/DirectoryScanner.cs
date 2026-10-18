using System.IO.Enumeration;

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

    /// <summary>
    /// Every non-hidden file under <paramref name="directory"/>. Windows hands over each entry's attributes for
    /// free, so it uses .NET's attribute filter. Elsewhere that filter makes .NET stat every single entry (a
    /// network round trip each on an SMB share: 3,098 files took 327 s cold and 27 s warm, against 5 ms without),
    /// so the Unix path skips the attribute check and treats a leading '.' as hidden, which is all .NET's own
    /// Unix "Hidden" means anyway.
    /// </summary>
    private static IEnumerable<string> EnumerateFiles(string directory, bool includeSubfolders)
    {
        if (OperatingSystem.IsWindows())
        {
            return Directory.EnumerateFiles(directory, "*", new EnumerationOptions
            {
                RecurseSubdirectories = includeSubfolders,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
                ReturnSpecialDirectories = false,
            });
        }
        return new FileSystemEnumerable<string>(directory, (ref FileSystemEntry e) => e.ToFullPath(), new EnumerationOptions
        {
            RecurseSubdirectories = includeSubfolders,
            IgnoreInaccessible = true,
            AttributesToSkip = 0,
            ReturnSpecialDirectories = false,
        })
        {
            ShouldIncludePredicate = (ref FileSystemEntry e) => !e.IsDirectory && e.FileName[0] != '.',
            ShouldRecursePredicate = (ref FileSystemEntry e) => e.FileName[0] != '.',
        };
    }

    /// <summary>How often (in files found) <see cref="Scan"/> reports progress.</summary>
    public const int ProgressInterval = 250;

    /// <param name="progress">Optional; receives the running count of matching files every <see cref="ProgressInterval"/> files.</param>
    public List<string> Scan(string directory, bool includeSubfolders, CancellationToken ct = default, IProgress<int>? progress = null)
    {
        var results = new List<string>();
        if (!Directory.Exists(directory)) return results;
        foreach (var path in EnumerateFiles(directory, includeSubfolders))
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
        foreach (var path in EnumerateFiles(directory, includeSubfolders))
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
        cache ??= new FileMetadataCache();
        var list = Scan(directory, includeSubfolders, ct, progress);
        cache.Prefetch(list, order.Key, ct);
        list.Sort(FileComparers.For(order, cache));
        return list;
    }
}
