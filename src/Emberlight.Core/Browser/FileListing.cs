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

/// <summary>What <see cref="FileListing{TItem}.Reconcile"/> did.</summary>
/// <param name="Replaced">True if the whole list was swapped in one step (many changes); false if changed in place.</param>
public sealed record ReconcileResult(int Added, int Removed, bool Replaced);

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
    private readonly Dictionary<string, TItem> _byPath = new(PathUtil.Comparer);
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
        NewSortRequest(out _);
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
        NewSortRequest(out _);   // a sort of the old list is moot
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

    private int _sortVersion;
    private CancellationTokenSource? _sortCts;

    /// <summary>Stops a sort that is still running, and makes it report "not applied". Used when the list is
    /// replaced, and by the next <see cref="SortAsync"/>.</summary>
    private CancellationToken NewSortRequest(out int version)
    {
        _sortCts?.Cancel();
        _sortCts = new CancellationTokenSource();
        version = ++_sortVersion;
        return _sortCts.Token;
    }

    /// <summary>
    /// Re-sorts the list in <see cref="Order"/>. The sorting runs on a worker thread (by date or EXIF date it opens every
    /// file); the list itself is only touched by the caller's thread, after the await. A newer request (another
    /// <see cref="SortAsync"/>, or the list being replaced) supersedes this one: the older sort stops, and the latest
    /// request is always the one that decides the final order. Returns true if this sort was applied, false if it
    /// was superseded (whoever superseded it is responsible for what happens next).
    /// </summary>
    public async Task<bool> SortAsync()
    {
        if (_items.Count == 0) return false;
        var ct = NewSortRequest(out int version);
        var paths = SnapshotPaths();
        var sorter = CaptureSorter();
        try { await Task.Run(() => sorter(paths, ct)); }
        catch (OperationCanceledException) { return false; }
        if (version != _sortVersion) return false;
        ApplyOrder(paths);
        return true;
    }

    /// <summary>
    /// A function that sorts a list of paths in the current <see cref="Order"/>, reading the files' dates or sizes
    /// in parallel first (<see cref="FileMetadataCache.Prefetch"/>). Take it on the calling thread, then run it
    /// on a worker: it keeps using the order and cache it was created with, even if those change meanwhile.
    /// </summary>
    public Action<List<string>, CancellationToken> CaptureSorter()
    {
        var comparer = Comparer;
        var meta = _meta;
        var field = Order.Key;
        return (paths, ct) =>
        {
            meta.Prefetch(paths, field, ct);
            paths.Sort(comparer);
        };
    }

    /// <summary>The listed paths in their current order (a copy, so it can be sorted on another thread).</summary>
    public List<string> SnapshotPaths() => _items.Select(_pathOf).ToList();

    /// <summary>
    /// Puts the items in the order of <paramref name="sortedPaths"/>. Files removed since that list was made are left
    /// out; files added since then are inserted at their sorted places.
    /// </summary>
    public void ApplyOrder(IReadOnlyList<string> sortedPaths)
    {
        var placed = new HashSet<string>(PathUtil.Comparer);
        var ordered = new List<TItem>(_byPath.Count);
        foreach (var path in sortedPaths)
        {
            if (_byPath.TryGetValue(path, out var item) && placed.Add(path)) ordered.Add(item);
        }
        var late = _byPath.Where(kv => !placed.Contains(kv.Key)).Select(kv => kv.Value).ToList();
        _replaceAll(ordered);
        foreach (var item in late) _items.Insert(FindInsertIndex(_pathOf(item)), item);
    }

    /// <summary>Above this many files gone plus files new, <see cref="Reconcile"/> swaps the whole list in one step.</summary>
    public const int DefaultMaxSteps = 200;

    /// <summary>
    /// Brings the list in line with a fresh scan of the folder on screen (<paramref name="sortedPaths"/>, in
    /// <see cref="Order"/>) without starting over: files still there keep their items, and so their thumbnails and
    /// selection. A few changes are made one by one (the grid only redraws what moved); more than
    /// <paramref name="maxSteps"/>, or a different order, swap the whole list in one step, still reusing the items.
    /// </summary>
    public ReconcileResult Reconcile(IReadOnlyList<string> sortedPaths, int maxSteps = DefaultMaxSteps)
    {
        var wanted = new HashSet<string>(sortedPaths, PathUtil.Comparer);
        var removed = _byPath.Keys.Where(k => !wanted.Contains(k)).ToList();
        var added = sortedPaths.Where(p => !_byPath.ContainsKey(p)).Distinct(PathUtil.Comparer).ToList();
        if (removed.Count == 0 && added.Count == 0) return new ReconcileResult(0, 0, false);
        foreach (string path in removed) _meta.Invalidate(path);

        // One by one only works if the files that stay are already in the scan's order (a date sort can reorder them).
        var keptNow = _items.Select(_pathOf).Where(wanted.Contains);
        var keptInScan = sortedPaths.Where(_byPath.ContainsKey);
        if (removed.Count + added.Count <= maxSteps && keptNow.SequenceEqual(keptInScan, PathUtil.Comparer))
        {
            foreach (string path in removed)
                if (_byPath.Remove(path, out var gone)) _items.Remove(gone);
            foreach (string path in added)
            {
                var item = _create(path);
                _byPath[path] = item;
                _items.Insert(FindInsertIndex(path), item);
            }
            return new ReconcileResult(added.Count, removed.Count, false);
        }

        var next = new Dictionary<string, TItem>(PathUtil.Comparer);
        var ordered = new List<TItem>(sortedPaths.Count);
        foreach (string path in sortedPaths)
        {
            if (next.ContainsKey(path)) continue;
            var item = _byPath.TryGetValue(path, out var existing) ? existing : _create(path);
            next[path] = item;
            ordered.Add(item);
        }
        _byPath.Clear();
        foreach (var (path, item) in next) _byPath[path] = item;
        _replaceAll(ordered);
        return new ReconcileResult(added.Count, removed.Count, true);
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
