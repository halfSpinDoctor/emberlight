using Emberlight.Core;

namespace Emberlight.Tests;

internal static class SlideshowTests
{
    static string[] Names(int n) => Enumerable.Range(0, n).Select(i => $"f{i}.jpg").ToArray();
    static SlideshowState Make(int n, int seed = 1, int start = 0, bool random = false)
    {
        var s = new SlideshowState(new RandomizableList<string>(new Random(seed)));
        s.Load(Names(n), start, random);
        return s;
    }

    public static void RunAll()
    {
        T.Group("SlideshowState");

        T.Run("forward/back navigation, clamping, and blocking at the ends", () =>
        {
            var s = Make(5);
            T.Eq("f0.jpg", s.CurrentFile!);
            T.Eq(JumpResult.Blocked, s.Jump(-1));
            T.Eq(JumpResult.Moved, s.Jump(1)); T.Eq(1, s.CurrentIndex);
            T.Eq(JumpResult.Moved, s.Jump(10)); T.Eq(4, s.CurrentIndex, "clamped to last");
            T.Eq(JumpResult.Blocked, s.Jump(1)); T.Eq(4, s.CurrentIndex);
            T.Eq(JumpResult.Moved, s.Jump(-10)); T.Eq(0, s.CurrentIndex, "clamped to first");
            T.Eq(JumpResult.Blocked, s.Jump(0));
            s.JumpTo(99); T.Eq(4, s.CurrentIndex);
            s.JumpTo(-5); T.Eq(0, s.CurrentIndex);
        });

        T.Run("loop mode wraps in both directions", () =>
        {
            var s = Make(3); s.Loop = true;
            s.JumpTo(2);
            T.Eq(JumpResult.Looped, s.Jump(1)); T.Eq(0, s.CurrentIndex);
            T.Eq(JumpResult.Looped, s.Jump(-1)); T.Eq(2, s.CurrentIndex);
            T.Eq(JumpResult.Moved, s.Jump(-1)); T.Eq(1, s.CurrentIndex);
        });

        T.Run("empty slideshow never moves or throws", () =>
        {
            var s = Make(0);
            T.Eq(JumpResult.Blocked, s.Jump(1)); T.Eq(JumpResult.Blocked, s.Jump(-1));
            T.Eq(JumpResult.Blocked, s.JumpTo(0)); T.True(s.CurrentFile == null); T.True(s.IsPastEnd);
            s.SetRandom(true); s.SetRandom(false);
        });

        T.Run("auto-advance: going back pauses, going forward resumes, off stays off", () =>
        {
            var s = Make(5, start: 2);
            T.True(!s.IsTimerActive);
            s.SetAutoAdvance(3);
            T.True(s.IsTimerActive);
            s.Jump(-1); T.True(s.Paused && !s.IsTimerActive);
            s.Jump(1); T.True(!s.Paused && s.IsTimerActive);
            s.Pause(); T.True(s.Paused);
            s.SetAutoAdvance(0); s.Jump(-1); T.True(!s.Paused, "no timer, nothing to pause");
        });

        T.Run("random mode: chosen start slide is first; ordered stepping follows the original sequence", () =>
        {
            var s = Make(10, seed: 7, start: 4, random: true);
            T.True(s.RandomMode);
            T.Eq("f4.jpg", s.CurrentFile!); T.Eq(0, s.CurrentIndex);
            s.SetAutoAdvance(2);
            T.Eq(JumpResult.Moved, s.Jump(1, ordered: true));
            T.Eq("f5.jpg", s.CurrentFile!);
            T.True(!s.IsTimerActive, "ordered jump in random mode stops auto-advance");
            s.Jump(-1, ordered: true); s.Jump(-1, ordered: true);
            T.Eq("f3.jpg", s.CurrentFile!);
            for (int i = 0; i < 3; i++) s.Jump(-1, ordered: true);
            T.Eq("f0.jpg", s.CurrentFile!);
            T.Eq(JumpResult.Blocked, s.Jump(-1, ordered: true));
            s.JumpTo(0); // whatever is first in the shuffle; walk forward to the end
            s.Jump(1, ordered: true);
            while (s.Jump(1, ordered: true) == JumpResult.Moved) { }
            T.Eq("f9.jpg", s.CurrentFile!);
        });

        T.Run("ordered flag is ignored when not in random mode", () =>
        {
            var s = Make(4);
            s.Jump(2, ordered: true); T.Eq(2, s.CurrentIndex);
        });

        T.Run("random + loop reshuffles on wrap-around but keeps the list consistent", () =>
        {
            var s = Make(12, seed: 3, random: true); s.Loop = true;
            var before = s.Files.ToList();
            s.JumpTo(11);
            T.Eq(JumpResult.Looped, s.Jump(1));
            s.Files.Validate();
            T.True(!before.SequenceEqual(s.Files), "reshuffled");
            T.SeqEq(Names(12).OrderBy(x => x), s.Files.OrderBy(x => x));

            var s2 = Make(12, seed: 3, random: true); s2.Loop = true; s2.RerandomizeOnLoop = false;
            var b2 = s2.Files.ToList();
            s2.JumpTo(11); s2.Jump(1);
            T.SeqEq(b2, s2.Files, "no reshuffle when disabled");
        });

        T.Run("SetRandom toggling keeps the current slide", () =>
        {
            var s = Make(10, seed: 5, start: 6);
            s.SetRandom(true);
            T.Eq("f6.jpg", s.CurrentFile!); T.Eq(0, s.CurrentIndex);
            s.Jump(3);
            string current = s.CurrentFile!;
            s.SetRandom(false);
            T.Eq(current, s.CurrentFile!);
            T.SeqEq(Names(10), s.Files);
            T.True(!s.RandomMode);
        });

        T.Run("random off continues in sort order from the slide on screen; random on again keeps it first and reshuffles", () =>
        {
            var s = Make(12, seed: 21, start: 3, random: true);
            s.Jump(4);                                           // somewhere in the shuffled order
            string current = s.CurrentFile!;
            int ordinal = Array.IndexOf(Names(12), current);
            var shuffledBefore = s.Files.ToList();

            s.SetRandom(false);
            T.True(!s.RandomMode);
            T.Eq(current, s.CurrentFile!, "the slide on screen doesn't change");
            T.Eq(ordinal, s.CurrentIndex, "now positioned at its place in the sort order");
            var seen = new List<string> { s.CurrentFile! };
            while (s.Jump(1) == JumpResult.Moved) seen.Add(s.CurrentFile!);
            T.SeqEq(Names(12).Skip(ordinal), seen, "proceeds through the sort order from there");

            s.JumpTo(ordinal);
            s.SetRandom(true);
            T.True(s.RandomMode);
            T.Eq(current, s.CurrentFile!, "still the same slide");
            T.Eq(0, s.CurrentIndex, "...now first in the new shuffle");
            T.SeqEq(Names(12).OrderBy(x => x), s.Files.OrderBy(x => x), "same files");
            T.True(!shuffledBefore.SequenceEqual(s.Files), "a fresh shuffle, not the old order");
            s.Files.Validate();
        });

        T.Run("toggling random repeatedly never loses the current slide or corrupts the list", () =>
        {
            var s = Make(9, seed: 4, start: 5);
            string current = s.CurrentFile!;
            for (int i = 0; i < 20; i++)
            {
                s.SetRandom(i % 2 == 0);
                T.Eq(current, s.CurrentFile!, $"toggle {i}");
                s.Files.Validate();
                T.SeqEq(Names(9).OrderBy(x => x), s.Files.OrderBy(x => x));
            }
            T.True(!s.RandomMode, "ended on 'off'");
            T.Eq(5, s.CurrentIndex);
            s.SetRandom(false);                       // already off: no change
            T.Eq(5, s.CurrentIndex);
        });

        T.Run("removing files keeps the current slide sensible", () =>
        {
            var s = Make(5, start: 2);
            int changes = 0; s.CurrentChanged += () => changes++;
            T.True(s.RemoveFile("f0.jpg"));
            T.Eq("f2.jpg", s.CurrentFile!); T.Eq(1, s.CurrentIndex); T.Eq(0, changes, "removing an earlier slide doesn't change the picture");
            T.True(s.RemoveFile("f3.jpg"));
            T.Eq("f2.jpg", s.CurrentFile!); T.Eq(0, changes);
            T.True(s.RemoveFile("f2.jpg"));
            T.Eq("f4.jpg", s.CurrentFile!); T.Eq(1, changes, "current slide removed -> next one shown");
            T.True(s.RemoveFile("f4.jpg"));
            T.True(s.IsPastEnd, "removed the last slide while on it"); T.True(s.CurrentFile == null);
            T.True(!s.RemoveFile("nope.jpg"));
            T.True(s.RemoveFile("f1.jpg")); T.True(s.IsPastEnd);
        });

        T.Run("removing while randomized keeps invariants", () =>
        {
            var s = Make(20, seed: 11, random: true);
            var rng = new Random(2);
            while (s.Files.Count > 0)
            {
                s.RemoveFile(s.Files[rng.Next(s.Files.Count)]);
                s.Files.Validate();
                T.True(s.CurrentIndex <= s.Files.Count);
            }
        });

        T.Run("AddFile inserts in sorted position and keeps the current slide", () =>
        {
            var s = Make(5, start: 3);
            var cmp = FileComparers.For(SortOrder.Default);
            s.AddFile("f1b.jpg", cmp);
            T.Eq("f3.jpg", s.CurrentFile!);
            T.SeqEq(new[] { "f0.jpg", "f1.jpg", "f1b.jpg", "f2.jpg", "f3.jpg", "f4.jpg" }, s.Files);
            s.AddFile("f9.jpg", cmp);
            T.Eq("f3.jpg", s.CurrentFile!);
            s.AddFile("f9.jpg", cmp); T.Eq(7, s.Files.Count);
        });

        T.Run("per-file view state is remembered and dropped on removal", () =>
        {
            var s = Make(3);
            var st = s.GetViewState("f1.jpg");
            st.Orientation = ImageOrientation.FromExif(6);
            st.Zoom = new ZoomInfo(2.5, 10, 20);
            T.True(s.TryGetViewState("F1.JPG", out var again), "case-insensitive keys");
            T.Eq(6, again!.Orientation!.Value.ToExif());
            T.Eq(2.5, again.Zoom!.Value.Zoom);
            T.True(!s.TryGetViewState("f0.jpg", out _));
            s.RemoveFile("f1.jpg");
            T.True(!s.TryGetViewState("f1.jpg", out _));
        });

        T.Run("CurrentChanged fires on navigation", () =>
        {
            var s = Make(4);
            int n = 0; s.CurrentChanged += () => n++;
            s.Jump(1); s.Jump(1); s.JumpTo(0);
            T.Eq(3, n);
            s.Jump(-1); // blocked: no change
            T.Eq(3, n);
        });

        WheelTests();
        GifTests();
        CommandTests();
        OverlayTests();
        WindowFitTests();
    }

