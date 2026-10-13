namespace Emberlight.Core;

/// <summary>
/// Watches a folder and reports batches of changed/deleted image files after a quiet period.
/// Replaces DYFileWatcher (FSEvents): same contract of "here are the files that changed, here are the
/// ones that vanished", with a coalescing delay (the original used 2 seconds).
/// Events are raised on a thread-pool thread; marshal to the UI thread in the handler.
/// </summary>
public sealed class FolderWatcher : IDisposable
{
    private readonly Func<string, bool> _shouldShow;
    private readonly TimeSpan _delay;
    private readonly object _lock = new();
    private readonly HashSet<string> _pending = new(StringComparer.OrdinalIgnoreCase);
    private FileSystemWatcher? _fsw;
    private Timer? _timer, _rootPoll;
    private bool _rescan, _rootChanged, _disposed, _rootGoneReported;

    public FolderWatcher(Func<string, bool> shouldShow, TimeSpan? delay = null)
    {
        _shouldShow = shouldShow;
        _delay = delay ?? TimeSpan.FromSeconds(2);
    }

    /// <summary>Files that exist and are readable now (new or modified), and files that no longer do.</summary>
    public event Action<IReadOnlyList<string> /*changed*/, IReadOnlyList<string> /*deleted*/>? FilesChanged;
    /// <summary>The watched folder itself was deleted or renamed.</summary>
    public event Action? RootChanged;
    /// <summary>Too many changes for the OS to report individually; the caller should rescan the folder.</summary>
    public event Action? RescanNeeded;

    public string? Path { get; private set; }
    public bool IncludeSubfolders { get; private set; }
    public bool IsWatching => _fsw != null;

    public bool Watch(string path, bool includeSubfolders)
    {
        Stop();
        try
        {
            string full = System.IO.Path.GetFullPath(path);
            // Refuse to watch a whole drive recursively: far too noisy.
            if (includeSubfolders && System.IO.Path.GetPathRoot(full) is { } root &&
                string.Equals(root.TrimEnd('\\', '/'), full.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
                return false;
            if (!Directory.Exists(full)) return false;

            var w = new FileSystemWatcher(full)
            {
                IncludeSubdirectories = includeSubfolders,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size
                             | NotifyFilters.CreationTime | NotifyFilters.DirectoryName,
                InternalBufferSize = 64 * 1024,
            };
            w.Created += (_, e) => Note(e.FullPath);
            w.Changed += (_, e) => Note(e.FullPath);
            w.Deleted += (_, e) => Note(e.FullPath);
            w.Renamed += (_, e) => { Note(e.OldFullPath); Note(e.FullPath); };
            w.Error += (_, _) => { lock (_lock) _rescan = true; Kick(); };

            lock (_lock)
            {
                _disposed = false;
                Path = full;
                IncludeSubfolders = includeSubfolders;
                _fsw = w;
                _rootGoneReported = false;
                // FileSystemWatcher doesn't reliably report the watched folder itself being deleted or
                // renamed (platform dependent), so also poll for its existence. One stat per interval.
                var poll = _delay < TimeSpan.FromSeconds(2) ? _delay : TimeSpan.FromSeconds(2);
                if (poll < TimeSpan.FromMilliseconds(50)) poll = TimeSpan.FromMilliseconds(50);
                _rootPoll = new Timer(_ => CheckRoot(), null, poll, poll);
            }
            w.EnableRaisingEvents = true;
            return true;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            Stop();
            return false;
        }
    }

    public void Stop()
    {
        FileSystemWatcher? w; Timer? t, poll;
        lock (_lock)
        {
            w = _fsw; t = _timer; poll = _rootPoll;
            _fsw = null; _timer = null; _rootPoll = null;
            _pending.Clear();
            _rescan = _rootChanged = false;
        }
        t?.Dispose();
        poll?.Dispose();
        if (w != null) { w.EnableRaisingEvents = false; w.Dispose(); }
    }

    public void Dispose() { _disposed = true; Stop(); }

    private void CheckRoot()
    {
        string? path;
        lock (_lock)
        {
            if (_fsw == null || _rootGoneReported) return;
            path = Path;
            if (path == null || Directory.Exists(path)) return;
            _rootGoneReported = true;
        }
        RootChanged?.Invoke();
    }

    private void Note(string fullPath)
    {
        string? root = Path;
        if (root != null && string.Equals(fullPath.TrimEnd('\\', '/'), root.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
        {
            lock (_lock) _rootChanged = true;
            Kick();
            return;
        }
        // Only files we'd display are interesting. A deleted file can't be inspected, so judge by name only.
        string name = System.IO.Path.GetFileName(fullPath);
        if (name.Length == 0) return;
        lock (_lock) _pending.Add(fullPath);
        Kick();
    }

    private void Kick()
    {
        lock (_lock)
        {
            if (_disposed || _fsw == null) return;
            // restart the quiet-period timer: events keep deferring the flush
            if (_timer == null) _timer = new Timer(_ => Flush(), null, _delay, Timeout.InfiniteTimeSpan);
            else _timer.Change(_delay, Timeout.InfiniteTimeSpan);
        }
    }

    private void Flush()
    {
        string[] paths; bool rescan, root;
        lock (_lock)
        {
            if (_fsw == null) return;
            paths = _pending.ToArray();
            rescan = _rescan; root = _rootChanged;
            _pending.Clear(); _rescan = _rootChanged = false;
        }

        if (root && Path != null && !Directory.Exists(Path))
        {
            RootChanged?.Invoke();
            return;
        }
        // A vanished path with no extension in a recursive watch is probably a whole folder; we can't
        // enumerate what was inside it any more, so ask the caller to rescan instead.
        if (!rescan && IncludeSubfolders)
            rescan = paths.Any(p => !System.IO.Path.HasExtension(p) && !File.Exists(p) && !Directory.Exists(p));
        if (rescan) RescanNeeded?.Invoke();

        var changed = new List<string>(); var deleted = new List<string>();
        foreach (var p in paths)
        {
            string? rootPath = Path;
            if (rootPath != null && !IncludeSubfolders &&
                !string.Equals(System.IO.Path.GetDirectoryName(p), rootPath, StringComparison.OrdinalIgnoreCase))
                continue;
            if (File.Exists(p))
            {
                if (_shouldShow(p)) changed.Add(p);
            }
            else if (!Directory.Exists(p) && LooksLikeShownName(p))
            {
                deleted.Add(p);
            }
        }
        if (changed.Count > 0 || deleted.Count > 0) FilesChanged?.Invoke(changed, deleted);
    }

    // For vanished paths we can only go by name. The caller treats "deleted" as "drop it if you have it".
    private static bool LooksLikeShownName(string path)
        => !System.IO.Path.GetFileName(path).StartsWith("._", StringComparison.Ordinal);
}
