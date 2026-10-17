namespace Emberlight.Core;

/// <summary>
/// The folders pinned above the folder tree, in the order they were pinned (top to bottom) unless moved since, and the
/// names the user gave some of them. Saved in <see cref="AppSettings.PinnedFolders"/> and
/// <see cref="AppSettings.PinnedFolderNames"/>. Nothing here touches the disk: a pinned folder may be on a drive or
/// share that isn't connected, and is only looked for when it is opened.
/// </summary>
public sealed class PinnedFolders
{
    /// <summary>How many pins get a Ctrl+number shortcut (Ctrl+1 to Ctrl+9).</summary>
    public const int ShortcutCount = 9;

    private readonly List<string> _paths = new();
    private readonly Dictionary<string, string> _names = new(PathUtil.Comparer);   // pinned path (as in _paths) -> the user's name

    /// <summary>
    /// Starts from saved paths, dropping blanks and repeats (the first one stays), and their saved names; names of
    /// folders that aren't pinned, and blank names, are dropped.
    /// </summary>
    public PinnedFolders(IEnumerable<string>? saved = null, IReadOnlyDictionary<string, string>? names = null)
    {
        foreach (string p in saved ?? []) Pin(p);
        foreach (var (path, name) in names ?? new Dictionary<string, string>()) SetName(path, name);
    }

    public IReadOnlyList<string> Paths => _paths;
    public int Count => _paths.Count;

    /// <summary>The names the user gave pins, keyed by the pinned path (for saving).</summary>
    public IReadOnlyDictionary<string, string> Names => _names;

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
        _names.Remove(_paths[i]);                                // pinning it again starts with its folder's name
        _paths.RemoveAt(i);
        return true;
    }

    /// <summary>The name the user gave the pin, or null when it uses the automatic label.</summary>
    public string? CustomName(string? path)
        => IndexOf(path) is >= 0 and var i && _names.TryGetValue(_paths[i], out string? name) ? name : null;

    /// <summary>
    /// Gives the pin its own name (spaces at the ends dropped), or goes back to the automatic label when the name is
    /// blank or null. False when the folder isn't pinned or nothing changed.
    /// </summary>
    public bool SetName(string path, string? name)
    {
        int i = IndexOf(path);
        if (i < 0) return false;
        string key = _paths[i];
        string? clean = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        if (clean == CustomName(key)) return false;
        if (clean == null) _names.Remove(key);
        else _names[key] = clean;
        return true;
    }

    /// <summary>
    /// The Rename Pin dialog's result: <paramref name="typed"/> becomes the pin's name, and a blank one goes back to the
    /// automatic label. Leaving the text the dialog started with (<paramref name="shown"/>) changes nothing, so an
    /// automatic label isn't turned into a fixed name just by clicking OK. False when nothing changed.
    /// </summary>
    public bool Rename(string path, string typed, string shown)
        => typed.Trim() != shown.Trim() && SetName(path, typed);

    /// <summary>The hint under the Rename Pin dialog's box.</summary>
    public const string RenameHint = "Leave it empty to use the folder's name. The full path is always in the tooltip.";

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
    /// <param name="modifier">The shortcut's modifier as the platform writes it, put before the number.</param>
    public static string? ShortcutText(int index, string modifier = "Ctrl+")
        => index is >= 0 and < ShortcutCount ? $"{modifier}{index + 1}" : null;

    /// <summary>
    /// What each pin is called, in order: the name the user gave it, exactly as given; otherwise its folder's name, or
    /// for pins with the same name, the name with its parent folder's, e.g. "2024 (Holidays)", and if that still
    /// matches, the full path. Only pins without a name of their own are compared. A drive or share is named as in
    /// the tree ("D:\", "share (\\server)").
    /// </summary>
    public IReadOnlyList<string> Labels()
    {
        var names = _paths.Select(Name).ToList();
        var labels = new List<string>(names);
        var automatic = Enumerable.Range(0, _paths.Count).Where(i => !_names.ContainsKey(_paths[i])).ToList();
        foreach (int i in automatic)
        {
            if (automatic.Count(j => PathUtil.Comparer.Equals(names[j], names[i])) < 2) continue;
            labels[i] = Path.GetDirectoryName(PathUtil.TrimSeparators(_paths[i])) is { Length: > 0 } parent
                ? $"{names[i]} ({Name(parent)})" : _paths[i];
        }
        var withParents = labels.ToList();
        foreach (int i in automatic)
            if (automatic.Count(j => PathUtil.Comparer.Equals(withParents[j], withParents[i])) > 1) labels[i] = _paths[i];
        for (int i = 0; i < _paths.Count; i++)
            if (_names.TryGetValue(_paths[i], out string? custom)) labels[i] = custom;
        return labels;
    }

    /// <summary>The pin's tooltip: its full path, and its shortcut when it has one.</summary>
    public string ToolTip(int index, string modifier = "Ctrl+")
        => ShortcutText(index, modifier) is { } keys ? $"{_paths[index]} ({keys})" : _paths[index];

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
