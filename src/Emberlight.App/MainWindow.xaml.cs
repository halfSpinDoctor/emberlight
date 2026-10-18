using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Emberlight.App.Services;
using Emberlight.App.ViewModels;
using Emberlight.Core;

namespace Emberlight.App;

/// <summary>
/// The browser window: folder tree, thumbnail grid, status bar and info panel. Port of
/// CreeveyMainWindowController + DirBrowserDelegate + DYWrappingMatrix. Sorting, scanning, grid geometry
/// and file watching are done by the core library; this class wires them to WPF controls.
/// </summary>
public partial class MainWindow : Window
{
    private readonly AppSettings _settings;
    private readonly string? _startPath;
    private readonly ImageFileTypes _types = new();
    private readonly PinnedFolders _pins;                 // the folders pinned above the tree, saved in the settings
    private MenuItem? _pinCurrentItem;                    // File > Pinned Folders > Pin/Unpin Current Folder
    private bool _suppressPinNav;                         // selecting a pin from code (the highlight) doesn't open it
    private int _pinOpenVersion;                          // the latest pin asked to open (finding it can be slow)
    private string? _contextFolder;                       // the folder a pin or tree right-click menu is for
    private readonly DirectoryScanner _scanner;
    private readonly BulkObservableCollection<ThumbnailItem> _items = new();   // what the grid shows; changed through _listing
    private readonly FileListing<ThumbnailItem> _listing;                     // keeps _items sorted and looks items up by path
    private readonly SubfolderScope _subfolders;
    private readonly ViewSettings _view = new();
    private readonly ThumbnailLoader _loader;
    private readonly FolderWatcher _watcher;

    private string? _currentFolder;
    private string? _pendingSelectPath;
    private readonly DispatcherTimer _followTimer = new() { Interval = SlideshowFollow.Delay };
    private readonly SlideshowFollow _follow = new();   // the browser follows a slideshow's folder jumps after a short pause
    private int _openVersion;              // bumped per File > Open / drop; only the latest one goes ahead
    private int _infoPanelVersion;         // bumped per info-panel update; stale background results are dropped
    private bool _isLoading;               // a folder navigation is in progress
    private int _keepFirst, _keepEnd;     // items in [_keepFirst, _keepEnd) may hold a decoded thumbnail
    private CancellationTokenSource? _scanCts;
    private ScrollViewer? _thumbScroll;
    private bool _suppressTreeNav;
    private readonly ClickExpandTracker<TreeViewItem> _clickExpanded = new();   // folders opened by clicking their name
    private readonly Dictionary<TreeViewItem, Task> _childLoads = new();   // folders whose subfolders are being read
    private int _treeSelectVersion;      // bumped by each tree walk and user selection; an older walk then stops
    private Task _treeRootsListed = Task.CompletedTask;    // done once the shortcuts are in and drive checks started
    private readonly Dictionary<string, Task> _driveChecks = new(PathUtil.Comparer);   // drive root -> its readiness check
    private readonly DispatcherTimer _drivePoll = new() { Interval = FolderTreeRoots.DrivePollInterval };   // drives coming and going
    private bool _drivePollBusy;

