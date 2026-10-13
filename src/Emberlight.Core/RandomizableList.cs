namespace Emberlight.Core;

/// <summary>
/// A list that can be shuffled and still remember its original ("ordered") sequence.
/// Port of DYRandomizableArray.
///
/// While randomized, the list keeps:
///   _items          the working (shuffled) order, which is what indexers expose
///   _ordered        the saved original order
///   _randomToOrdered[i]  = index in _ordered of _items[i]
///   _orderedToRandom[j]  = index in _items of _ordered[j]
/// When not randomized only _items is populated and all indices are identity.
/// </summary>
public sealed class RandomizableList<T> : IReadOnlyList<T>
{
    public const int NotFound = -1;

    private readonly List<T> _items = new();
    private readonly List<T> _ordered = new();
    private readonly List<int> _randomToOrdered = new();
    private readonly List<int> _orderedToRandom = new();
    private readonly Random _rng;
    private bool _randomized;

    public RandomizableList() : this(Random.Shared) { }

    /// <summary>Inject a Random for deterministic tests.</summary>
    public RandomizableList(Random rng) => _rng = rng ?? throw new ArgumentNullException(nameof(rng));

    public int Count => _items.Count;
    public bool IsRandomized => _randomized;
    public T this[int index] => _items[index];

    public IEnumerator<T> GetEnumerator() => _items.GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

    // ---- searching ---------------------------------------------------------

    /// <summary>Linear search of the working order.</summary>
    public int IndexOf(T item) => _items.IndexOf(item);

    /// <summary>
    /// Binary search using <paramref name="cmp"/>, which must match the *ordered* sequence.
    /// Returns the index in the working order, or NotFound.
    /// </summary>
    public int IndexOf(T item, IComparer<T> cmp)
    {
        var a = Sequence;
        int idx = LowerBound(a, item, cmp);
        if (idx < a.Count && cmp.Compare(a[idx], item) == 0)
            return ToWorkingIndex(idx);
        return NotFound;
    }

    /// <summary>
    /// Like IndexOf(item, cmp), but when the item is absent reports (via
    /// <paramref name="insertIndex"/>) where it would be inserted in the *ordered* sequence.
    /// </summary>
    public int IndexOf(T item, IComparer<T> cmp, out int insertIndex)
    {
        var a = Sequence;
        int idx = LowerBound(a, item, cmp);
        if (idx < a.Count && EqualityComparer<T>.Default.Equals(a[idx], item))
        {
            insertIndex = idx;
            return ToWorkingIndex(idx);
        }
        insertIndex = idx;
        return NotFound;
    }

    // ---- mutation ----------------------------------------------------------

    public void SetItems(IEnumerable<T> items)
    {
        _items.Clear();
        _items.AddRange(items);
        ClearRandomState();
    }

    public void Clear()
    {
        _items.Clear();
        ClearRandomState();
    }

    public void RemoveAt(int index)
    {
        if ((uint)index >= (uint)_items.Count) throw new ArgumentOutOfRangeException(nameof(index));
        if (_randomized)
        {
            int count = _items.Count;
            int orderedIndex = _randomToOrdered[index];
            _ordered.RemoveAt(orderedIndex);

            for (int i = 0; i < count; i++)
                if (_randomToOrdered[i] > orderedIndex) _randomToOrdered[i]--;
            _randomToOrdered.RemoveAt(index);

            for (int i = 0; i < count; i++)
                if (_orderedToRandom[i] > index) _orderedToRandom[i]--;
            _orderedToRandom.RemoveAt(orderedIndex);
        }
        _items.RemoveAt(index);
        if (_items.Count == 0) ClearRandomState();
    }

    /// <summary>
    /// Inserts <paramref name="item"/> at its sorted position in the ordered sequence.
    /// While randomized the item is placed at working position <paramref name="atIndex"/>
    /// (clamped); otherwise atIndex is ignored. If an equal item is already present nothing
    /// is inserted. Returns the working index of the item either way.
    /// </summary>
    public int Insert(T item, IComparer<T> cmp, int atIndex)
    {
        int count = _items.Count;
        if (atIndex > count) atIndex = count;
        if (atIndex < 0) atIndex = 0;
        var a = Sequence;
        int oIdx = LowerBound(a, item, cmp);
        if (oIdx < count && EqualityComparer<T>.Default.Equals(a[oIdx], item))
            return ToWorkingIndex(oIdx);
        return InsertAtOrderedIndex(item, oIdx, atIndex);
    }

    /// <summary>
    /// Inserts at an already-known ordered index. orderedIndex must be valid (0..Count).
    /// </summary>
    public int InsertAtOrderedIndex(T item, int orderedIndex, int atIndex)
    {
        int count = _items.Count;
        if ((uint)orderedIndex > (uint)count) throw new ArgumentOutOfRangeException(nameof(orderedIndex));
        if (_randomized)
        {
            if (atIndex > count) atIndex = count;
            if (atIndex < 0) atIndex = 0;
            _ordered.Insert(orderedIndex, item);

            for (int i = 0; i < count; i++)
                if (_randomToOrdered[i] >= orderedIndex) _randomToOrdered[i]++;
            _randomToOrdered.Insert(atIndex, orderedIndex);

            for (int i = 0; i < count; i++)
                if (_orderedToRandom[i] >= atIndex) _orderedToRandom[i]++;
            _orderedToRandom.Insert(orderedIndex, atIndex);
        }
        else
        {
            atIndex = orderedIndex;
        }
        _items.Insert(atIndex, item);
        return atIndex;
    }

