using System.Globalization;
using Emberlight.Core;

namespace Emberlight.Tests;

internal static class CoreTests
{
    public static void RunAll()
    {
        RandomizableListTests();
        ListExtensionTests();
        NaturalComparerTests();
        FileSizeTests();
        BuildVersionTests();
        OrientationTests();
        LruTests();
        FileTypeTests();
        SettingsTests();
    }

    // ------------------------------------------------------------------------------------------
    static void RandomizableListTests()
    {
        T.Group("RandomizableList");
        var cmp = Comparer<int>.Default;

        T.Run("randomize keeps the same items and remembers original order", () =>
        {
            var l = new RandomizableList<int>(new Random(1));
            l.SetItems(Enumerable.Range(0, 50));
            l.Randomize();
            l.Validate();
            T.True(l.IsRandomized);
            T.SeqEq(Enumerable.Range(0, 50), l.OrderBy(x => x));
            T.True(!l.SequenceEqual(Enumerable.Range(0, 50)), "50 items should not shuffle to identity");
            for (int i = 0; i < l.Count; i++) T.Eq(l[i], l.OrderedIndexFromIndex(i));
            l.Derandomize();
            T.SeqEq(Enumerable.Range(0, 50), l);
            T.True(!l.IsRandomized);
        });

        T.Run("randomize(start) puts the chosen item first", () =>
        {
            for (int seed = 0; seed < 20; seed++)
            {
                var l = new RandomizableList<int>(new Random(seed));
                l.SetItems(Enumerable.Range(100, 30));
                l.Randomize(17);
                l.Validate();
                T.Eq(117, l[0]);
                T.SeqEq(Enumerable.Range(100, 30), l.OrderBy(x => x));
            }
        });

        T.Run("randomizing again reshuffles but keeps the original order", () =>
        {
            var l = new RandomizableList<int>(new Random(5));
            l.SetItems(Enumerable.Range(0, 20));
            l.Randomize();
            var first = l.ToList();
            l.Randomize();
            l.Validate();
            T.True(!first.SequenceEqual(l), "second shuffle should differ");
            l.Derandomize();
            T.SeqEq(Enumerable.Range(0, 20), l);
        });

        T.Run("edge cases: empty and single-item lists", () =>
        {
            var l = new RandomizableList<int>(new Random(2));
            l.Randomize(); l.Validate(); T.Eq(0, l.Count); T.True(!l.IsRandomized);
            l.SetItems(new[] { 7 });
            l.Randomize(0); l.Validate(); T.Eq(7, l[0]);
            l.Randomize(); l.Validate();
            T.Eq(RandomizableList<int>.NotFound, l.IndexOfNextInOrder(0));
            T.Eq(RandomizableList<int>.NotFound, l.IndexOfPreviousInOrder(0));
            l.RemoveAt(0); l.Validate(); T.Eq(0, l.Count);
            T.Throws<ArgumentOutOfRangeException>(() => l.Randomize(0));
        });

        T.Run("ordered next/previous walk the original sequence", () =>
        {
            var l = new RandomizableList<int>(new Random(3));
            l.SetItems(Enumerable.Range(0, 25));
            l.Randomize();
            int idx = l.IndexOf(0);
            var walked = new List<int> { l[idx] };
            while ((idx = l.IndexOfNextInOrder(idx)) != RandomizableList<int>.NotFound) walked.Add(l[idx]);
            T.SeqEq(Enumerable.Range(0, 25), walked);
            idx = l.IndexOf(24);
            walked.Clear(); walked.Add(l[idx]);
            while ((idx = l.IndexOfPreviousInOrder(idx)) != RandomizableList<int>.NotFound) walked.Add(l[idx]);
            T.SeqEq(Enumerable.Range(0, 25).Reverse(), walked);
        });

        T.Run("not randomized: ordered navigation is plain adjacency", () =>
        {
            var l = new RandomizableList<int>();
            l.SetItems(new[] { 10, 20, 30 });
            T.Eq(1, l.IndexOfNextInOrder(0));
            T.Eq(RandomizableList<int>.NotFound, l.IndexOfNextInOrder(2));
            T.Eq(RandomizableList<int>.NotFound, l.IndexOfPreviousInOrder(0));
            T.Eq(2, l.OrderedIndexFromIndex(2));
        });

        T.Run("binary search finds items in randomized lists", () =>
        {
            var l = new RandomizableList<int>(new Random(4));
            l.SetItems(Enumerable.Range(0, 40).Select(x => x * 2));
            l.Randomize();
            for (int v = 0; v < 80; v += 2) T.Eq(l.IndexOf(v), l.IndexOf(v, cmp), $"item {v}");
            T.Eq(RandomizableList<int>.NotFound, l.IndexOf(5, cmp));
            int ins;
            T.Eq(RandomizableList<int>.NotFound, l.IndexOf(5, cmp, out ins));
            T.Eq(3, ins, "5 belongs after 0,2,4");
            T.Eq(l.IndexOf(6), l.IndexOf(6, cmp, out ins));
            T.Eq(3, ins);
        });

        T.Run("insert places duplicates at the existing index and does not grow the list", () =>
        {
            var l = new RandomizableList<int>(new Random(6));
            l.SetItems(new[] { 1, 3, 5 });
            l.Randomize();
            int existing = l.IndexOf(3);
            T.Eq(existing, l.Insert(3, cmp, 0));
            T.Eq(3, l.Count);
        });

        T.Run("fuzz: random inserts/removes/shuffles keep every invariant", () =>
        {
            for (int seed = 0; seed < 60; seed++)
            {
                var rng = new Random(seed);
                var l = new RandomizableList<int>(new Random(seed + 1000));
                var model = new List<int>();            // original order, sorted ascending
                int next = 1;
                for (int i = 0; i < 12; i++) { model.Add(next); next += 2; }
                l.SetItems(model);

                for (int step = 0; step < 120; step++)
                {
                    switch (rng.Next(6))
                    {
                        case 0: l.Randomize(); break;
                        case 1: if (l.Count > 0) { int s = rng.Next(l.Count); int item = l[s]; l.Randomize(s); T.Eq(item, l[0]); } break;
                        case 2: l.Derandomize(); break;
                        case 3:
                        case 4:
                        {
                            int v = rng.Next(0, 400) * 2 + 2; // even numbers differ from the odd seeds
                            if (model.Contains(v)) break;
                            int at = rng.Next(l.Count + 1);
                            int idx = l.Insert(v, cmp, at);
                            T.Eq(v, l[idx], "inserted item at returned index");
                            if (l.IsRandomized) T.Eq(at, idx, "randomized insert honours atIndex");
                            model.Add(v); model.Sort();
                            break;
                        }
                        default:
                            if (l.Count > 0)
                            {
                                int idx = rng.Next(l.Count);
                                model.Remove(l[idx]);
                                l.RemoveAt(idx);
                            }
                            break;
                    }
                    l.Validate();
                    T.SeqEq(model, l.OrderBy(x => x), $"seed {seed} step {step}: contents");
                    for (int i = 0; i < l.Count; i++)
                    {
                        int mi = model.IndexOf(l[i]);
                        T.Eq(mi, l.OrderedIndexFromIndex(i), $"seed {seed} step {step}: ordered index");
                        T.Eq(i, l.IndexOf(l[i], cmp), $"seed {seed} step {step}: binary search");
                        int expectNext = mi + 1 < model.Count ? l.IndexOf(model[mi + 1]) : RandomizableList<int>.NotFound;
                        T.Eq(expectNext, l.IndexOfNextInOrder(i), $"seed {seed} step {step}: next in order");
                        int expectPrev = mi > 0 ? l.IndexOf(model[mi - 1]) : RandomizableList<int>.NotFound;
                        T.Eq(expectPrev, l.IndexOfPreviousInOrder(i), $"seed {seed} step {step}: prev in order");
                    }
                }
            }
        });

        T.Run("ChangeBase rewrites paths in both orders", () =>
        {
            var l = new RandomizableList<string>(new Random(8));
            l.SetItems(new[] { @"C:\old\a.jpg", @"C:\old\b.jpg", @"C:\old\c.jpg" });
            l.Randomize();
            l.ChangeBase(@"C:\old", @"D:\new");
            l.Validate();
            T.True(l.All(p => p.StartsWith(@"D:\new\")), "all rebased");
            l.Derandomize();
            T.SeqEq(new[] { @"D:\new\a.jpg", @"D:\new\b.jpg", @"D:\new\c.jpg" }, l);
        });
    }

    // ------------------------------------------------------------------------------------------
    static void ListExtensionTests()
    {
        T.Group("List extensions");
        T.Run("MoveItem", () =>
        {
            var l = new List<int> { 1, 2, 3, 4 };
            l.MoveItem(0, 3);
            T.SeqEq(new[] { 2, 3, 4, 1 }, l);
            l.MoveItem(3, 1);
            T.SeqEq(new[] { 2, 1, 3, 4 }, l);
        });
        T.Run("UpdateIndexOfItem re-sorts one item whose key changed", () =>
        {
            // sort by value, but "b"'s value has changed from 2 to 9 since it was inserted
            var values = new Dictionary<string, int> { ["a"] = 1, ["b"] = 9, ["c"] = 3, ["d"] = 4 };
            var cmp = Comparer<string>.Create((x, y) => values[x].CompareTo(values[y]));
            var l = new List<string> { "a", "b", "c", "d" };
            int newIdx = l.UpdateIndexOfItem("b", cmp, out int old);
            T.Eq(1, old); T.Eq(3, newIdx);
            T.SeqEq(new[] { "a", "c", "d", "b" }, l);
            T.Eq(-1, l.UpdateIndexOfItem("zzz", cmp, out _));
        });
        T.Run("ChangeBase on plain lists leaves non-matching paths alone", () =>
        {
            var l = new List<string> { @"C:\x\1.jpg", @"E:\y\2.jpg" };
            l.ChangeBase(@"c:\x", @"C:\z");
            T.SeqEq(new[] { @"C:\z\1.jpg", @"E:\y\2.jpg" }, l);
        });
    }

    // ------------------------------------------------------------------------------------------
    static void NaturalComparerTests()
    {
        T.Group("NaturalStringComparer");
        var c = NaturalStringComparer.Instance;
        T.Run("numbers compare by value", () =>
        {
            T.True(c.Compare("img2.jpg", "img10.jpg") < 0);
            T.True(c.Compare("img10.jpg", "img2.jpg") > 0);
            var sorted = new[] { "img10", "img1", "img2", "img20", "img3" }.OrderBy(x => x, c);
            T.SeqEq(new[] { "img1", "img2", "img3", "img10", "img20" }, sorted);
        });
        T.Run("case-insensitive", () =>
        {
            T.True(c.Compare("Apple", "banana") < 0);
            T.True(c.Compare("apple", "Banana") < 0);
        });
        T.Run("leading zeros: same value stays deterministic", () =>
        {
            T.True(c.Compare("a01", "a1") != 0, "distinct strings never compare equal");
            T.Eq(-Math.Sign(c.Compare("a01", "a1")), Math.Sign(c.Compare("a1", "a01")));
            T.True(c.Compare("a01", "a2") < 0);
            T.True(c.Compare("a002", "a10") < 0);
        });
        T.Run("prefix and digits-before-letters", () =>
        {
            T.True(c.Compare("a", "a1") < 0);
            T.True(c.Compare("1a", "a") < 0);
            T.True(c.Compare(null, "a") < 0);
            T.Eq(0, c.Compare("same", "same"));
        });
        T.Run("very long digit runs do not overflow", () =>
        {
            T.True(c.Compare("x" + new string('9', 60), "x1" + new string('0', 60)) < 0);
        });
    }

    // ------------------------------------------------------------------------------------------
    static void FileSizeTests()
    {
        T.Group("FileSizeFormatter");
        var inv = CultureInfo.InvariantCulture;
        T.Run("formats like the original", () =>
        {
            T.Eq("0 bytes", FileSizeFormatter.Format(0, inv));
            T.Eq("1023 bytes", FileSizeFormatter.Format(1023, inv));
            T.Eq("1.0 KB", FileSizeFormatter.Format(1024, inv));
            T.Eq("1.5 KB", FileSizeFormatter.Format(1536, inv));
            T.Eq("1.0 MB", FileSizeFormatter.Format(1024UL * 1024, inv));
            T.Eq("5.0 GB", FileSizeFormatter.Format(5UL * 1024 * 1024 * 1024, inv));
        });
        T.Run("terabytes are scaled correctly (original mislabelled these)", () =>
        {
            T.Eq("1.0 TB", FileSizeFormatter.Format(1UL << 40, inv));
            T.Eq("2.0 TB", FileSizeFormatter.Format(2UL << 40, inv));
            T.Eq("2048.0 TB", FileSizeFormatter.Format(2048UL << 40, inv));
        });
    }

    // ------------------------------------------------------------------------------------------
    static void BuildVersionTests()
    {
        T.Group("BuildVersion");
        const string sha = "E707B9E1234567890abcdef1234567890abcdef0";
        T.Run("splits the SDK's version+commit string", () =>
        {
            var v = BuildVersion.Parse("0.1.0+" + sha);
            T.Eq("0.1.0", v.Version);
            T.Eq(sha.ToLowerInvariant(), v.Commit);
            T.Eq("e707b9e", v.ShortCommit);
            T.Eq("0.1.0 (commit e707b9e)", v.ToString());
        });
        T.Run("plain version has no commit", () =>
        {
            var v = BuildVersion.Parse("0.2.0-beta");
            T.Eq("0.2.0-beta", v.Version);
            T.Eq(null, v.Commit);
            T.Eq("0.2.0-beta", v.ToString());
        });
        T.Run("commit is the last part of multi-part build metadata", () =>
        {
            T.Eq("abcdef1", BuildVersion.Parse("1.0.0+ci.42.abcdef1234").ShortCommit);
        });
        T.Run("non-hex or empty metadata and missing input are handled", () =>
        {
            T.Eq(null, BuildVersion.Parse("1.0.0+local").Commit);
            T.Eq(null, BuildVersion.Parse("1.0.0+").Commit);
            T.Eq("unknown", BuildVersion.Parse(null).Version);
            T.Eq("unknown", BuildVersion.Parse("  ").Version);
            T.Eq("unknown", BuildVersion.Parse("+" + sha).Version);
        });
    }

    // ------------------------------------------------------------------------------------------
    static void OrientationTests()
    {
        T.Group("ImageOrientation");

        // Where the EXIF spec says source pixel (0,0) and (W-1,0) land (T/B = top/bottom, L/R = left/right).
        // Written from the spec's table ("0th row is visual X, 0th column is visual Y"), independent of the implementation.
        var spec = new Dictionary<int, (string first, string endOfRow0)>
        {
            [1] = ("TL", "TR"), [2] = ("TR", "TL"), [3] = ("BR", "BL"), [4] = ("BL", "BR"),
            [5] = ("TL", "BL"), [6] = ("TR", "BR"), [7] = ("BR", "TR"), [8] = ("BL", "TL"),
        };

        static string Corner(int x, int y, int w, int h) => (y == 0 ? "T" : y == h - 1 ? "B" : "?") + (x == 0 ? "L" : x == w - 1 ? "R" : "?");

        T.Run("all eight EXIF values place the image corners where the spec says", () =>
        {
            const int W = 5, H = 3;
            foreach (var (value, (first, endRow)) in spec)
            {
                var o = ImageOrientation.FromExif(value);
                var (dw, dh) = o.ApplyToSize(W, H);
                var p0 = o.MapPoint(0, 0, W, H);
                var p1 = o.MapPoint(W - 1, 0, W, H);
                T.Eq(first, Corner(p0.X, p0.Y, dw, dh), $"EXIF {value}: pixel (0,0)");
                T.Eq(endRow, Corner(p1.X, p1.Y, dw, dh), $"EXIF {value}: pixel (W-1,0)");
                T.Eq(value is 5 or 6 or 7 or 8, o.SwapsAxes, $"EXIF {value}: axis swap");
            }
        });

        T.Run("mapping is a bijection onto the displayed image", () =>
        {
            const int W = 4, H = 3;
            for (int v = 1; v <= 8; v++)
            {
                var o = ImageOrientation.FromExif(v);
                var (dw, dh) = o.ApplyToSize(W, H);
                var seen = new HashSet<(int, int)>();
                for (int y = 0; y < H; y++)
                    for (int x = 0; x < W; x++)
                    {
                        var p = o.MapPoint(x, y, W, H);
                        T.True(p.X >= 0 && p.X < dw && p.Y >= 0 && p.Y < dh, $"EXIF {v}: in bounds");
                        seen.Add(p);
                    }
                T.Eq(W * H, seen.Count, $"EXIF {v}: bijective");
            }
        });

        T.Run("FromExif/ToExif round trip; unknown values become identity", () =>
        {
            for (int v = 1; v <= 8; v++) T.Eq(v, ImageOrientation.FromExif(v).ToExif());
            T.Eq(ImageOrientation.Identity, ImageOrientation.FromExif(0));
            T.Eq(ImageOrientation.Identity, ImageOrientation.FromExif(99));
        });

        T.Run("rotation normalizes and composes", () =>
        {
            T.Eq(270, new ImageOrientation(-90, false).RotationCw);
            T.Eq(0, new ImageOrientation(360, false).RotationCw);
            T.Eq(90, ImageOrientation.Identity.RotatedBy(450).RotationCw);
            var o = ImageOrientation.Identity;
            for (int i = 0; i < 4; i++) o = o.RotatedBy(90);
            T.Eq(ImageOrientation.Identity, o);
            T.Eq(6, ImageOrientation.Identity.RotatedBy(90).ToExif());
            T.Eq(8, ImageOrientation.Identity.RotatedBy(-90).ToExif());
        });

        T.Run("flipping the displayed image mirrors the final picture (checked pixel-by-pixel)", () =>
        {
            const int W = 4, H = 3;
            for (int v = 1; v <= 8; v++)
            {
                var o = ImageOrientation.FromExif(v);
                var f = o.FlippedDisplay();
                var (dw, dh) = o.ApplyToSize(W, H);
                for (int y = 0; y < H; y++)
                    for (int x = 0; x < W; x++)
                    {
                        var shown = o.MapPoint(x, y, W, H);
                        var mirrored = (X: dw - 1 - shown.X, Y: shown.Y);
                        T.Eq(mirrored, f.MapPoint(x, y, W, H), $"EXIF {v} pixel ({x},{y})");
                    }
                T.Eq(o, f.FlippedDisplay(), $"EXIF {v}: flip twice is identity");
            }
        });

        T.Run("rotating the displayed image rotates the final picture (checked pixel-by-pixel)", () =>
        {
            const int W = 4, H = 3;
            for (int v = 1; v <= 8; v++)
            {
                var o = ImageOrientation.FromExif(v);
                var r = o.RotatedBy(90);
                var (dw, dh) = o.ApplyToSize(W, H);
                for (int y = 0; y < H; y++)
                    for (int x = 0; x < W; x++)
                    {
                        var s = o.MapPoint(x, y, W, H);
                        var rotated = (X: dh - 1 - s.Y, Y: s.X); // 90° clockwise of the displayed image
                        T.Eq(rotated, r.MapPoint(x, y, W, H), $"EXIF {v} pixel ({x},{y})");
                    }
            }
        });
    }

    // ------------------------------------------------------------------------------------------
    static void LruTests()
    {
        T.Group("LruCache");
        T.Run("evicts the least recently used entry", () =>
        {
            var evicted = new List<string>();
            var c = new LruCache<string, int>(3, (k, _) => evicted.Add(k));
            c.Set("a", 1); c.Set("b", 2); c.Set("c", 3);
            T.True(c.TryGet("a", out _));          // a is now most recent
            c.Set("d", 4);                         // evicts b
            T.SeqEq(new[] { "b" }, evicted);
            T.True(!c.ContainsKey("b")); T.True(c.ContainsKey("a"));
            T.Eq(3, c.Count);
        });
        T.Run("set overwrites, remove and clear work, capacity can shrink", () =>
        {
            var c = new LruCache<int, string>(4);
            for (int i = 0; i < 4; i++) c.Set(i, "v" + i);
            c.Set(2, "new");
            T.True(c.TryGet(2, out var v) && v == "new");
            T.Eq(4, c.Count);
            T.True(c.Remove(0)); T.True(!c.Remove(0));
            c.Capacity = 1;
            T.Eq(1, c.Count);
            T.True(c.ContainsKey(2), "most recently used survives");
            c.Clear(); T.Eq(0, c.Count);
            T.Throws<ArgumentOutOfRangeException>(() => new LruCache<int, int>(0));
        });
    }

    // ------------------------------------------------------------------------------------------
    static void FileTypeTests()
    {
        T.Group("ImageFileTypes");
        T.Run("built-in formats are recognised case-insensitively", () =>
        {
            var t = new ImageFileTypes();
            foreach (var f in new[] { "a.jpg", "A.JPEG", "b.png", "c.TIF", "d.tiff", "e.bmp", "f.gif" })
                T.True(t.IsSupportedFile(f), f);
            foreach (var f in new[] { "a.txt", "noext", "b.heic", "c.webp", "d.docx" })
                T.True(!t.IsSupportedFile(f), f);
        });
        T.Run("register / disable / ignored list", () =>
        {
            var t = new ImageFileTypes();
            T.True(!t.IsSupportedFile("x.jp2"));
            t.Register("jp2");
            T.True(t.IsSupportedFile("x.JP2"));
            t.SetEnabled(".png", false);
            T.True(!t.IsSupportedFile("x.png"));
            T.SeqEq(new[] { ".png" }, t.DisabledExtensions);
            t.ApplyIgnored(new[] { "gif", ".bmp" });
            T.True(!t.IsSupportedFile("a.gif") && !t.IsSupportedFile("a.bmp"));
            t.SetEnabled(".png", true);
            T.True(t.IsSupportedFile("x.png"));
        });
        T.Run("IsJpeg", () =>
        {
            T.True(ImageFileTypes.IsJpeg("a.JPG")); T.True(ImageFileTypes.IsJpeg("a.jpeg")); T.True(!ImageFileTypes.IsJpeg("a.png"));
        });
        T.Run("IsGif", () =>
        {
            T.True(ImageFileTypes.IsGif("a.gif")); T.True(ImageFileTypes.IsGif("pics/B.GIF")); T.True(!ImageFileTypes.IsGif("gif.png"));
        });
    }


    // ------------------------------------------------------------------------------------------
    static void SettingsTests()
    {
        T.Group("Settings");
        T.Run("defaults match the original app's registered defaults", () =>
        {
            var s = new AppSettings();
            T.Eq(120.0, s.ThumbCellWidth); T.Eq(5.5, s.SlideshowAutoAdvanceSeconds); T.Eq(100, s.MaxThumbsToLoad);
            T.True(s.ShowFilenames && s.AutoRotateByOrientationTag && s.RerandomizeOnLoop && s.OpenFilesDoSlideshow);
            T.True(!s.SlideshowLoop && !s.SlideshowRandom && !s.SlideshowAutoAdvance);
            T.Eq(new SortOrder(SortField.Name, false), s.SortOrder);
            T.Eq(SubfolderMode.ResetOnNewTree, s.IncludeSubfoldersMode, "the behaviour before this setting existed");
            T.True(!s.IncludeSubfolders && !s.InfoPanelMoreExif);
            T.True(s.SlideshowPlayGifs && s.SlideshowGifsFinish, "animated GIFs play, and finish before auto-advance");
            T.Eq("#FFFFFF", s.TransparentImageBackgroundColor, "white behind transparent pictures");
            T.Eq(280.0, s.InfoPanelWidth);
        });
        T.Run("sort order legacy encoding round-trips", () =>
        {
            for (int v = -7; v <= 7; v++)
            {
                if (v == 0) continue;
                T.Eq(v, SortOrder.FromLegacy(v).ToLegacy());
            }
            T.Eq(new SortOrder(SortField.ExifDate, true), SortOrder.FromLegacy(-3));
            T.Eq(new SortOrder(SortField.Name, false), SortOrder.FromLegacy(99));
        });
        T.Run("save and load round trip; no temp file left behind", () =>
        {
            using var d = new TempDir();
            string p = d.File("sub/settings.json");
            var s = new AppSettings { ThumbCellWidth = 200, LastFolderPath = @"C:\Pics", SlideshowLoop = true,
                                      IncludeSubfoldersMode = SubfolderMode.Always, InfoPanelMoreExif = true,
                                      SlideshowPlayGifs = false, SlideshowGifsFinish = false };
            s.SortOrder = new SortOrder(SortField.Size, true);
            s.IgnoredFileTypes.Add(".bmp");
            T.True(SettingsStore.Save(s, p));
            T.True(!File.Exists(p + ".tmp"));
            var l = SettingsStore.Load(p);
            T.Eq(200.0, l.ThumbCellWidth); T.Eq(@"C:\Pics", l.LastFolderPath); T.True(l.SlideshowLoop);
            T.Eq(new SortOrder(SortField.Size, true), l.SortOrder);
            T.SeqEq(new[] { ".bmp" }, l.IgnoredFileTypes);
            T.Eq(SubfolderMode.Always, l.IncludeSubfoldersMode); T.True(l.InfoPanelMoreExif);
            T.True(!l.SlideshowPlayGifs && !l.SlideshowGifsFinish);
        });
        T.Run("missing, corrupt or hostile files fall back to sane values", () =>
        {
            using var d = new TempDir();
            T.Eq(120.0, SettingsStore.Load(d.File("nope.json")).ThumbCellWidth);
            File.WriteAllText(d.File("bad.json"), "{ this is not json");
            T.Eq(120.0, SettingsStore.Load(d.File("bad.json")).ThumbCellWidth);
            File.WriteAllText(d.File("null.json"), "null");
            T.Eq(120.0, SettingsStore.Load(d.File("null.json")).ThumbCellWidth);
            File.WriteAllText(d.File("wild.json"), "{\"ThumbCellWidth\": 99999, \"SlideshowAutoAdvanceSeconds\": -3, \"SortBy\": 42, \"MaxThumbsToLoad\": 0, \"IgnoredFileTypes\": null, \"IncludeSubfoldersMode\": 7, \"TransparentImageBackgroundColor\": null, \"InfoPanelWidth\": 5}");
            var s = SettingsStore.Load(d.File("wild.json"));
            T.Eq(AppSettings.MaxThumbCellWidth, s.ThumbCellWidth);
            T.Eq(5.5, s.SlideshowAutoAdvanceSeconds);
            T.Eq(1, s.SortBy); T.Eq(1, s.MaxThumbsToLoad); T.True(s.IgnoredFileTypes != null);
            T.Eq(SubfolderMode.ResetOnNewTree, s.IncludeSubfoldersMode, "unknown mode");
            T.Eq("#FFFFFF", s.TransparentImageBackgroundColor, "missing colour");
            T.Eq(AppSettings.MinInfoPanelWidth, s.InfoPanelWidth, "info panel too narrow");
        });
    }
}
