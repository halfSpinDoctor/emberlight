namespace Emberlight.Core;

/// <summary>
/// The folders pinned above the folder tree, in the order they were pinned (top to bottom) unless moved since. Saved in
/// <see cref="AppSettings.PinnedFolders"/>. Nothing here touches the disk: a pinned folder may be on a drive or share
/// that isn't connected, and is only looked for when it is opened.
/// </summary>
public sealed class PinnedFolders
{
    /// <summary>How many pins get a Ctrl+number shortcut (Ctrl+1 to Ctrl+9).</summary>
    public const int ShortcutCount = 9;

    private readonly List<string> _paths = new();

    /// <summary>Starts from saved paths, dropping blanks and repeats (the first one stays).</summary>
    public PinnedFolders(IEnumerable<string>? saved = null)
    {
        foreach (string p in saved ?? []) Pin(p);
    }

    public IReadOnlyList<string> Paths => _paths;
    public int Count => _paths.Count;

    /// <summary>The pin's position, or -1 (ignoring case and trailing separators).</summary>
    public int IndexOf(string? path) => path == null ? -1 : _paths.FindIndex(p => PathUtil.SamePath(p, path));

    public bool Contains(string? path) => IndexOf(path) >= 0;

    /// <summary>Adds the folder at the bottom; false if it is blank or already pinned.</summary>
    public bool Pin(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Contains(path)) return false;
        _paths.Add(path);
        return true;
    }

    public bool Unpin(string path)
    {
        int i = IndexOf(path);
        if (i < 0) return false;
        _paths.RemoveAt(i);
        return true;
    }

    /// <summary>Whether the pin can move one place up (<paramref name="delta"/> -1) or down (+1).</summary>
    public bool CanMove(string path, int delta)
    {
        int i = IndexOf(path);
        return i >= 0 && i + delta >= 0 && i + delta < _paths.Count;
    }

    /// <summary>Swaps the pin with its neighbour above (-1) or below (+1); false at the ends.</summary>
    public bool Move(string path, int delta)
    {
        if (!CanMove(path, delta)) return false;
        int i = IndexOf(path);
        (_paths[i], _paths[i + delta]) = (_paths[i + delta], _paths[i]);
        return true;
    }

    /// <summary>The pin a Ctrl+number key opens (1 to 9), or null when there is none.</summary>
    public string? ForShortcut(int number) => number is >= 1 and <= ShortcutCount && number <= _paths.Count ? _paths[number - 1] : null;

    /// <summary>The shortcut shown beside the pin at <paramref name="index"/>, e.g. "Ctrl+1"; null after the ninth.</summary>
    public static string? ShortcutText(int index) => index is >= 0 and < ShortcutCount ? $"Ctrl+{index + 1}" : null;

    /// <summary>
    /// What each pin is called, in order: its folder's name, or for two pins with the same name, the name with its
    /// parent folder's, e.g. "2024 (Holidays)". Still the same after that: the full path. A drive or share is
    /// named as in the tree ("D:\", "share (\\server)").
    /// </summary>
    public IReadOnlyList<string> Labels()
    {
        var names = _paths.Select(Name).ToList();
        var labels = new List<string>(names);
        for (int i = 0; i < labels.Count; i++)
        {
            if (names.Count(n => PathUtil.Comparer.Equals(n, names[i])) < 2) continue;
            labels[i] = Path.GetDirectoryName(PathUtil.TrimSeparators(_paths[i])) is { Length: > 0 } parent
                ? $"{names[i]} ({Name(parent)})" : _paths[i];
        }
        var withParents = labels.ToList();
        for (int i = 0; i < labels.Count; i++)
            if (withParents.Count(l => PathUtil.Comparer.Equals(l, withParents[i])) > 1) labels[i] = _paths[i];
        return labels;
    }

    /// <summary>The pin's tooltip: its full path, and its shortcut when it has one.</summary>
    public string ToolTip(int index)
        => ShortcutText(index) is { } keys ? $"{_paths[index]} ({keys})" : _paths[index];

    /// <summary>A folder's own name, or a drive's or share's label when it is one.</summary>
    public static string Name(string path)
    {
        string trimmed = PathUtil.TrimSeparators(path);
        string? root = Path.GetPathRoot(path);
        bool isRoot = trimmed.Length == 0 || (root != null && PathUtil.SamePath(root, path));
        if (isRoot) return PathUtil.NetworkShareLabel(path) ?? path;
        string name = Path.GetFileName(trimmed);
        return name.Length > 0 ? name : path;
    }

    /// <summary>The File menu's (and tree's) pin command for a folder: "Pin" or "Unpin" followed by <paramref name="what"/>.</summary>
    public string PinCommandText(string? path, string what) => (Contains(path) ? "Unpin " : "Pin ") + what;

    /// <summary>The status bar line when a pinned folder can't be opened.</summary>
    public static string NotFound(string path)
        => $"Can't find the pinned folder \"{path}\". It may be on a drive or network share that isn't connected.";

    /// <summary>The status bar line when the pins couldn't be saved.</summary>
    public const string NotSaved = "The pinned folders couldn't be saved; they will be lost when Emberlight closes.";
}
