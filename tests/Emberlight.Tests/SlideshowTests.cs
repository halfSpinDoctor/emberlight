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
