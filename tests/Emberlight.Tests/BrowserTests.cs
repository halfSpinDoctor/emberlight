using Emberlight.Core;

namespace Emberlight.Tests;

/// <summary>Browser-window logic: the sorted file list, the "Include subfolders" scope, and decode sizes.</summary>
internal static class BrowserTests
{
    /// <summary>Stands in for the WPF grid's ThumbnailItem.</summary>
    sealed class Item(string path)
    {
        public string Path { get; } = path;
        public override string ToString() => System.IO.Path.GetFileName(Path);
    }

    static FileListing<Item> Listing(List<Item> items, SortOrder? order = null)
        => new(items, p => new Item(p), i => i.Path, order ?? SortOrder.Default);

    static string[] Names(IEnumerable<Item> items) => items.Select(i => i.ToString()).ToArray();

    /// <summary>The listing's lookup and its list must always hold the same items.</summary>
    static void Consistent(FileListing<Item> listing, List<Item> items)
    {
        T.Eq(items.Count, listing.Count, "count");
        foreach (var i in items) T.True(listing.TryGet(i.Path, out var found) && ReferenceEquals(found, i), $"{i} is looked up");
    }

    public static void RunAll()
    {
        FileListingTests();
        SubfolderScopeTests();
        DecodePolicyTests();
        ImageInfoTests();
        BrowserTextTests();
        SortChoiceTests();
        StartupFolderTests();
        PathHelperTests();
        SlideshowSelectionTests();
        GridSizeTests();
    }

    static void ImageInfoTests()
    {
        T.Group("ImageInfo");

        T.Run("a cached picture matches its file until the size or modified time changes", () =>
        {
            using var tmp = new TempDir();
            string p = tmp.Write("a.jpg", new byte[100]);
            var fi = new FileInfo(p);
            var info = new ImageInfo(40, 30, ImageOrientation.Identity, true, fi.Length, fi.LastWriteTimeUtc);
            T.True(info.MatchesFile(p));
            T.True(info.MatchesFile(p.ToUpperInvariant()) || !OperatingSystem.IsWindows(), "any case on Windows");

            File.SetLastWriteTimeUtc(p, fi.LastWriteTimeUtc.AddSeconds(5));
            T.True(!info.MatchesFile(p), "touched");
            File.SetLastWriteTimeUtc(p, fi.LastWriteTimeUtc);
            T.True(info.MatchesFile(p), "time put back");

            File.WriteAllBytes(p, new byte[101]);
            File.SetLastWriteTimeUtc(p, fi.LastWriteTimeUtc);
            T.True(!info.MatchesFile(p), "different size");
        });

        T.Run("a missing file or a bad path never matches", () =>
        {
            var info = new ImageInfo(1, 1, ImageOrientation.Identity, true, 0, DateTime.MinValue);
            T.True(!info.MatchesFile(Path.Combine(Path.GetTempPath(), "phx-missing-" + Guid.NewGuid().ToString("N") + ".jpg")));
            T.True(!info.MatchesFile("bad\0path"));
            T.True(!info.MatchesFile(""));
        });

        T.Run("ForFile reads the size and modified time from the file", () =>
        {
            using var tmp = new TempDir();
            string p = tmp.Write("a.jpg", new byte[77]);
            var info = ImageInfo.ForFile(new FileInfo(p), 8, 6, ImageOrientation.FromExif(6), false);
            T.Eq(77L, info.FileSize);
            T.Eq(new FileInfo(p).LastWriteTimeUtc, info.ModifiedUtc);
            T.Eq((8, 6), (info.PixelWidth, info.PixelHeight));
            T.Eq(ImageOrientation.FromExif(6), info.ExifOrientation);
            T.True(!info.IsFullResolution && info.Animation == null);
            T.True(info.MatchesFile(p));
        });
    }

