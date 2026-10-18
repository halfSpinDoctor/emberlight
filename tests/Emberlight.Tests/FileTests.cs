using Emberlight.Core;

namespace Emberlight.Tests;

internal static class FileTests
{
    public static void RunAll()
    {
        T.Group("Sorting");

        T.Run("name sort is natural and ignores directory for the primary key", () =>
        {
            using var d = new TempDir();
            var files = new[] { "img10.jpg", "img2.jpg", "Img1.jpg", "zeta/aaa.jpg" }
                .Select(n => d.Write(n, new byte[] { 1 })).ToList();
            files.Sort(FileComparers.For(new SortOrder(SortField.Name, false)));
            T.SeqEq(new[] { "aaa.jpg", "Img1.jpg", "img2.jpg", "img10.jpg" }, files.Select(Path.GetFileName));
            files.Sort(FileComparers.For(new SortOrder(SortField.Name, true)));
            T.SeqEq(new[] { "img10.jpg", "img2.jpg", "Img1.jpg", "aaa.jpg" }, files.Select(Path.GetFileName));
        });

        T.Run("size sort, ascending/descending, ties fall back to name", () =>
        {
            using var d = new TempDir();
            var files = new List<string>
            {
                d.Write("c.jpg", new byte[30]), d.Write("a.jpg", new byte[10]),
                d.Write("b.jpg", new byte[10]), d.Write("d.jpg", new byte[5]),
            };
            files.Sort(FileComparers.For(new SortOrder(SortField.Size, false)));
            T.SeqEq(new[] { "d.jpg", "a.jpg", "b.jpg", "c.jpg" }, files.Select(Path.GetFileName));
            files.Sort(FileComparers.For(new SortOrder(SortField.Size, true)));
            T.SeqEq(new[] { "c.jpg", "b.jpg", "a.jpg", "d.jpg" }, files.Select(Path.GetFileName));
        });

        T.Run("date modified sort", () =>
        {
            using var d = new TempDir();
            var a = d.Write("a.jpg", new byte[1]); var b = d.Write("b.jpg", new byte[1]); var c = d.Write("c.jpg", new byte[1]);
            File.SetLastWriteTimeUtc(a, new DateTime(2020, 1, 3, 0, 0, 0, DateTimeKind.Utc));
            File.SetLastWriteTimeUtc(b, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            File.SetLastWriteTimeUtc(c, new DateTime(2020, 1, 2, 0, 0, 0, DateTimeKind.Utc));
            var l = new List<string> { a, b, c };
            l.Sort(FileComparers.For(new SortOrder(SortField.DateModified, false)));
            T.SeqEq(new[] { "b.jpg", "c.jpg", "a.jpg" }, l.Select(Path.GetFileName));
            l.Sort(FileComparers.For(new SortOrder(SortField.DateModified, true)));
            T.SeqEq(new[] { "a.jpg", "c.jpg", "b.jpg" }, l.Select(Path.GetFileName));
        });

        T.Run("type sort groups by extension", () =>
        {
            using var d = new TempDir();
            var l = new[] { "b.png", "a.tif", "c.jpg", "a.jpg", "z.png" }.Select(n => d.Write(n, new byte[1])).ToList();
            l.Sort(FileComparers.For(new SortOrder(SortField.Type, false)));
            T.SeqEq(new[] { "a.jpg", "c.jpg", "b.png", "z.png", "a.tif" }, l.Select(Path.GetFileName));
        });

        T.Run("EXIF date sort uses the capture date, not the file date", () =>
        {
            using var d = new TempDir();
            byte[] Photo(string date)
            {
                var b = new ExifTestBuilder(false);
                var ifd0 = new ExifTestBuilder.Ifd();
                var exif = new ExifTestBuilder.Ifd();
                exif.Entries.Add(b.Ascii(0x9003, date));
                return ExifTestBuilder.WrapInJpeg(b.BuildTiff(ifd0, exif));
            }
            // alphabetical order is the reverse of capture order; mtimes are deliberately misleading
            var a = d.Write("a.jpg", Photo("2022:03:01 10:00:00"));
            var b2 = d.Write("b.jpg", Photo("2019:12:31 23:59:59"));
            var c = d.Write("c.jpg", Photo("2021:07:04 12:00:00"));
            File.SetLastWriteTimeUtc(a, new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            File.SetLastWriteTimeUtc(c, new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            var l = new List<string> { a, b2, c };
            l.Sort(FileComparers.For(new SortOrder(SortField.ExifDate, false)));
            T.SeqEq(new[] { "b.jpg", "c.jpg", "a.jpg" }, l.Select(Path.GetFileName));
            l.Sort(FileComparers.For(new SortOrder(SortField.ExifDate, true)));
            T.SeqEq(new[] { "a.jpg", "c.jpg", "b.jpg" }, l.Select(Path.GetFileName));
        });

        T.Run("metadata cache: sorting uses cached values until invalidated", () =>
        {
            using var d = new TempDir();
            var a = d.Write("a.jpg", new byte[100]); var b = d.Write("b.jpg", new byte[50]);
            var cache = new FileMetadataCache();
            var cmp = FileComparers.For(new SortOrder(SortField.Size, false), cache);
            T.True(cmp.Compare(a, b) > 0);
            File.WriteAllBytes(a, new byte[1]);                // a is now smaller...
            T.True(cmp.Compare(a, b) > 0, "...but the cache still has the old size");
            cache.Invalidate(a);
            T.True(cmp.Compare(a, b) < 0, "after Invalidate the new size is seen");
        });

        T.Run("metadata cache: Prefetch reads every file up front, in parallel, for the sorts that need it", () =>
        {
            using var d = new TempDir();
            var paths = new List<string>();
            for (int i = 0; i < 60; i++) paths.Add(d.Write($"f{i:D2}.jpg", new byte[(i * 37) % 61 + 1]));
            foreach (var field in new[] { SortField.Size, SortField.DateModified, SortField.DateAdded, SortField.ExifDate })
            {
                var cache = new FileMetadataCache();
                cache.Prefetch(paths, field);
                var expected = paths.ToList(); expected.Sort(FileComparers.For(new SortOrder(field, false), new FileMetadataCache()));
                foreach (var p in paths) File.Delete(p);              // sorting now has to come from the cache alone
                var got = paths.ToList(); got.Sort(FileComparers.For(new SortOrder(field, false), cache));
                T.SeqEq(expected, got);
                foreach (var p in paths) d.Write(Path.GetFileName(p), new byte[(Path.GetFileName(p).GetHashCode() & 31) + 1]);
            }
        });

        T.Run("FileListing.CaptureSorter sorts paths in the order it was captured with", () =>
        {
            using var d = new TempDir();
            var a = d.Write("a.jpg", new byte[100]); var b = d.Write("b.jpg", new byte[50]); var c = d.Write("c.jpg", new byte[70]);
            var listing = new FileListing<string>(new List<string>(), p => p, p => p, new SortOrder(SortField.Size, false));
            var sorter = listing.CaptureSorter();
            listing.SetOrder(new SortOrder(SortField.Name, false));   // changing the order later must not affect it
            var list = new List<string> { a, b, c };
            sorter(list, default);
            T.SeqEq(new[] { b, c, a }, list);
        });

        T.Run("missing files never throw; they fall back to name order", () =>
        {
            using var d = new TempDir();
            foreach (SortField k in Enum.GetValues<SortField>())
            {
                var cmp = FileComparers.For(new SortOrder(k, false));
                T.True(cmp.Compare(d.File("a.jpg"), d.File("b.jpg")) < 0, k.ToString());
                T.Eq(0, cmp.Compare(d.File("a.jpg"), d.File("a.jpg")), k.ToString());
            }
        });

        T.Run("comparers are consistent (antisymmetric) for a mixed folder", () =>
        {
            using var d = new TempDir();
            var rng = new Random(9);
            var files = Enumerable.Range(0, 30).Select(i => d.Write($"f{rng.Next(100)}_{i}.jpg", new byte[rng.Next(5)])).ToList();
            foreach (SortField k in Enum.GetValues<SortField>())
            {
                var cmp = FileComparers.For(new SortOrder(k, false));
                foreach (var x in files) foreach (var y in files)
                    T.Eq(-Math.Sign(cmp.Compare(x, y)), Math.Sign(cmp.Compare(y, x)), k.ToString());
            }
        });

        // -----------------------------------------------------------------------------------
        T.Group("DirectoryScanner");

        T.Run("finds supported files, skips others and macOS companion files", () =>
        {
            using var d = new TempDir();
            d.Write("a.jpg", new byte[1]); d.Write("b.PNG", new byte[1]); d.Write("c.txt", new byte[1]);
            d.Write("._a.jpg", new byte[1]); d.Write("d.heic", new byte[1]); d.Write("noext", new byte[1]);
            d.Write("sub/e.tif", new byte[1]);
            var s = new DirectoryScanner(new ImageFileTypes());
            T.SeqEq(new[] { "a.jpg", "b.PNG" }, s.Scan(d.Path, false).Select(Path.GetFileName).OrderBy(x => x));
            T.SeqEq(new[] { "a.jpg", "b.PNG", "e.tif" }, s.Scan(d.Path, true).Select(Path.GetFileName).OrderBy(x => x));
        });

        T.Run("skips dot files and dot folders on macOS/Linux", () =>
        {
            if (OperatingSystem.IsWindows()) return;   // Windows uses the Hidden attribute instead
            using var d = new TempDir();
            d.Write("a.jpg", new byte[1]); d.Write(".hidden.jpg", new byte[1]);
            d.Write(".cache/b.jpg", new byte[1]); d.Write("sub/c.jpg", new byte[1]);
            var s = new DirectoryScanner(new ImageFileTypes());
            T.SeqEq(new[] { "a.jpg", "c.jpg" }, s.Scan(d.Path, true).Select(Path.GetFileName).OrderBy(x => x));
            T.True(s.HasAnyImage(d.Path, false), "HasAnyImage sees a.jpg");
        });

        T.Run("honours disabled types and returns sorted results", () =>
        {
            using var d = new TempDir();
            d.Write("img10.jpg", new byte[1]); d.Write("img2.jpg", new byte[1]); d.Write("x.png", new byte[1]);
            var types = new ImageFileTypes();
            types.SetEnabled(".png", false);
            var s = new DirectoryScanner(types);
            T.SeqEq(new[] { "img2.jpg", "img10.jpg" }, s.ScanSorted(d.Path, false, SortOrder.Default).Select(Path.GetFileName));
        });

        T.Run("missing directory yields an empty list; cancellation is honoured", () =>
        {
            using var d = new TempDir();
            var s = new DirectoryScanner(new ImageFileTypes());
            T.Eq(0, s.Scan(d.File("nope"), true).Count);
            d.Write("a.jpg", new byte[1]);
            using var cts = new CancellationTokenSource(); cts.Cancel();
            T.Throws<OperationCanceledException>(() => s.Scan(d.Path, false, cts.Token));
        });

        T.Run("ShouldShowFile mirrors scan rules (used for watcher events)", () =>
        {
            using var d = new TempDir();
            var s = new DirectoryScanner(new ImageFileTypes());
            T.True(s.ShouldShowFile(d.Write("a.jpg", new byte[1])));
            T.True(!s.ShouldShowFile(d.Write("b.txt", new byte[1])));
            T.True(!s.ShouldShowFile(d.Write("._c.jpg", new byte[1])));
            T.True(!s.ShouldShowFile(d.File("deleted.jpg")));
        });
    }
}

internal static class StartupArgsTests
{
    public static void RunAll()
    {
        T.Group("StartupArgs");
        var scanner = new DirectoryScanner(new ImageFileTypes());
        var cmp = FileComparers.For(SortOrder.Default);

        T.Run("one file: shows its folder, starting on that file", () =>
        {
            using var d = new TempDir();
            foreach (var n in new[] { "a.jpg", "b.jpg", "c.jpg", "note.txt" }) d.Write(n, new byte[1]);
            var t = StartupArgs.Resolve(new[] { d.File("b.jpg") }, scanner, cmp)!;
            T.SeqEq(new[] { "a.jpg", "b.jpg", "c.jpg" }, t.Files.Select(Path.GetFileName));
            T.Eq(1, t.StartIndex);
            T.Eq(d.Path, t.Folder!);
        });
        T.Run("one folder: shows everything in it, optionally including subfolders", () =>
        {
            using var d = new TempDir();
            d.Write("a.jpg", new byte[1]); d.Write("sub/b.png", new byte[1]);
            T.Eq(1, StartupArgs.Resolve(new[] { d.Path }, scanner, cmp)!.Files.Count);
            T.Eq(2, StartupArgs.Resolve(new[] { d.Path }, scanner, cmp, includeSubfolders: true)!.Files.Count);
        });
        T.Run("several paths: exactly those files, de-duplicated and sorted; quotes stripped", () =>
        {
            using var d = new TempDir();
            d.Write("b.jpg", new byte[1]); d.Write("a.jpg", new byte[1]); d.Write("c.jpg", new byte[1]); d.Write("x.txt", new byte[1]);
            var t = StartupArgs.Resolve(new[] { "\"" + d.File("b.jpg") + "\"", d.File("a.jpg"), d.File("B.jpg"), d.File("x.txt") }, scanner, cmp)!;
            T.SeqEq(new[] { "a.jpg", "b.jpg" }, t.Files.Select(Path.GetFileName));
            T.Eq(0, t.StartIndex); T.True(t.Folder == null);
        });
        T.Run("nothing displayable gives null (and never throws)", () =>
        {
            using var d = new TempDir();
            d.Write("x.txt", new byte[1]);
            T.True(StartupArgs.Resolve(Array.Empty<string>(), scanner, cmp) == null);
            T.True(StartupArgs.Resolve(new[] { "  " }, scanner, cmp) == null);
            T.True(StartupArgs.Resolve(new[] { d.File("x.txt") }, scanner, cmp) == null);
            T.True(StartupArgs.Resolve(new[] { d.File("missing.jpg") }, scanner, cmp) == null);
            T.True(StartupArgs.Resolve(new[] { "bad\0path" }, scanner, cmp) == null);
            T.True(StartupArgs.Resolve(new[] { d.File("x.txt"), d.File("y.txt") }, scanner, cmp) == null);
        });
    }
}


internal static class PathAndProgressTests
{
    private sealed class SyncProgress : IProgress<int>
    {
        public readonly List<int> Reports = new();
        public void Report(int value) => Reports.Add(value);
    }

    public static void RunAll()
    {
        T.Group("PathUtil");
        string root = Path.Combine(Path.GetTempPath(), "phx-tree", "Pics");
        T.Run("same folder and anything beneath it is inside the tree", () =>
        {
            T.True(PathUtil.IsSameOrDescendant(root, root));
            T.True(PathUtil.IsSameOrDescendant(Path.Combine(root, "2020"), root));
            T.True(PathUtil.IsSameOrDescendant(Path.Combine(root, "2020", "June", "x"), root));
            T.True(PathUtil.IsSameOrDescendant(root + Path.DirectorySeparatorChar, root));
            T.True(PathUtil.IsSameOrDescendant(Path.Combine(root, "a"), root + Path.DirectorySeparatorChar));
            T.True(PathUtil.IsSameOrDescendant(Path.Combine(root, "A"), root.ToUpperInvariant()), "case-insensitive");
        });
        T.Run("siblings, parents, look-alike names and unrelated folders are outside", () =>
        {
            T.True(!PathUtil.IsSameOrDescendant(root + "2", root), "prefix is not ancestry");
            T.True(!PathUtil.IsSameOrDescendant(Path.Combine(Path.GetDirectoryName(root)!, "Other"), root));
            T.True(!PathUtil.IsSameOrDescendant(Path.GetDirectoryName(root)!, root), "a parent is not beneath its child");
            T.True(!PathUtil.IsSameOrDescendant(Path.Combine(Path.GetTempPath(), "elsewhere"), root));
        });
        T.Run("drive/filesystem roots work as tree roots; bad input never throws", () =>
        {
            string fsRoot = Path.GetPathRoot(root)!;
            T.True(PathUtil.IsSameOrDescendant(root, fsRoot));
            T.True(!PathUtil.IsSameOrDescendant(fsRoot, root));
            T.True(!PathUtil.IsSameOrDescendant("bad\0path", root));
            T.True(!PathUtil.IsSameOrDescendant(root, ""));
        });
        T.Run("network share roots get an Explorer-style label; other paths get none", () =>
        {
            T.Eq(@"photos (\\truenas.local)", PathUtil.NetworkShareLabel(@"\\truenas.local\photos"));
            T.Eq(@"photos (\\nas)", PathUtil.NetworkShareLabel(@"\\nas\photos\"));
            T.Eq(@"photos (\\nas)", PathUtil.NetworkShareLabel("//nas/photos"));
            T.Eq(null, PathUtil.NetworkShareLabel(@"C:\"));
            T.Eq(null, PathUtil.NetworkShareLabel(@"\\nas"), "a server alone is not a share");
            T.Eq(null, PathUtil.NetworkShareLabel(@"\\?\C:\"));
            T.Eq(null, PathUtil.NetworkShareLabel(@"\\.\PhysicalDrive0"));
            T.Eq(null, PathUtil.NetworkShareLabel(@"\photos"));
            T.Eq(null, PathUtil.NetworkShareLabel(""));
            T.Eq(null, PathUtil.NetworkShareLabel(null));
        });
        T.Run("subfolders stay on: inside the tree when resetting, anywhere when set to Always", () =>
        {
            string inside = Path.Combine(root, "2020"), outside = Path.Combine(Path.GetDirectoryName(root)!, "Other");
            T.True(PathUtil.SubfoldersStayOn(SubfolderMode.ResetOnNewTree, root, root));
            T.True(PathUtil.SubfoldersStayOn(SubfolderMode.ResetOnNewTree, inside, root));
            T.True(!PathUtil.SubfoldersStayOn(SubfolderMode.ResetOnNewTree, outside, root));
            T.True(!PathUtil.SubfoldersStayOn(SubfolderMode.ResetOnNewTree, Path.GetDirectoryName(root)!, root), "parent");
            T.True(PathUtil.SubfoldersStayOn(SubfolderMode.Always, outside, root));
            T.True(PathUtil.SubfoldersStayOn(SubfolderMode.Always, Path.GetDirectoryName(root)!, root));
        });

        ClickExpandTests();

        T.Group("DirectoryScanner progress");
        T.Run("reports the running count every 250 files, in increasing order", () =>
        {
            using var d = new TempDir();
            for (int i = 0; i < 620; i++) File.WriteAllBytes(d.File($"f{i:D4}.jpg"), new byte[1]);
            File.WriteAllBytes(d.File("ignored.txt"), new byte[1]);
            var p = new SyncProgress();
            var files = new DirectoryScanner(new ImageFileTypes()).Scan(d.Path, false, default, p);
            T.Eq(620, files.Count);
            T.SeqEq(new[] { 250, 500 }, p.Reports);
        });
        T.Run("progress is optional and also flows through ScanSorted", () =>
        {
            using var d = new TempDir();
            for (int i = 0; i < 300; i++) File.WriteAllBytes(d.File($"f{i}.png"), new byte[1]);
            var scanner = new DirectoryScanner(new ImageFileTypes());
            T.Eq(300, scanner.Scan(d.Path, false).Count);
            var p = new SyncProgress();
            T.Eq(300, scanner.ScanSorted(d.Path, false, SortOrder.Default, progress: p).Count);
            T.SeqEq(new[] { 250 }, p.Reports);
        });
    }

    private sealed class Node(string name, Node? parent = null)
    {
        public Node? Parent { get; } = parent;
        public IEnumerable<Node> SelfAndAncestors() { for (var n = this; n != null; n = n.Parent) yield return n; }
        public override string ToString() => name;
    }

    static void ClickExpandTests()
    {
        T.Group("ClickExpandTracker");
        // C:\ > Pics > 2020 > June, and C:\ > Docs; plus a shortcut entry for Pics outside the drive tree.
        var c = new Node("C");
        var pics = new Node("Pics", c);
        var y2020 = new Node("2020", pics);
        var june = new Node("June", y2020);
        var docs = new Node("Docs", c);
        var picsShortcut = new Node("*Pics");

        T.Run("clicking a sibling collapses the folder opened by the previous click", () =>
        {
            var t = new ClickExpandTracker<Node>();
            T.Eq(0, t.Click(pics, pics.SelfAndAncestors(), true).Count);
            T.SeqEq(new[] { pics }, t.Click(docs, docs.SelfAndAncestors(), true));
            T.SeqEq(new[] { docs }, t.Expanded);
        });
        T.Run("clicking deeper keeps the ancestors open; clicking away collapses the whole chain, deepest first", () =>
        {
            var t = new ClickExpandTracker<Node>();
            t.Click(c, c.SelfAndAncestors(), true);
            t.Click(pics, pics.SelfAndAncestors(), true);
            T.Eq(0, t.Click(y2020, y2020.SelfAndAncestors(), true).Count);
            T.Eq(0, t.Click(june, june.SelfAndAncestors(), false).Count, "leaf: nothing to expand or collapse");
            T.SeqEq(new[] { y2020, pics }, t.Click(docs, docs.SelfAndAncestors(), false), "C stays: it is Docs' parent");
            T.SeqEq(new[] { c }, t.Expanded);
        });
        T.Run("clicking back up to an ancestor collapses what was opened below it", () =>
        {
            var t = new ClickExpandTracker<Node>();
            t.Click(pics, pics.SelfAndAncestors(), true);
            t.Click(y2020, y2020.SelfAndAncestors(), true);
            T.SeqEq(new[] { y2020 }, t.Click(pics, pics.SelfAndAncestors(), false));
            T.SeqEq(new[] { pics }, t.Expanded);
        });
        T.Run("folders not opened by a click (arrow, keyboard, already open) are never collapsed", () =>
        {
            var t = new ClickExpandTracker<Node>();
            T.Eq(0, t.Click(pics, pics.SelfAndAncestors(), false).Count, "Pics was already open");
            T.Eq(0, t.Click(docs, docs.SelfAndAncestors(), true).Count);
        });
        T.Run("forgetting a folder (its arrow was used) keeps it open", () =>
        {
            var t = new ClickExpandTracker<Node>();
            t.Click(pics, pics.SelfAndAncestors(), true);
            t.Forget(pics);
            T.Eq(0, t.Click(docs, docs.SelfAndAncestors(), true).Count);
            t.Clear();
            T.Eq(0, t.Expanded.Count);
        });
        T.Run("nodes compare by identity: a shortcut to the same folder is a different node", () =>
        {
            var t = new ClickExpandTracker<Node>();
            t.Click(pics, pics.SelfAndAncestors(), true);
            T.SeqEq(new[] { pics }, t.Click(picsShortcut, picsShortcut.SelfAndAncestors(), true));
            t.Click(picsShortcut, picsShortcut.SelfAndAncestors(), true);
            T.SeqEq(new[] { picsShortcut }, t.Expanded, "clicking the same node twice tracks it once");
        });
    }
}


internal static class FolderNavigatorTests
{
    public static void RunAll()
    {
        T.Group("FolderNavigator");
        var scanner = new DirectoryScanner(new ImageFileTypes());
        var cmp = FileComparers.For(SortOrder.Default);

        // a tree of sibling folders: 1, 2 (empty), 3, 10, 11 (only a text file), 12
        static TempDir MakeTree(params string[] files)
        {
            var d = new TempDir();
            foreach (var f in files) d.Write(f, new byte[] { 1 });
            return d;
        }
        var layout = new[]
        {
            "lib/1/a.jpg", "lib/1/b.jpg", "lib/2/readme.txt", "lib/3/z.png", "lib/10/p.jpg",
            "lib/11/notes.txt", "lib/12/q.tif",
        };

        T.Run("siblings come back in natural name order, including the current folder", () =>
        {
            using var d = MakeTree(layout);
            Directory.CreateDirectory(d.File("lib/4"));
            var sibs = FolderNavigator.GetSiblingFolders(d.File("lib/3")).Select(Path.GetFileName).ToList();
            T.SeqEq(new[] { "1", "2", "3", "4", "10", "11", "12" }, sibs);
        });

        T.Run("subfolders (for the folder tree): natural order, folders only, hidden skipped, missing is empty", () =>
        {
            using var d = MakeTree(layout);
            d.Write("lib/loose.jpg", new byte[] { 1 });
            var hidden = Directory.CreateDirectory(d.File("lib/.hidden"));
            if (OperatingSystem.IsWindows()) hidden.Attributes |= FileAttributes.Hidden;
            var subs = FolderNavigator.GetSubfolders(d.File("lib")).Select(Path.GetFileName).ToList();
            if (!OperatingSystem.IsWindows()) subs.Remove(".hidden");   // "hidden" is a Windows attribute
            T.SeqEq(new[] { "1", "2", "3", "10", "11", "12" }, subs);
            T.Eq(0, FolderNavigator.GetSubfolders(d.File("lib/1")).Count, "no subfolders");
            T.Eq(0, FolderNavigator.GetSubfolders(d.File("nope")).Count, "missing folder");
            T.Eq(0, FolderNavigator.GetSubfolders("bad\0path").Count, "invalid path");
        });

        T.Run("next and previous skip folders that have no images", () =>
        {
            using var d = MakeTree(layout);
            T.Eq("3", Path.GetFileName(FolderNavigator.FindSibling(d.File("lib/1"), +1, scanner, cmp, false)!.Path));   // 2 is skipped
            T.Eq("1", Path.GetFileName(FolderNavigator.FindSibling(d.File("lib/3"), -1, scanner, cmp, false)!.Path));
            T.Eq("10", Path.GetFileName(FolderNavigator.FindSibling(d.File("lib/3"), +1, scanner, cmp, false)!.Path));
            T.Eq("12", Path.GetFileName(FolderNavigator.FindSibling(d.File("lib/10"), +1, scanner, cmp, false)!.Path)); // 11 skipped
            T.Eq("10", Path.GetFileName(FolderNavigator.FindSibling(d.File("lib/12"), -1, scanner, cmp, false)!.Path));
        });

        T.Run("stops at the ends: no wrap-around", () =>
        {
            using var d = MakeTree(layout);
            T.True(FolderNavigator.FindSibling(d.File("lib/12"), +1, scanner, cmp, false) == null, "after the last folder");
            T.True(FolderNavigator.FindSibling(d.File("lib/1"), -1, scanner, cmp, false) == null, "before the first folder");
        });

        T.Run("if every later folder is empty there is no next folder", () =>
        {
            using var d = MakeTree("lib/1/a.jpg", "lib/2/x.txt", "lib/3/y.txt");
            T.True(FolderNavigator.FindSibling(d.File("lib/1"), +1, scanner, cmp, false) == null);
        });

        T.Run("result has sorted files and the folder's place among its siblings", () =>
        {
            using var d = MakeTree(layout);
            d.Write("lib/1/img10.jpg", new byte[1]); d.Write("lib/1/img2.jpg", new byte[1]);
            var r = FolderNavigator.FindSibling(d.File("lib/3"), -1, scanner, cmp, false)!;
            T.SeqEq(new[] { "a.jpg", "b.jpg", "img2.jpg", "img10.jpg" }, r.Files.Select(Path.GetFileName));
            T.Eq(1, r.Position); T.Eq(6, r.Total, "six sibling folders: 1, 2, 3, 10, 11, 12");
            var r2 = FolderNavigator.FindSibling(d.File("lib/3"), +1, scanner, cmp, false)!;
            T.Eq(4, r2.Position);
        });

        T.Run("a sort order is honoured (size, descending)", () =>
        {
            using var d = new TempDir();
            d.Write("lib/1/x.jpg", new byte[1]);
            d.Write("lib/2/small.jpg", new byte[5]); d.Write("lib/2/big.jpg", new byte[50]); d.Write("lib/2/mid.jpg", new byte[20]);
            var bySize = FileComparers.For(new SortOrder(SortField.Size, true));
            var r = FolderNavigator.FindSibling(d.File("lib/1"), +1, scanner, bySize, false)!;
            T.SeqEq(new[] { "big.jpg", "mid.jpg", "small.jpg" }, r.Files.Select(Path.GetFileName));
        });

        T.Run("include-subfolders decides whether nested images make a folder non-empty", () =>
        {
            using var d = MakeTree("lib/1/a.jpg", "lib/2/deep/inner/b.jpg", "lib/3/c.jpg");
            T.Eq("3", Path.GetFileName(FolderNavigator.FindSibling(d.File("lib/1"), +1, scanner, cmp, false)!.Path));
            var with = FolderNavigator.FindSibling(d.File("lib/1"), +1, scanner, cmp, true)!;
            T.Eq("2", Path.GetFileName(with.Path));
            T.SeqEq(new[] { "b.jpg" }, with.Files.Select(Path.GetFileName));
        });

        T.Run("trailing separators and different case still identify the current folder", () =>
        {
            using var d = MakeTree(layout);
            var r = FolderNavigator.FindSibling(d.File("lib/1") + Path.DirectorySeparatorChar, +1, scanner, cmp, false);
            T.True(r != null && Path.GetFileName(r.Path) == "3");
        });

        T.Run("missing folder, drive root and direction 0 give null without throwing", () =>
        {
            using var d = MakeTree(layout);
            T.True(FolderNavigator.FindSibling(d.File("lib/nope"), +1, scanner, cmp, false) == null);
            T.True(FolderNavigator.FindSibling(Path.GetPathRoot(d.Path)!, +1, scanner, cmp, false) == null);
            T.True(FolderNavigator.FindSibling(d.File("lib/1"), 0, scanner, cmp, false) == null);
            T.Eq(0, FolderNavigator.GetSiblingFolders(Path.GetPathRoot(d.Path)!).Count);
        });

        T.Run("cancellation is honoured", () =>
        {
            using var d = MakeTree(layout);
            using var cts = new CancellationTokenSource(); cts.Cancel();
            T.Throws<OperationCanceledException>(() => FolderNavigator.FindSibling(d.File("lib/1"), +1, scanner, cmp, false, cts.Token));
        });

        T.Run("reference folder: the folder you started in wins while the slide is inside it", () =>
        {
            string root = Path.Combine(Path.GetTempPath(), "lib", "Root");
            T.Eq(root, FolderNavigator.ResolveReferenceFolder(root, Path.Combine(root, "A", "x.jpg"), out var a1)!);
            T.Eq(root, a1!);
            T.Eq(root, FolderNavigator.ResolveReferenceFolder(root, Path.Combine(root, "A", "deep", "y.jpg"), out _)!);
            T.Eq(root, FolderNavigator.ResolveReferenceFolder(root, Path.Combine(root, "z.jpg"), out _)!);
        });
        T.Run("reference folder: without an anchor, or outside it, the slide's own folder is used", () =>
        {
            string root = Path.Combine(Path.GetTempPath(), "lib", "Root"), other = Path.Combine(Path.GetTempPath(), "lib", "Other");
            T.Eq(Path.Combine(root, "A"), FolderNavigator.ResolveReferenceFolder(null, Path.Combine(root, "A", "x.jpg"), out var a1)!);
            T.True(a1 == null);
            T.Eq(other, FolderNavigator.ResolveReferenceFolder(root, Path.Combine(other, "q.jpg"), out var a2)!);
            T.True(a2 == null, "anchor dropped once the slide leaves it");
            T.Eq(Path.Combine(Path.GetTempPath(), "lib", "Root2"),
                 FolderNavigator.ResolveReferenceFolder(root, Path.Combine(Path.GetTempPath(), "lib", "Root2", "i.jpg"), out _)!, "look-alike name is not inside");
        });
        T.Run("reference folder: with no slide (e.g. all deleted) the anchor is kept", () =>
        {
            T.Eq("keep", FolderNavigator.ResolveReferenceFolder("keep", null, out var a)!);
            T.Eq("keep", a!);
            T.True(FolderNavigator.ResolveReferenceFolder(null, null, out _) == null);
        });

        T.Run("empty root + include subfolders: jumping goes to the root's sibling, not to a subfolder of the root", () =>
        {
            // lib/Root is empty itself; its subfolders A and B hold the images. lib/Zed (sorts after Root) has images too.
            using var d = MakeTree("lib/Root/A/a.jpg", "lib/Root/B/b.jpg", "lib/Zed/n.jpg");
            string root = d.File("lib/Root");
            string firstImage = d.File("lib/Root/A/a.jpg");

            // OLD behaviour (no anchor): reference = the first image's folder, so we step A -> B inside the root
            string oldRef = FolderNavigator.ResolveReferenceFolder(null, firstImage, out _)!;
            T.Eq("B", Path.GetFileName(FolderNavigator.FindSibling(oldRef, +1, scanner, cmp, true)!.Path));

            // NEW behaviour (anchor = the folder we started in): reference = Root, so we go on to Root's sibling
            string newRef = FolderNavigator.ResolveReferenceFolder(root, firstImage, out _)!;
            T.Eq(root, newRef);
            var next = FolderNavigator.FindSibling(newRef, +1, scanner, cmp, true)!;
            T.Eq("Zed", Path.GetFileName(next.Path));
            T.SeqEq(new[] { "n.jpg" }, next.Files.Select(Path.GetFileName));
            T.True(FolderNavigator.FindSibling(newRef, -1, scanner, cmp, true) == null, "Root is the first sibling");
        });

        T.Group("DirectoryScanner.HasAnyImage");
        T.Run("finds an image, ignores others, respects recursion and companion files", () =>
        {
            using var d = new TempDir();
            d.Write("a/note.txt", new byte[1]); d.Write("b/._x.jpg", new byte[1]); d.Write("c/deep/y.png", new byte[1]); d.Write("e/z.jpg", new byte[1]);
            T.True(!scanner.HasAnyImage(d.File("a"), true));
            T.True(!scanner.HasAnyImage(d.File("b"), true), "macOS companion file isn't an image");
            T.True(!scanner.HasAnyImage(d.File("c"), false)); T.True(scanner.HasAnyImage(d.File("c"), true));
            T.True(scanner.HasAnyImage(d.File("e"), false));
            T.True(!scanner.HasAnyImage(d.File("missing"), true));
        });
    }
}
