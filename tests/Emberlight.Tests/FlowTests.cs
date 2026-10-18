using Emberlight.Core;

namespace Emberlight.Tests;

/// <summary>
/// File transfers, the browser following a slideshow, the remaining on-screen messages, startup routing, the folder
/// tree's top level and building GIF animations.
/// </summary>
internal static class FlowTests
{
    public static void RunAll()
    {
        TransferTests();
        FollowTests();
        MessageTests();
        StartupPlanTests();
        TreeRootTests();
        GifBuilderTests();
    }

    static void TransferTests()
    {
        T.Group("File transfer results");

        T.Run("copying counts each file and keeps the originals", () =>
        {
            using var src = new TempDir();
            using var dest = new TempDir();
            string a = src.Write("a.jpg", [1]), b = src.Write("b.jpg", [2]);
            var r = FileTransfer.TransferAll([a, b], dest.Path, move: false);
            T.Eq(2, r.Done); T.Eq(0, r.Failed); T.Eq(null, r.FirstError);
            T.Eq("Copied 2 file(s)", r.Summary);
            T.True(File.Exists(a) && File.Exists(dest.File("a.jpg")) && File.Exists(dest.File("b.jpg")));
        });

        T.Run("moving reports each file that went, and the first error for the rest", () =>
        {
            using var src = new TempDir();
            using var dest = new TempDir();
            string a = src.Write("a.jpg", [1]);
            string missing = src.File("gone.jpg"), alsoMissing = src.File("gone2.jpg");
            var moved = new List<string>();
            var r = FileTransfer.TransferAll([a, missing, alsoMissing], dest.Path, move: true, moved.Add);
            T.Eq(1, r.Done); T.Eq(2, r.Failed);
            T.SeqEq(new[] { a }, moved, "only the file that moved");
            T.True(r.FirstError != null && r.FirstError.Contains("gone.jpg"), r.FirstError);
            T.Eq($"Moved 1 file(s); 2 failed: {r.FirstError}", r.Summary);
            T.True(!File.Exists(a) && File.Exists(dest.File("a.jpg")));
        });

        T.Run("moving files into the folder they are already in leaves them, and in the grid", () =>
        {
            using var d = new TempDir();
            string a = d.Write("a.jpg", [1]), b = d.Write("b.jpg", [2]);
            using var other = new TempDir();
            string c = other.Write("c.jpg", [3]);
            var moved = new List<string>();
            var r = FileTransfer.TransferAll([a, c, b], d.Path + Path.DirectorySeparatorChar, move: true, moved.Add);
            T.SeqEq(new[] { c }, moved, "only the file that really moved is reported");
            T.Eq(1, r.Done); T.Eq(2, r.AlreadyThere); T.Eq(0, r.Failed);
            T.Eq("Moved 1 file(s); 2 already in that folder", r.Summary);
            T.True(File.Exists(a) && File.Exists(b), "nothing moved or deleted");
            T.True(!File.Exists(d.File("a (2).jpg")), "no renamed copy either");
        });

        T.Run("nothing to transfer: nothing done", () =>
        {
            using var dest = new TempDir();
            T.Eq("Copied 0 file(s)", FileTransfer.TransferAll([], dest.Path, move: false).Summary);
        });
    }