    static void BrowserTextTests()
    {
        T.Group("DisplayText (browser)");
        var info = new ImageInfo(4000, 3000, ImageOrientation.Identity, false, 2_500_000, DateTime.UtcNow);
        string size = FileSizeFormatter.Format(2_500_000);

        T.Run("status bar: image count, then the selection", () =>
        {
            T.Eq("0 images", DisplayText.BrowserStatus(0, 0, null, null));
            T.Eq("1 image", DisplayText.BrowserStatus(1, 0, null, null));
            T.Eq("1500 images", DisplayText.BrowserStatus(1500, 0, null, null), "no thousands separator, as before");
            T.Eq("12 images  \u00B7  3 selected", DisplayText.BrowserStatus(12, 3, null, null));
            T.Eq("12 images  \u00B7  a.jpg", DisplayText.BrowserStatus(12, 1, "a.jpg", null), "not loaded yet");
            T.Eq($"12 images  \u00B7  a.jpg  \u00B7  4000 \u00D7 3000  \u00B7  {size}", DisplayText.BrowserStatus(12, 1, "a.jpg", info));
        });

        T.Run("thumbnail tooltip: the name, plus size once loaded", () =>
        {
            T.Eq("a.jpg", DisplayText.ThumbnailToolTip("a.jpg", null));
            T.Eq($"a.jpg\n4000 \u00D7 3000   {size}", DisplayText.ThumbnailToolTip("a.jpg", info));
        });

        T.Run("info panel heading: no selection, several, or the file name", () =>
        {
            T.Eq("No selection", DisplayText.InfoPanelHeader(0, null));
            T.Eq("3 files selected", DisplayText.InfoPanelHeader(3, null));
            T.Eq("a.jpg", DisplayText.InfoPanelHeader(1, "a.jpg"));
        });

        T.Run("info panel: folder, size, date and dimensions; EXIF after a blank line", () =>
        {
            using var tmp = new TempDir();
            string plain = tmp.Write("plain.png", new byte[512]);
            var fi = new FileInfo(plain);
            string head = $"Folder: {fi.DirectoryName}\nSize: 512 bytes\nModified: {fi.LastWriteTime:g}\n";
            T.Eq(head.Replace("\n", Environment.NewLine), DisplayText.InfoPanelBody(plain, null, false), "no size, no EXIF");
            T.Eq((head + "Dimensions: 8 \u00D7 6\n").Replace("\n", Environment.NewLine), DisplayText.InfoPanelBody(plain, (8, 6), false));

            string jpg = tmp.Write("photo.jpg", ExifTestBuilder.WrapInJpeg(new ExifTestBuilder(false).BuildSample()));
            foreach (bool more in new[] { false, true })
            {
                string exif = ExifReader.Read(jpg)!.ToDisplayString(more);
                T.True(exif.Length > 0);
                string body = DisplayText.InfoPanelBody(jpg, null, more);
                string nl = Environment.NewLine;
                T.True(body.EndsWith($"Modified: {new FileInfo(jpg).LastWriteTime:g}{nl}{nl}{exif}{nl}"),
                    $"EXIF block after a blank line (more = {more})");
            }
        });

        T.Run("info panel: a file that has gone gets no file lines, rather than half of them", () =>
        {
            string gone = Path.Combine(Path.GetTempPath(), "phx-gone-" + Guid.NewGuid().ToString("N") + ".jpg");
            T.Eq("", DisplayText.InfoPanelBody(gone, null, false));
            T.Eq("Dimensions: 2 \u00D7 1" + Environment.NewLine, DisplayText.InfoPanelBody(gone, (2, 1), false));
        });
    }

    static void SortChoiceTests()
    {
        T.Group("Sort choices");

        T.Run("every sort field is offered once, in menu order, with its label", () =>
        {
            T.SeqEq(new[] { SortField.Name, SortField.DateModified, SortField.ExifDate, SortField.DateAdded, SortField.Type,
                            SortField.Size, SortField.FilePath }, SortOrder.Choices.Select(c => c.Field));
            T.SeqEq(new[] { "Name", "Date Modified", "EXIF Date", "Date Created", "File Type", "File Size", "Full Path" },
                SortOrder.Choices.Select(c => c.Label));
            T.Eq(Enum.GetValues<SortField>().Length, SortOrder.Choices.Count);
            T.Eq("EXIF Date", SortOrder.LabelFor(SortField.ExifDate));
        });

        T.Run("choosing a new field: dates newest first, the rest ascending", () =>
        {
            var current = new SortOrder(SortField.Name, false);
            T.Eq(new SortOrder(SortField.DateModified, true), current.Select(SortField.DateModified));
            T.Eq(new SortOrder(SortField.ExifDate, true), current.Select(SortField.ExifDate));
            T.Eq(new SortOrder(SortField.DateAdded, true), current.Select(SortField.DateAdded));
            T.Eq(new SortOrder(SortField.Size, false), current.Select(SortField.Size));
            T.Eq(new SortOrder(SortField.Type, false), current.Select(SortField.Type));
            T.Eq(new SortOrder(SortField.Name, false), new SortOrder(SortField.Size, true).Select(SortField.Name));
        });

        T.Run("choosing the current field again reverses it", () =>
        {
            T.Eq(new SortOrder(SortField.Name, true), new SortOrder(SortField.Name, false).Select(SortField.Name));
            T.Eq(new SortOrder(SortField.DateModified, false), new SortOrder(SortField.DateModified, true).Select(SortField.DateModified));
        });
    }

