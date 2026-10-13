namespace Emberlight.Core;

/// <summary>
/// Small thread-safe LRU cache. Replaces NSCache in DYImageCache; the WPF layer uses it to hold
/// decoded thumbnails/images (BitmapSource instances are freed by the GC once evicted, so the
/// original's begin/endContentAccess pinning isn't needed).
/// </summary>
public sealed class LruCache<TKey, TValue> where TKey : notnull
{
    private readonly Dictionary<TKey, LinkedListNode<KeyValuePair<TKey, TValue>>> _map;
    private readonly LinkedList<KeyValuePair<TKey, TValue>> _order = new(); // first = most recent
    private readonly object _lock = new();
    private readonly Action<TKey, TValue>? _onEvict;
    private int _capacity;

    public LruCache(int capacity, Action<TKey, TValue>? onEvict = null, IEqualityComparer<TKey>? comparer = null)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
        _onEvict = onEvict;
        _map = new Dictionary<TKey, LinkedListNode<KeyValuePair<TKey, TValue>>>(comparer);
    }

    public int Capacity
    {
        get { lock (_lock) return _capacity; }
        set
        {
            if (value < 1) throw new ArgumentOutOfRangeException(nameof(value));
            lock (_lock) { _capacity = value; Trim(); }
        }
    }

    public int Count { get { lock (_lock) return _map.Count; } }

    public bool TryGet(TKey key, out TValue value)
    {
        lock (_lock)
        {
            if (_map.TryGetValue(key, out var node))
            {
                _order.Remove(node);
                _order.AddFirst(node);
                value = node.Value.Value;
                return true;
            }
        }
        value = default!;
        return false;
    }

    /// <summary>Checks for presence without affecting recency.</summary>
    public bool ContainsKey(TKey key) { lock (_lock) return _map.ContainsKey(key); }

    public void Set(TKey key, TValue value)
    {
        lock (_lock)
        {
            if (_map.TryGetValue(key, out var existing))
            {
                _order.Remove(existing);
                _map.Remove(key);
            }
            var node = new LinkedListNode<KeyValuePair<TKey, TValue>>(new(key, value));
            _order.AddFirst(node);
            _map[key] = node;
            Trim();
        }
    }

    public bool Remove(TKey key)
    {
        lock (_lock)
        {
            if (!_map.TryGetValue(key, out var node)) return false;
            _order.Remove(node);
            _map.Remove(key);
            return true;
        }
    }

    public void Clear() { lock (_lock) { _map.Clear(); _order.Clear(); } }

    private void Trim()
    {
        while (_map.Count > _capacity && _order.Last is { } last)
        {
            _order.RemoveLast();
            _map.Remove(last.Value.Key);
            _onEvict?.Invoke(last.Value.Key, last.Value.Value);
        }
    }
}
