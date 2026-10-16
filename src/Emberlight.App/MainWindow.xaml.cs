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
        _watcher.RescanNeeded += () => Dispatcher.BeginInvoke(new Action(Rescan));
        _view.PropertyChanged += OnViewSettingsChanged;

        BuildSortMenu();
        SyncMenuState();

        _followTimer.Tick += (_, _) => FlushFolderFollow();
        Loaded += OnLoaded;
        Closing += OnClosing;
        Closed += (_, _) => { _watcher.Dispose(); _loader.Dispose(); };
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
    }

    // ===============================================================================================
    // Folder tree
    // ===============================================================================================

    /// <summary>
    /// Fills the tree's top level: the \u2605 shortcuts, then the drives in letter order. All file-system checks run on
    /// worker threads. Asking whether a drive is ready can take many seconds (a mapped drive whose server is
    /// offline), so each drive is checked separately and slotted into place as soon as it answers. Returns once the
    /// shortcuts are in and the drive checks have started (see <see cref="_driveChecks"/>).
    /// </summary>
    private async Task BuildTreeRootsAsync()
    {
        FolderTree.Items.Clear();
        _clickExpanded.Clear();
        _driveChecks.Clear();

        var (shortcuts, drives) = await Task.Run(() =>
        {
            DriveInfo[] all;
            try { all = DriveInfo.GetDrives(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { all = []; }
            return (FolderTreeRoots.Shortcuts(), all);
        });

        foreach (var (path, label) in shortcuts) FolderTree.Items.Add(MakeTreeItem(path, label));

        int firstDriveIndex = FolderTree.Items.Count;
        var ready = new bool[drives.Length];
        for (int i = 0; i < drives.Length; i++)
            _driveChecks[PathUtil.TrimSeparators(drives[i].RootDirectory.FullName)] = AddDriveWhenReadyAsync(drives[i], i);

        async Task AddDriveWhenReadyAsync(DriveInfo drive, int i)
        {
            bool isReady = await Task.Run(() =>
            {
                try { return drive.IsReady; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
            });
            if (!isReady) return;
            // Continuations run on the UI thread, so the slot arithmetic can't race with another drive's.
            ready[i] = true;
            FolderTree.Items.Insert(FolderTreeRoots.DriveSlot(ready, i, firstDriveIndex), MakeTreeItem(drive.RootDirectory.FullName, drive.Name));
        }
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
        if (e.OriginalSource is TreeViewItem item && ReferenceEquals(item, sender)) _ = EnsureChildrenLoadedAsync(item);
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

    private async void NavigateTo(string path)
    {
        try { await NavigateToAsync(path); }
        catch (Exception ex) { StatusText.Text = ex.Message; }
    }

    private async Task NavigateToAsync(string path)
    {
        _scanCts?.Cancel();
        var cts = _scanCts = new CancellationTokenSource();
        var ct = cts.Token;

        if (_subfolders.BeforeNavigate(_currentFolder, path)) SyncMenuState();   // left the tree: it switched itself off
        _isLoading = true;
        _currentFolder = path;
        _watcher.Stop();
        _loader.Reset();
        _listing.ResetMetadata();
        Title = DisplayText.BrowserTitle(path);

        // Show an empty grid and a busy indicator immediately, so the user can see the click was received.
        _keepFirst = _keepEnd = 0;
        _listing.Clear();
        SlideshowButton.IsEnabled = false;
        BeginBusy(DisplayText.ScanStarted);

        var comparer = _listing.Comparer;
        bool recurse = _settings.IncludeSubfolders;
        // Progress<T> captures the UI context, so this callback runs on the UI thread.
        var scanProgress = new Progress<int>(n =>
        {
            if (!ct.IsCancellationRequested) StatusText.Text = DisplayText.Scanning(n);
        });

        List<string> files;
        try
        {
            files = await Task.Run(() => _scanner.Scan(path, recurse, ct, scanProgress), ct);
            if (ct.IsCancellationRequested) return;

            // Sorting by a file-system or EXIF date has to open every file, which can take a while.
            BeginBusy(DisplayText.Sorting(files.Count, _listing.Order));
            await Task.Run(() => files.Sort(comparer), ct);
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

        SetBusyProgress(files.Count, files.Count, DisplayText.Loading(files.Count));
        Populate(files);

        _watcher.Watch(path, recurse);
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

    private void Rescan()
    {
        if (_currentFolder != null) NavigateTo(_currentFolder);
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

    private void UpdateInfoPanel()
    {
        if (InfoPanel.Visibility != Visibility.Visible) return;
        var sel = SelectedItems();
        InfoHeader.Text = DisplayText.InfoPanelHeader(sel.Count, sel.Count == 1 ? sel[0].FileName : null);
        if (sel.Count != 1)
        {
            InfoBody.Text = "";
            return;
        }

        string path = sel[0].Path;
        (int, int)? pixelSize = ImageService.TryReadSize(path, out int w, out int h) ? (w, h) : null;
        InfoBody.Text = DisplayText.InfoPanelBody(path, pixelSize, _settings.InfoPanelMoreExif);
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
        await _listing.SortAsync();

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
        show.Closed += (_, _) =>
        {
            FlushFolderFollow();       // if the browser hasn't caught up with the last jump yet, do it now
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

    private void FlushFolderFollow()
    {
        _followTimer.Stop();
        if (_follow.TakeFolderToOpen(_currentFolder, Directory.Exists) is not { } path) return;

        // The slideshow just showed this folder (with its subfolders if that option is on), so the browser
        // should too: make it the new root of the "include subfolders" tree rather than switching the option off.
        _subfolders.FollowedSlideshowTo(path);
        OpenFolder(path);
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

    /// <summary>Keys for the thumbnail grid itself (only while it has focus): Delete and moving the selection.</summary>
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
        if (!BrowserCommands.CanRun(input, _items.Count)) return false;
        Execute(input);
        return true;
    }

    /// <summary>The grid's keys. Arrows, Page Up/Down, Home and End move by item index and row arithmetic rather than
    /// by the containers on screen: in a virtualized grid most items have no container to move focus between.</summary>
    private static BrowserInput MapGridKey(Key key, ModifierKeys mods)
    {
        if (key == Key.Delete) return new(BrowserCommand.Delete);
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
            case BrowserCommand.Rescan: Rescan(); break;
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
    // Menu: file operations
    // ===============================================================================================

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Choose a folder of images" };
        if (_currentFolder != null) dlg.InitialDirectory = _currentFolder;
        if (dlg.ShowDialog(this) == true) OpenFolder(dlg.FolderName);
    }

    private void OpenFolder(string path)
    {
        if (!Directory.Exists(path)) return;
        SelectPathInTree(path);
        NavigateTo(path);
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
    private void OnRefresh(object sender, RoutedEventArgs e) => Rescan();

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

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] dropped || dropped.Length == 0) return;
        string first = dropped[0];
        if (Directory.Exists(first)) { OpenFolder(first); return; }
        if (File.Exists(first) && Path.GetDirectoryName(first) is { } dir)
        {
            _pendingSelectPath = first;
            OpenFolder(dir);
        }
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

    private void OnWatcherRootChanged()
    {
        // The folder we were showing is gone (deleted or renamed): fall back to its nearest existing parent.
        if (PathUtil.NearestExistingFolder(_currentFolder) is { } p) OpenFolder(p);
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