    static void StartupFolderTests()
    {
        T.Group("Startup folder");

        T.Run("a folder given on the command line is opened as it is", () =>
        {
            using var tmp = new TempDir();
            T.Eq(tmp.Path, StartupArgs.BrowserFolder(tmp.Path, new AppSettings(), out var select));
            T.Eq(null, select);
        });

        T.Run("a file given on the command line opens its folder and selects it", () =>
        {
            using var tmp = new TempDir();
            string f = tmp.Write("a.jpg", new byte[1]);
            T.Eq(tmp.Path, StartupArgs.BrowserFolder(f, new AppSettings(), out var select));
            T.Eq(Path.GetFullPath(f), select);
        });

        T.Run("otherwise the last folder or the chosen pictures folder, as set", () =>
        {
            using var last = new TempDir();
            using var pics = new TempDir();
            var s = new AppSettings { LastFolderPath = last.Path, PicturesFolderPath = pics.Path, StartupOption = StartupFolder.LastFolder };
            T.Eq(last.Path, StartupArgs.BrowserFolder(null, s, out var select));
            T.Eq(null, select);
            s.StartupOption = StartupFolder.PicturesFolder;
            T.Eq(pics.Path, StartupArgs.BrowserFolder(null, s, out _));
            T.Eq(pics.Path, StartupArgs.BrowserFolder(Path.Combine(last.Path, "missing"), s, out select), "a missing path is ignored");
            T.Eq(null, select);
        });

        T.Run("a remembered folder that has gone falls back to an existing folder", () =>
        {
            var s = new AppSettings { LastFolderPath = Path.Combine(Path.GetTempPath(), "phx-gone-" + Guid.NewGuid().ToString("N")) };
            string folder = StartupArgs.BrowserFolder(null, s, out _);
            T.True(Directory.Exists(folder), folder);
            string pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            if (Directory.Exists(pictures)) T.Eq(pictures, folder, "the user's Pictures folder");
            T.True(Directory.Exists(StartupArgs.BrowserFolder("", new AppSettings { LastFolderPath = "" }, out _)));
        });
    }

    static void PathHelperTests()
    {
        T.Group("PathUtil helpers");
        string root = Path.GetPathRoot(Path.GetTempPath())!;

        T.Run("ancestor chain: the root, then each folder down to the path", () =>
        {
            string a = Path.Combine(root, "phx-a"), b = Path.Combine(a, "b"), c = Path.Combine(b, "c");
            T.SeqEq(new[] { root, a, b, c }, PathUtil.AncestorChain(c));
            T.SeqEq(new[] { root, a, b, c }, PathUtil.AncestorChain(c + Path.DirectorySeparatorChar), "trailing separator");
            T.SeqEq(new[] { root }, PathUtil.AncestorChain(root));
        });

        T.Run("ancestor chain of a network share starts at the share", () =>
        {
            if (!OperatingSystem.IsWindows()) return;           // UNC paths are a Windows thing
            T.SeqEq(new[] { @"\\nas\photos", @"\\nas\photos\2020", @"\\nas\photos\2020\June" },
                PathUtil.AncestorChain(@"\\nas\photos\2020\June"));
        });

        T.Run("ancestor chain of bad input is empty", () =>
        {
            T.Eq(0, PathUtil.AncestorChain("").Count);
            T.Eq(0, PathUtil.AncestorChain("bad\0path").Count);
        });

        T.Run("same path ignores trailing separators and case, but not real differences", () =>
        {
            string a = Path.Combine(root, "phx-x", "Pics");
            T.True(PathUtil.SamePath(a, a + Path.DirectorySeparatorChar));
            T.True(PathUtil.SamePath(a, a.ToUpperInvariant()));
            T.True(PathUtil.SamePath(root, root));
            T.True(!PathUtil.SamePath(a, a + "2"));
            T.True(!PathUtil.SamePath(a, null));
            T.True(PathUtil.SamePath(null, null));
            T.True(PathUtil.Comparer.Equals(a, a.ToLowerInvariant()));
            T.True(string.Equals(a, a.ToLowerInvariant(), PathUtil.Comparison));
        });

        T.Run("nearest existing folder walks up past folders that have gone", () =>
        {
            using var tmp = new TempDir();
            string gone = Path.Combine(tmp.Path, "x", "y", "z");
            T.Eq(tmp.Path, PathUtil.NearestExistingFolder(gone));
            T.Eq(tmp.Path, PathUtil.NearestExistingFolder(tmp.Path), "an existing folder is its own answer");
            T.Eq(null, PathUtil.NearestExistingFolder(""));
            T.Eq(null, PathUtil.NearestExistingFolder(null));
        });
    }

