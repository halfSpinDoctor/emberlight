namespace Emberlight.Core;

/// <summary>What <see cref="FileListing{TItem}.Upsert"/> did with a file the watcher reported.</summary>
public enum UpsertResult
{
    /// <summary>A new file: a new item was inserted at its sorted place.</summary>
    Added,
    /// <summary>A known file whose sort position can't have changed (name or path order); it stayed put.</summary>
    Updated,
    /// <summary>A known file taken out and put back at its sorted place, since its date or size may have changed.</summary>
    Repositioned,
}

/// <summary>
/// The browser's file list: one item per image, in sort order, looked up by path, and kept in order as the folder
/// watcher reports changes. The items live in a list the UI supplies (on WPF, the observable collection the grid is
/// bound to), so this class decides where things go and the UI only renders them. It also owns the sort order and the
/// metadata cache that date and size sorting read.
/// </summary>
public sealed class FileListing<TItem> where TItem : class
{
    private readonly IList<TItem> _items;
    private readonly Func<string, TItem> _create;
    private readonly Func<TItem, string> _pathOf;
    private readonly Action<IEnumerable<TItem>> _replaceAll;
    private readonly Dictionary<string, TItem> _byPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly FileMetadataCache _meta = new();

    /// <param name="items">The list to keep in order; only Insert, Remove and the indexer are used on it.</param>
    /// <param name="create">Makes the item for a newly listed file.</param>
    /// <param name="pathOf">The file an item stands for.</param>
    /// <param name="replaceAll">Replaces the whole list in one go (a single change notification for a big folder).
    /// Defaults to clearing the list and adding the items one by one.</param>
    public FileListing(IList<TItem> items, Func<string, TItem> create, Func<TItem, string> pathOf, SortOrder order,
        Action<IEnumerable<TItem>>? replaceAll = null)
    {
        _items = items;
        _create = create;
        _pathOf = pathOf;
        _replaceAll = replaceAll ?? (all =>
        {
            var list = all.ToList();
            _items.Clear();
            foreach (var item in list) _items.Add(item);
        });
        Order = order;
        Comparer = FileComparers.For(order, _meta);
    }

    public SortOrder Order { get; private set; }

    /// <summary>Compares paths in <see cref="Order"/>. Safe to use on a worker thread (the metadata cache locks).</summary>
    public IComparer<string> Comparer { get; private set; }

    public int Count => _items.Count;

    /// <summary>Whether a changed file can move (its date or size may have changed); false in name or path order.</summary>
    public bool ChangesCanReorder => Order.Key is not (SortField.Name or SortField.FilePath);

    public bool TryGet(string path, out TItem item) => _byPath.TryGetValue(path, out item!);

    /// <summary>Changes the sort order (and forgets cached metadata). Doesn't move anything: see <see cref="SortAsync"/>.</summary>
    public void SetOrder(SortOrder order)
    {
        Order = order;
        _meta.Clear();
        Comparer = FileComparers.For(order, _meta);
    }

    /// <summary>Forgets cached dates and sizes, e.g. before listing another folder.</summary>
    public void ResetMetadata() => SetOrder(Order);

    /// <summary>Forgets one file's cached dates and size (it was edited).</summary>
    public void Invalidate(string path) => _meta.Invalidate(path);

    /// <summary>Shows exactly these files, which must already be in <see cref="Order"/>.</summary>
    public void Populate(IEnumerable<string> sortedPaths)
    {
        _byPath.Clear();
        var items = new List<TItem>();
        foreach (var path in sortedPaths)
        {
            var item = _create(path);
            _byPath[path] = item;
            items.Add(item);
        }
        _replaceAll(items);
    }

    public void Clear()
    {
        _byPath.Clear();
        _replaceAll(Array.Empty<TItem>());
    }

    /// <summary>Takes a file out of the list. False if it wasn't listed.</summary>
    public bool Remove(string path)
    {
        _meta.Invalidate(path);
        if (!_byPath.Remove(path, out var item)) return false;
        _items.Remove(item);
        return true;
    }

    /// <summary>
    /// A file appeared or changed. A new file gets an item at its sorted place. A known file keeps its item; unless the
    /// list is in name or path order, it is taken out and put back, since the date or size it sorts by may have changed.
    /// </summary>
    public UpsertResult Upsert(string path, out TItem item)
    {
        _meta.Invalidate(path);
        if (_byPath.TryGetValue(path, out item!))
        {
            if (!ChangesCanReorder) return UpsertResult.Updated;
            _items.Remove(item);
            _items.Insert(FindInsertIndex(path), item);
            return UpsertResult.Repositioned;
        }

        item = _create(path);
        _byPath[path] = item;
        _items.Insert(FindInsertIndex(path), item);
        return UpsertResult.Added;
    }

    /// <summary>
    /// Re-sorts the list in <see cref="Order"/>. The sorting runs on a worker thread (by date or EXIF date it opens every
    /// file); the list itself is only touched by the caller's thread, after the await.
    /// </summary>
    public async Task SortAsync()
    {
        if (_items.Count == 0) return;
        var paths = SnapshotPaths();
        var comparer = Comparer;
        await Task.Run(() => paths.Sort(comparer));
        ApplyOrder(paths);
    }

    /// <summary>The listed paths in their current order (a copy, so it can be sorted on another thread).</summary>
    public List<string> SnapshotPaths() => _items.Select(_pathOf).ToList();

    /// <summary>
    /// Puts the items in the order of <paramref name="sortedPaths"/>. Files removed since that list was made are left
    /// out; files added since then are inserted at their sorted places.
    /// </summary>
    public void ApplyOrder(IReadOnlyList<string> sortedPaths)
    {
        var placed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ordered = new List<TItem>(_byPath.Count);
        foreach (var path in sortedPaths)
        {
            if (_byPath.TryGetValue(path, out var item) && placed.Add(path)) ordered.Add(item);
        }
        var late = _byPath.Where(kv => !placed.Contains(kv.Key)).Select(kv => kv.Value).ToList();
        _replaceAll(ordered);
        foreach (var item in late) _items.Insert(FindInsertIndex(_pathOf(item)), item);
    }

    private int FindInsertIndex(string path)
    {
        int lo = 0, hi = _items.Count;
        while (lo < hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            if (Comparer.Compare(_pathOf(_items[mid]), path) < 0) lo = mid + 1; else hi = mid;
        }
        return lo;
    }
}
