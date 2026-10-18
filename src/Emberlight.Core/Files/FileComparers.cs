using System.Collections.Concurrent;

namespace Emberlight.Core;

/// <summary>
/// Per-file metadata used by sorting (modified time, size, creation time, EXIF date),
/// cached so a sort of N files stats each file once rather than O(N log N) times.
/// Create one per window/sort and call <see cref="Invalidate"/> when the file watcher reports a change.
/// </summary>
public sealed class FileMetadataCache
{
    private sealed class Entry
    {
        public bool StatOk;
        public DateTime LastWriteUtc, CreatedLocal;
        public long Length;
        public bool ExifResolved;
        public DateTime? SortDate;
    }

    private readonly ConcurrentDictionary<string, Entry> _entries = new(PathUtil.Comparer);

    /// <summary>How many files <see cref="Prefetch"/> examines at once. Reading a file's dates or EXIF on a network
    /// share is mostly waiting for the server, so overlapping the waits is what makes it fast.</summary>
    public const int PrefetchParallelism = 16;

    public void Invalidate(string path) => _entries.TryRemove(path, out _);
    public void Clear() => _entries.Clear();

    // The file-system read happens outside any lock (GetOrAdd may run it twice for one path in a race, harmlessly),
    // so several threads can be waiting on the disk or network at the same time.
    private Entry Get(string path) => _entries.GetOrAdd(path, static p =>
    {
        var e = new Entry();
        try
        {
            var fi = new FileInfo(p);
            if (fi.Exists)
            {
                e.StatOk = true;
                e.LastWriteUtc = fi.LastWriteTimeUtc;
                e.CreatedLocal = fi.CreationTime;
                e.Length = fi.Length;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return e;
    });

    public bool TryGetLastWrite(string path, out DateTime utc) { var e = Get(path); utc = e.LastWriteUtc; return e.StatOk; }
    public bool TryGetCreated(string path, out DateTime local) { var e = Get(path); local = e.CreatedLocal; return e.StatOk; }
    public bool TryGetLength(string path, out long length) { var e = Get(path); length = e.Length; return e.StatOk; }

    /// <summary>EXIF capture date if the file has one, otherwise the creation time (as the original did).</summary>
    public bool TryGetSortDate(string path, out DateTime date)
    {
        var e = Get(path);
        lock (e)   // per file: other files' EXIF reads aren't held up
        {
            if (!e.ExifResolved)
            {
                if (ImageFileTypes.MayHaveExif(path))
                    e.SortDate = ExifReader.GetDateTaken(path);
                e.SortDate ??= e.StatOk ? e.CreatedLocal : null;
                e.ExifResolved = true;
            }
            date = e.SortDate ?? default;
            return e.SortDate.HasValue;
        }
    }

    /// <summary>
    /// Reads what sorting by <paramref name="field"/> needs for every file, <see cref="PrefetchParallelism"/> at a
    /// time, so the sort itself then finds it all in the cache. Does nothing for the sorts that only look at names.
    /// </summary>
    public void Prefetch(IReadOnlyList<string> paths, SortField field, CancellationToken ct = default)
    {
        if (field is SortField.Name or SortField.FilePath or SortField.Type) return;
        Parallel.ForEach(paths, new ParallelOptions { MaxDegreeOfParallelism = PrefetchParallelism, CancellationToken = ct }, path =>
        {
            if (field == SortField.ExifDate) TryGetSortDate(path, out _); else Get(path);
        });
    }
}

/// <summary>Builds the path comparers behind the View &gt; Sort menu. Ties fall back to a natural path comparison.</summary>
public static class FileComparers
{
    public static IComparer<string> For(SortOrder order, FileMetadataCache? cache = null)
    {
        cache ??= new FileMetadataCache();
        IComparer<string> asc = order.Key switch
        {
            SortField.Name => Comparer<string>.Create(CompareName),
            SortField.DateModified => Comparer<string>.Create((a, b) => CompareBy(a, b, cache, static (c, p) => c.TryGetLastWrite(p, out var v) ? v : (DateTime?)null)),
            SortField.ExifDate => Comparer<string>.Create((a, b) => CompareBy(a, b, cache, static (c, p) => c.TryGetSortDate(p, out var v) ? v : (DateTime?)null)),
            SortField.DateAdded => Comparer<string>.Create((a, b) => CompareBy(a, b, cache, static (c, p) => c.TryGetCreated(p, out var v) ? v : (DateTime?)null)),
            SortField.Size => Comparer<string>.Create((a, b) => CompareBy(a, b, cache, static (c, p) => c.TryGetLength(p, out var v) ? v : (long?)null)),
            SortField.Type => Comparer<string>.Create(CompareType),
            SortField.FilePath => NaturalStringComparer.Instance,
            _ => StringComparer.Ordinal,
        };
        return order.Descending ? Comparer<string>.Create((a, b) => asc.Compare(b, a)) : asc;
    }

    private static int CompareName(string a, string b)
    {
        int c = NaturalStringComparer.Instance.Compare(Path.GetFileName(a), Path.GetFileName(b));
        return c != 0 ? c : NaturalStringComparer.Instance.Compare(a, b);
    }

    private static int CompareType(string a, string b)
    {
        int c = string.Compare(Path.GetExtension(a), Path.GetExtension(b), StringComparison.OrdinalIgnoreCase);
        return c != 0 ? c : NaturalStringComparer.Instance.Compare(a, b);
    }

    private static int CompareBy<T>(string a, string b, FileMetadataCache cache, Func<FileMetadataCache, string, T?> key)
        where T : struct, IComparable<T>
    {
        // If either file can't be examined, fall back to name order (mod times etc. aren't unique,
        // names are).
        if (key(cache, a) is { } ka && key(cache, b) is { } kb)
        {
            int c = ka.CompareTo(kb);
            if (c != 0) return c;
        }
        return NaturalStringComparer.Instance.Compare(a, b);
    }
}