    static void SlideshowSelectionTests()
    {
        T.Group("Slideshow from the browser");
        string[] grid = ["a.jpg", "b.jpg", "c.jpg", "d.jpg"];

        T.Run("nothing selected: the whole folder from the start", () =>
        {
            var t = StartupTarget.FromSelection(grid, [], "dir");
            T.SeqEq(grid, t.Files); T.Eq(0, t.StartIndex); T.Eq("dir", t.Folder);
        });

        T.Run("one file selected: the whole folder, starting there", () =>
        {
            var t = StartupTarget.FromSelection(grid, ["C.JPG"], null);
            T.SeqEq(grid, t.Files); T.Eq(2, t.StartIndex);
        });

        T.Run("several selected: just those, in grid order, from the first", () =>
        {
            var t = StartupTarget.FromSelection(grid, ["d.jpg", "b.jpg"], "dir");
            T.SeqEq(new[] { "b.jpg", "d.jpg" }, t.Files); T.Eq(0, t.StartIndex);
        });

        T.Run("a selected file that is no longer in the grid starts at the beginning", () =>
        {
            var t = StartupTarget.FromSelection(grid, ["zzz.jpg"], null);
            T.SeqEq(grid, t.Files); T.Eq(0, t.StartIndex);
        });
    }

    static void GridSizeTests()
    {
        T.Group("Thumbnail sizes");

        T.Run("cell widths are whole numbers within the allowed range", () =>
        {
            T.Eq(120.0, ThumbnailGridMath.ClampCellWidth(120));
            T.Eq(121.0, ThumbnailGridMath.ClampCellWidth(120.6));
            T.Eq(AppSettings.MinThumbCellWidth, ThumbnailGridMath.ClampCellWidth(10));
            T.Eq(AppSettings.MaxThumbCellWidth, ThumbnailGridMath.ClampCellWidth(5000));
        });

        T.Run("cells have a 4 px margin and a 20 px caption; size steps are 20", () =>
        {
            T.Eq(4.0, ThumbnailGridMath.CellMargin);
            T.Eq(20.0, ThumbnailGridMath.CaptionHeight);
            T.Eq(20.0, ThumbnailGridMath.SizeStep);
            T.Eq(128.0, ThumbnailGridMath.OuterSize(120));
            T.Eq(110.0, ThumbnailGridMath.CellHeight(120, true), "90 for the picture, 20 for the caption");
            T.Eq(90.0, ThumbnailGridMath.CellHeight(120, false));
        });
    }