    /// <summary>Applies a transform to every element (both orders). Used for path rebasing.</summary>
    public void Transform(Func<T, T> f)
    {
        for (int i = 0; i < _items.Count; i++) _items[i] = f(_items[i]);
        for (int i = 0; i < _ordered.Count; i++) _ordered[i] = f(_ordered[i]);
    }

    // ---- randomizing -------------------------------------------------------

    public void Randomize() => RandomizeCore(-1);

    /// <summary>Shuffles, but guarantees the item currently at <paramref name="startIndex"/> ends up first.</summary>
    public void Randomize(int startIndex)
    {
        if ((uint)startIndex >= (uint)_items.Count) throw new ArgumentOutOfRangeException(nameof(startIndex));
        RandomizeCore(startIndex);
    }

    public void Derandomize()
    {
        if (!_randomized) return;
        _items.Clear();
        _items.AddRange(_ordered);
        ClearRandomState();
    }

    private void RandomizeCore(int start)
    {
        int count = _items.Count;
        if (count == 0) return;

        if (!_randomized)
        {
            _ordered.Clear();
            _ordered.AddRange(_items);
            _randomToOrdered.Clear();
            _orderedToRandom.Clear();
            for (int k = 0; k < count; k++)
            {
                _randomToOrdered.Add(k);
                _orderedToRandom.Add(k);
            }
            _randomized = true;
        }

        int i = count;
        if (start >= 0)
        {
            i--;                // park the chosen item at the end so the shuffle skips it
            Swap(start, i);
        }
        while (i > 1)           // Fisher-Yates
        {
            i--;
            Swap(i, _rng.Next(i + 1));
        }
        if (start >= 0)
            Swap(0, count - 1); // bring the chosen item to the front

        for (int k = 0; k < count; k++)
            _orderedToRandom[_randomToOrdered[k]] = k;
    }

    private void Swap(int a, int b)
    {
        if (a == b) return;
        (_items[a], _items[b]) = (_items[b], _items[a]);
        (_randomToOrdered[a], _randomToOrdered[b]) = (_randomToOrdered[b], _randomToOrdered[a]);
    }

    // ---- ordered navigation --------------------------------------------------

    /// <summary>Index in the ordered sequence of the item at working index <paramref name="index"/>.</summary>
    public int OrderedIndexFromIndex(int index) => _randomized ? _randomToOrdered[index] : index;

    /// <summary>Working index of the item that follows this one in the original order, or NotFound.</summary>
    public int IndexOfNextInOrder(int index)
    {
        int o = OrderedIndexFromIndex(index) + 1;
        if (o >= _items.Count) return NotFound;
        return ToWorkingIndex(o);
    }

    /// <summary>Working index of the item that precedes this one in the original order, or NotFound.</summary>
    public int IndexOfPreviousInOrder(int index)
    {
        int o = OrderedIndexFromIndex(index);
        if (o == 0) return NotFound;
        return ToWorkingIndex(o - 1);
    }

    // ---- helpers -------------------------------------------------------------

    private List<T> Sequence => _randomized ? _ordered : _items;
    private int ToWorkingIndex(int orderedIndex) => _randomized ? _orderedToRandom[orderedIndex] : orderedIndex;

    private void ClearRandomState()
    {
        _randomized = false;
        _ordered.Clear();
        _randomToOrdered.Clear();
        _orderedToRandom.Clear();
    }

    private static int LowerBound(List<T> a, T item, IComparer<T> cmp)
    {
        int lo = 0, hi = a.Count;
        while (lo < hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            if (cmp.Compare(a[mid], item) < 0) lo = mid + 1; else hi = mid;
        }
        return lo;
    }

    /// <summary>Checks the internal invariants; used by tests.</summary>
    internal void Validate()
    {
        if (!_randomized)
        {
            if (_ordered.Count + _randomToOrdered.Count + _orderedToRandom.Count != 0)
                throw new InvalidOperationException("Non-randomized list has leftover ordered state.");
            return;
        }
        int n = _items.Count;
        if (_ordered.Count != n || _randomToOrdered.Count != n || _orderedToRandom.Count != n)
            throw new InvalidOperationException("Parallel arrays out of sync.");
        for (int i = 0; i < n; i++)
        {
            int o = _randomToOrdered[i];
            if (!EqualityComparer<T>.Default.Equals(_items[i], _ordered[o]))
                throw new InvalidOperationException($"items[{i}] != ordered[{o}]");
            if (_orderedToRandom[o] != i)
                throw new InvalidOperationException($"o2r[{o}] != {i}");
        }
    }
}