    static void FollowTests()
    {
        T.Group("Slideshow follow");
        string root = Path.Combine(Path.GetTempPath(), "phx-follow");
        string a = Path.Combine(root, "A"), b = Path.Combine(root, "B");
        static bool Exists(string _) => true;

        T.Run("the browser opens the folder the slideshow last jumped to", () =>
        {
            var f = new SlideshowFollow();
            f.Jumped(a);
            f.Jumped(b);                                     // holding the key: only the last one counts
            T.Eq(b, f.TakeFolderToOpen(a, Exists));
            T.Eq(null, f.TakeFolderToOpen(a, Exists), "taken once");
        });

        T.Run("not if it is already showing that folder, or the folder has gone", () =>
        {
            var f = new SlideshowFollow();
            f.Jumped(a);
            T.Eq(null, f.TakeFolderToOpen(a.ToUpperInvariant(), Exists), "same folder, any case");
            f.Jumped(b);
            T.Eq(null, f.TakeFolderToOpen(a, _ => false), "deleted meanwhile");
            T.Eq(null, f.TakeFolderToOpen(a, Exists), "and forgotten");
        });

        T.Run("TakePending leaves the existence check to the caller", () =>
        {
            var f = new SlideshowFollow();
            f.Jumped(b);
            T.Eq(b, f.TakePending(a));
            T.Eq(null, f.TakePending(a), "taken once");
            f.Jumped(a);
            T.Eq(null, f.TakePending(a.ToUpperInvariant()), "same folder, any case");
        });

        T.Run("nothing jumped: nothing to open", () =>
            T.Eq(null, new SlideshowFollow().TakeFolderToOpen(a, Exists)));

        T.Run("the pause before following is 400 ms", () => T.Eq(TimeSpan.FromMilliseconds(400), SlideshowFollow.Delay));

        T.Run("when the slideshow closes: select its last slide, or remember it while the folder loads", () =>
        {
            T.Eq(CloseSelection.Select, SlideshowFollow.AfterClose("x.jpg", isListed: true, isLoading: false));
            T.Eq(CloseSelection.Select, SlideshowFollow.AfterClose("x.jpg", isListed: true, isLoading: true));
            T.Eq(CloseSelection.SelectWhenLoaded, SlideshowFollow.AfterClose("x.jpg", isListed: false, isLoading: true));
            T.Eq(CloseSelection.None, SlideshowFollow.AfterClose("x.jpg", isListed: false, isLoading: false));
            T.Eq(CloseSelection.None, SlideshowFollow.AfterClose(null, isListed: false, isLoading: true));
        });
    }

    static void MessageTests()
    {
        T.Group("DisplayText (messages)");

        T.Run("loading a folder: scanning, sorting or preparing, then loading", () =>
        {
            T.Eq("Scanning folder\u2026", DisplayText.ScanStarted);
            T.Eq($"Scanning\u2026 {1234:N0} images found", DisplayText.Scanning(1234));
            T.Eq($"Sorting {1500:N0} images\u2026", DisplayText.Sorting(1500, new SortOrder(SortField.DateModified, true)));
            T.Eq($"Preparing {1500:N0} images\u2026", DisplayText.Sorting(1500, SortOrder.Default), "name order opens no files");
            T.Eq($"Loading {20:N0} images\u2026", DisplayText.Loading(20));
            T.Eq("Opening \"C:\\a\"\u2026", DisplayText.OpeningPath("C:\\a"));
            T.Eq("Can't find \"C:\\a\". It may be on a drive or network share that isn't connected.", DisplayText.PathNotFound("C:\\a"));
            T.Eq("Sorting\u2026", DisplayText.Resorting);
        });

        T.Run("window titles: the folder or picture, then the app's name", () =>
        {
            string pics = Path.Combine(Path.GetTempPath(), "Holiday 2024");
            T.Eq("Holiday 2024 \u2014 Emberlight", DisplayText.BrowserTitle(pics));
            T.Eq("beach.jpg \u2014 Emberlight", DisplayText.SlideshowTitle(Path.Combine(pics, "beach.jpg")));
        });

        T.Run("slideshow messages", () =>
        {
            T.Eq("No images", DisplayText.PastEnd(0));
            T.Eq("No more images\n(press \u2190 or Esc)", DisplayText.PastEnd(3));
            T.Eq("No more images\n(press \u2190 or Esc)", DisplayText.PastEnd(1), "one slide left behind is still 'no more'");
            T.Eq("Could not open\nbeach.jpg", DisplayText.CouldNotOpen(Path.Combine(Path.GetTempPath(), "beach.jpg")));
            T.Eq("Random order: ON  (reshuffled)", DisplayText.RandomToggled(true));
            T.Eq("Random order: OFF  (continuing in sort order)", DisplayText.RandomToggled(false));
            T.Eq("Couldn't move to the Recycle Bin:\naccess denied", DisplayText.RecycleFailed("access denied"));
            T.Eq("Couldn't move to the Trash:\naccess denied", DisplayText.RecycleFailed("access denied", "Trash"));
        });

        T.Run("preferences saved, or not", () =>
        {
            T.Eq("Preferences saved", DisplayText.PreferencesSaved);
            T.Eq("Your preferences were applied, but they couldn't be saved to disk.\n\nC:\\x\\settings.json",
                DisplayText.PreferencesNotSaved("C:\\x\\settings.json"));
        });
    }