    static void OverlayTests()
    {
        T.Group("Slideshow overlay");

        T.Run("I cycles none -> file info -> file info + EXIF -> none", () =>
        {
            var s = new AppSettings { SlideshowInfoMode = 0 };
            var o = new SlideshowOverlay(s);
            T.True(!o.ShowsInfo && !o.ShowsExif);
            o.Cycle(); T.Eq(1, s.SlideshowInfoMode); T.True(o.ShowsInfo && !o.ShowsExif);
            o.Cycle(); T.Eq(2, s.SlideshowInfoMode); T.True(o.ShowsInfo && o.ShowsExif);
            o.Cycle(); T.Eq(0, s.SlideshowInfoMode); T.True(!o.ShowsInfo);
        });

        T.Run("Shift+I jumps to the EXIF view with more details, then toggles the details", () =>
        {
            var s = new AppSettings { SlideshowInfoMode = 0 };
            var o = new SlideshowOverlay(s);
            T.True(!o.MoreExif);
            o.ToggleMoreExif(); T.Eq(2, s.SlideshowInfoMode); T.True(o.MoreExif);
            o.ToggleMoreExif(); T.Eq(2, s.SlideshowInfoMode); T.True(!o.MoreExif);
            o.ToggleMoreExif(); T.True(o.MoreExif);
            s.SlideshowInfoMode = 1;
            o.ToggleMoreExif(); T.Eq(2, s.SlideshowInfoMode); T.True(o.MoreExif, "from file info: always more");
        });

        T.Run("P toggles the full path and shows the info if it was hidden", () =>
        {
            var s = new AppSettings { SlideshowInfoMode = 0, SlideshowShowPath = false };
            var o = new SlideshowOverlay(s);
            o.TogglePath(); T.True(s.SlideshowShowPath); T.Eq(1, s.SlideshowInfoMode);
            s.SlideshowInfoMode = 2;
            o.TogglePath(); T.True(!s.SlideshowShowPath); T.Eq(2, s.SlideshowInfoMode, "an open view stays as it is");
        });

        string dir = Path.Combine(Path.GetTempPath(), "phx-show");
        var info = new ImageInfo(6000, 4000, ImageOrientation.Identity, false, 3_000_000, DateTime.UtcNow);
        string size = FileSizeFormatter.Format(3_000_000);
        SlideshowState Show(int n, int start = 0)
        {
            var s = new SlideshowState(new RandomizableList<string>(new Random(1)));
            s.Load(Enumerable.Range(0, n).Select(i => Path.Combine(dir, $"f{i}.jpg")), start);
            return s;
        }

        T.Run("info line: position and name, then size, file size and zoom", () =>
        {
            var s = Show(12, 2);
            string pct = (0.25 * 100).ToString("0.#");
            T.Eq($"3/12   f2.jpg\n6000 × 4000   {size}   {pct}%",
                DisplayText.SlideshowInfo(s, false, info, 0.25, ImageOrientation.Identity));
            T.Eq($"3/12   {Path.Combine(dir, "f2.jpg")}\n6000 × 4000   {size}   {pct}%",
                DisplayText.SlideshowInfo(s, true, info, 0.25, ImageOrientation.Identity), "full path");
            T.Eq("3/12   f2.jpg", DisplayText.SlideshowInfo(s, false, null, 1, ImageOrientation.Identity), "couldn't be opened");
        });

        T.Run("info line: rotation and flip the user applied", () =>
        {
            var s = Show(3);
            T.True(DisplayText.SlideshowInfo(s, false, info, 1, ImageOrientation.Identity.RotatedBy(90)).EndsWith("100%   rotated 90°"));
            T.True(DisplayText.SlideshowInfo(s, false, info, 1, ImageOrientation.Identity.FlippedDisplay()).Contains(", flipped"));
        });

        T.Run("info line: random, loop and auto-advance flags", () =>
        {
            var s = Show(5);
            s.Loop = true;
            s.SetRandom(true);
            s.SetAutoAdvance(2.5);
            string text = DisplayText.SlideshowInfo(s, false, info, 1, ImageOrientation.Identity);
            T.True(text.EndsWith($"\n[random] [loop] [auto {2.5:0.#}s]"), text);
            s.Pause();
            T.True(DisplayText.SlideshowInfo(s, false, info, 1, ImageOrientation.Identity).EndsWith(" (paused)]"));
        });

        T.Run("info line: an empty or finished show says so", () =>
        {
            T.Eq("No images", DisplayText.SlideshowInfo(Show(0), false, null, 1, ImageOrientation.Identity));
            var s = Show(1);
            s.RemoveFile(s.CurrentFile!);
            T.Eq("No images", DisplayText.SlideshowInfo(s, false, null, 1, ImageOrientation.Identity));
        });

        T.Run("EXIF overlay: the tags, or a note when there are none", () =>
        {
            using var tmp = new TempDir();
            string jpg = tmp.Write("photo.jpg", ExifTestBuilder.WrapInJpeg(new ExifTestBuilder(false).BuildSample()));
            T.Eq(ExifReader.Read(jpg)!.ToDisplayString(true), DisplayText.ExifOverlay(jpg, true));
            T.Eq(ExifReader.Read(jpg)!.ToDisplayString(false), DisplayText.ExifOverlay(jpg, false));
            T.Eq("(no EXIF data)", DisplayText.ExifOverlay(tmp.Write("plain.png", new byte[10]), false));
        });
    }

