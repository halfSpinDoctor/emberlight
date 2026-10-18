namespace Emberlight.Core;

/// <summary>
/// The bookkeeping a grid cell carries for <see cref="ThumbnailScheduler{TItem, TImage}"/>. Worker threads read and
/// write these, so implementations should back them with volatile fields.
/// </summary>
public interface IThumbnailSlot
{
    string Path { get; }
    /// <summary>The cell already shows its thumbnail (or its "broken" placeholder).</summary>
    bool IsLoaded { get; }
    /// <summary>A decode for this cell is waiting or running.</summary>
    bool IsQueued { get; set; }
    /// <summary>The stamp of the last batch that wanted this cell (see <see cref="ThumbnailScheduler{TItem, TImage}.NextStamp"/>).</summary>
    int WantedStamp { get; set; }
    /// <summary>The scheduler generation the cell was queued in; a reset makes it stale.</summary>
    int Generation { get; set; }
}

/// <summary>
/// Background thumbnail generation. Requests are served newest-first (what you just scrolled to matters
/// more than what you scrolled past), requests for cells that have since scrolled out of view are
/// dropped without decoding, and finished thumbnails are kept in an LRU cache so revisiting a folder is
/// instant. Replaces DYImageCache's thumbnail queue.
///
/// The front end supplies the decoder, a check that a cached thumbnail still matches its file, how to show
/// a result on a cell, and how to run that on its UI thread. <see cref="Request"/> and the delivery run on
/// the UI thread; decoding runs on worker threads. With no workers, <see cref="ProcessNext"/> does one step
/// on the caller's thread (used by the tests).
/// </summary>
public sealed class ThumbnailScheduler<TItem, TImage> : IDisposable
    where TItem : class, IThumbnailSlot
    where TImage : class
{
    private readonly Func<string, int, TImage?> _decode;
    private readonly Func<string, TImage, bool> _isCurrent;
    private readonly Action<TItem, TImage?> _show;
    private readonly Action<Action> _post;
    private readonly Stack<TItem> _pending = new();
    private readonly object _lock = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _cts = new();

    private LruCache<string, TImage> _cache;
    private int _generation;
    private int _decodeSize;
    private int _stamp;

    /// <param name="decode">Decodes a thumbnail of about the given size (worker thread); null if the file can't be read.</param>
    /// <param name="isCurrent">Whether a cached thumbnail still matches the file on disk (UI thread).</param>
    /// <param name="show">Shows a result on a cell; null means the file couldn't be read (UI thread).</param>
    /// <param name="post">Runs an action later on the UI thread, at low priority.</param>
    /// <param name="workerCount">Decoding threads; 0 starts none, so <see cref="ProcessNext"/> must be called.</param>
    public ThumbnailScheduler(Func<string, int, TImage?> decode, Func<string, TImage, bool> isCurrent,
        Action<TItem, TImage?> show, Action<Action> post, int decodeSize = 160, int workerCount = 0)
    {
        _decode = decode;
        _isCurrent = isCurrent;
        _show = show;
        _post = post;
        _decodeSize = decodeSize;
        _cache = NewCache(decodeSize);
        for (int i = 0; i < workerCount; i++) Task.Run(() => WorkerLoop(_cts.Token));
    }

    /// <summary>Half the processors, between 1 and 4.</summary>
    public static int DefaultWorkerCount => Math.Clamp(Environment.ProcessorCount / 2, 1, 4);

    public int DecodeSize { get { lock (_lock) return _decodeSize; } }

    /// <summary>Requests waiting for a worker (including stale ones not yet dropped).</summary>
    public int PendingCount { get { lock (_lock) return _pending.Count; } }

    /// <summary>Starts a new "batch" of wanted items; items stamped with an older value are skipped.</summary>
    public int NextStamp() => Interlocked.Increment(ref _stamp);

    /// <summary>Forget everything queued (e.g. when navigating to another folder).</summary>
    public void Reset()
    {
        lock (_lock)
        {
            _generation++;
            _pending.Clear();
        }
    }

    /// <summary>Changes the decode size. Returns true if it changed (callers should then reset item thumbnails).</summary>
    public bool Configure(int decodeSize)
    {
        lock (_lock)
        {
            if (decodeSize == _decodeSize) return false;
            _decodeSize = decodeSize;
            _cache = NewCache(decodeSize);
            _generation++;
            _pending.Clear();
            return true;
        }
    }

    public void Invalidate(string path)
    {
        lock (_lock) _cache.Remove(path);
    }

    /// <summary>
    /// UI thread only. Asks for the items in [first, end) (the cells on or near the screen): they get a new
    /// stamp, so older requests for cells no longer in this range are dropped, and they are queued last to
    /// first so the first one is decoded first.
    /// </summary>
    public void RequestRange(IReadOnlyList<TItem> items, int first, int end)
    {
        int stamp = NextStamp();
        for (int i = first; i < end; i++) items[i].WantedStamp = stamp;
        for (int i = end - 1; i >= first; i--) Request(items[i]);   // reversed: served newest-first
    }

    /// <summary>
    /// UI thread only. Moves those of <paramref name="topFirst"/> that are waiting for a decode to the front of the
    /// queue, in the given order (the first is decoded first); everything else keeps its place behind them. For a front
    /// end that requests cells one at a time as they appear (each <see cref="Request"/> goes on top of the stack, so
    /// a burst of cells would otherwise be served last to first): after the burst, pass the visible cells in reading order.
    /// </summary>
    public void Prioritize(IReadOnlyList<TItem> topFirst)
    {
        lock (_lock)
        {
            if (_pending.Count == 0) return;
            var pending = new HashSet<TItem>(_pending, ReferenceEqualityComparer.Instance);
            var front = new List<TItem>();                // the listed cells that are waiting, first listing wins
            var seen = new HashSet<TItem>(ReferenceEqualityComparer.Instance);
            foreach (var item in topFirst)
                if (pending.Contains(item) && seen.Add(item)) front.Add(item);
            if (front.Count == 0) return;

            var behind = _pending.Where(i => !seen.Contains(i)).ToList();   // the stack enumerates from the top down
            _pending.Clear();
            for (int i = behind.Count - 1; i >= 0; i--) _pending.Push(behind[i]);
            for (int i = front.Count - 1; i >= 0; i--) _pending.Push(front[i]);
        }
    }

    /// <summary>UI thread only. Loads from cache immediately or queues a background decode.</summary>
    public void Request(TItem item)
    {
        if (item.IsLoaded || item.IsQueued) return;

        LruCache<string, TImage> cache;
        lock (_lock) cache = _cache;
        if (cache.TryGet(item.Path, out var cached) && _isCurrent(item.Path, cached))
        {
            _show(item, cached);
            return;
        }

        lock (_lock)
        {
            item.Generation = _generation;
            item.IsQueued = true;
            _pending.Push(item);
        }
        _signal.Release();
    }

    /// <summary>
    /// Takes the newest request and decodes it unless it is stale, posting the result to the UI thread.
    /// Returns false when nothing was waiting. Worker threads call this; so can a front end without workers.
    /// </summary>
    public bool ProcessNext()
    {
        TItem? item = null;
        int generation, size;
        lock (_lock)
        {
            if (_pending.Count > 0) item = _pending.Pop();
            generation = _generation;
            size = _decodeSize;
        }
        if (item == null) return false;
        if (item.Generation != generation) return true;
        if (item.WantedStamp != Volatile.Read(ref _stamp))
        {
            item.IsQueued = false;      // scrolled out of view; may be requested again later
            return true;
        }

        var image = _decode(item.Path, size);
        _post(() => Deliver(item, image, generation, size));
        return true;
    }

    private async Task WorkerLoop(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await _signal.WaitAsync(ct).ConfigureAwait(false);
                ProcessNext();
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
        catch (ObjectDisposedException)
        {
            // shutting down
        }
    }

    private void Deliver(TItem item, TImage? image, int generation, int size)
    {
        bool stale;
        LruCache<string, TImage> cache;
        lock (_lock)
        {
            stale = generation != _generation || size != _decodeSize;
            cache = _cache;
        }
        item.IsQueued = false;
        if (stale) return;
        if (image != null) cache.Set(item.Path, image);
        _show(item, image);
    }

    /// <summary>Keeps the thumbnail cache to roughly 160 MB regardless of thumbnail size.</summary>
    private static LruCache<string, TImage> NewCache(int decodeSize)
        => new(DecodePolicy.ThumbnailCacheCapacity(decodeSize), comparer: PathUtil.Comparer);

    public void Dispose()
    {
        _cts.Cancel();
        _signal.Dispose();
        _cts.Dispose();
    }
}