    static void FileListingTests()
    {
        T.Group("FileListing");
        string dir = Path.Combine(Path.GetTempPath(), "phx-listing");
        string P(string name) => Path.Combine(dir, name);

        T.Run("populate keeps the given order and looks items up by path, ignoring case", () =>
        {
            var items = new List<Item>();
            var l = Listing(items);
            l.Populate([P("a.jpg"), P("b.jpg"), P("c.jpg")]);
            T.SeqEq(["a.jpg", "b.jpg", "c.jpg"], Names(items));
            T.True(l.TryGet(P("B.JPG"), out var b) && b == items[1]);
            T.True(!l.TryGet(P("d.jpg"), out _));
            Consistent(l, items);
            l.Populate([P("x.jpg")]);
            T.SeqEq(["x.jpg"], Names(items), "a second populate replaces everything");
            T.True(!l.TryGet(P("a.jpg"), out _));
            l.Clear();
            T.Eq(0, items.Count); T.Eq(0, l.Count);
        });

        T.Run("populate goes through the bulk replace when one is given", () =>
        {
            var items = new List<Item>();
            int bulk = 0;
            var l = new FileListing<Item>(items, p => new Item(p), i => i.Path, SortOrder.Default,
                all => { bulk++; items.Clear(); items.AddRange(all); });
            l.Populate([P("a.jpg"), P("b.jpg")]);
            l.Clear();
            T.Eq(2, bulk);
        });

        T.Run("new files are inserted at their sorted place (natural name order)", () =>
        {
            var items = new List<Item>();
            var l = Listing(items);
            l.Populate([P("img1.jpg"), P("img5.jpg"), P("img10.jpg")]);
            T.Eq(UpsertResult.Added, l.Upsert(P("img7.jpg"), out var added));
            T.Eq("img7.jpg", added.ToString());
            T.Eq(UpsertResult.Added, l.Upsert(P("img0.jpg"), out _));
            T.Eq(UpsertResult.Added, l.Upsert(P("img20.jpg"), out _));
            T.SeqEq(["img0.jpg", "img1.jpg", "img5.jpg", "img7.jpg", "img10.jpg", "img20.jpg"], Names(items));
            Consistent(l, items);
        });

        T.Run("descending order inserts in descending place", () =>
        {
            var items = new List<Item>();
            var l = Listing(items, new SortOrder(SortField.Name, true));
            l.Populate([P("c.jpg"), P("a.jpg")]);
            l.Upsert(P("b.jpg"), out _);
            l.Upsert(P("d.jpg"), out _);
            T.SeqEq(["d.jpg", "c.jpg", "b.jpg", "a.jpg"], Names(items));
        });

        T.Run("a changed file keeps its item; by name or path it stays put", () =>
        {
            foreach (var field in new[] { SortField.Name, SortField.FilePath })
            {
                var items = new List<Item>();
                var l = Listing(items, new SortOrder(field, false));
                l.Populate([P("a.jpg"), P("b.jpg")]);
                var before = items[1];
                T.Eq(UpsertResult.Updated, l.Upsert(P("b.jpg"), out var same), field.ToString());
                T.True(ReferenceEquals(before, same) && ReferenceEquals(before, items[1]));
                T.Eq(2, items.Count);
            }
        });

        T.Run("a changed file moves when the sort key can change (size)", () =>
        {
            using var tmp = new TempDir();
            string small = tmp.Write("small.jpg", new byte[10]), mid = tmp.Write("mid.jpg", new byte[20]), big = tmp.Write("big.jpg", new byte[30]);
            var items = new List<Item>();
            var l = Listing(items, new SortOrder(SortField.Size, false));
            l.Populate([small, mid, big]);
            var smallItem = items[0];
            File.WriteAllBytes(small, new byte[40]);                 // now the biggest
            T.Eq(UpsertResult.Repositioned, l.Upsert(small, out var moved));
            T.True(ReferenceEquals(smallItem, moved), "same item object");
            T.SeqEq(["mid.jpg", "big.jpg", "small.jpg"], Names(items));
            Consistent(l, items);
        });

        T.Run("remove takes the item out of the list and the lookup; unknown paths are ignored", () =>
        {
            var items = new List<Item>();
            var l = Listing(items);
            l.Populate([P("a.jpg"), P("b.jpg"), P("c.jpg")]);
            T.True(l.Remove(P("B.jpg")));
            T.SeqEq(["a.jpg", "c.jpg"], Names(items));
            T.True(!l.Remove(P("b.jpg")), "already gone");
            T.True(!l.Remove(P("zzz.jpg")));
            Consistent(l, items);
        });

        T.Run("set order swaps the comparer; sort re-orders the same item objects", () =>
        {
            var items = new List<Item>();
            var l = Listing(items);
            l.Populate([P("a.jpg"), P("b.jpg"), P("c.jpg")]);
            var a = items[0];
            var oldComparer = l.Comparer;
            l.SetOrder(new SortOrder(SortField.Name, true));
            T.Eq(new SortOrder(SortField.Name, true), l.Order);
            T.True(!ReferenceEquals(oldComparer, l.Comparer), "new comparer");
            T.SeqEq(["a.jpg", "b.jpg", "c.jpg"], Names(items), "setting the order alone doesn't move anything");
            l.SortAsync().GetAwaiter().GetResult();
            T.SeqEq(["c.jpg", "b.jpg", "a.jpg"], Names(items));
            T.True(ReferenceEquals(a, items[2]));
            Consistent(l, items);
        });

        T.Run("a file that arrives while sorting is kept, at its sorted place", () =>
        {
            var items = new List<Item>();
            var l = Listing(items);
            l.Populate([P("a.jpg"), P("c.jpg")]);
            l.SetOrder(new SortOrder(SortField.Name, true));
            var paths = l.SnapshotPaths();                 // what SortAsync sorts in the background
            paths.Sort(l.Comparer);
            l.Upsert(P("b.jpg"), out _);                   // the watcher reports a new file meanwhile
            l.ApplyOrder(paths);
            T.SeqEq(["c.jpg", "b.jpg", "a.jpg"], Names(items));
            Consistent(l, items);
        });

        T.Run("a file removed while sorting stays removed", () =>
        {
            var items = new List<Item>();
            var l = Listing(items);
            l.Populate([P("a.jpg"), P("b.jpg"), P("c.jpg")]);
            l.SetOrder(new SortOrder(SortField.Name, true));
            var paths = l.SnapshotPaths();
            paths.Sort(l.Comparer);
            l.Remove(P("b.jpg"));
            l.ApplyOrder(paths);
            T.SeqEq(["c.jpg", "a.jpg"], Names(items));
            Consistent(l, items);
        });

        T.Run("sorting an empty list does nothing", () =>
        {
            var items = new List<Item>();
            var l = Listing(items);
            l.SortAsync().GetAwaiter().GetResult();
            T.Eq(0, items.Count);
        });

        T.Run("only name, path and type sorts can skip opening the files", () =>
        {
            T.True(!new SortOrder(SortField.Name, false).NeedsFileInspection);
            T.True(!new SortOrder(SortField.FilePath, true).NeedsFileInspection);
            T.True(!new SortOrder(SortField.Type, false).NeedsFileInspection);
            T.True(new SortOrder(SortField.DateModified, false).NeedsFileInspection);
            T.True(new SortOrder(SortField.ExifDate, false).NeedsFileInspection);
            T.True(new SortOrder(SortField.DateAdded, false).NeedsFileInspection);
            T.True(new SortOrder(SortField.Size, false).NeedsFileInspection);
        });

        T.Run("fuzz: random adds, removes, changes and re-sorts keep list and lookup in sorted step", () =>
        {
            for (int seed = 0; seed < 30; seed++)
            {
                var rnd = new Random(seed);
                var items = new List<Item>();
                var l = Listing(items, new SortOrder(SortField.Name, rnd.Next(2) == 0));
                l.Populate([]);
                for (int step = 0; step < 150; step++)
                {
                    string p = P($"img{rnd.Next(40)}.jpg");
                    switch (rnd.Next(5))
                    {
                        case 0: case 1: case 2: l.Upsert(p, out _); break;
                        case 3: l.Remove(p); break;
                        default:
                            l.SetOrder(new SortOrder(SortField.Name, rnd.Next(2) == 0));
                            l.SortAsync().GetAwaiter().GetResult();
                            break;
                    }
                    var paths = items.Select(i => i.Path).ToList();
                    var sorted = paths.ToList(); sorted.Sort(l.Comparer);
                    T.SeqEq(sorted, paths, $"seed {seed} step {step}: sorted");
                    T.Eq(paths.Count, paths.Distinct(StringComparer.OrdinalIgnoreCase).Count(), "no duplicates");
                    Consistent(l, items);
                }
            }
        });
    }

