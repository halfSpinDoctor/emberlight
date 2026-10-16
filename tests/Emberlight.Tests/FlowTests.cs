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

        T.Run("drives slot in letter order, after the shortcuts, as each one answers", () =>
        {
            var ready = new bool[4];                      // C, D, E, F
            ready[2] = true; T.Eq(4, FolderTreeRoots.DriveSlot(ready, 2, firstDriveIndex: 4), "E first: right after the shortcuts");
            ready[0] = true; T.Eq(4, FolderTreeRoots.DriveSlot(ready, 0, 4), "C goes before E");
            ready[3] = true; T.Eq(6, FolderTreeRoots.DriveSlot(ready, 3, 4), "F after C and E");
            ready[1] = true; T.Eq(5, FolderTreeRoots.DriveSlot(ready, 1, 4), "D between C and E");
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
