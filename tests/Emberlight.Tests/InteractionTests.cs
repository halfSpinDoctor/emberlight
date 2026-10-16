using System.Globalization;
using Emberlight.Core;

namespace Emberlight.Tests;

/// <summary>
/// Logic behind the windows' input and commands: thumbnail scheduling, viewer input maths, slide preloading,
/// grid keyboard movement, rotate/delete outcomes and the Preferences checks.
/// </summary>
internal static class InteractionTests
{
    /// <summary>Stands in for the WPF grid's ThumbnailItem.</summary>
    sealed class Slot(string path) : IThumbnailSlot
    {
        public string Path { get; } = path;
        public bool IsLoaded { get; set; }
        public bool IsQueued { get; set; }
        public int WantedStamp { get; set; } = -1;
        public int Generation { get; set; }
        public string? Shown;
        public int ShowCount;
        public override string ToString() => Path;
    }

    /// <summary>A scheduler with no worker threads: decodes run in ProcessNext, deliveries wait in Posted.</summary>
    sealed class Harness
    {
        public readonly List<string> Decoded = new();
        public readonly List<Action> Posted = new();
        public Func<string, bool> Current = _ => true;
        public Func<string, int, string?> Decode = (p, size) => $"{p}@{size}";
        public readonly ThumbnailScheduler<Slot, string> Scheduler;

        public Harness(int size = 160)
        {
            Scheduler = new ThumbnailScheduler<Slot, string>(
                (p, s) => { Decoded.Add(p); return Decode(p, s); },
                (p, img) => Current(p),
                (slot, img) => { slot.IsLoaded = true; slot.IsQueued = false; slot.Shown = img; slot.ShowCount++; },
                a => Posted.Add(a), size);
        }

        /// <summary>Asks for these cells as the newest visible batch (as the browser does on every scroll).</summary>
        public void Ask(params Slot[] slots) => Scheduler.RequestRange(slots, 0, slots.Length);

        /// <summary>Decodes everything waiting, then delivers on the "UI thread".</summary>
        public void Drain()
        {
            while (Scheduler.ProcessNext()) { }
            RunPosted();
        }

        public void RunPosted()
        {
            var actions = Posted.ToList();
            Posted.Clear();
            foreach (var a in actions) a();
        }
    }

    static List<Slot> Slots(int n) => Enumerable.Range(0, n).Select(i => new Slot($"img{i}.jpg")).ToList();

    public static void RunAll()
    {
        SchedulerTests();
        ReleaseTests();
        GridMoveTests();
        ViewerInputTests();
        PreloadTests();
        RotateTests();
        DeleteTests();
        RenameTests();
        PreferencesTests();
        ColorTextTests();
    }