    static void WindowFitTests()
    {
        T.Group("WindowFit");
        var work = (W: 1920.0, H: 1040.0);
        var chrome = (W: 16.0, H: 39.0);
        var window = new WindowBounds(100, 100, 800, 600);

        T.Run("the window grows or shrinks to the picture plus its frame, keeping its centre", () =>
        {
            var b = WindowFit.FitToImage(window, chrome, work, 1000, 700, ImageOrientation.Identity, 1);
            T.Eq(new WindowBounds(-8, 30.5, 1016, 739), b);
            T.Eq(window.Left + window.Width / 2, b.Left + b.Width / 2, "same centre x");
            T.Eq(window.Top + window.Height / 2, b.Top + b.Height / 2, "same centre y");
        });

        T.Run("picture pixels are converted to screen units at high DPI", () =>
        {
            var b = WindowFit.FitToImage(window, chrome, work, 1000, 700, ImageOrientation.Identity, 2);
            T.Eq((516.0, 389.0), (b.Width, b.Height));
        });

        T.Run("a rotated picture swaps width and height", () =>
        {
            var b = WindowFit.FitToImage(window, chrome, work, 700, 500, ImageOrientation.FromExif(6), 1);
            T.Eq((516.0, 739.0), (b.Width, b.Height));
        });

        T.Run("at least 320 x 240, at most 95% of the work area", () =>
        {
            var tiny = WindowFit.FitToImage(window, chrome, work, 10, 10, ImageOrientation.Identity, 1);
            T.Eq((320.0, 240.0), (tiny.Width, tiny.Height));
            var huge = WindowFit.FitToImage(window, chrome, work, 8000, 6000, ImageOrientation.Identity, 1);
            T.Eq((1920 * 0.95, 1040 * 0.95), (huge.Width, huge.Height));
        });

        T.Run("a work area smaller than the minimum doesn't throw", () =>
        {
            var b = WindowFit.FitToImage(window, chrome, (200, 100), 10, 10, ImageOrientation.Identity, 1);
            T.Eq((320.0, 240.0), (b.Width, b.Height));
        });
    }