    static void SubfolderScopeTests()
    {
        T.Group("SubfolderScope");
        string root = Path.Combine(Path.GetTempPath(), "phx-scope", "Pics");
        string inside = Path.Combine(root, "2020"), deeper = Path.Combine(inside, "June");
        string outside = Path.Combine(Path.GetDirectoryName(root)!, "Other");

        T.Run("off: navigation never switches it on and keeps no root", () =>
        {
            var s = new AppSettings { IncludeSubfolders = false };
            var scope = new SubfolderScope(s);
            T.True(!scope.BeforeNavigate(null, root));
            T.True(!s.IncludeSubfolders); T.Eq(null, scope.Root);
        });

        T.Run("switched on: stays on inside the tree, switches off outside it", () =>
        {
            var s = new AppSettings();
            var scope = new SubfolderScope(s);
            scope.Set(true, root);
            T.True(s.IncludeSubfolders); T.Eq(root, scope.Root);
            T.True(!scope.BeforeNavigate(root, inside), "a subfolder is inside");
            T.True(!scope.BeforeNavigate(inside, deeper));
            T.True(!scope.BeforeNavigate(deeper, root), "back up to the root");
            T.Eq(root, scope.Root, "the root doesn't follow you down");
            T.True(scope.BeforeNavigate(root, outside), "leaving the tree switches it off");
            T.True(!s.IncludeSubfolders); T.Eq(null, scope.Root);
            T.True(!scope.BeforeNavigate(outside, root), "and it stays off");
        });

        T.Run("Always mode keeps it on anywhere", () =>
        {
            var s = new AppSettings { IncludeSubfoldersMode = SubfolderMode.Always };
            var scope = new SubfolderScope(s);
            scope.Set(true, inside);
            T.True(!scope.BeforeNavigate(inside, outside));
            T.True(!scope.BeforeNavigate(outside, Path.GetDirectoryName(root)!));
            T.True(s.IncludeSubfolders);
        });

        T.Run("on from the last session: the first folder shown becomes the root", () =>
        {
            var s = new AppSettings { IncludeSubfolders = true };
            var scope = new SubfolderScope(s);
            T.True(!scope.BeforeNavigate(null, inside));
            T.Eq(inside, scope.Root);
            T.True(scope.BeforeNavigate(inside, root), "the parent is outside that tree");
        });

        T.Run("on while a folder is already shown: that folder is the root", () =>
        {
            var s = new AppSettings { IncludeSubfolders = true };
            var scope = new SubfolderScope(s);
            T.True(!scope.BeforeNavigate(root, root));
            T.Eq(root, scope.Root);
        });

        T.Run("switching it off forgets the root", () =>
        {
            var s = new AppSettings();
            var scope = new SubfolderScope(s);
            scope.Set(true, root);
            scope.Set(false, root);
            T.True(!s.IncludeSubfolders); T.Eq(null, scope.Root);
        });

        T.Run("following a slideshow folder jump moves the root there instead of switching off", () =>
        {
            var s = new AppSettings();
            var scope = new SubfolderScope(s);
            scope.Set(true, root);
            scope.FollowedSlideshowTo(outside);
            T.Eq(outside, scope.Root);
            T.True(!scope.BeforeNavigate(root, outside));
            T.True(s.IncludeSubfolders);

            scope.Set(false, outside);
            scope.FollowedSlideshowTo(root);
            T.Eq(null, scope.Root, "nothing to move while it's off");
        });

        T.Run("preferences: a change of on/off or mode re-roots at the folder on screen", () =>
        {
            var s = new AppSettings();
            var scope = new SubfolderScope(s);

            s.IncludeSubfolders = true;                                  // switched on in Preferences
            T.True(scope.AfterPreferences(false, SubfolderMode.ResetOnNewTree, inside), "on/off changed: rescan");
            T.Eq(inside, scope.Root);

            s.IncludeSubfoldersMode = SubfolderMode.Always;              // only the mode changed
            T.True(!scope.AfterPreferences(true, SubfolderMode.ResetOnNewTree, deeper));
            T.Eq(deeper, scope.Root);

            T.True(!scope.AfterPreferences(true, SubfolderMode.Always, root), "nothing changed");
            T.Eq(deeper, scope.Root, "root kept");

            s.IncludeSubfolders = false;
            T.True(scope.AfterPreferences(true, SubfolderMode.Always, root));
            T.Eq(null, scope.Root);
        });
    }