    static void SchedulerTests()
    {
        T.Group("ThumbnailScheduler");

        T.Run("a decoded thumbnail is shown, cached, and served from the cache next time", () =>
        {
            var h = new Harness();
            var a = new Slot("a.jpg");
            h.Ask(a);
            T.True(a.IsQueued && !a.IsLoaded, "queued, not shown yet");
            T.Eq(0, h.Decoded.Count, "nothing decoded on the UI thread");
            h.Drain();
            T.Eq("a.jpg@160", a.Shown);
            T.True(!a.IsQueued, "no longer queued");

            var again = new Slot(OperatingSystem.IsWindows() ? "A.JPG" : "a.jpg");
            h.Ask(again);
            T.Eq("a.jpg@160", again.Shown, "shown straight from the cache (any case on Windows)");
            T.True(!again.IsQueued);
            T.Eq(1, h.Decoded.Count, "no second decode");
        });

        T.Run("a cached thumbnail that no longer matches its file is decoded again", () =>
        {
            var h = new Harness();
            h.Ask(new Slot("a.jpg"));
            h.Drain();
            h.Current = _ => false;
            var b = new Slot("a.jpg");
            h.Ask(b);
            T.True(b.IsQueued && b.Shown == null, "queued instead of shown");
            h.Drain();
            T.Eq(2, h.Decoded.Count);
            T.Eq("a.jpg@160", b.Shown);
        });

        T.Run("loaded or already queued items aren't queued twice", () =>
        {
            var h = new Harness();
            var a = new Slot("a.jpg");
            h.Ask(a);
            h.Ask(a);
            T.Eq(1, h.Scheduler.PendingCount);
            h.Drain();
            h.Ask(a);
            T.Eq(0, h.Scheduler.PendingCount, "loaded");
            T.Eq(1, h.Decoded.Count);
            T.Eq(1, a.ShowCount);
        });

        T.Run("newest requests are served first; a range is decoded top to bottom", () =>
        {
            var h = new Harness();
            var items = Slots(8);
            h.Scheduler.RequestRange(items, 2, 6);
            h.Drain();
            T.SeqEq(new[] { "img2.jpg", "img3.jpg", "img4.jpg", "img5.jpg" }, h.Decoded);

            h.Decoded.Clear();
            int stamp = h.Scheduler.NextStamp();
            foreach (var s in new[] { items[0], items[1], items[7] }) { s.WantedStamp = stamp; h.Scheduler.Request(s); }
            h.Drain();
            T.SeqEq(new[] { "img7.jpg", "img1.jpg", "img0.jpg" }, h.Decoded, "last requested first");
        });

        T.Run("requests for cells scrolled away are dropped without decoding and can be requested again", () =>
        {
            var h = new Harness();
            var items = Slots(10);
            h.Scheduler.RequestRange(items, 0, 4);
            h.Scheduler.RequestRange(items, 5, 8);           // scrolled on before any decode ran
            h.Drain();
            T.SeqEq(new[] { "img5.jpg", "img6.jpg", "img7.jpg" }, h.Decoded);
            for (int i = 0; i < 4; i++) T.True(!items[i].IsQueued && !items[i].IsLoaded, $"item {i} dropped");
            h.Scheduler.RequestRange(items, 0, 2);           // scrolled back
            h.Drain();
            T.True(items[0].IsLoaded && items[1].IsLoaded && !items[2].IsLoaded);
        });

        T.Run("an item still in the newest range keeps its place when the range moves", () =>
        {
            var h = new Harness();
            var items = Slots(10);
            h.Scheduler.RequestRange(items, 0, 4);
            h.Scheduler.RequestRange(items, 2, 6);           // 2 and 3 are still wanted (already queued)
            h.Drain();
            T.SeqEq(new[] { "img4.jpg", "img5.jpg", "img2.jpg", "img3.jpg" }, h.Decoded);
        });

        T.Run("reset forgets the queue; results finished before a reset are thrown away", () =>
        {
            var h = new Harness();
            var items = Slots(3);
            h.Scheduler.RequestRange(items, 0, 3);
            T.True(h.Scheduler.ProcessNext(), "one decoded");
            h.Scheduler.Reset();
            T.Eq(0, h.Scheduler.PendingCount, "queue cleared");
            h.RunPosted();
            T.True(items[0].Shown == null && !items[0].IsQueued, "stale result not shown, item free to ask again");
            h.Ask(items[0]);
            h.Drain();
            T.Eq(2, h.Decoded.Count(p => p == "img0.jpg"), "decoded again, not taken from the cache");
            T.True(items[0].IsLoaded);
        });

        T.Run("items queued before a reset are skipped when a worker reaches them", () =>
        {
            var h = new Harness();
            var a = new Slot("a.jpg");
            h.Ask(a);
            a.Generation = -5;                               // as if queued in an older generation
            h.Drain();
            T.Eq(0, h.Decoded.Count);
        });

        T.Run("a new decode size clears the cache and discards results at the old size", () =>
        {
            var h = new Harness(160);
            T.True(!h.Scheduler.Configure(160), "same size: no change");
            var a = new Slot("a.jpg");
            h.Ask(a);
            h.Drain();
            var b = new Slot("b.jpg");
            h.Ask(b);
            h.Scheduler.ProcessNext();                       // decoded at 160...
            T.True(h.Scheduler.Configure(320), "changed");  // ...but the size changed before delivery
            T.Eq(320, h.Scheduler.DecodeSize);
            h.RunPosted();
            T.True(b.Shown == null && !b.IsQueued, "result at the old size dropped");

            var a2 = new Slot("a.jpg");
            h.Ask(a2);
            T.True(a2.Shown == null, "old cache gone");
            h.Drain();
            T.Eq("a.jpg@320", a2.Shown);
        });

        T.Run("invalidate forgets one cached thumbnail", () =>
        {
            var h = new Harness();
            h.Ask(new Slot("a.jpg"), new Slot("b.jpg"));
            h.Drain();
            h.Scheduler.Invalidate(OperatingSystem.IsWindows() ? "A.jpg" : "a.jpg");
            var a = new Slot("a.jpg"); var b = new Slot("b.jpg");
            h.Ask(a, b);
            T.True(a.IsQueued && b.IsLoaded, "a decoded again, b still cached");
        });

        T.Run("a file that can't be decoded shows as broken and isn't cached", () =>
        {
            var h = new Harness { Decode = (p, s) => null };
            var a = new Slot("a.jpg");
            h.Ask(a);
            h.Drain();
            T.True(a.IsLoaded && a.Shown == null && a.ShowCount == 1, "shown as broken");
            h.Ask(new Slot("a.jpg"));
            h.Drain();
            T.Eq(2, h.Decoded.Count, "tried again for a new cell");
        });

        T.Run("worker threads decode every visible item", () =>
        {
            var shown = new System.Collections.Concurrent.ConcurrentDictionary<string, string?>();
            using var s = new ThumbnailScheduler<Slot, string>(
                (p, size) => p.ToUpperInvariant(), (p, img) => true,
                (slot, img) => { slot.IsLoaded = true; shown[slot.Path] = img; },
                a => a(), 100, workerCount: 3);
            var items = Slots(50);
            s.RequestRange(items, 0, 50);
            var until = DateTime.UtcNow.AddSeconds(10);
            while (shown.Count < 50 && DateTime.UtcNow < until) Thread.Sleep(10);
            T.Eq(50, shown.Count);
            T.True(items.All(i => shown[i.Path] == i.Path.ToUpperInvariant() && !i.IsQueued));
        });

        T.Run("default worker count is half the processors, 1 to 4", () =>
        {
            int n = ThumbnailScheduler<Slot, string>.DefaultWorkerCount;
            T.Eq(Math.Clamp(Environment.ProcessorCount / 2, 1, 4), n);
        });
    }

