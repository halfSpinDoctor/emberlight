namespace Emberlight.Core;

/// <summary>
/// Where "Include subfolders" applies. By default it stays on while the browser moves around inside the folder tree
/// where it was switched on, and switches itself off when a folder outside that tree is opened; with
/// <see cref="SubfolderMode.Always"/> it stays on everywhere. The on/off state is
/// <see cref="AppSettings.IncludeSubfolders"/>; this class keeps the tree's root.
/// </summary>
public sealed class SubfolderScope
{
    private readonly AppSettings _settings;

    public SubfolderScope(AppSettings settings) => _settings = settings;

    /// <summary>The folder at which the option was switched on; null while it is off.</summary>
    public string? Root { get; private set; }

    /// <summary>
    /// Call before the browser shows <paramref name="newPath"/>. Returns true if this switched the option off
    /// (the new folder is outside the tree), so the UI can update its check boxes.
    /// </summary>
    public bool BeforeNavigate(string? currentFolder, string newPath)
    {
        if (!_settings.IncludeSubfolders) { Root = null; return false; }

        // First navigation of the session (a remembered "on" from last time): the start folder is the tree root.
        Root ??= currentFolder ?? newPath;
        if (PathUtil.SubfoldersStayOn(_settings.IncludeSubfoldersMode, newPath, Root)) return false;

        _settings.IncludeSubfolders = false;
        Root = null;
        return true;
    }

    /// <summary>The user switched the option on or off; the tree starts at the folder on screen.</summary>
    public void Set(bool on, string? currentFolder)
    {
        _settings.IncludeSubfolders = on;
        Root = on ? currentFolder : null;
    }

    /// <summary>
    /// The browser is following a slideshow that jumped to <paramref name="folder"/> (with its subfolders, if the option
    /// is on), so that folder becomes the new root rather than switching the option off.
    /// </summary>
    public void FollowedSlideshowTo(string folder)
    {
        if (_settings.IncludeSubfolders) Root = folder;
    }

    /// <summary>
    /// Preferences were saved. If the option or its mode changed, the tree starts at the folder on screen. Returns true
    /// if the option was switched on or off (so the file list changes and must be rescanned).
    /// </summary>
    public bool AfterPreferences(bool wasOn, SubfolderMode oldMode, string? currentFolder)
    {
        bool switched = _settings.IncludeSubfolders != wasOn;
        if (switched || _settings.IncludeSubfoldersMode != oldMode)
            Root = _settings.IncludeSubfolders ? currentFolder : null;
        return switched;
    }
}
