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

    private readonly Dictionary<string, Entry> _entries = new(PathUtil.Comparer);
    private readonly object _lock = new();

    public void Invalidate(string path) { lock (_lock) _entries.Remove(path); }
    public void Clear() { lock (_lock) _entries.Clear(); }

    private Entry Get(string path)
    {
        lock (_lock)
        {
            if (_entries.TryGetValue(path, out var e)) return e;
            e = new Entry();
            try
            {
                var fi = new FileInfo(path);
                if (fi.Exists)
                {
                    e.StatOk = true;
                    e.LastWriteUtc = fi.LastWriteTimeUtc;
                    e.CreatedLocal = fi.CreationTime;
                    e.Length = fi.Length;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            _entries[path] = e;
            return e;
        }
    }

    public bool TryGetLastWrite(string path, out DateTime utc) { var e = Get(path); utc = e.LastWriteUtc; return e.StatOk; }
    public bool TryGetCreated(string path, out DateTime local) { var e = Get(path); local = e.CreatedLocal; return e.StatOk; }
    public bool TryGetLength(string path, out long length) { var e = Get(path); length = e.Length; return e.StatOk; }

    /// <summary>EXIF capture date if the file has one, otherwise the creation time (as the original did).</summary>
    public bool TryGetSortDate(string path, out DateTime date)
    {
        var e = Get(path);
        lock (_lock)
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