    static void ReleaseTests()
    {
        T.Group("ThumbnailGridMath (releasing thumbnails)");

        T.Run("released items are the old window minus the new one", () =>
        {
            T.SeqEq(new[] { 0, 1, 2 }, ThumbnailGridMath.ReleasedIndices(100, 0, 10, 3, 20));
            T.SeqEq(new[] { 15, 16, 17, 18, 19 }, ThumbnailGridMath.ReleasedIndices(100, 0, 20, 0, 15));
            T.SeqEq(new[] { 0, 1, 8, 9 }, ThumbnailGridMath.ReleasedIndices(100, 0, 10, 2, 8));
            T.SeqEq(Enumerable.Range(0, 10), ThumbnailGridMath.ReleasedIndices(100, 0, 10, 50, 60), "jumped far");
            T.SeqEq(Array.Empty<int>(), ThumbnailGridMath.ReleasedIndices(100, 10, 20, 0, 50), "new window covers the old");
            T.SeqEq(Array.Empty<int>(), ThumbnailGridMath.ReleasedIndices(100, 0, 0, 0, 10), "nothing kept before");
        });

        T.Run("a list that shrank only visits items that still exist", () =>
        {
            T.SeqEq(new[] { 0, 1, 2, 3, 4 }, ThumbnailGridMath.ReleasedIndices(5, 0, 50, 5, 5));
            T.SeqEq(Array.Empty<int>(), ThumbnailGridMath.ReleasedIndices(5, 10, 50, 0, 0), "old window entirely past the end");
            T.SeqEq(Array.Empty<int>(), ThumbnailGridMath.ReleasedIndices(0, 0, 50, 0, 0));
        });

        T.Run("retain margin is four screens of items", () =>
        {
            T.Eq(5 * 3 * 4, ThumbnailGridMath.RetainMargin(500, 300, 100, 100));
            T.Eq(1 * 1 * 4, ThumbnailGridMath.RetainMargin(50, 50, 100, 100), "at least one column and row");
        });

        T.Run("fuzz: scrolling around never leaves a decoded thumbnail outside the retained window", () =>
        {
            var rng = new Random(7);
            for (int seed = 0; seed < 200; seed++)
            {
                int count = rng.Next(0, 400);
                var loaded = new HashSet<int>();
                int keepFirst = 0, keepEnd = 0;
                for (int step = 0; step < 40; step++)
                {
                    if (rng.Next(10) == 0) count = Math.Max(0, count - rng.Next(0, 50));   // files deleted
                    loaded.RemoveWhere(i => i >= count);
                    int first = count == 0 ? 0 : rng.Next(0, count);
                    int end = Math.Min(count, first + rng.Next(0, 30));
                    int margin = rng.Next(0, 60);
                    foreach (int i in Enumerable.Range(first, end - first)) loaded.Add(i);
                    var (kf, ke) = ThumbnailGridMath.RetainedRange(count, first, end, margin);
                    foreach (int i in ThumbnailGridMath.ReleasedIndices(count, keepFirst, keepEnd, kf, ke))
                    {
                        T.True(i >= 0 && i < count && (i < kf || i >= ke), $"released {i} is outside the new window");
                        loaded.Remove(i);
                    }
                    keepFirst = kf; keepEnd = ke;
                    T.True(loaded.All(i => i >= kf && i < ke), $"seed {seed} step {step}: all decoded items inside [{kf}, {ke})");
                }
            }
        });
    }