    static void StartupPlanTests()
    {
        T.Group("Startup plan");

        T.Run("no arguments: the browser on its own, at its usual folder", () =>
        {
            var plan = StartupArgs.Plan([], new AppSettings());
            T.Eq(null, plan.BrowserPath); T.Eq(null, plan.Slideshow);
        });

        T.Run("a picture opened with the app: a slideshow of its folder, the browser behind it there", () =>
        {
            using var d = new TempDir();
            d.Write("a.jpg", [1]);
            string b = d.Write("b.jpg", [1]);
            var plan = StartupArgs.Plan([b], new AppSettings { OpenFilesDoSlideshow = true });
            T.True(plan.Slideshow != null);
            T.Eq(2, plan.Slideshow!.Files.Count);
            T.Eq(1, plan.Slideshow.StartIndex, "starts on the picture opened");
            T.True(PathUtil.SamePath(Path.GetFullPath(d.Path), plan.BrowserPath), plan.BrowserPath);
        });

        T.Run("with the slideshow preference off, the browser opens at the argument", () =>
        {
            using var d = new TempDir();
            string b = d.Write("b.jpg", [1]);
            var plan = StartupArgs.Plan([b], new AppSettings { OpenFilesDoSlideshow = false });
            T.Eq(null, plan.Slideshow); T.Eq(b, plan.BrowserPath);
        });

        T.Run("several files from different places: the browser opens at the first one shown", () =>
        {
            using var d1 = new TempDir();
            using var d2 = new TempDir();
            string a = d1.Write("a.jpg", [1]), z = d2.Write("z.jpg", [1]);
            var plan = StartupArgs.Plan([z, a], new AppSettings { OpenFilesDoSlideshow = true });
            T.Eq(null, plan.Slideshow!.Folder);
            T.Eq(plan.Slideshow.Files[plan.Slideshow.StartIndex], plan.BrowserPath);
        });

        T.Run("nothing showable (an ignored type, or a missing file): just the browser, at the argument", () =>
        {
            using var d = new TempDir();
            string png = d.Write("p.png", [1]);
            var plan = StartupArgs.Plan([png], new AppSettings { OpenFilesDoSlideshow = true, IgnoredFileTypes = ["png"] });
            T.Eq(null, plan.Slideshow); T.Eq(png, plan.BrowserPath);
            string gone = d.File("gone.jpg");
            T.Eq(gone, StartupArgs.Plan([gone], new AppSettings { OpenFilesDoSlideshow = true }).BrowserPath);
        });
    }

