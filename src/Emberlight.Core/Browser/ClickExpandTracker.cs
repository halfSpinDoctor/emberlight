namespace Emberlight.Core;

/// <summary>
/// Folder-tree accordion behaviour: remembers which nodes were expanded by clicking their name, and when
/// another node is clicked, says which of those to collapse again (all except the clicked node and its
/// ancestors). Nodes expanded any other way (the arrow, the keyboard, programmatically) are never tracked,
/// so they stay open. Nodes are compared by identity of <typeparamref name="TNode"/>, not by path, because the
/// same folder can appear twice in the tree (a shortcut entry and its place under its drive).
/// </summary>
public sealed class ClickExpandTracker<TNode> where TNode : class
{
    private readonly List<TNode> _expanded = new();

    /// <summary>Nodes currently remembered as click-expanded, oldest first.</summary>
    public IReadOnlyList<TNode> Expanded => _expanded;

    /// <summary>
    /// The user clicked <paramref name="node"/>'s name. <paramref name="selfAndAncestors"/> is the node plus every
    /// node above it; <paramref name="clickExpandsIt"/> is true when the click opens a closed node with children.
    /// Returns the nodes to collapse, deepest (most recently expanded) first.
    /// </summary>
    public IReadOnlyList<TNode> Click(TNode node, IEnumerable<TNode> selfAndAncestors, bool clickExpandsIt)
    {
        var keep = new HashSet<TNode>(selfAndAncestors, ReferenceEqualityComparer.Instance) { node };
        var collapse = new List<TNode>();
        for (int i = _expanded.Count - 1; i >= 0; i--)
        {
            if (keep.Contains(_expanded[i])) continue;
            collapse.Add(_expanded[i]);
            _expanded.RemoveAt(i);
        }
        if (clickExpandsIt && !_expanded.Any(n => ReferenceEquals(n, node))) _expanded.Add(node);
        return collapse;
    }

    /// <summary>The user took manual control of <paramref name="node"/> (e.g. its arrow): stop tracking it.</summary>
    public void Forget(TNode node) => _expanded.RemoveAll(n => ReferenceEquals(n, node));

    /// <summary>The tree was rebuilt; the old nodes no longer exist.</summary>
    public void Clear() => _expanded.Clear();
}