    static void GridMoveTests()
    {
        T.Group("ThumbnailGridMath (keyboard movement)");

        // 23 items in 5 columns; 2 whole rows visible, so a page is 10 items
        static int Move(GridMove m, int from) => ThumbnailGridMath.MoveTarget(m, from, 23, 5, 2);

        T.Run("arrows move by one item or one row", () =>
        {
            T.Eq(6, Move(GridMove.Left, 7)); T.Eq(8, Move(GridMove.Right, 7));
            T.Eq(2, Move(GridMove.Up, 7)); T.Eq(12, Move(GridMove.Down, 7));
            T.Eq(5, Move(GridMove.Right, 4), "right from a row's end wraps to the next row");
            T.Eq(4, Move(GridMove.Left, 5), "and left wraps back");
        });

        T.Run("page keys move by the visible rows; Home and End go to the ends", () =>
        {
            T.Eq(17, Move(GridMove.PageDown, 7)); T.Eq(2, Move(GridMove.PageUp, 12));
            T.Eq(0, Move(GridMove.Home, 13)); T.Eq(22, Move(GridMove.End, 3));
        });

        T.Run("moves stop at the ends of the list", () =>
        {
            T.Eq(0, Move(GridMove.Left, 0)); T.Eq(0, Move(GridMove.Up, 3)); T.Eq(0, Move(GridMove.PageUp, 9));
            T.Eq(22, Move(GridMove.Right, 22)); T.Eq(22, Move(GridMove.Down, 19), "down into the short last row");
            T.Eq(22, Move(GridMove.PageDown, 15));
        });

        T.Run("nothing selected counts as the first item; an empty list has no target", () =>
        {
            T.Eq(1, Move(GridMove.Right, -1)); T.Eq(5, Move(GridMove.Down, -1)); T.Eq(0, Move(GridMove.Left, -1));
            T.Eq(-1, ThumbnailGridMath.MoveTarget(GridMove.Home, 0, 0, 5, 2));
            T.Eq(3, ThumbnailGridMath.MoveTarget(GridMove.Down, 2, 10, 0, 0), "zero columns or rows count as one");
            T.Eq(2, ThumbnailGridMath.MoveTarget(GridMove.PageDown, 0, 10, 2, 0), "a page is at least one row");
        });

        T.Run("fuzz: every move lands in the list, and down/up stay in the same column when they can", () =>
        {
            var rng = new Random(3);
            for (int n = 0; n < 3000; n++)
            {
                int count = rng.Next(1, 200), cols = rng.Next(1, 12), rows = rng.Next(1, 8), from = rng.Next(-1, count);
                foreach (GridMove m in Enum.GetValues<GridMove>())
                {
                    int t = ThumbnailGridMath.MoveTarget(m, from, count, cols, rows);
                    T.True(t >= 0 && t < count, $"{m} from {from} of {count} -> {t}");
                }
                int at = Math.Max(0, from);
                int down = ThumbnailGridMath.MoveTarget(GridMove.Down, from, count, cols, rows);
                if (at + cols < count) T.Eq(at % cols, down % cols, "same column");
                if (at - cols >= 0) T.Eq(at - cols, ThumbnailGridMath.MoveTarget(GridMove.Up, from, count, cols, rows));
            }
        });
    }

    static void ViewerInputTests()
    {
        T.Group("ViewerInput");

        T.Run("Ctrl+wheel zooms 25% in or 20% out per event, so one notch each way is a round trip", () =>
        {
            T.Eq(0.25, ViewerInput.WheelZoomStep(120)); T.Eq(0.25, ViewerInput.WheelZoomStep(1));
            T.Eq(-0.2, ViewerInput.WheelZoomStep(-120)); T.Eq(-0.2, ViewerInput.WheelZoomStep(0));
            T.True(Math.Abs((1 + ViewerInput.WheelZoomStep(120)) * (1 + ViewerInput.WheelZoomStep(-120)) - 1) < 1e-12);
        });

        T.Run("the wheel pans 128 px per notch, scaled to device pixels; Shift pans sideways", () =>
        {
            T.Eq((0.0, 128.0), ViewerInput.WheelPan(120, 1, false));
            T.Eq((0.0, -256.0), ViewerInput.WheelPan(-120, 2, false));
            T.Eq((192.0, 0.0), ViewerInput.WheelPan(120, 1.5, true));
            T.Eq((0.0, 64.0), ViewerInput.WheelPan(60, 1, false), "half a notch (touchpad)");
        });

        T.Run("a press becomes a drag once it moves more than 3 px on either axis", () =>
        {
            T.True(!ViewerInput.IsDrag(10, 10, 13, 7), "exactly 3 is still a click");
            T.True(ViewerInput.IsDrag(10, 10, 13.01, 10));
            T.True(ViewerInput.IsDrag(10, 10, 10, 6.9));
            T.True(!ViewerInput.IsDrag(10, 10, 12, 12), "diagonal within the box");
            T.True(ViewerInput.IsNegligibleMove(0.5, -0.4)); T.True(!ViewerInput.IsNegligibleMove(0.5, -0.5));
        });

        T.Run("pixels are drawn sharp from 3x zoom", () =>
        {
            T.True(ViewerInput.UseNearestNeighbour(3)); T.True(ViewerInput.UseNearestNeighbour(12));
            T.True(!ViewerInput.UseNearestNeighbour(2.999)); T.True(!ViewerInput.UseNearestNeighbour(1));
        });

        T.Run("keypad keys pan half a view towards their side; 5 and non-digits do nothing", () =>
        {
            var expect = new Dictionary<int, (double, double)>
            {
                [1] = (50, -30), [2] = (0, -30), [3] = (-50, -30),
                [4] = (50, 0), [6] = (-50, 0),
                [7] = (50, 30), [8] = (0, 30), [9] = (-50, 30),
            };
            foreach (var (digit, d) in expect) T.Eq<(double, double)?>(d, ViewerInput.KeypadPan(digit, 100, 60), $"key {digit}");
            foreach (int d in new[] { 5, 0, 10, -1 }) T.True(ViewerInput.KeypadPan(d, 100, 60) == null, $"key {d}");
        });

        T.Run("keypad pans move the viewport the right way", () =>
        {
            var v = new ImageViewport();
            v.SetView(100, 100);
            v.SetImage(400, 400);
            v.ZoomActualSize();
            var (dx, dy) = ViewerInput.KeypadPan(7, v.ViewWidth, v.ViewHeight)!.Value;   // top-left
            v.PanBy(dx, dy);
            T.True(v.CenterX < 200 && v.CenterY < 200, "shows more of the top-left");
        });
    }