    static void DecodePolicyTests()
    {
        T.Group("DecodePolicy");

        T.Run("thumbnails decode at one of four sizes, by cell width in device pixels", () =>
        {
            T.Eq(160, DecodePolicy.ThumbnailDecodeSize(40, 1));
            T.Eq(160, DecodePolicy.ThumbnailDecodeSize(150, 1));
            T.Eq(256, DecodePolicy.ThumbnailDecodeSize(151, 1));
            T.Eq(256, DecodePolicy.ThumbnailDecodeSize(230, 1));
            T.Eq(360, DecodePolicy.ThumbnailDecodeSize(231, 1));
            T.Eq(360, DecodePolicy.ThumbnailDecodeSize(330, 1));
            T.Eq(512, DecodePolicy.ThumbnailDecodeSize(331, 1));
            T.Eq(512, DecodePolicy.ThumbnailDecodeSize(400, 1));
            T.Eq(256, DecodePolicy.ThumbnailDecodeSize(120, 1.5), "120 DIPs at 150% = 180 px");
            T.Eq(512, DecodePolicy.ThumbnailDecodeSize(200, 2));
        });

        T.Run("thumbnail cache holds about 160 MB, between 100 and 4000 entries", () =>
        {
            T.Eq(2083, DecodePolicy.ThumbnailCacheCapacity(160));
            T.Eq(813, DecodePolicy.ThumbnailCacheCapacity(256));
            T.Eq(203, DecodePolicy.ThumbnailCacheCapacity(512));
            T.Eq(4000, DecodePolicy.ThumbnailCacheCapacity(50), "upper limit");
            T.Eq(100, DecodePolicy.ThumbnailCacheCapacity(2000), "lower limit");
        });

        T.Run("slides decode to the view's longer side, full size for 100% view, 3840 before layout", () =>
        {
            T.Eq(1920, DecodePolicy.SlideDecodeTarget(false, 1920, 1080));
            T.Eq(1081, DecodePolicy.SlideDecodeTarget(false, 800, 1080.2), "rounded up");
            T.Eq(0, DecodePolicy.SlideDecodeTarget(true, 1920, 1080), "actual size needs every pixel");
            T.Eq(3840, DecodePolicy.SlideDecodeTarget(false, 0, 0), "view not laid out yet");
            T.Eq(3840, DecodePolicy.SlideDecodeTarget(false, 64, 10));
            T.Eq(65, DecodePolicy.SlideDecodeTarget(false, 65, 10));
        });

        T.Run("scale only when the picture is bigger than the limit; 0 means no limit", () =>
        {
            T.True(DecodePolicy.ShouldScale(4000, 3000, 1920));
            T.True(DecodePolicy.ShouldScale(3000, 4000, 1920), "portrait");
            T.True(!DecodePolicy.ShouldScale(1920, 1080, 1920), "exactly the limit");
            T.True(!DecodePolicy.ShouldScale(800, 600, 1920));
            T.True(!DecodePolicy.ShouldScale(8000, 6000, 0));
        });

        T.Run("scaled size keeps the aspect ratio with the longer side at the limit, never 0", () =>
        {
            T.Eq((1920, 1440), DecodePolicy.ScaledSize(4000, 3000, 1920));
            T.Eq((1440, 1920), DecodePolicy.ScaledSize(3000, 4000, 1920));
            T.Eq((160, 1), DecodePolicy.ScaledSize(10000, 10, 160), "a sliver keeps 1 px");
            T.Eq((160, 107), DecodePolicy.ScaledSize(6000, 4000, 160), "rounded");
        });

        T.Run("embedded EXIF previews are used when at least 3/4 of the wanted size", () =>
        {
            T.True(DecodePolicy.EmbeddedPreviewIsBigEnough(160, 120, 160, 6000, 4000));
            T.True(DecodePolicy.EmbeddedPreviewIsBigEnough(120, 90, 160, 6000, 4000), "exactly 3/4");
            T.True(!DecodePolicy.EmbeddedPreviewIsBigEnough(119, 89, 160, 6000, 4000));
            T.True(!DecodePolicy.EmbeddedPreviewIsBigEnough(160, 120, 512, 6000, 4000), "too small for big thumbnails");
            T.True(DecodePolicy.EmbeddedPreviewIsBigEnough(100, 75, 512, 120, 90), "the photo itself is small");
            T.True(DecodePolicy.EmbeddedPreviewIsBigEnough(90, 120, 160, 4000, 6000), "portrait preview");
        });

        T.Run("full resolution is needed once zoom passes what the decoded bitmap holds", () =>
        {
            // A 6000 px photo decoded at 1920 px holds detail up to a scale of 0.32.
            T.True(!DecodePolicy.NeedsFullResolution(1920, 1280, 6000, 4000, 0.32));
            T.True(!DecodePolicy.NeedsFullResolution(1920, 1280, 6000, 4000, 0.3203), "within the 0.1% tolerance");
            T.True(DecodePolicy.NeedsFullResolution(1920, 1280, 6000, 4000, 0.33));
            T.True(DecodePolicy.NeedsFullResolution(1920, 1280, 6000, 4000, 1));
            T.True(!DecodePolicy.NeedsFullResolution(6000, 4000, 6000, 4000, 8), "already full size");
            T.True(DecodePolicy.NeedsFullResolution(1280, 1920, 4000, 6000, 0.5), "portrait");
        });
    }
}