    public MainWindow(AppSettings settings, string? startPath)
    {
        InitializeComponent();
        _settings = settings;
        _startPath = startPath;
        UseAccentColorForBusyBar();

        _types.ApplyIgnored(settings.IgnoredFileTypes);
        _scanner = new DirectoryScanner(_types);
        _listing = new FileListing<ThumbnailItem>(_items, p => new ThumbnailItem(p), i => i.Path, settings.SortOrder, _items.ReplaceAll);
        _subfolders = new SubfolderScope(settings);
        _pins = new PinnedFolders(settings.PinnedFolders, settings.PinnedFolderNames);

        _view.CellWidth = settings.ThumbCellWidth;
        _view.ShowFilenames = settings.ShowFilenames;
        DataContext = _view;
        ThumbnailItem.AutoRotate = settings.AutoRotateByOrientationTag;

        Width = settings.MainWindowWidth;
        Height = settings.MainWindowHeight;
        TreeColumn.Width = new GridLength(settings.FolderTreeWidth);
        ThumbList.ItemsSource = _items;
        ThumbList.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, _) => UpdateVisibleThumbnails()));
        ThumbList.SizeChanged += (_, _) => UpdateVisibleThumbnails();

        _loader = new ThumbnailLoader(Dispatcher);
        _watcher = new FolderWatcher(_scanner.ShouldShowFile);
        _watcher.FilesChanged += (changed, deleted) => Dispatcher.BeginInvoke(new Action(() => OnWatcherChanged(changed, deleted)));
        _watcher.RootChanged += () => Dispatcher.BeginInvoke(new Action(OnWatcherRootChanged));
        _watcher.RescanNeeded += () => Dispatcher.BeginInvoke(new Action(() => Rescan()));
        _watcher.FoldersChanged += folders => Dispatcher.BeginInvoke(new Action(() => RefreshTreeFolders(folders)));
        _view.PropertyChanged += OnViewSettingsChanged;

        BuildSortMenu();
        SyncMenuState();
        RebuildPins();

        _followTimer.Tick += async (_, _) => await FlushFolderFollowAsync();
        _drivePoll.Tick += OnDrivePoll;
        Loaded += OnLoaded;
        Closing += OnClosing;
        Closed += (_, _) => { _drivePoll.Stop(); RunOnWatcherQueue(_watcher.Dispose); _loader.Dispose(); };
    }

    // ===============================================================================================
    // Startup / shutdown
    // ===============================================================================================

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _treeRootsListed = BuildTreeRootsAsync();     // SelectPathInTree waits for this (and for its own drive)
        _loader.Configure(ThumbDecodeSize());

        string start = StartupArgs.BrowserFolder(_startPath, _settings, out string? fileToSelect);
        if (fileToSelect != null) _pendingSelectPath = fileToSelect;
        SelectPathInTree(start);
        NavigateTo(start);

        if (_settings.InfoPanelVisible) SetInfoPanelVisible(true);
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        _scanCts?.Cancel();
        CaptureSessionState();
        SettingsStore.Save(_settings);
    }

    /// <summary>
    /// Copies state that lives in the UI (current folder, thumbnail size, sort order, window and column
    /// sizes) into the settings object so that whenever settings are saved they are current.
    /// </summary>
    private void CaptureSessionState()
    {
        if (_currentFolder != null) _settings.LastFolderPath = _currentFolder;
        _settings.ThumbCellWidth = _view.CellWidth;
        _settings.ShowFilenames = _view.ShowFilenames;
        _settings.SortOrder = _listing.Order;
        if (WindowState == WindowState.Normal)
        {
            _settings.MainWindowWidth = ActualWidth;
            _settings.MainWindowHeight = ActualHeight;
        }
        if (TreeColumn.ActualWidth > 0) _settings.FolderTreeWidth = TreeColumn.ActualWidth;
        if (InfoPanel.Visibility == Visibility.Visible && InfoColumn.ActualWidth > 0) _settings.InfoPanelWidth = InfoColumn.ActualWidth;
        _settings.IgnoredFileTypes = _types.DisabledExtensions.ToList();
        _settings.PinnedFolders = _pins.Paths.ToList();
        _settings.PinnedFolderNames = _pins.Names.ToDictionary();
    }

    // ===============================================================================================
    // Folder tree
    // ===============================================================================================

    /// <summary>
    /// Fills the tree's top level: the \u2605 shortcuts, then the drives in letter order. All file-system checks run on
    /// worker threads. Asking whether a drive is ready can take many seconds (a mapped drive whose server is
    /// offline), so each drive is checked separately and slotted into place as soon as it answers. Returns once the
    /// shortcuts are in and the drive checks have started (see <see cref="_driveChecks"/>). From then on the drive
    /// letters are looked at again every few seconds (<see cref="OnDrivePoll"/>).
    /// </summary>
    private async Task BuildTreeRootsAsync()
    {
        FolderTree.Items.Clear();
        _clickExpanded.Clear();
        _driveChecks.Clear();

        var (shortcuts, drives) = await Task.Run(() => (FolderTreeRoots.Shortcuts(), ListDrives()));

        foreach (var (path, label) in shortcuts) FolderTree.Items.Add(MakeTreeItem(path, label));
        foreach (var drive in drives) StartDriveCheck(drive);
        _drivePoll.Start();
    }

    private static DriveInfo[] ListDrives()
    {
        try { return DriveInfo.GetDrives(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    private IEnumerable<TreeViewItem> TopItems => FolderTree.Items.OfType<TreeViewItem>();

    /// <summary>The folders of the top-level entries after the shortcuts (drives, then network shares), in tree order.</summary>
    private List<string> RootsAfterShortcuts()
        => TopItems.Where(i => !FolderTreeRoots.IsShortcut(i.Header.ToString()!)).Select(i => (string)i.Tag).ToList();

    private List<string> DriveRootsInTree() => RootsAfterShortcuts().Where(r => PathUtil.NetworkShareLabel(r) == null).ToList();

    /// <summary>Checks (on a worker thread) whether a drive is ready, and if so slots it into the tree in letter order.</summary>
    private void StartDriveCheck(DriveInfo drive)
        => _driveChecks[PathUtil.TrimSeparators(drive.RootDirectory.FullName)] = AddDriveWhenReadyAsync(drive);

    private async Task AddDriveWhenReadyAsync(DriveInfo drive)
    {
        bool isReady = await Task.Run(() =>
        {
            try { return drive.IsReady; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
        });
        string root = drive.RootDirectory.FullName;
        // Continuations run on the UI thread, so the slot arithmetic can't race with another drive's.
        if (!isReady || DriveRootsInTree().Any(r => PathUtil.SamePath(r, root))) return;
        int shortcuts = TopItems.Count(i => FolderTreeRoots.IsShortcut(i.Header.ToString()!));
        FolderTree.Items.Insert(shortcuts + FolderTreeRoots.DriveInsertIndex(RootsAfterShortcuts(), root), MakeTreeItem(root, drive.Name));
    }

    /// <summary>
    /// Every few seconds: adds drives that appeared or became ready (a network drive reconnecting with a VPN, a USB
    /// stick) and removes drives whose letter has gone. Only new or not-yet-ready letters get the slow readiness check.
    /// </summary>
    private async void OnDrivePoll(object? sender, EventArgs e)
    {
        if (_drivePollBusy) return;
        _drivePollBusy = true;
        try
        {
            var drives = await Task.Run(ListDrives);
            var checking = _driveChecks.Where(kv => !kv.Value.IsCompleted).Select(kv => kv.Key);
            var update = FolderTreeRoots.CompareDrives(DriveRootsInTree(), checking, drives.Select(d => d.RootDirectory.FullName));
            foreach (string gone in update.Removed)
            {
                if (TopItems.FirstOrDefault(i => i.Tag is string t && PathUtil.SamePath(t, gone)) is { } item)
                {
                    _clickExpanded.Forget(item);
                    FolderTree.Items.Remove(item);
                }
                _driveChecks.Remove(PathUtil.TrimSeparators(gone));
            }
            foreach (string root in update.ToCheck)
                if (drives.FirstOrDefault(d => PathUtil.SamePath(d.RootDirectory.FullName, root)) is { } drive) StartDriveCheck(drive);
        }
        catch (Exception ex) { StatusText.Text = ex.Message; }
        finally { _drivePollBusy = false; }
    }

    private TreeViewItem MakeTreeItem(string path, string header)
    {
        var item = new TreeViewItem { Header = header, Tag = path };
        item.Items.Add(new TreeViewItem { Header = "\u2026" });   // placeholder child so the expander arrow shows
        item.Expanded += OnTreeItemExpanded;
        return item;
    }

    private static bool IsPlaceholder(TreeViewItem item)
        => item.Items.Count == 1 && item.Items[0] is TreeViewItem { Tag: null };

    private void OnTreeItemExpanded(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is TreeViewItem item && ReferenceEquals(item, sender)) _ = RefreshChildrenAsync(item);
    }

    /// <summary>
    /// Replaces a folder's placeholder child with its real subfolders. The folder is read on a worker thread,
    /// since that can take seconds on a network share or a sleeping disk; the "…" placeholder shows meanwhile.
    /// Calls made while a read is in progress share it.
    /// </summary>
    private Task EnsureChildrenLoadedAsync(TreeViewItem item)
    {
        if (!IsPlaceholder(item) || item.Tag is not string path) return Task.CompletedTask;
        if (_childLoads.TryGetValue(item, out var running)) return running;
        var load = LoadChildrenAsync(item, path);
        if (!load.IsCompleted) _childLoads[item] = load;
        return load;
    }

    private async Task LoadChildrenAsync(TreeViewItem item, string path)
    {
        try
        {
            var dirs = await Task.Run(() => FolderNavigator.GetSubfolders(path));
            item.Items.Clear();
            foreach (var d in dirs) item.Items.Add(MakeTreeItem(d, Path.GetFileName(d)));
        }
        finally { _childLoads.Remove(item); }
    }

    /// <summary>
    /// Brings a folder's subfolders up to date: loads them the first time, and after that re-reads the folder and adds
    /// or removes entries in place (<see cref="TreeChildren"/>), so subfolders already shown keep their open/closed
    /// state. Runs on every expand, so folders created or deleted elsewhere (in Explorer, say) show up.
    /// </summary>
    private Task RefreshChildrenAsync(TreeViewItem item)
    {
        if (IsPlaceholder(item)) return EnsureChildrenLoadedAsync(item);
        if (item.Tag is not string path) return Task.CompletedTask;
        if (_childLoads.TryGetValue(item, out var running)) return running;
        var update = UpdateChildrenAsync(item, path);
        if (!update.IsCompleted) _childLoads[item] = update;
        return update;
    }

    private async Task UpdateChildrenAsync(TreeViewItem item, string path)
    {
        try
        {
            var dirs = await Task.Run(() => FolderNavigator.GetSubfolders(path));
            var shown = item.Items.OfType<TreeViewItem>().Where(c => c.Tag is string).ToList();
            var update = TreeChildren.Compare(shown.Select(c => (string)c.Tag).ToList(), dirs);
            foreach (string gone in update.Removed)
            {
                var child = shown.First(c => PathUtil.Comparer.Equals((string)c.Tag, gone));
                _clickExpanded.Forget(child);
                item.Items.Remove(child);
            }
            foreach (var (index, folder) in update.Inserted) item.Items.Insert(index, MakeTreeItem(folder, Path.GetFileName(folder)));
        }
        finally { _childLoads.Remove(item); }
    }

    /// <summary>
    /// The watcher saw subfolders come or go in these folders (or F5 was pressed): re-read them wherever the tree has
    /// them loaded, under a drive or under a \u2605 shortcut. Folders the tree hasn't opened yet read fresh when opened.
    /// </summary>
    private void RefreshTreeFolders(IEnumerable<string> folders)
    {
        foreach (string folder in folders)
            foreach (var node in LoadedNodesFor(folder))
                _ = RefreshChildrenAsync(node);
    }

    /// <summary>The tree entries for <paramref name="folder"/> whose subfolders are loaded (one per top-level route to it).</summary>
    private IEnumerable<TreeViewItem> LoadedNodesFor(string folder)
    {
        foreach (var top in TopItems)
        {
            if (top.Tag is not string topPath || !PathUtil.IsSameOrDescendant(folder, topPath)) continue;
            var node = top;
            while (node != null && !PathUtil.SamePath((string)node.Tag, folder))
                node = IsPlaceholder(node) ? null
                    : node.Items.OfType<TreeViewItem>().FirstOrDefault(c => c.Tag is string t && PathUtil.IsSameOrDescendant(folder, t));
            if (node != null && !IsPlaceholder(node)) yield return node;
        }
    }

    /// <summary>Expands the tree down to <paramref name="path"/> (via its drive) and selects it without navigating.</summary>
    private async void SelectPathInTree(string path)
    {
        try { await SelectPathInTreeAsync(path); }
        catch (Exception ex) { StatusText.Text = ex.Message; }
    }

    /// <summary>
    /// Expands the tree down to <paramref name="path"/> and selects it (without navigating: the caller does that).
    /// Each level's subfolders are read in the background, so this can take a while on a network share; it gives up
    /// if another folder is selected in the meantime.
    /// </summary>
    private async Task SelectPathInTreeAsync(string path)
    {
        int version = ++_treeSelectVersion;
        var chain = PathUtil.AncestorChain(path);     // the drive or share, then each folder down to the path
        if (chain.Count == 0) return;

        string root = chain[0];
        TreeViewItem? FindRoot() => FolderTree.Items.OfType<TreeViewItem>().FirstOrDefault(i =>
            i.Tag is string t && PathUtil.SamePath(t, root) && !FolderTreeRoots.IsShortcut(i.Header.ToString()!));
        await _treeRootsListed;
        if (version != _treeSelectVersion) return;
        var current = FindRoot();
        if (current == null && _driveChecks.TryGetValue(PathUtil.TrimSeparators(root), out var driveCheck))
        {
            await driveCheck;                         // the drive may still be answering whether it is ready
            if (version != _treeSelectVersion) return;
            current = FindRoot();
        }
        // A network share without a drive letter (\\server\share) has no root entry until a folder on it is opened.
        // (Shares can't be listed from the server name alone without native calls, so the share is the root.)
        if (current == null && PathUtil.NetworkShareLabel(root) is { } shareLabel)
        {
            bool exists = await Task.Run(() => Directory.Exists(root));
            if (version != _treeSelectVersion) return;
            current = FindRoot();                     // another call may have added it while we waited
            if (current == null && exists)
            {
                current = MakeTreeItem(root, shareLabel);
                FolderTree.Items.Add(current);
            }
        }
        if (current == null) return;

        foreach (string next in chain.Skip(1))
        {
            OpenAsIfClicked(current);
            await EnsureChildrenLoadedAsync(current);
            if (version != _treeSelectVersion) return;   // the user picked another folder while this one loaded
            var child = current.Items.OfType<TreeViewItem>().FirstOrDefault(i =>
                i.Tag is string t && string.Equals(t, next, PathUtil.Comparison));
            if (child == null) break;
            current = child;
        }
        OpenAsIfClicked(current);                     // the folder itself too, as a click would

        _suppressTreeNav = true;
        try { current.IsSelected = true; }
        finally { _suppressTreeNav = false; }
        // The rows above it were only just added and haven't been laid out, so scrolling now would use stale
        // positions; wait for layout first.
        var target = current;
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => target.BringIntoView());
    }

    /// <summary>
    /// Clicking a folder's name expands it, so its subfolders appear without a second click on the arrow.
    /// The arrow itself still toggles normally, and a double-click no longer collapses what the first click
    /// just opened (WPF's default double-click toggles). Clicking another folder collapses the folders that
    /// earlier clicks opened, apart from its own parents (<see cref="ClickExpandTracker{TNode}"/>). Keyboard
    /// navigation neither expands nor collapses anything.
    /// </summary>
    private void OnTreePreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        var source = e.OriginalSource as DependencyObject;
        if (FindAncestor<TreeViewItem>(source) is not { } item) return;
        if (FindAncestor<ToggleButton>(source) != null)                   // the expander arrow handles itself,
        {
            _clickExpanded.Forget(item);                                  // and puts that folder under manual control
            return;
        }

        if (e.ClickCount > 1) { e.Handled = true; return; }               // keep it open on double-click
        OpenAsIfClicked(item);
    }

    /// <summary>
    /// Opens a folder the way clicking its name does: expands it and closes the folders earlier clicks opened,
    /// except this one's parents. Also used for each level when the app opens a folder itself (startup folder,
    /// File &gt; Open, drag and drop), so those folders close again when the user clicks elsewhere.
    /// </summary>
    private void OpenAsIfClicked(TreeViewItem item)
    {
        bool expands = item.HasItems && !item.IsExpanded;
        // WPF moves the selection onto a folder that is collapsed around it; don't load that folder, since
        // the caller is about to select the one opened.
        _suppressTreeNav = true;
        try { foreach (var other in _clickExpanded.Click(item, SelfAndTreeAncestors(item), expands)) other.IsExpanded = false; }
        finally { _suppressTreeNav = false; }
        if (item.HasItems) item.IsExpanded = true;                        // loads the children on first expand
    }

    private static IEnumerable<TreeViewItem> SelfAndTreeAncestors(TreeViewItem item)
    {
        for (var i = item; i != null; i = ItemsControl.ItemsControlFromItemContainer(i) as TreeViewItem) yield return i;
    }

    private static T? FindAncestor<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d != null)
        {
            if (d is T match) return match;
            d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);   // Runs etc. aren't Visuals
        }
        return null;
    }

    private void OnFolderSelected(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (_suppressTreeNav) return;
        _treeSelectVersion++;                     // the user chose a folder: stop any tree walk still in progress
        if (e.NewValue is TreeViewItem { Tag: string path }) NavigateTo(path);
    }

    // ===============================================================================================
    // Navigation and scanning
    // ===============================================================================================

    private Task _watcherQueue = Task.CompletedTask;

    /// <summary>
    /// Runs a watcher call (Stop, Watch, Dispose) on a worker thread, one at a time and in order. Closing or opening a
    /// directory handle on a share whose connection has dropped can block for tens of seconds, which must not freeze the UI.
    /// </summary>
    private void RunOnWatcherQueue(Action action)
        => _watcherQueue = _watcherQueue.ContinueWith(_ => { try { action(); } catch { } }, TaskScheduler.Default);

    /// <param name="inPlace">Rescanning the folder on screen: keep the grid as it is while scanning, then update it in
    /// place (<see cref="ApplyRescan"/>), so nothing flickers. Opening a folder (or F5) starts from an empty grid.</param>
    private async void NavigateTo(string path, bool inPlace = false)
    {
        try { await NavigateToAsync(path, inPlace); }
        catch (Exception ex) { StatusText.Text = ex.Message; }
    }

    private async Task NavigateToAsync(string path, bool inPlace = false)
    {
        _scanCts?.Cancel();
        var cts = _scanCts = new CancellationTokenSource();
        var ct = cts.Token;

        inPlace &= _currentFolder != null && PathUtil.SamePath(path, _currentFolder);
        if (_subfolders.BeforeNavigate(_currentFolder, path)) SyncMenuState();   // left the tree: it switched itself off
        _isLoading = true;
        _currentFolder = path;
        HighlightCurrentPin();
        RunOnWatcherQueue(_watcher.Stop);
        _listing.ResetMetadata();
        if (inPlace)
        {
            // Only show the busy indicator if the scan takes a moment, so a quick rescan changes nothing on screen.
            _ = ShowBusySoonAsync(ct);
        }
        else
        {
            _loader.Reset();
            Title = DisplayText.BrowserTitle(path);
            // Show an empty grid and a busy indicator immediately, so the user can see the click was received.
            _keepFirst = _keepEnd = 0;
            _listing.Clear();
            SlideshowButton.IsEnabled = false;
            BeginBusy(DisplayText.ScanStarted);
        }

        var sortFiles = _listing.CaptureSorter();
        bool recurse = _settings.IncludeSubfolders;
        // Progress<T> captures the UI context, so this callback runs on the UI thread.
        var scanProgress = new Progress<int>(n =>
        {
            if (!ct.IsCancellationRequested && IsBusyShown) StatusText.Text = DisplayText.Scanning(n);
        });

        List<string> files;
        try
        {
            files = await Task.Run(() => _scanner.Scan(path, recurse, ct, scanProgress), ct);
            if (ct.IsCancellationRequested) return;

            // Sorting by a file-system or EXIF date has to open every file, which can take a while.
            if (IsBusyShown) BeginBusy(DisplayText.Sorting(files.Count, _listing.Order));
            await Task.Run(() => sortFiles(files, ct), ct);
        }
        catch (OperationCanceledException) { return; }      // a newer navigation owns the status bar now
        catch (Exception ex)
        {
            _isLoading = false;
            EndBusy();
            StatusText.Text = ex.Message;
            return;
        }

        if (ct.IsCancellationRequested || !string.Equals(path, _currentFolder, PathUtil.Comparison)) return;

        if (inPlace) ApplyRescan(files);
        else
        {
            SetBusyProgress(files.Count, files.Count, DisplayText.Loading(files.Count));
            Populate(files);
        }

        RunOnWatcherQueue(() => _watcher.Watch(path, recurse));
        if (_pendingSelectPath != null && _listing.TryGet(_pendingSelectPath, out var wanted))
        {
            ThumbList.SelectedItem = wanted;
            ThumbList.ScrollIntoView(wanted);
        }
        _pendingSelectPath = null;
        _isLoading = false;
        EndBusy();
        UpdateStatus();
    }

    private bool IsBusyShown => BusyItem.Visibility == Visibility.Visible;

    private async Task ShowBusySoonAsync(CancellationToken ct)
    {
        try { await Task.Delay(400, ct); }
        catch (OperationCanceledException) { return; }
        if (_isLoading) BeginBusy(DisplayText.ScanStarted);
    }

    /// <summary>
    /// Updates the grid from a rescan of the folder on screen without starting over (<see cref="FileListing{TItem}.Reconcile"/>):
    /// pictures still there keep their thumbnails, selection and place, so the grid doesn't flash.
    /// </summary>
    private void ApplyRescan(List<string> files)
    {
        var selected = SelectedItems();
        if (_listing.Reconcile(files).Replaced)
        {
            // The whole list was swapped (many changes): any item may have moved, and the selection needs putting back.
            _keepFirst = 0;
            _keepEnd = _items.Count;
            foreach (var item in selected)
                if (_listing.TryGet(item.Path, out var same) && ReferenceEquals(same, item)) ThumbList.SelectedItems.Add(item);
        }
        UpdateVisibleThumbnails();
    }

    /// <summary>
    /// Hands the whole list to the grid. The grid is virtualized, so this only creates containers for the
    /// rows on screen no matter how many files there are; the cost is just wrapping each path in an item.
    /// </summary>
    private void Populate(List<string> files)
    {
        _keepFirst = _keepEnd = 0;
        _listing.Populate(files);
        UpdateVisibleThumbnails();       // start loading the first screenful straight away
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(UpdateVisibleThumbnails));   // and again once layout has run
    }

    // ---- busy indicator in the status bar ----

    private void BeginBusy(string message)
    {
        BusyItem.Visibility = Visibility.Visible;
        BusyBar.IsIndeterminate = true;
        StatusText.Text = message;
    }

    private void SetBusyProgress(int done, int total, string message)
    {
        BusyBar.IsIndeterminate = false;
        BusyBar.Maximum = Math.Max(1, total);
        BusyBar.Value = done;
        StatusText.Text = message;
    }

    /// <summary>
    /// Tightens Fluent's menu item spacing, which its templates hard-code (a 10 px margin around menu bar headers,
    /// 8x6 px inside drop-down items, a 20 px check box square). Looks the template parts up by name; if a future
    /// Fluent template renames them, that item just keeps the default spacing.
    /// </summary>
    private void OnMenuItemLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem item || item.Template is not { } template) return;
        bool onMenuBar = item.Role is MenuItemRole.TopLevelHeader or MenuItemRole.TopLevelItem;
        if (template.FindName("Border", item) is not Border border) return;

        border.Margin = onMenuBar ? new Thickness(2) : new Thickness(4, 0, 4, 0);
        // The content grid is the border's child, or (menu bar headers with a drop-down) that grid's first child.
        var content = border.Child as Grid;
        if (content is { Margin.Top: 0 } && content.Children.Count > 0 && content.Children[0] is Grid inner) content = inner;
        // Every drop-down indents its text by a check box column (16 px square + 6 px gap). Fluent sizes that column
        // per menu, and to zero in menus without toggles, so it is fixed here instead; items that open a submenu
        // (Sort By) have no such column and get the same amount as a margin.
        const double checkColumn = 16 + 6;
        double left = item.Role == MenuItemRole.SubmenuHeader ? 8 + checkColumn : 8;
        if (content != null) content.Margin = onMenuBar ? new Thickness(8, 4, 8, 4) : new Thickness(left, 3, 8, 3);
        if (item.Role == MenuItemRole.SubmenuItem && content is { ColumnDefinitions.Count: 4 })
        {
            content.ColumnDefinitions[0].SharedSizeGroup = null;
            content.ColumnDefinitions[0].Width = new GridLength(checkColumn);
        }

        if (template.FindName("CheckBoxIconBorder", item) is not Border check) return;
        check.Width = check.Height = 16;

        // Sort By items: the sort direction arrow (carried in the item's Icon, see SyncMenuState) goes in the check
        // box column instead of the icon column after it.
        if (item.Tag is SortField && template.FindName("CheckBoxIcon", item) is TextBlock glyph
            && template.FindName("Icon", item) is ContentPresenter iconColumn)
        {
            check.Visibility = Visibility.Visible;
            check.BorderThickness = new Thickness(0);
            check.Background = Brushes.Transparent;
            glyph.FontFamily = FontFamily;                 // Fluent's symbol font has no triangles
            glyph.FontSize = 10;
            glyph.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(MenuItem.Icon)) { Source = item });
            iconColumn.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>
    /// Colours the loading bar with the Windows accent colour, as a dynamic resource so it follows the user changing it
    /// in Settings. If the accent colour can't be read, the ember orange set in the XAML stays.
    /// </summary>
    private void UseAccentColorForBusyBar()
    {
        try
        {
            if (SystemColors.AccentColor.A == 0) return;       // nothing usable (e.g. no accent colour on this system)
            BusyBar.SetResourceReference(ForegroundProperty, SystemColors.AccentColorBrushKey);
        }
        catch (Exception) { /* keep the fallback colour; a cosmetic setting must never stop the window opening */ }
    }

    private void EndBusy() => BusyItem.Visibility = Visibility.Collapsed;

    /// <param name="startOver">F5 / Refresh: reload everything from an empty grid. Otherwise (the watcher, the
    /// subfolders switch, Preferences) the grid is updated in place, without flicker.</param>
    private void Rescan(bool startOver = false)
    {
        if (_currentFolder == null) return;
        RefreshTreeFolders([_currentFolder]);
        NavigateTo(_currentFolder, inPlace: !startOver);
    }

    // ===============================================================================================
    // Thumbnail scheduling
    // ===============================================================================================

    private int ThumbDecodeSize() => DecodePolicy.ThumbnailDecodeSize(_view.CellWidth, VisualTreeHelper.GetDpi(this).DpiScaleX);

    private void OnViewSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ViewSettings.CellWidth) && _loader.Configure(ThumbDecodeSize()))
            foreach (var item in _items) item.ResetThumbnail();   // different decode size: start over
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(UpdateVisibleThumbnails));
    }

    private ScrollViewer? FindThumbScroll()
    {
        if (_thumbScroll != null) return _thumbScroll;
        static ScrollViewer? Find(DependencyObject o)
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(o); i++)
            {
                var child = VisualTreeHelper.GetChild(o, i);
                if (child is ScrollViewer sv) return sv;
                if (Find(child) is { } nested) return nested;
            }
            return null;
        }
        return _thumbScroll = Find(ThumbList);
    }

    /// <summary>Asks the loader for thumbnails of the cells that are (nearly) on screen.</summary>
    private void UpdateVisibleThumbnails()
    {
        if (_items.Count == 0) return;
        var sv = FindThumbScroll();
        if (sv == null || sv.ViewportHeight <= 0) return;

        var (first, end) = ThumbnailGridMath.VisibleRange(_items.Count, sv.ViewportWidth, sv.ViewportHeight,
            sv.VerticalOffset, _view.OuterCellWidth, _view.OuterCellHeight, overscanRows: 2);
        _loader.RequestRange(_items, first, end);

        ReleaseDistantThumbnails(sv, first, end);
    }

    /// <summary>
    /// A virtualized grid only has containers for what's on screen, but every item would otherwise keep its
    /// decoded bitmap forever. Items that fall outside a window of a few screens around the viewport let
    /// theirs go (the shared thumbnail cache still holds recent ones, so scrolling back is quick).
    /// Only the part of the old window that fell out of the new one is visited, so this is cheap.
    /// </summary>
    private void ReleaseDistantThumbnails(ScrollViewer sv, int first, int end)
    {
        int margin = ThumbnailGridMath.RetainMargin(sv.ViewportWidth, sv.ViewportHeight, _view.OuterCellWidth, _view.OuterCellHeight);
        var (keepFirst, keepEnd) = ThumbnailGridMath.RetainedRange(_items.Count, first, end, margin);
        foreach (int i in ThumbnailGridMath.ReleasedIndices(_items.Count, _keepFirst, _keepEnd, keepFirst, keepEnd))
        {
            if (_items[i].IsLoaded) _items[i].ResetThumbnail();
        }
        _keepFirst = keepFirst;
        _keepEnd = keepEnd;
    }

    // ===============================================================================================
    // Selection, status, info panel
    // ===============================================================================================

    private List<ThumbnailItem> SelectedItems() => ThumbList.SelectedItems.Cast<ThumbnailItem>().ToList();

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateStatus();
        UpdateInfoPanel();
    }

    private void UpdateStatus()
    {
        SlideshowButton.IsEnabled = _items.Count > 0;
        var sel = SelectedItems();
        var one = sel.Count == 1 ? sel[0] : null;
        StatusText.Text = DisplayText.BrowserStatus(_items.Count, sel.Count, one?.FileName, one?.Info);
    }

    /// <summary>Reading the image's size and EXIF is disk I/O (slow on a network share), so it runs on a worker
    /// thread; a result that arrives after the selection has changed again is dropped.</summary>
    private void UpdateInfoPanel()
    {
        if (InfoPanel.Visibility != Visibility.Visible) return;
        int version = ++_infoPanelVersion;
        var sel = SelectedItems();
        string header = DisplayText.InfoPanelHeader(sel.Count, sel.Count == 1 ? sel[0].FileName : null);
        if (sel.Count != 1)
        {
            InfoHeader.Text = header;
            InfoBody.Text = "";
            return;
        }

        string path = sel[0].Path;
        bool moreExif = _settings.InfoPanelMoreExif;
        _ = Task.Run(() =>
        {
            (int, int)? pixelSize = ImageService.TryReadSize(path, out int w, out int h) ? (w, h) : null;
            return DisplayText.InfoPanelBody(path, pixelSize, moreExif);
        }).ContinueWith(t =>
        {
            if (version != _infoPanelVersion) return;
            InfoHeader.Text = header;
            InfoBody.Text = t.IsCompletedSuccessfully ? t.Result : "";
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void SetInfoPanelVisible(bool visible)
    {
        // The panel's width is the user's (dragged with InfoSplitter); remember it when the panel is hidden.
        if (!visible && InfoPanel.Visibility == Visibility.Visible && InfoColumn.ActualWidth > 0)
            _settings.InfoPanelWidth = InfoColumn.ActualWidth;
        InfoPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        InfoSplitter.Visibility = InfoPanel.Visibility;
        InfoColumn.MinWidth = visible ? AppSettings.MinInfoPanelWidth : 0;
        InfoColumn.Width = new GridLength(visible ? _settings.InfoPanelWidth : 0);
        _settings.InfoPanelVisible = visible;
        MenuInfoPanel.IsChecked = visible;
        UpdateInfoPanel();
    }

    // ===============================================================================================
    // Sorting
    // ===============================================================================================

    private void BuildSortMenu()
    {
        SortMenu.Items.Clear();
        foreach (var (field, label) in SortOrder.Choices)
        {
            var mi = new MenuItem { Header = label, Tag = field };
            mi.Click += OnSortClick;
            SortMenu.Items.Add(mi);
        }
    }

    private void SyncMenuState()
    {
        foreach (MenuItem mi in SortMenu.Items)
        {
            var field = (SortField)mi.Tag;
            string label = SortOrder.LabelFor(field);
            bool active = field == _listing.Order.Key;
            mi.IsChecked = active;
            mi.Header = label;
            // The direction arrow; OnMenuItemLoaded shows it to the left of the label, in the check box column.
            mi.Icon = active ? (_listing.Order.Descending ? "\u25BC" : "\u25B2") : null;
        }
        MenuShowFilenames.IsChecked = _view.ShowFilenames;
        MenuSubfolders.IsChecked = _settings.IncludeSubfolders;
        SubfoldersCheck.IsChecked = _settings.IncludeSubfolders;
        MenuInfoPanel.IsChecked = InfoPanel.Visibility == Visibility.Visible;
        MenuMoreExif.IsChecked = _settings.InfoPanelMoreExif;
        MenuLoop.IsChecked = _settings.SlideshowLoop;
        MenuRandom.IsChecked = _settings.SlideshowRandom;
        RandomCheck.IsChecked = _settings.SlideshowRandom;
        MenuScaleUp.IsChecked = _settings.SlideshowScaleUp;
        MenuActualSize.IsChecked = _settings.SlideshowActualSize;
        MenuAutoAdvance.IsChecked = _settings.SlideshowAutoAdvance;
        MenuFullScreen.IsChecked = _settings.SlideshowDefaultFullScreen;
    }

    private async void OnSortClick(object sender, RoutedEventArgs e)
    {
        var field = (SortField)((MenuItem)sender).Tag;
        try { await ApplySortAsync(_listing.Order.Select(field)); }   // same field again reverses it; dates start newest first
        catch (Exception ex) { StatusText.Text = ex.Message; }
    }

    private async Task ApplySortAsync(SortOrder order)
    {
        _listing.SetOrder(order);
        SyncMenuState();
        if (_items.Count == 0) return;

        var selected = SelectedItems();
        StatusText.Text = DisplayText.Resorting;
        if (!await _listing.SortAsync()) return;     // superseded by a newer sort or folder: that one finishes the job

        // Indices just moved, so any item might now be far from the viewport: treat everything as possibly
        // loaded and let the next update release whatever falls outside the window.
        _keepFirst = 0;
        _keepEnd = _items.Count;
        foreach (var s in selected) ThumbList.SelectedItems.Add(s);
        if (selected.Count > 0) ThumbList.ScrollIntoView(selected[0]);
        UpdateStatus();
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(UpdateVisibleThumbnails));
    }

    // ===============================================================================================
    // Slideshow
    // ===============================================================================================

    private void StartSlideshow(bool fullScreen)
    {
        if (_items.Count == 0) return;
        // Several files selected: just those; one or none: the whole folder, starting there.
        var target = StartupTarget.FromSelection(_items.Select(i => i.Path).ToList(),
            SelectedItems().Select(i => i.Path).ToList(), _currentFolder);
        ShowSlideshow(target.Files, target.StartIndex, _settings.IncludeSubfolders, target.Folder, fullScreen);
    }

    /// <summary>
    /// Opens a slideshow that reports back to this browser: it follows folder jumps, and when the slideshow
    /// closes it selects the last image shown. Also used for slideshows started from "Open with".
    /// </summary>
    public void ShowSlideshow(IReadOnlyList<string> files, int start, bool includeSubfolders, string? startFolder, bool fullScreen)
    {
        var show = new SlideshowWindow(_settings, files, start, _listing.Order, _scanner, includeSubfolders, startFolder, fullScreen);
        show.FolderJumped += OnSlideshowFolderJumped;
        show.Closed += async (_, _) =>
        {
            await FlushFolderFollowAsync();    // if the browser hasn't caught up with the last jump yet, do it now
            string? last = show.LastShownPath;
            ThumbnailItem? item = null;
            switch (SlideshowFollow.AfterClose(last, last != null && _listing.TryGet(last, out item), _isLoading))
            {
                case CloseSelection.Select:
                    ThumbList.SelectedItem = item;
                    ThumbList.ScrollIntoView(item);
                    break;
                case CloseSelection.SelectWhenLoaded:
                    _pendingSelectPath = last;    // the folder is still loading; select the slide when it arrives
                    break;
            }
            Activate();
        };
        show.Show();
    }

    /// <summary>
    /// The slideshow jumped to another folder (Ctrl+Left/Right). Bring the browser along quietly in the
    /// background, after a short pause so holding the key down doesn't reload the browser for every folder.
    /// </summary>
    private void OnSlideshowFolderJumped(string folder)
    {
        _follow.Jumped(folder);
        _followTimer.Stop();
        _followTimer.Start();
    }

    /// <summary>Whether the folder still exists is asked on a worker thread (it may be on a share that has dropped).
    /// Awaited when the slideshow closes, so the folder is loading before the last slide is looked for.</summary>
    private async Task FlushFolderFollowAsync()
    {
        _followTimer.Stop();
        if (_follow.TakePending(_currentFolder) is not { } path) return;
        if (!await Task.Run(() => Directory.Exists(path))) return;

        // The slideshow just showed this folder (with its subfolders if that option is on), so the browser
        // should too: make it the new root of the "include subfolders" tree rather than switching the option off.
        _subfolders.FollowedSlideshowTo(path);
        SelectPathInTree(path);
        NavigateTo(path);
    }

    private void OnStartSlideshow(object sender, RoutedEventArgs e) => StartSlideshow(_settings.SlideshowDefaultFullScreen);
    private void OnStartSlideshowWindowed(object sender, RoutedEventArgs e) => StartSlideshow(false);

    private void OnThumbDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && ItemsControl.ContainerFromElement(ThumbList, d) is ListBoxItem)
            StartSlideshow(_settings.SlideshowDefaultFullScreen);
    }

    private void OnSlideshowOption(object sender, RoutedEventArgs e)
    {
        _settings.SlideshowLoop = MenuLoop.IsChecked;
        _settings.SlideshowRandom = MenuRandom.IsChecked;
        _settings.SlideshowScaleUp = MenuScaleUp.IsChecked;
        _settings.SlideshowActualSize = MenuActualSize.IsChecked;
        _settings.SlideshowAutoAdvance = MenuAutoAdvance.IsChecked;
        _settings.SlideshowDefaultFullScreen = MenuFullScreen.IsChecked;
        SyncMenuState();                       // keeps the status-bar Random checkbox in step with the menu
    }

    /// <summary>The status-bar "Random" checkbox: the same setting as Slideshow &gt; Random Order.</summary>
    private void OnToggleRandom(object sender, RoutedEventArgs e)
    {
        _settings.SlideshowRandom = RandomCheck.IsChecked == true;
        SyncMenuState();
    }

    // ===============================================================================================
    // Keyboard
    // ===============================================================================================

    /// <summary>Keys for the thumbnail grid itself (only while it has focus): Delete, F2 (rename) and moving the selection.</summary>
    private void OnThumbKeyDown(object sender, KeyEventArgs e)
    {
        if (RunIfApplicable(MapGridKey(e.Key, Keyboard.Modifiers))) e.Handled = true;
    }

    /// <summary>Window-wide keys, so they work wherever the focus is (Enter works with nothing selected, for instance).</summary>
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Handled) return;
        if (RunIfApplicable(MapWindowKey(e.Key, Keyboard.Modifiers))) e.Handled = true;
    }

    private bool RunIfApplicable(BrowserInput input)
    {
        if (!BrowserCommands.CanRun(input, _items.Count, _pins.Count)) return false;
        Execute(input);
        return true;
    }

    /// <summary>The grid's keys. Arrows, Page Up/Down, Home and End move by item index and row arithmetic rather than
    /// by the containers on screen: in a virtualized grid most items have no container to move focus between.</summary>
    private static BrowserInput MapGridKey(Key key, ModifierKeys mods)
    {
        if (key == Key.Delete) return new(BrowserCommand.Delete);
        if (key == Key.F2 && mods == ModifierKeys.None) return new(BrowserCommand.Rename);
        if ((mods & (ModifierKeys.Shift | ModifierKeys.Control)) != 0) return default;   // leave extend-selection keys to the list
        return key switch
        {
            Key.Left => new(BrowserCommand.MoveSelection, GridMove.Left),
            Key.Right => new(BrowserCommand.MoveSelection, GridMove.Right),
            Key.Up => new(BrowserCommand.MoveSelection, GridMove.Up),
            Key.Down => new(BrowserCommand.MoveSelection, GridMove.Down),
            Key.PageUp => new(BrowserCommand.MoveSelection, GridMove.PageUp),
            Key.PageDown => new(BrowserCommand.MoveSelection, GridMove.PageDown),
            Key.Home => new(BrowserCommand.MoveSelection, GridMove.Home),
            Key.End => new(BrowserCommand.MoveSelection, GridMove.End),
            _ => default,
        };
    }

    /// <summary>The browser's window-wide keys (the View and File menus list the same shortcuts).</summary>
    private static BrowserInput MapWindowKey(Key key, ModifierKeys mods)
    {
        bool ctrl = (mods & ModifierKeys.Control) != 0;
        if (key == Key.F5) return new(BrowserCommand.Rescan);
        if (key == Key.Enter && mods == ModifierKeys.None && EnterStartsSlideshow()) return new(BrowserCommand.StartSlideshow);
        if (!ctrl) return default;
        if (key is >= Key.D1 and <= Key.D9) return new(BrowserCommand.OpenPinned, Number: key - Key.D0);
        if (key is >= Key.NumPad1 and <= Key.NumPad9) return new(BrowserCommand.OpenPinned, Number: key - Key.NumPad0);
        return key switch
        {
            Key.O => new(BrowserCommand.OpenFolder),
            Key.I => new(BrowserCommand.ToggleInfoPanel),
            Key.F => new(BrowserCommand.ToggleSubfolders),
            Key.S => new(BrowserCommand.ToggleRandom),
            Key.OemPlus or Key.Add => new(BrowserCommand.LargerThumbnails),
            Key.OemMinus or Key.Subtract => new(BrowserCommand.SmallerThumbnails),
            _ => default,
        };
    }

    /// <summary>False where Enter already does something: an open menu, a focused button or a text box.</summary>
    private static bool EnterStartsSlideshow()
        => Keyboard.FocusedElement is not (MenuItem or Button or TextBoxBase);

    private void Execute(BrowserInput input)
    {
        switch (input.Command)
        {
            case BrowserCommand.Rescan: Rescan(startOver: true); break;
            case BrowserCommand.StartSlideshow: StartSlideshow(_settings.SlideshowDefaultFullScreen); break;
            case BrowserCommand.OpenFolder: OnOpenFolder(this, new RoutedEventArgs()); break;
            case BrowserCommand.ToggleInfoPanel: SetInfoPanelVisible(InfoPanel.Visibility != Visibility.Visible); break;
            case BrowserCommand.ToggleSubfolders:
                SubfoldersCheck.IsChecked = SubfoldersCheck.IsChecked != true;
                OnToggleSubfolders(SubfoldersCheck, new RoutedEventArgs());
                break;
            case BrowserCommand.ToggleRandom:
                RandomCheck.IsChecked = RandomCheck.IsChecked != true;
                OnToggleRandom(RandomCheck, new RoutedEventArgs());
                break;
            case BrowserCommand.LargerThumbnails: _view.CellWidth += ThumbnailGridMath.SizeStep; break;
            case BrowserCommand.SmallerThumbnails: _view.CellWidth -= ThumbnailGridMath.SizeStep; break;
            case BrowserCommand.Delete: OnDelete(this, new RoutedEventArgs()); break;
            case BrowserCommand.Rename: OnRename(this, new RoutedEventArgs()); break;
            case BrowserCommand.OpenPinned:
                if (_pins.ForShortcut(input.Number) is { } pin && !PathUtil.SamePath(pin, _currentFolder)) OpenPinned(pin);
                break;
            case BrowserCommand.MoveSelection: MoveSelection(input.Move); break;
        }
    }

    private void MoveSelection(GridMove move)
    {
        var sv = FindThumbScroll();
        int cols = ThumbnailGridMath.Columns(sv?.ViewportWidth ?? ThumbList.ActualWidth, _view.OuterCellWidth);
        int rows = ThumbnailGridMath.VisibleRowCount(sv?.ViewportHeight ?? ThumbList.ActualHeight, _view.OuterCellHeight);
        SelectOnly(ThumbnailGridMath.MoveTarget(move, ThumbList.SelectedIndex, _items.Count, cols, rows));
    }

    private void SelectOnly(int index)
    {
        ThumbList.SelectedItems.Clear();
        ThumbList.SelectedIndex = index;
        var item = _items[index];
        ThumbList.ScrollIntoView(item);
        // The item's container may not exist until the next layout pass scrolls it into the realized range.
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            (ThumbList.ItemContainerGenerator.ContainerFromItem(item) as ListBoxItem)?.Focus()));
    }

    // ===============================================================================================
    // Pinned folders
    // ===============================================================================================

    /// <summary>
    /// Shows the pins (<see cref="PinnedFolders"/>) above the tree and in File &gt; Pinned Folders, with their Ctrl+number
    /// shortcuts. The list above the tree is only there while something is pinned.
    /// </summary>
    private void RebuildPins()
    {
        var labels = _pins.Labels();
        _suppressPinNav = true;
        try
        {
            PinList.Items.Clear();
            for (int i = 0; i < _pins.Count; i++)
                PinList.Items.Add(new ListBoxItem { Content = PinRow(labels[i]), Tag = _pins.Paths[i], ToolTip = _pins.ToolTip(i) });
        }
        finally { _suppressPinNav = false; }
        PinList.Visibility = _pins.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        HighlightCurrentPin();

        PinnedMenu.Items.Clear();
        for (int i = 0; i < _pins.Count; i++)
        {
            string path = _pins.Paths[i];
            var item = new MenuItem
            {
                Header = labels[i].Replace("_", "__"),           // an underscore would otherwise be an access key
                InputGestureText = PinnedFolders.ShortcutText(i) ?? "",
                ToolTip = path,
            };
            item.Click += (_, _) => { if (!PathUtil.SamePath(path, _currentFolder)) OpenPinned(path); };
            PinnedMenu.Items.Add(item);
        }
        if (_pins.Count == 0) PinnedMenu.Items.Add(new MenuItem { Header = "No pinned folders", IsEnabled = false });
        PinnedMenu.Items.Add(new Separator());
        _pinCurrentItem = new MenuItem { Header = "_" + _pins.PinCommandText(_currentFolder, "Current Folder") };
        _pinCurrentItem.Click += (_, _) => { if (_currentFolder != null) TogglePin(_currentFolder); };
        PinnedMenu.Items.Add(_pinCurrentItem);
    }

    /// <summary>A pin's row: a pin glyph and the folder's label.</summary>
    private static StackPanel PinRow(string label)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new TextBlock
        {
            Text = "",                                     // Pin, in Windows 11's icon font (MDL2 on older systems)
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 10,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 1, 6, 0),
        });
        row.Children.Add(new TextBlock { Text = label, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
        return row;
    }

    /// <summary>Highlights the pin for the folder on screen, if it is pinned.</summary>
    private void HighlightCurrentPin()
    {
        _suppressPinNav = true;
        try { PinList.SelectedIndex = _pins.IndexOf(_currentFolder); }
        finally { _suppressPinNav = false; }
    }

    private void OnPinSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressPinNav) return;
        if (PinList.SelectedItem is ListBoxItem { Tag: string path }) OpenPinned(path);
    }

    /// <summary>A right-click would select the pin, and so open it; only its menu should appear.</summary>
    private void OnPinRightButtonDown(object sender, MouseButtonEventArgs e) => e.Handled = true;

    /// <summary>
    /// Opens a pinned folder the way File &gt; Open does. Whether it exists is asked on a worker thread: it may be on a
    /// drive or share that isn't connected, which can take a while to answer. A missing folder stays pinned.
    /// </summary>
    private async void OpenPinned(string path)
    {
        int version = ++_pinOpenVersion;
        bool exists = await Task.Run(() => Directory.Exists(path));
        if (version != _pinOpenVersion) return;                  // another pin was picked meanwhile
        if (!exists)
        {
            StatusText.Text = PinnedFolders.NotFound(path);
            HighlightCurrentPin();
            return;
        }
        SelectPathInTree(path);
        NavigateTo(path);
    }

    /// <summary>Pins the folder, or unpins it if it is pinned, and saves the pins straight away.</summary>
    private void TogglePin(string path)
    {
        if (!_pins.Unpin(path)) _pins.Pin(path);
        PinsChanged();
    }

    private void PinsChanged()
    {
        RebuildPins();
        CaptureSessionState();                                   // the settings file also gets current values for the rest
        if (!SettingsStore.Save(_settings)) StatusText.Text = PinnedFolders.NotSaved;
    }

    private void OnPinContextMenuOpened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu) return;
        _contextFolder = (menu.PlacementTarget as ListBoxItem)?.Tag as string;
        foreach (var item in menu.Items.OfType<MenuItem>())
            item.IsEnabled = _contextFolder != null && item.Tag switch
            {
                "Up" => _pins.CanMove(_contextFolder, -1),
                "Down" => _pins.CanMove(_contextFolder, +1),
                _ => true,
            };
    }

    private void OnTreeContextMenuOpened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu) return;
        _contextFolder = (menu.PlacementTarget as TreeViewItem)?.Tag as string;   // null for the "…" placeholder
        foreach (var item in menu.Items.OfType<MenuItem>())
        {
            item.Header = "_" + _pins.PinCommandText(_contextFolder, "Folder");
            item.IsEnabled = _contextFolder != null;
        }
    }

    private void OnContextTogglePin(object sender, RoutedEventArgs e)
    {
        if (_contextFolder != null) TogglePin(_contextFolder);
    }

    private void OnContextRenamePin(object sender, RoutedEventArgs e)
    {
        if (_contextFolder != null) RenamePin(_contextFolder);
    }

    /// <summary>F2 in the pin list renames the highlighted pin (the folder on screen), as F2 in the grid renames a file.</summary>
    private void OnPinKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.F2 || Keyboard.Modifiers != ModifierKeys.None) return;
        if (PinList.SelectedItem is ListBoxItem { Tag: string path }) RenamePin(path);
        e.Handled = true;
    }

    /// <summary>Gives a pin a name of its own (or takes it away) through <see cref="PinNameWindow"/>.</summary>
    private void RenamePin(string path)
    {
        int index = _pins.IndexOf(path);
        if (index < 0) return;
        string shown = _pins.Labels()[index];
        var dlg = new PinNameWindow(_pins.Paths[index], shown) { Owner = this };
        if (dlg.ShowDialog() == true && _pins.Rename(path, dlg.NewName, shown)) PinsChanged();
    }

    private void OnPinMoveUp(object sender, RoutedEventArgs e) => MovePin(-1);
    private void OnPinMoveDown(object sender, RoutedEventArgs e) => MovePin(+1);

    private void MovePin(int delta)
    {
        if (_contextFolder != null && _pins.Move(_contextFolder, delta)) PinsChanged();
    }

    // ===============================================================================================
    // Menu: file operations
    // ===============================================================================================

    private async void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Choose a folder of images" };
        if (_currentFolder != null) dlg.InitialDirectory = _currentFolder;
        if (dlg.ShowDialog(this) == true) await OpenPathAsync(dlg.FolderName, allowFile: false);
    }

    /// <summary>
    /// Opens a folder (or, with <paramref name="allowFile"/>, a file's folder with the file selected) if it exists.
    /// Whether it exists is asked on a worker thread: the path may be on a share that isn't connected, which can take
    /// as long as Windows takes to give up. The status bar says what is being waited for, and says so if nothing is
    /// found. Only the latest open goes ahead.
    /// </summary>
    private async Task OpenPathAsync(string path, bool allowFile)
    {
        int version = ++_openVersion;
        bool ownsBusy = !_isLoading;            // a folder load in progress already has the busy indicator
        if (ownsBusy) BeginBusy(DisplayText.OpeningPath(path));
        int kind = await Task.Run(() => Directory.Exists(path) ? 1 : allowFile && File.Exists(path) ? 2 : 0);
        if (version != _openVersion) return;    // a newer open took over
        if (kind == 0)
        {
            if (ownsBusy && !_isLoading) EndBusy();
            StatusText.Text = DisplayText.PathNotFound(path);
            return;
        }
        string folder = kind == 1 ? path : Path.GetDirectoryName(path)!;
        if (kind == 2) _pendingSelectPath = path;
        SelectPathInTree(folder);
        NavigateTo(folder);
    }

    private void OnExit(object sender, RoutedEventArgs e) => Close();

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        var sel = SelectedItems();
        if (sel.Count == 0) return;
        if (MessageBox.Show(this, FileDeletion.ConfirmationText(sel.Select(i => i.Path).ToList()), "Emberlight",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        int index = ThumbList.SelectedIndex;
        var result = FileDeletion.Delete(sel.Select(i => i.Path), RecycleBin.Instance, RemoveItem);
        if (FileDeletion.SelectionAfter(index, _items.Count) is { } next) SelectOnly(next);
        UpdateStatus();
        if (result.FailureText is { } failure) StatusText.Text = failure;
    }

    /// <summary>
    /// Rename works on one file at a time, so the menu item is greyed out unless exactly one is selected. Pin Current
    /// Folder says Unpin when the folder on screen is pinned.
    /// </summary>
    private void OnFileMenuOpened(object sender, RoutedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, sender)) return;   // a submenu (Pinned Folders) opening
        MenuRename.IsEnabled = SelectedItems().Count == 1;
        if (_pinCurrentItem != null)
        {
            _pinCurrentItem.Header = "_" + _pins.PinCommandText(_currentFolder, "Current Folder");
            _pinCurrentItem.IsEnabled = _currentFolder != null;
        }
    }

    /// <summary>The thumbnails' right-click menu (its items are all File menu items; see ThumbContextMenu).</summary>
    private void OnThumbContextMenuOpened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu) return;
        foreach (var item in menu.Items.OfType<MenuItem>().Where(i => "Rename".Equals(i.Tag)))
            item.IsEnabled = SelectedItems().Count == 1;
    }

    /// <summary>
    /// Renames the selected file through <see cref="RenameWindow"/>, then puts it back in the grid under its new name
    /// (moving it if the sort order says so), still selected. A name the browser doesn't show takes it out of the grid.
    /// </summary>
    private void OnRename(object sender, RoutedEventArgs e)
    {
        var sel = SelectedItems();
        if (sel.Count != 1)
        {
            StatusText.Text = FileRename.SelectOneFile;
            return;
        }
        string path = sel[0].Path;
        var dlg = new RenameWindow(path, name => _types.IsSupportedFile(name)) { Owner = this };
        if (dlg.ShowDialog() != true || FileRename.IsUnchanged(path, dlg.NewName)) return;

        var result = FileRename.Rename(path, dlg.NewName);
        if (result.NewPath is not { } newPath)
        {
            StatusText.Text = result.FailureText;
            return;
        }
        RemoveItem(path);
        if (_types.IsSupportedFile(newPath))
        {
            UpsertItem(newPath);
            if (_listing.TryGet(newPath, out var item)) SelectOnly(_items.IndexOf(item));
        }
        UpdateStatus();
        UpdateVisibleThumbnails();
    }

    private void OnCopyTo(object sender, RoutedEventArgs e) => TransferSelected(move: false);
    private void OnMoveTo(object sender, RoutedEventArgs e) => TransferSelected(move: true);

    private void TransferSelected(bool move)
    {
        var sel = SelectedItems();
        if (sel.Count == 0) return;
        var dlg = new OpenFolderDialog { Title = move ? "Move selected files to..." : "Copy selected files to..." };
        if (dlg.ShowDialog(this) != true) return;

        StatusText.Text = FileTransfer.TransferAll(sel.Select(i => i.Path), dlg.FolderName, move, RemoveItem).Summary;
    }

    // ===============================================================================================
    // Menu: lossless rotation via the EXIF orientation tag
    // ===============================================================================================

    private void OnRotateLeft(object sender, RoutedEventArgs e) => RotateSelected(-90);
    private void OnRotateRight(object sender, RoutedEventArgs e) => RotateSelected(+90);
    private void OnResetOrientation(object sender, RoutedEventArgs e) => RotateSelected(null);

    /// <summary>Rotates losslessly by rewriting the EXIF orientation tag (<see cref="OrientationEditor"/>), then reloads those thumbnails.</summary>
    private void RotateSelected(int? degreesCw)
    {
        var selected = SelectedItems();
        var result = OrientationEditor.Rotate(selected.Select(i => i.Path), degreesCw, _settings.PreserveModifiedDateOnEdit);
        var updated = result.Updated.ToHashSet(PathUtil.Comparer);
        foreach (var item in selected.Where(i => updated.Contains(i.Path)))
        {
            _loader.Invalidate(item.Path);
            _listing.Invalidate(item.Path);
            item.ResetThumbnail();
        }
        UpdateVisibleThumbnails();
        StatusText.Text = result.Summary;
    }

    // ===============================================================================================
    // Menu: view
    // ===============================================================================================

    private void OnToggleFilenames(object sender, RoutedEventArgs e) => _view.ShowFilenames = MenuShowFilenames.IsChecked;
    /// <summary>Shared by the status-bar checkbox and the View menu item; keeps both in sync.</summary>
    private void OnToggleSubfolders(object sender, RoutedEventArgs e)
    {
        bool on = sender is MenuItem ? MenuSubfolders.IsChecked : SubfoldersCheck.IsChecked == true;
        _subfolders.Set(on, _currentFolder);                // the tree starts at the folder you're looking at
        MenuSubfolders.IsChecked = on;
        SubfoldersCheck.IsChecked = on;
        Rescan();
    }
    private void OnToggleInfoPanel(object sender, RoutedEventArgs e) => SetInfoPanelVisible(InfoPanel.Visibility != Visibility.Visible);
    private void OnToggleMoreExif(object sender, RoutedEventArgs e) { _settings.InfoPanelMoreExif = MenuMoreExif.IsChecked; UpdateInfoPanel(); }
    private void OnLarger(object sender, RoutedEventArgs e) => _view.CellWidth += ThumbnailGridMath.SizeStep;
    private void OnSmaller(object sender, RoutedEventArgs e) => _view.CellWidth -= ThumbnailGridMath.SizeStep;
    private void OnRefresh(object sender, RoutedEventArgs e) => Rescan(startOver: true);

    // ===============================================================================================
    // Menu: preferences / about
    // ===============================================================================================

    private async void OnPreferences(object sender, RoutedEventArgs e)
    {
        CaptureSessionState();                   // so the dialog shows the current sort order and filename setting
        var oldSort = _listing.Order;
        bool oldSubfolders = _settings.IncludeSubfolders;
        var oldMode = _settings.IncludeSubfoldersMode;

        var dlg = new PreferencesWindow(_settings, _types) { Owner = this };
        if (dlg.ShowDialog() != true) return;

        ThumbnailItem.AutoRotate = _settings.AutoRotateByOrientationTag;
        foreach (var item in _items) item.RefreshOrientation();
        _view.ShowFilenames = _settings.ShowFilenames;
        SetInfoPanelVisible(_settings.InfoPanelVisible);     // also refreshes it for the "More EXIF details" setting

        // Include subfolders (or how it behaves) changed: the tree it applies to starts at the folder on screen.
        bool subfoldersChanged = _subfolders.AfterPreferences(oldSubfolders, oldMode, _currentFolder);
        var followUp = PreferencesFollowUp.From(dlg.FileTypesChanged, subfoldersChanged, oldSort, _settings.SortOrder);
        bool rescan = followUp.Rescan;

        // A new sort order: a rescan sorts with it anyway; otherwise re-sort what is on screen.
        Task resort = Task.CompletedTask;
        if (followUp.Sort == SortFollowUp.SetOrder) _listing.SetOrder(_settings.SortOrder);
        else if (followUp.Sort == SortFollowUp.Resort) resort = ApplySortAsync(_settings.SortOrder);   // sets the order before its first await
        SyncMenuState();

        // Save now rather than waiting for exit. Capture first so the file also holds the current
        // folder, thumbnail size and sort order instead of stale values.
        CaptureSessionState();
        if (!SettingsStore.Save(_settings))
            MessageBox.Show(this, DisplayText.PreferencesNotSaved(SettingsStore.DefaultPath),
                "Emberlight", MessageBoxButton.OK, MessageBoxImage.Warning);

        if (rescan) Rescan();                    // different file types or subfolders: the file list changes
        else StatusText.Text = DisplayText.PreferencesSaved;
        try { await resort; }
        catch (Exception ex) { StatusText.Text = ex.Message; }
    }

    private void OnAbout(object sender, RoutedEventArgs e)
        => new AboutWindow { Owner = this }.ShowDialog();

    // ===============================================================================================
    // Drag and drop
    // ===============================================================================================

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Link : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] dropped || dropped.Length == 0) return;
        string first = dropped[0];
        await OpenPathAsync(first, allowFile: true);    // a folder opens as is; a file opens its folder, selected
    }

    // ===============================================================================================
    // Live updates from the file watcher
    // ===============================================================================================

    private void OnWatcherChanged(IReadOnlyList<string> changed, IReadOnlyList<string> deleted)
    {
        foreach (var p in deleted) RemoveItem(p);
        foreach (var p in changed) UpsertItem(p);
        UpdateStatus();
        UpdateVisibleThumbnails();
    }

    private async void OnWatcherRootChanged()
    {
        // The folder we were showing is gone (deleted or renamed, or its share dropped): fall back to its nearest
        // existing parent. The search asks the disk several times, so it runs on a worker thread.
        string? folder = _currentFolder;
        if (await Task.Run(() => PathUtil.NearestExistingFolder(folder)) is not { } p) return;
        SelectPathInTree(p);
        NavigateTo(p);
    }

    private void RemoveItem(string path)
    {
        _loader.Invalidate(path);
        _listing.Remove(path);
    }

    private void UpsertItem(string path)
    {
        _loader.Invalidate(path);

        // A changed file may move (its date or size may have changed); taking it out of the list drops it from the
        // selection, so note whether it was selected first.
        bool wasSelected = _listing.ChangesCanReorder && _listing.TryGet(path, out var existing)
                           && ThumbList.SelectedItems.Contains(existing);
        var result = _listing.Upsert(path, out var item);
        if (result != UpsertResult.Added) item.ResetThumbnail();
        if (result == UpsertResult.Repositioned && wasSelected) ThumbList.SelectedItems.Add(item);
    }
}
