namespace Emberlight.Core;

/// <summary>Port of NSMutableArray+DYMovable plus the path-rebasing helpers.</summary>
public static class ListExtensions
{
    public static void MoveItem<T>(this List<T> list, int fromIndex, int toIndex)
    {
        T item = list[fromIndex];
        list.RemoveAt(fromIndex);
        list.Insert(toIndex, item);
    }

    /// <summary>
    /// Re-sorts a single item whose sort key changed. Finds it by linear search (its old position may
    /// no longer match the comparer), removes it, and re-inserts it at the sorted position.
    /// Returns the new index, or -1 if the item is not in the list.
    /// </summary>
    public static int UpdateIndexOfItem<T>(this List<T> list, T item, IComparer<T> cmp, out int oldIndex)
    {
        oldIndex = list.IndexOf(item);
        if (oldIndex < 0) return -1;
        list.RemoveAt(oldIndex);
        int lo = 0, hi = list.Count;
        while (lo < hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            if (cmp.Compare(list[mid], item) < 0) lo = mid + 1; else hi = mid;
        }
        list.Insert(lo, item);
        return lo;
    }

    /// <summary>Replaces the leading <paramref name="oldBase"/> of every path with <paramref name="newBase"/> (used when a folder is renamed).</summary>
    public static void ChangeBase(this List<string> paths, string oldBase, string newBase)
    {
        for (int i = 0; i < paths.Count; i++)
            paths[i] = Rebase(paths[i], oldBase, newBase);
    }

    public static void ChangeBase(this RandomizableList<string> paths, string oldBase, string newBase)
        => paths.Transform(p => Rebase(p, oldBase, newBase));

    private static string Rebase(string path, string oldBase, string newBase)
        => path.StartsWith(oldBase, StringComparison.OrdinalIgnoreCase)
            ? string.Concat(newBase, path.AsSpan(oldBase.Length))
            : path;
}