    static void TreeRootTests()
    {
        T.Group("Folder tree roots");

        T.Run("shortcuts: Pictures, Desktop, Documents, Downloads, starred, for the folders that exist", () =>
        {
            var all = FolderTreeRoots.Shortcuts(_ => true);
            T.SeqEq(new[] { "\u2605 Pictures", "\u2605 Desktop", "\u2605 Documents", "\u2605 Downloads" }, all.Select(s => s.Label));
            T.True(all.All(s => s.Path.Length > 0) || !OperatingSystem.IsWindows(), "each has a folder");
            T.True(all[3].Path.EndsWith("Downloads"), all[3].Path);
            var some = FolderTreeRoots.Shortcuts(p => p.EndsWith("Downloads"));
            T.SeqEq(new[] { "\u2605 Downloads" }, some.Select(s => s.Label), "missing folders left out");
            T.Eq(0, FolderTreeRoots.Shortcuts(_ => false).Count);
        });

        T.Run("a starred label marks a shortcut, not a drive", () =>
        {
            T.True(FolderTreeRoots.IsShortcut("\u2605 Pictures"));
            T.True(!FolderTreeRoots.IsShortcut("C:\\"));
            T.True(!FolderTreeRoots.IsShortcut("photos (\\\\nas)"));
        });

        T.Run("drives slot in letter order, before any network shares, as each one answers", () =>
        {
            var roots = new List<string>();               // the tree's entries after the shortcuts
            void AddDrive(string root) => roots.Insert(FolderTreeRoots.DriveInsertIndex(roots, root), root);
            AddDrive(@"E:\");
            AddDrive(@"C:\");
            roots.Add(@"\\nas\photos");                   // a share opened meanwhile: shares go last
            AddDrive(@"F:\");
            AddDrive(@"d:\");
            T.SeqEq(new[] { @"C:\", @"d:\", @"E:\", @"F:\", @"\\nas\photos" }, roots);
        });

        T.Run("drive checks: new letters are checked, vanished ones removed, checks in progress left alone", () =>
        {
            var u = FolderTreeRoots.CompareDrives(inTree: [@"C:\", @"Z:\"], checking: [@"D:\"], present: [@"C:\", @"D:\", @"E:\"]);
            T.SeqEq(new[] { @"E:\" }, u.ToCheck, "E appeared (D is already being checked)");
            T.SeqEq(new[] { @"Z:\" }, u.Removed, "Z went away");
        });

        T.Run("drive checks: a letter that isn't ready yet is checked again on every pass", () =>
        {
            // Y: is mapped but offline (VPN down): present, not in the tree, no check running.
            var u = FolderTreeRoots.CompareDrives(inTree: [@"C:\"], checking: [], present: [@"C:\", @"Y:\"]);
            T.SeqEq(new[] { @"Y:\" }, u.ToCheck);
            T.Eq(0, u.Removed.Count);
        });

        T.Run("drive checks: case and trailing separators don't matter", () =>
        {
            var u = FolderTreeRoots.CompareDrives(inTree: ["c:"], checking: ["d:\\"], present: [@"C:\", "D:"]);
            T.Eq(0, u.ToCheck.Count); T.Eq(0, u.Removed.Count);
        });

        T.Run("drives are looked for every 5 seconds", () => T.Eq(TimeSpan.FromSeconds(5), FolderTreeRoots.DrivePollInterval));

        T.Run("ignored roots are dropped by a '*' pattern or an exact match; an empty list is a no-op", () =>
        {
            string[] present = ["/", "/Volumes/Backup", "/Volumes/.timemachine/ABC/1", "/Volumes/Recovery", "/dev"];
            var kept = FolderTreeRoots.FilterIgnored(present,
                ["/dev", "/Volumes/Recovery", "/Volumes/.timemachine/*"]);
            T.SeqEq(new[] { "/", "/Volumes/Backup" }, kept);
            T.SeqEq(present, FolderTreeRoots.FilterIgnored(present, []), "no patterns - nothing dropped");
        });

        T.Group("Pattern matching (PathUtil)");

        T.Run("'*' matches any run of characters, including path separators", () =>
        {
            T.True(PathUtil.MatchesPattern("/System/Volumes/Data", "/System/*"));
            T.True(PathUtil.MatchesPattern("/System", "/System"), "no wildcard - exact match");
            T.True(!PathUtil.MatchesPattern("/System2", "/System"), "no wildcard - not a prefix match");
            T.True(!PathUtil.MatchesPattern("/Other/Volumes/Data", "/System/*"));
        });

        T.Run("pattern matching is case-insensitive and ignores a trailing separator on either side", () =>
        {
            T.True(PathUtil.MatchesPattern("/Volumes/RECOVERY", "/volumes/recovery"));
            T.True(PathUtil.MatchesPattern("/dev/", "/dev"));
        });

        T.Run("a literal '.' in a pattern is matched literally, not as a regex wildcard", () =>
        {
            T.True(PathUtil.MatchesPattern("/Volumes/.timemachine/x", "/Volumes/.timemachine/*"));
            T.True(!PathUtil.MatchesPattern("/VolumesX timemachine/x", "/Volumes/.timemachine/*"),
                "a literal dot shouldn't match any character");
        });

        T.Run("matches-any is true if any pattern matches, false if the list is empty or none do", () =>
        {
            T.True(PathUtil.MatchesAnyPattern("/dev", ["/System/*", "/dev"]));
            T.True(!PathUtil.MatchesAnyPattern("/dev", ["/System/*"]));
            T.True(!PathUtil.MatchesAnyPattern("/dev", []));
        });

        T.Group("Folder tree updates");
        string P(string name) => Path.Combine(Path.GetTempPath(), "phx-tree", name);
        static List<string> Apply(IReadOnlyList<string> shown, ChildrenUpdate u)
        {
            var list = shown.Where(s => !u.Removed.Contains(s, PathUtil.Comparer)).ToList();
            foreach (var (index, path) in u.Inserted) list.Insert(index, path);
            return list;
        }

        T.Run("a new subfolder is inserted where it belongs; existing ones stay put", () =>
        {
            string[] shown = [P("2019"), P("2021")], current = [P("2019"), P("2020"), P("2021")];
            var u = TreeChildren.Compare(shown, current);
            T.Eq(0, u.Removed.Count);
            T.SeqEq(new[] { (1, P("2020")) }, u.Inserted);
            T.SeqEq(current, Apply(shown, u));
        });

        T.Run("a deleted subfolder is removed; a renamed one is removed and inserted", () =>
        {
            string[] shown = [P("a"), P("b"), P("c")], current = [P("a"), P("c2")];
            var u = TreeChildren.Compare(shown, current);
            T.SeqEq(new[] { P("b"), P("c") }, u.Removed);
            T.SeqEq(new[] { (1, P("c2")) }, u.Inserted);
            T.SeqEq(current, Apply(shown, u));
        });

        T.Run("no change: nothing to do; a folder with none gets its first", () =>
        {
            var same = TreeChildren.Compare([P("a")], [P("a").ToUpperInvariant()]);
            T.True(same.IsEmpty, "case doesn't make a folder new");
            var first = TreeChildren.Compare([], [P("new")]);
            T.SeqEq(new[] { (0, P("new")) }, first.Inserted);
        });

        T.Run("fuzz: applying the update always gives the folder's current subfolders", () =>
        {
            string[] all = Enumerable.Range(0, 12).Select(i => P($"f{i:D2}")).ToArray();
            for (int seed = 0; seed < 300; seed++)
            {
                var rnd = new Random(seed);
                var shown = all.Where(_ => rnd.Next(2) == 0).ToList();
                var current = all.Where(_ => rnd.Next(2) == 0).ToList();
                var u = TreeChildren.Compare(shown, current);
                T.SeqEq(current, Apply(shown, u), $"seed {seed}");
                T.True(u.Removed.All(r => !current.Contains(r)), "only vanished folders are removed");
            }
        });
    }

    static void GifBuilderTests()
    {
        T.Group("GifAnimationBuilder");
        static byte[] Px(int w, int h) => new byte[w * h * 4];

        T.Run("frames become an animation on the canvas size from the file", () =>
        {
            var g = new GifAnimationBuilder();
            T.True(g.TryReserve(4, 3)); g.Add(0, 0, 4, 3, 10, 1, Px(4, 3));
            T.True(g.TryReserve(2, 2)); g.Add(1, 1, 2, 2, 5, 2, Px(2, 2));
            var anim = g.Build(10, 8)!;
            T.Eq((10, 8), (anim.Width, anim.Height));
            T.Eq(2, anim.Frames.Count);
            T.Eq((1, 1, 2, 2, 5, GifDisposal.RestoreBackground), (anim.Frames[1].Left, anim.Frames[1].Top, anim.Frames[1].Width,
                anim.Frames[1].Height, anim.Frames[1].DelayCentiseconds, anim.Frames[1].Disposal));
        });

        T.Run("a missing canvas size falls back to the area the frames cover", () =>
        {
            var g = new GifAnimationBuilder();
            g.TryReserve(4, 3); g.Add(0, 0, 4, 3, 10, 0, Px(4, 3));
            g.TryReserve(2, 5); g.Add(3, 1, 2, 5, 10, 0, Px(2, 5));
            var anim = g.Build(0, 0)!;
            T.Eq((5, 6), (anim.Width, anim.Height));
            T.Eq((5, 6), (g.Build(0, 7)!.Width, g.Build(0, 7)!.Height), "either side missing");
        });

        T.Run("a single frame is not an animation", () =>
        {
            var g = new GifAnimationBuilder();
            g.TryReserve(2, 2); g.Add(0, 0, 2, 2, 10, 0, Px(2, 2));
            T.Eq(null, g.Build(2, 2));
            T.Eq(null, new GifAnimationBuilder().Build(2, 2));
        });

        T.Run("frames that would need more than the memory limit are refused", () =>
        {
            T.Eq(256L * 1024 * 1024, GifAnimationBuilder.DefaultMaxBytes);
            var g = new GifAnimationBuilder(maxBytes: 100);
            T.True(g.TryReserve(5, 5), "100 bytes: exactly the limit");
            T.True(!g.TryReserve(1, 1), "one pixel more is too much");
            T.Eq(100L, g.ReservedBytes, "a refused frame isn't counted");
            T.True(new GifAnimationBuilder().TryReserve(8192, 8192), "a 256 MB frame fits the default");
        });

        T.Run("disposal codes 0-3 map to their meaning; anything else is unspecified", () =>
        {
            T.Eq(GifDisposal.Unspecified, GifAnimationBuilder.DisposalFromCode(0));
            T.Eq(GifDisposal.Keep, GifAnimationBuilder.DisposalFromCode(1));
            T.Eq(GifDisposal.RestoreBackground, GifAnimationBuilder.DisposalFromCode(2));
            T.Eq(GifDisposal.RestorePrevious, GifAnimationBuilder.DisposalFromCode(3));
            foreach (int bad in new[] { -1, 4, 7, 99 }) T.Eq(GifDisposal.Unspecified, GifAnimationBuilder.DisposalFromCode(bad), bad.ToString());
        });
    }
}