    static void PreloadTests()
    {
        T.Group("Slide preloading");

        string[] s = { "a", "b", "c", "d", "e" };

        T.Run("the next two slides, then the previous one", () =>
        {
            T.SeqEq(new[] { "c", "d", "a" }, SlidePreload.Candidates(s, 1));
            T.SeqEq(new[] { "b", "c" }, SlidePreload.Candidates(s, 0), "at the start");
            T.SeqEq(new[] { "e", "c" }, SlidePreload.Candidates(s, 3));
            T.SeqEq(new[] { "d" }, SlidePreload.Candidates(s, 4), "at the end");
            T.SeqEq(Array.Empty<string>(), SlidePreload.Candidates(new[] { "a" }, 0));
            T.SeqEq(Array.Empty<string>(), SlidePreload.Candidates(Array.Empty<string>(), 0));
        });

        T.Run("works on a shuffled slideshow's list order", () =>
        {
            var list = new RandomizableList<string>();
            list.SetItems(s);
            list.Randomize(2);
            var c = SlidePreload.Candidates(list, 2);
            T.SeqEq(new[] { list[3], list[4], list[1] }, c);
        });

        T.Run("no preloading within 200 ms of a navigation", () =>
        {
            T.True(!SlidePreload.IsSettled(1199, 1000));
            T.True(SlidePreload.IsSettled(1200, 1000));
            T.True(SlidePreload.IsSettled(5000, 0));
        });
    }