    // A frame filled with one "colour" (stored in the blue byte), or transparent where colour is -1.
    static GifFrame Frame(int left, int top, int w, int h, int colour, GifDisposal disposal = GifDisposal.Keep, int delay = 10)
    {
        var px = new byte[w * h * 4];
        if (colour >= 0)
            for (int i = 0; i < px.Length; i += 4) { px[i] = (byte)colour; px[i + 3] = 255; }
        return new GifFrame(left, top, w, h, delay, disposal, px);
    }

    // The canvas as rows of colours ('.' = transparent, otherwise the colour's digit), for readable assertions.
    static string Rows(GifCompositor c, int w, int h)
    {
        var sb = new System.Text.StringBuilder();
        for (int y = 0; y < h; y++)
        {
            if (y > 0) sb.Append('|');
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                sb.Append(c.Canvas[i + 3] == 0 ? '.' : (char)('0' + c.Canvas[i]));
            }
        }
        return sb.ToString();
    }

    static void GifTests()
    {
        T.Group("GifCompositor");
        T.Run("a frame smaller than the canvas is drawn at its offset; the rest stays transparent", () =>
        {
            var c = new GifCompositor(new GifAnimation(4, 3, [Frame(1, 1, 2, 1, 5)]));
            T.Eq("....|.55.|....", Rows(c, 4, 3));
        });
        T.Run("Keep: the next frame is drawn over the last, and its transparent pixels show what is underneath", () =>
        {
            var hole = Frame(0, 0, 3, 1, 2);
            hole.Bgra[4 + 3] = 0;                                   // middle pixel transparent
            var c = new GifCompositor(new GifAnimation(3, 1, [Frame(0, 0, 3, 1, 1), hole]));
            c.Advance();
            T.Eq("212", Rows(c, 3, 1));
        });
        T.Run("RestoreBackground clears the frame's own rectangle before the next frame", () =>
        {
            var c = new GifCompositor(new GifAnimation(3, 1,
                [Frame(0, 0, 3, 1, 1), Frame(1, 0, 1, 1, 2, GifDisposal.RestoreBackground), Frame(0, 0, 1, 1, 3)]));
            c.Advance(); T.Eq("121", Rows(c, 3, 1));
            c.Advance(); T.Eq("3.1", Rows(c, 3, 1), "frame 2's pixel was cleared, the rest kept");
        });
        T.Run("RestorePrevious puts back the canvas from before the frame", () =>
        {
            var c = new GifCompositor(new GifAnimation(2, 1,
                [Frame(0, 0, 2, 1, 1), Frame(0, 0, 2, 1, 2, GifDisposal.RestorePrevious), Frame(1, 0, 1, 1, 3)]));
            c.Advance(); T.Eq("22", Rows(c, 2, 1));
            c.Advance(); T.Eq("13", Rows(c, 2, 1), "frame 1 undone, then frame 2 drawn");
        });
        T.Run("after the last frame it loops to the first on a clear canvas, for ever", () =>
        {
            var c = new GifCompositor(new GifAnimation(2, 1, [Frame(0, 0, 1, 1, 1), Frame(1, 0, 1, 1, 2)]));
            c.Advance(); T.Eq("12", Rows(c, 2, 1));
            c.Advance(); T.Eq(0, c.FrameIndex); T.Eq("1.", Rows(c, 2, 1), "frame 2's pixel is gone after the loop");
            for (int i = 0; i < 101; i++) c.Advance();
            T.Eq(1, c.FrameIndex);
        });
        T.Run("frames hanging off the canvas are clipped, including when disposed", () =>
        {
            var c = new GifCompositor(new GifAnimation(2, 2,
                [Frame(-1, -1, 2, 2, 4, GifDisposal.RestoreBackground), Frame(1, 1, 5, 5, 6)]));
            T.Eq("4.|..", Rows(c, 2, 2));
            c.Advance(); T.Eq("..|.6", Rows(c, 2, 2));
        });
        T.Run("delays: hundredths of a second, with 0 and 1 treated as 100 ms like browsers do", () =>
        {
            T.Eq(100.0, Frame(0, 0, 1, 1, 1, delay: 0).Delay.TotalMilliseconds);
            T.Eq(100.0, Frame(0, 0, 1, 1, 1, delay: 1).Delay.TotalMilliseconds);
            T.Eq(20.0, Frame(0, 0, 1, 1, 1, delay: 2).Delay.TotalMilliseconds);
            T.Eq(500.0, Frame(0, 0, 1, 1, 1, delay: 50).Delay.TotalMilliseconds);
        });
        T.Run("one play-through lasts the sum of the frame delays", () =>
        {
            var anim = new GifAnimation(1, 1, [Frame(0, 0, 1, 1, 1, delay: 50), Frame(0, 0, 1, 1, 2, delay: 0), Frame(0, 0, 1, 1, 3, delay: 25)]);
            T.Eq(850.0, anim.Duration.TotalMilliseconds, "500 + 100 (a zero delay) + 250");
        });
        T.Run("an empty canvas, no frames or pixel data of the wrong size are rejected", () =>
        {
            T.Throws<ArgumentOutOfRangeException>(() => new GifAnimation(0, 1, [Frame(0, 0, 1, 1, 1)]));
            T.Throws<ArgumentException>(() => new GifAnimation(1, 1, []));
            T.Throws<ArgumentException>(() => new GifAnimation(1, 1, [new GifFrame(0, 0, 2, 2, 0, GifDisposal.Keep, new byte[4])]));
            T.Eq(8L, new GifAnimation(2, 1, [Frame(0, 0, 1, 1, 1), Frame(1, 0, 1, 1, 2)]).FrameBytes);
        });
    }

    static void CommandTests()
    {
        T.Group("Slideshow commands");

        T.Run("after the last slide only going back, first/last, folders, help and quit still work", () =>
        {
            var run = new[]
            {
                SlideshowCommand.Previous, SlideshowCommand.First, SlideshowCommand.Last, SlideshowCommand.BackPage,
                SlideshowCommand.NextFolder, SlideshowCommand.PreviousFolder, SlideshowCommand.Help, SlideshowCommand.Quit,
            };
            foreach (var c in run) T.Eq(PastEndRule.Run, SlideshowCommands.WhenPastEnd(c), c.ToString());
            T.Eq(PastEndRule.Swallow, SlideshowCommands.WhenPastEnd(SlideshowCommand.PauseOrNext), "Space is eaten");
            foreach (var c in Enum.GetValues<SlideshowCommand>().Except(run).Except([SlideshowCommand.PauseOrNext]))
                T.Eq(PastEndRule.Ignore, SlideshowCommands.WhenPastEnd(c), c.ToString());
        });

        T.Run("first slide: jumps back to the start, blocked when already there", () =>
        {
            var s = Make(5, start: 3);
            T.Eq(JumpResult.Moved, s.JumpToFirst()); T.Eq(0, s.CurrentIndex);
            T.Eq(JumpResult.Blocked, s.JumpToFirst());
            s.Loop = true;
            T.Eq(JumpResult.Blocked, s.JumpToFirst(), "loop mode doesn't wrap to the end");
        });

        T.Run("first slide pauses auto-advance like any step back", () =>
        {
            var s = Make(5, start: 3);
            s.SetAutoAdvance(2);
            s.JumpToFirst();
            T.True(s.Paused);
        });

        T.Run("last slide: jumps to the end, even from past the end; blocked when empty", () =>
        {
            var s = Make(5, start: 1);
            T.Eq(JumpResult.Moved, s.JumpToLast()); T.Eq(4, s.CurrentIndex);
            s.RemoveFile("f4.jpg");
            T.True(s.IsPastEnd);
            T.Eq(JumpResult.Moved, s.JumpToLast()); T.Eq(3, s.CurrentIndex);
            T.Eq(JumpResult.Blocked, Make(0).JumpToLast());
        });

        T.Run("a page is 10 slides", () => T.Eq(10, SlideshowCommands.PageSize));

        T.Run("starting auto-advance from off moves on once; changing a running timer doesn't", () =>
        {
            var s = Make(5);
            T.Eq(JumpResult.Moved, s.StartAutoAdvance(3)); T.Eq(1, s.CurrentIndex);
            T.Eq(3.0, s.AutoAdvanceSeconds); T.True(s.IsTimerActive);
            T.Eq(null, s.StartAutoAdvance(5)); T.Eq(1, s.CurrentIndex, "already running: no extra step");
            T.Eq(5.0, s.AutoAdvanceSeconds);
        });

        T.Run("starting auto-advance while paused also moves on and resumes", () =>
        {
            var s = Make(5, start: 2);
            s.SetAutoAdvance(3);
            s.Pause();
            T.Eq(JumpResult.Moved, s.StartAutoAdvance(4)); T.Eq(3, s.CurrentIndex);
            T.True(s.IsTimerActive && !s.Paused);
        });

        T.Run("starting auto-advance on the last slide still starts the timer", () =>
        {
            var s = Make(3, start: 2);
            T.Eq(JumpResult.Blocked, s.StartAutoAdvance(2));
            T.True(s.IsTimerActive);
            s.Loop = true; s.SetAutoAdvance(0);
            T.Eq(JumpResult.Looped, s.StartAutoAdvance(2)); T.Eq(0, s.CurrentIndex);
        });

        T.Run("Space pauses a running timer, otherwise moves on (in original order with Alt)", () =>
        {
            var s = Make(5);
            s.SetAutoAdvance(3);
            T.Eq(null, s.PauseOrNext(inOrder: false)); T.True(s.Paused); T.Eq(0, s.CurrentIndex);
            T.Eq(JumpResult.Moved, s.PauseOrNext(inOrder: false)); T.Eq(1, s.CurrentIndex);
            T.True(s.IsTimerActive, "moving forward resumes");

            var r = Make(6, seed: 4, random: true);
            int ordered = r.Files.OrderedIndexFromIndex(r.CurrentIndex);
            T.Eq(JumpResult.Moved, r.PauseOrNext(inOrder: true));
            T.Eq(ordered + 1, r.Files.OrderedIndexFromIndex(r.CurrentIndex), "next in sorted order");
        });

        T.Run("timer tick: carries on, except at the end of a non-looping show, where it stops", () =>
        {
            var s = Make(3);
            s.SetAutoAdvance(2);
            T.True(s.ContinueAutoAdvance());
            s.JumpTo(2);
            T.True(!s.ContinueAutoAdvance());
            T.Eq(0.0, s.AutoAdvanceSeconds, "switched off");
            s.Loop = true; s.SetAutoAdvance(2);
            T.True(s.ContinueAutoAdvance(), "a looping show goes round");
        });

        T.Run("auto-advance interval: the set time, or longer to let an animation finish", () =>
        {
            var s = Make(3);
            s.SetAutoAdvance(2.5);
            T.Eq(TimeSpan.FromSeconds(2.5), s.AutoAdvanceInterval(null));
            T.Eq(TimeSpan.FromSeconds(2.5), s.AutoAdvanceInterval(TimeSpan.FromSeconds(1)), "short GIF");
            T.Eq(TimeSpan.FromSeconds(4), s.AutoAdvanceInterval(TimeSpan.FromSeconds(4)), "long GIF plays through");
        });
    }

    static void WheelTests()
    {
        T.Group("WheelStepper");
        T.Run("one notch is one slide: up = back, down = forward", () =>
        {
            var w = new WheelStepper();
            T.Eq(-1, w.Add(120));
            T.Eq(1, w.Add(-120));
            T.Eq(-2, w.Add(240), "a fast spin reported as one event");
        });
        T.Run("small touchpad steps add up to a notch", () =>
        {
            var w = new WheelStepper();
            int total = 0;
            for (int i = 0; i < 11; i++) total += w.Add(-30);     // 330 units: two notches plus a remainder
            T.Eq(2, total);
            T.Eq(1, w.Add(-30), "the remainder carries over to the next notch");
        });
        T.Run("reversing direction or resetting drops partial movement", () =>
        {
            var w = new WheelStepper();
            T.Eq(0, w.Add(-90));
            T.Eq(0, w.Add(60), "turned back: starts again from zero");
            T.Eq(-1, w.Add(60));
            T.Eq(0, w.Add(-100)); w.Reset();
            T.Eq(0, w.Add(-100), "reset forgot the earlier 100");
            T.Eq(0, w.Add(0));
        });
    }
}
