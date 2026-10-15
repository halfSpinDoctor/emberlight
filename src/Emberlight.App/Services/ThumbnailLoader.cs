using System.Windows.Threading;
using Emberlight.App.ViewModels;
using Emberlight.Core;

namespace Emberlight.App.Services;

/// <summary>
/// Background thumbnail generation. Requests are served newest-first (what you just scrolled to matters
/// more than what you scrolled past), requests for cells that have since scrolled out of view are
/// dropped without decoding, and finished thumbnails are kept in an LRU cache so revisiting a folder is
/// instant. Replaces DYImageCache's thumbnail queue.
/// </summary>
public sealed class ThumbnailLoader : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly Stack<ThumbnailItem> _pending = new();
    private readonly object _lock = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _cts = new();

    private LruCache<string, LoadedImage> _cache;
    private int _generation;
    private int _decodeSize = 160;
    private int _stamp;

    public ThumbnailLoader(Dispatcher dispatcher, int workerCount = 0)
    {
        _dispatcher = dispatcher;
        _cache = new LruCache<string, LoadedImage>(CapacityFor(_decodeSize), comparer: PathUtil.Comparer);
        int n = workerCount > 0 ? workerCount : Math.Clamp(Environment.ProcessorCount / 2, 1, 4);
        for (int i = 0; i < n; i++) Task.Run(() => WorkerLoop(_cts.Token));
    }

    /// <summary>Use the JPEG's embedded EXIF preview when it's big enough (much faster than decoding the photo).</summary>
    public bool UseEmbeddedPreviews { get; set; } = true;

    public int DecodeSize { get { lock (_lock) return _decodeSize; } }

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
            _cache = new LruCache<string, LoadedImage>(CapacityFor(decodeSize), comparer: PathUtil.Comparer);
            _generation++;
            _pending.Clear();
            return true;
        }
    }

    public void Invalidate(string path)
    {
        lock (_lock) _cache.Remove(path);
    }

    /// <summary>UI thread only. Loads from cache immediately or queues a background decode.</summary>
    public void Request(ThumbnailItem item)
    {
        if (item.IsLoaded || item.IsQueued) return;

        LruCache<string, LoadedImage> cache;
        lock (_lock) cache = _cache;
        if (cache.TryGet(item.Path, out var cached) && cached.Info.MatchesFile(item.Path))
        {
            item.SetLoaded(cached);
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

    private async Task WorkerLoop(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await _signal.WaitAsync(ct).ConfigureAwait(false);

                ThumbnailItem? item = null;
                int generation, size;
                bool embedded;
                lock (_lock)
                {
                    if (_pending.Count > 0) item = _pending.Pop();
                    generation = _generation;
                    size = _decodeSize;
                    embedded = UseEmbeddedPreviews;
                }
                if (item == null || item.Generation != generation) continue;
                if (item.WantedStamp != Volatile.Read(ref _stamp))
                {
                    item.IsQueued = false;      // scrolled out of view; may be requested again later
                    continue;
                }

                var image = ImageService.LoadThumbnail(item.Path, size, embedded);
                _ = _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => Deliver(item, image, generation, size)));
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }

    private void Deliver(ThumbnailItem item, LoadedImage? image, int generation, int size)
    {
        bool stale;
        LruCache<string, LoadedImage> cache;
        lock (_lock)
        {
            stale = generation != _generation || size != _decodeSize;
            cache = _cache;
        }
        if (stale) { item.IsQueued = false; return; }
        if (image != null) cache.Set(item.Path, image);
        item.SetLoaded(image);
    }

    /// <summary>Keeps the thumbnail cache to roughly 160 MB regardless of thumbnail size.</summary>
    private static int CapacityFor(int decodeSize) => DecodePolicy.ThumbnailCacheCapacity(decodeSize);

    public void Dispose()
    {
        _cts.Cancel();
        _signal.Dispose();
        _cts.Dispose();
    }
}