    static void RotateTests()
    {
        T.Group("OrientationEditor");

        static ExifData? Exif(int orientation, bool include = true)
            => ExifReader.Read(new MemoryStream(new ExifTestBuilder(false).BuildSample(orientation, include)));

        T.Run("new orientation: turned from the current one, or upright for Reset", () =>
        {
            T.Eq(3, OrientationEditor.NewOrientation(Exif(6), 90), "90 then 90 more is 180");
            T.Eq(1, OrientationEditor.NewOrientation(Exif(6), -90));
            T.Eq(6, OrientationEditor.NewOrientation(Exif(1), 90));
            T.Eq(8, OrientationEditor.NewOrientation(Exif(1), -90));
            T.Eq(1, OrientationEditor.NewOrientation(Exif(8), null));
            T.Eq<int?>(null, OrientationEditor.NewOrientation(Exif(1, include: false), 90), "no tag");
            T.Eq<int?>(null, OrientationEditor.NewOrientation(null, 90), "no EXIF");
        });

        T.Run("rotating files updates the tagged ones and skips the rest", () =>
        {
            using var dir = new TempDir();
            string tagged = dir.Write("a.jpg", ExifTestBuilder.WrapInJpeg(new ExifTestBuilder(false).BuildSample(orientation: 6)));
            string untagged = dir.Write("b.jpg", ExifTestBuilder.WrapInJpeg(new ExifTestBuilder(false).BuildSample(includeOrientation: false)));
            string png = dir.Write("c.png", new byte[] { 1, 2, 3 });
            string missing = dir.File("gone.jpg");
            var when = new DateTime(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(tagged, when);

            var r = OrientationEditor.Rotate(new[] { tagged, untagged, png, missing }, 90, preserveModifiedTime: true);
            T.SeqEq(new[] { tagged }, r.Updated);
            T.Eq(3, r.Skipped);
            T.Eq((ushort)3, ExifReader.GetOrientation(tagged));
            T.Eq(when, File.GetLastWriteTimeUtc(tagged), "modified time kept");

            r = OrientationEditor.Rotate(new[] { tagged }, null, preserveModifiedTime: false);
            T.Eq(1, r.Updated.Count);
            T.Eq((ushort)1, ExifReader.GetOrientation(tagged), "reset to upright");
        });

        T.Run("summary text", () =>
        {
            T.Eq("Nothing selected", new RotateResult(Array.Empty<string>(), 0).Summary);
            T.Eq("Updated 2 file(s)", new RotateResult(new[] { "a", "b" }, 0).Summary);
            T.Eq("Updated 0 file(s); 3 skipped (no EXIF orientation tag to change)", new RotateResult(Array.Empty<string>(), 3).Summary);
            T.Eq("Updated 1 file(s); 1 skipped (no EXIF orientation tag to change)", new RotateResult(new[] { "a" }, 1).Summary);
        });
    }

    sealed class FakeBin(params string[] refuse) : IRecycleBin
    {
        public readonly List<string> Asked = new();
        public bool TryDelete(string path, out string? error)
        {
            Asked.Add(path);
            error = refuse.Contains(path) ? $"{path} is locked" : null;
            return error == null;
        }
    }

    static void DeleteTests()
    {
        T.Group("FileDeletion");

        T.Run("every delete asks first, naming the file when there is one", () =>
        {
            string dir = Path.Combine(Path.GetTempPath(), "phx-del");
            T.Eq("Are you sure you want to send \"beach.jpg\" to the Recycle Bin?",
                FileDeletion.ConfirmationText([Path.Combine(dir, "beach.jpg")]));
            T.Eq("Are you sure you want to send these 3 files to the Recycle Bin?",
                FileDeletion.ConfirmationText([Path.Combine(dir, "a.jpg"), Path.Combine(dir, "b.jpg"), Path.Combine(dir, "c.jpg")]));
            T.Eq("Are you sure you want to send these 2 files to the Recycle Bin?",
                FileDeletion.ConfirmationText([Path.Combine(dir, "a.jpg"), Path.Combine(dir, "b.jpg")]));
            T.Eq(null, FileDeletion.ConfirmationText([]), "nothing to delete, nothing to ask");
        });

        T.Run("deletes each file, reports each one that went, and keeps the first error", () =>
        {
            var bin = new FakeBin("b", "d");
            var removed = new List<string>();
            var r = FileDeletion.Delete(new[] { "a", "b", "c", "d" }, bin, removed.Add);
            T.SeqEq(new[] { "a", "b", "c", "d" }, bin.Asked, "every file tried, in order");
            T.SeqEq(new[] { "a", "c" }, removed);
            T.SeqEq(new[] { "a", "c" }, r.Deleted);
            T.Eq(2, r.Failed);
            T.Eq("2 file(s) couldn't be deleted: b is locked", r.FailureText);
            T.Eq<string?>(null, FileDeletion.Delete(new[] { "x" }, new FakeBin()).FailureText, "no failures, no text");
        });

        T.Run("afterwards the item at the old position is selected, or the last one", () =>
        {
            T.Eq<int?>(3, FileDeletion.SelectionAfter(3, 10), "the next file moved up into the old place");
            T.Eq<int?>(4, FileDeletion.SelectionAfter(7, 5), "deleted at the end: the new last item");
            T.Eq<int?>(0, FileDeletion.SelectionAfter(-1, 5));
            T.Eq<int?>(null, FileDeletion.SelectionAfter(0, 0), "nothing left");
        });
    }

    static void RenameTests()
    {
        T.Group("FileRename");

        T.Run("the dialog first selects the name without its extension", () =>
        {
            T.Eq((0, 5), FileRename.StemSelection("beach.jpg"));
            T.Eq((0, 9), FileRename.StemSelection("beach.old.jpg"), "only the last extension is kept");
            T.Eq((0, 5), FileRename.StemSelection("beach"), "no extension: the whole name");
            T.Eq((0, 7), FileRename.StemSelection(".hidden.jpg"));
            T.Eq((0, 4), FileRename.StemSelection(".jpg"), "a name that starts with its only dot: the whole name");
        });

        T.Run("names Windows won't accept are explained, good ones pass", () =>
        {
            using var d = new TempDir();
            string a = d.Write("a.jpg", new byte[1]);
            T.Eq(null, FileRename.Problem(a, "b.jpg"));
            T.Eq(null, FileRename.Problem(a, "  b.jpg  "), "spaces at the ends are dropped");
            T.Eq(null, FileRename.Problem(a, "a.jpg"), "unchanged is not a problem");
            T.Eq(null, FileRename.Problem(a, "A.JPG"), "a change of case is allowed");
            T.Eq(null, FileRename.Problem(a, "console.jpg"), "only exact device names are reserved");
            T.Eq("Type a name for the file.", FileRename.Problem(a, "   "));
            foreach (char c in FileRename.InvalidCharacters + "\t")
                T.True(FileRename.Problem(a, $"x{c}y.jpg")?.StartsWith("A file name can't contain") == true, $"char {(int)c}");
            T.Eq("A file name can't end with a dot.", FileRename.Problem(a, "b."));
            T.True(FileRename.Problem(a, new string('x', 252) + ".jpg") != null, "256 characters is too long");
            T.Eq(null, FileRename.Problem(a, new string('x', 251) + ".jpg"), "255 is fine");
            foreach (string reserved in new[] { "CON.jpg", "nul", "Com1.tif", "lpt9.old.jpg", "aux .jpg" })
                T.True(FileRename.Problem(a, reserved)?.Contains("reserves") == true, reserved);
        });

        T.Run("a name already taken in the folder is refused", () =>
        {
            using var d = new TempDir();
            string a = d.Write("a.jpg", new byte[1]);
            d.Write("b.jpg", new byte[1]);
            Directory.CreateDirectory(d.File("folder"));
            T.Eq("There is already a file named \"b.jpg\" in this folder.", FileRename.Problem(a, "b.jpg"));
            T.True(FileRename.Problem(a, "folder") != null, "a subfolder's name is taken too");
        });

        T.Run("changing the extension asks first, and says when the file will leave the grid", () =>
        {
            T.Eq(null, FileRename.ExtensionChangeQuestion("a.jpg", "b.jpg", true));
            T.Eq(null, FileRename.ExtensionChangeQuestion("a.jpg", "b.JPG", true), "case of the extension doesn't count");
            T.Eq("If you change the extension from \".jpg\" to \".png\", the file may not open properly.\n\nAre you sure you want to change it?",
                FileRename.ExtensionChangeQuestion("a.jpg", "a.png", true));
            T.True(FileRename.ExtensionChangeQuestion("a.jpg", "a.txt", false)!.Contains("disappear from the browser"));
            T.True(FileRename.ExtensionChangeQuestion("a.jpg", "a", false)!.StartsWith("If you remove the extension \".jpg\""));
            T.True(FileRename.ExtensionChangeQuestion("a", "a.jpg", true)!.StartsWith("If you add the extension \".jpg\""));
        });

        T.Run("renaming moves the file in its folder, never overwriting", () =>
        {
            using var d = new TempDir();
            string a = d.Write("a.jpg", new byte[] { 7 });
            string b = d.Write("b.jpg", new byte[] { 8 });
            var r = FileRename.Rename(a, " c.jpg ");
            T.True(r.NewPath != null && PathUtil.SamePath(d.File("c.jpg"), r.NewPath), "new path");
            T.True(!File.Exists(a) && File.ReadAllBytes(d.File("c.jpg"))[0] == 7, "moved, contents kept");
            T.Eq(null, r.FailureText);

            var taken = FileRename.Rename(d.File("c.jpg"), "b.jpg");
            T.Eq(null, taken.NewPath);
            T.True(taken.FailureText!.StartsWith("Couldn't rename the file: There is already"));
            T.Eq((byte)8, File.ReadAllBytes(b)[0], "b untouched");

            var same = FileRename.Rename(b, "b.jpg");
            T.Eq(b, same.NewPath, "unchanged: nothing to do");
            T.Eq(null, FileRename.Rename(d.File("gone.jpg"), "x.jpg").NewPath, "a missing file fails");
        });

        T.Run("a change of case only renames the file", () =>
        {
            using var d = new TempDir();
            string a = d.Write("photo.jpg", new byte[] { 5 });
            var r = FileRename.Rename(a, "Photo.JPG");
            T.True(r.NewPath != null && r.NewPath.EndsWith("Photo.JPG"), $"new path {r.NewPath}");
            T.SeqEq(new[] { "Photo.JPG" }, Directory.GetFiles(d.Path).Select(Path.GetFileName), "one file, new case, no temp left");
        });
    }

    static void PreferencesTests()
    {
        T.Group("Preferences input");

        var inv = CultureInfo.InvariantCulture;
        var de = CultureInfo.GetCultureInfo("de-DE");

        T.Run("auto-advance seconds: a number above 0 and up to an hour, in the user's number format", () =>
        {
            T.True(PreferencesInput.TryParseSeconds("2.5", inv, out double s) && s == 2.5);
            T.True(PreferencesInput.TryParseSeconds(" 3 ", inv, out s) && s == 3, "spaces allowed");
            T.True(PreferencesInput.TryParseSeconds("1e1", inv, out s) && s == 10, "exponent allowed");
            T.True(PreferencesInput.TryParseSeconds("2,5", de, out s) && s == 2.5, "German decimal comma");
            T.True(PreferencesInput.TryParseSeconds("3600", inv, out _));
            foreach (string bad in new[] { "0", "-1", "3600.01", "abc", "", "NaN", "Infinity", "1,000" })
                T.True(!PreferencesInput.TryParseSeconds(bad, inv, out _), $"'{bad}' rejected");
            T.True(!PreferencesInput.TryParseSeconds(null, inv, out _));
        });

        T.Run("seconds are shown with up to two decimals", () =>
        {
            T.Eq("2.5", PreferencesInput.FormatSeconds(2.5, inv));
            T.Eq("0.33", PreferencesInput.FormatSeconds(1.0 / 3, inv));
            T.Eq("5", PreferencesInput.FormatSeconds(5, inv));
            T.Eq("2,5", PreferencesInput.FormatSeconds(2.5, de));
        });

        T.Run("both colours must be valid", () =>
        {
            T.True(PreferencesInput.ColorsValid("#000000", "White"));
            T.True(!PreferencesInput.ColorsValid("#000000", "#12"));
            T.True(!PreferencesInput.ColorsValid("Nope", "White"));
        });

        var date = new SortOrder(SortField.DateModified, false);
        var name = new SortOrder(SortField.Name, false);

        T.Run("what changed decides between rescan, re-sort and nothing", () =>
        {
            T.Eq(new PreferencesFollowUp(false, SortFollowUp.None), PreferencesFollowUp.From(false, false, name, name));
            T.Eq(new PreferencesFollowUp(false, SortFollowUp.Resort), PreferencesFollowUp.From(false, false, name, date));
            T.Eq(new PreferencesFollowUp(false, SortFollowUp.Resort), PreferencesFollowUp.From(false, false, name, name with { Descending = true }),
                "direction alone is a new order");
            T.Eq(new PreferencesFollowUp(true, SortFollowUp.None), PreferencesFollowUp.From(true, false, name, name));
            T.Eq(new PreferencesFollowUp(true, SortFollowUp.None), PreferencesFollowUp.From(false, true, name, name));
            T.Eq(new PreferencesFollowUp(true, SortFollowUp.SetOrder), PreferencesFollowUp.From(true, false, name, date),
                "the rescan sorts with the new order");
            T.Eq(new PreferencesFollowUp(true, SortFollowUp.SetOrder), PreferencesFollowUp.From(false, true, date, name));
        });
    }

    static void ColorTextTests()
    {
        T.Group("ColorText");

        static uint P(string s) { T.True(ColorText.TryParse(s, out uint v), $"'{s}' parses"); return v; }

        T.Run("hex colours: #RRGGBB is opaque, #AARRGGBB has alpha, either case", () =>
        {
            T.Eq(0xFF1E90FFu, P("#1E90FF"));
            T.Eq(0xFF1E90FFu, P("#1e90ff"));
            T.Eq(0x801E90FFu, P("#801E90FF"));
            T.Eq(0x00000000u, P("#00000000"));
            T.Eq(0xFFFFFFFFu, P("#FFFFFFFF"));
        });

        T.Run("short hex: #RGB and #ARGB double each digit", () =>
        {
            T.Eq(0xFFFF8800u, P("#F80"));
            T.Eq(0x88FF8800u, P("#8F80"));
            T.Eq(0xFF000000u, P("#000"));
        });

        T.Run("colour names in any case, spaces around ignored", () =>
        {
            T.Eq(0xFF000000u, P("Black"));
            T.Eq(0xFFFFFFFFu, P("white"));
            T.Eq(0xFF6495EDu, P("CORNFLOWERBLUE"));
            T.Eq(0x00FFFFFFu, P("Transparent"));
            T.Eq(0xFF00FFFFu, P("Aqua")); T.Eq(0xFF00FFFFu, P("Cyan"));
            T.Eq(0xFF000000u, P("  #000000\t"));
            T.Eq(0xFFFFFFFFu, P(" White "));
        });

        T.Run("anything else is rejected", () =>
        {
            foreach (string bad in new[] { "", "   ", "#", "#1", "#12", "#12345", "#1234567", "#123456789", "#GG0000", "#12 345",
                         "000000", "Blackish", "Light Blue", "sc#1,0,0,0", "rgb(0,0,0)", "# 000000", "RebeccaPurple" })
                T.True(!ColorText.TryParse(bad, out _), $"'{bad}' rejected");
            T.True(!ColorText.TryParse(null, out _));
            T.True(!ColorText.IsValid("nope") && ColorText.IsValid("red"));
        });

        T.Run("split gives the four channels", () =>
        {
            T.Eq(((byte)0x80, (byte)0x1E, (byte)0x90, (byte)0xFF), ColorText.Split(0x801E90FF));
        });

        T.Run("defaults in AppSettings are valid colours", () =>
        {
            var s = new AppSettings();
            T.True(ColorText.IsValid(s.SlideshowBackgroundColor) && ColorText.IsValid(s.TransparentImageBackgroundColor));
        });
    }
}
