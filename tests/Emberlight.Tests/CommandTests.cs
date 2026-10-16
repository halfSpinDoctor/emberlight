using Emberlight.Core;

namespace Emberlight.Tests;

/// <summary>Browser commands, the slideshow's key help, and next/previous folder jumps.</summary>
internal static class CommandTests
{
    static string Full(string path) => Path.GetFullPath(path);

    /// <summary>Same folder, whichever separators the two paths use.</summary>
    static void Same(string expected, string? actual, string? msg = null)
        => T.True(actual != null && PathUtil.SamePath(Full(expected), Full(actual)), $"{msg ?? "paths differ"}: expected <{expected}> but was <{actual}>");

    public static void RunAll()
    {
        BrowserCommandTests();
        KeyHelpTests();
        FolderJumperTests();
    }

    static void BrowserCommandTests()
    {
        T.Group("Browser commands");

        T.Run("moving in the grid needs something in the grid; pin keys need the pin; everything else always runs", () =>
        {
            var move = new BrowserInput(BrowserCommand.MoveSelection, GridMove.Down);
            T.True(!BrowserCommands.CanRun(move, 0), "empty grid: leave the key to the list");
            T.True(BrowserCommands.CanRun(move, 1));
            foreach (var c in Enum.GetValues<BrowserCommand>().Except([BrowserCommand.None, BrowserCommand.MoveSelection, BrowserCommand.OpenPinned]))
                T.True(BrowserCommands.CanRun(new BrowserInput(c), 0), c.ToString());
            T.True(!BrowserCommands.CanRun(default, 5), "None never runs");
        });

        T.Run("an input carries its grid direction", () =>
        {
            T.Eq(GridMove.PageUp, new BrowserInput(BrowserCommand.MoveSelection, GridMove.PageUp).Move);
            T.Eq(BrowserCommand.None, default(BrowserInput).Command);
        });
    }

    static void KeyHelpTests()
    {
        T.Group("Key help");

        T.Run("keys and descriptions line up in two columns, sections apart, footer last", () =>
        {
            string text = KeyHelp.Format("Keys",
                [[new HelpLine("A", "first"), new HelpLine("Shift+B", "second")], [new HelpLine("C", "third")]],
                "Mouse: click");
            T.Eq("Keys\n\nA        first\nShift+B  second\n\nC        third\n\nMouse: click", text);
        });

        T.Run("no footer, and an empty section, add nothing", () =>
        {
            T.Eq("Keys\n\nA  one", KeyHelp.Format("Keys", [[new HelpLine("A", "one")], []]));
        });

        T.Run("every slideshow command is described by some help topic", () =>
        {
            var covered = SlideshowHelp.Sections.SelectMany(s => s).SelectMany(t => t.Commands).ToHashSet();
            var missing = Enum.GetValues<SlideshowCommand>().Where(c => c != SlideshowCommand.None && !covered.Contains(c)).ToList();
            T.Eq(0, missing.Count, "undocumented: " + string.Join(", ", missing));
        });

        T.Run("topics are listed once each, and each has a description", () =>
        {
            var topics = SlideshowHelp.Sections.SelectMany(s => s).ToList();
            T.Eq(topics.Count, topics.Select(t => t.Topic).Distinct().Count(), "no topic twice");
            T.Eq(Enum.GetValues<SlideshowHelpTopic>().Length, topics.Count, "every topic listed");
            T.True(topics.All(t => t.Description.Length > 0 && t.Commands.Count > 0));
        });

        T.Run("the page size in the help follows the command's page size", () =>
        {
            var pages = SlideshowHelp.Sections.SelectMany(s => s).Single(t => t.Topic == SlideshowHelpTopic.Pages);
            T.True(pages.Description.Contains(SlideshowCommands.PageSize.ToString()), pages.Description);
        });

        T.Run("building the help uses the platform's key names, in topic order", () =>
        {
            var keys = Enum.GetValues<SlideshowHelpTopic>().ToDictionary(t => t, t => "k" + (int)t);
            string text = SlideshowHelp.Build(keys, "footer");
            var lines = text.Split('\n');
            T.Eq("Slideshow keys", lines[0]);
            T.True(lines[2].StartsWith("k" + (int)SlideshowHelpTopic.Next + " ") && lines[2].EndsWith("next slide"), lines[2]);
            T.Eq("footer", lines[^1]);
            foreach (var topic in SlideshowHelp.Sections.SelectMany(s => s))
                T.True(text.Contains(topic.Description), topic.Description);
        });

        T.Run("a topic without key names shows a question mark instead of failing", () =>
        {
            string text = SlideshowHelp.Build(new Dictionary<SlideshowHelpTopic, string>(), null);
            T.True(text.Split('\n').Skip(2).Where(l => l.Length > 0).All(l => l.StartsWith("?")), text);
        });
    }

    static void FolderJumperTests()
    {
        T.Group("FolderJumper");
        var scanner = new DirectoryScanner(new ImageFileTypes());
        static TempDir Tree() // siblings 1, 2 (no images), 3
        {
            var d = new TempDir();
            foreach (var f in new[] { "lib/1/a.jpg", "lib/1/b.jpg", "lib/2/readme.txt", "lib/3/z.png" }) d.Write(f, [1]);
            return d;
        }

        T.Run("next folder: skips folders without images, reports where it is, and becomes the reference", () =>
        {
            using var d = Tree();
            var j = new FolderJumper(d.File("lib/1"), scanner, SortOrder.Default, includeSubfolders: false);
            string? searching = null;
            var r = j.JumpAsync(+1, d.File("lib/1/a.jpg"), s => searching = s).GetAwaiter().GetResult();
            T.Eq(FolderJumpOutcome.Jumped, r.Outcome);
            T.Eq("Next folder…", searching);
            Same(d.File("lib/3"), r.Target!.Path);
            T.SeqEq(new[] { Full(d.File("lib/3/z.png")) }, r.Target.Files);
            T.Eq("3   (3 of 3)", r.Message);
            Same(d.File("lib/3"), j.Anchor);
            T.True(!r.ShouldBeep && !j.IsBusy);
        });

        T.Run("previous folder from there goes back past the empty folder", () =>
        {
            using var d = Tree();
            var j = new FolderJumper(d.File("lib/3"), scanner, SortOrder.Default, false);
            string? searching = null;
            var r = j.JumpAsync(-1, d.File("lib/3/z.png"), s => searching = s).GetAwaiter().GetResult();
            T.Eq("Previous folder…", searching);
            Same(d.File("lib/1"), r.Target!.Path);
            T.Eq("1   (1 of 3)", r.Message);
        });

        T.Run("at the end: a beep and 'No next folder' / 'No previous folder', reference kept", () =>
        {
            using var d = Tree();
            var j = new FolderJumper(d.File("lib/3"), scanner, SortOrder.Default, false);
            var r = j.JumpAsync(+1, d.File("lib/3/z.png")).GetAwaiter().GetResult();
            T.Eq(FolderJumpOutcome.NotFound, r.Outcome);
            T.Eq("No next folder", r.Message);
            T.True(r.ShouldBeep && r.Target == null);
            Same(d.File("lib/3"), j.Anchor);
            var back = new FolderJumper(d.File("lib/1"), scanner, SortOrder.Default, false).JumpAsync(-1, d.File("lib/1/a.jpg")).GetAwaiter().GetResult();
            T.Eq("No previous folder", back.Message);
        });

        T.Run("a slide outside the reference folder makes its own folder the reference", () =>
        {
            using var d = Tree();
            var j = new FolderJumper(d.File("lib/3"), scanner, SortOrder.Default, false);
            var r = j.JumpAsync(+1, d.File("lib/1/b.jpg")).GetAwaiter().GetResult();
            Same(d.File("lib/3"), r.Target!.Path, "relative to lib/1, the slide's folder");
        });

        T.Run("a start folder that no longer exists is not used as the reference", () =>
        {
            using var d = Tree();
            var j = new FolderJumper(d.File("lib/gone"), scanner, SortOrder.Default, false);
            T.Eq(null, j.Anchor);
            var r = j.JumpAsync(+1, d.File("lib/1/a.jpg")).GetAwaiter().GetResult();
            Same(d.File("lib/3"), r.Target!.Path, "relative to the slide's folder instead");
        });

        T.Run("nothing to go by: a beep, no search and no message", () =>
        {
            var j = new FolderJumper(null, scanner, SortOrder.Default, false);
            bool searched = false;
            var r = j.JumpAsync(+1, null, _ => searched = true).GetAwaiter().GetResult();
            T.Eq(FolderJumpOutcome.NoFolder, r.Outcome);
            T.True(r.ShouldBeep && r.Message == null && !searched);
        });

        T.Run("a folder that can't be read: a beep and 'Couldn't read that folder'", () =>
        {
            using var d = Tree();
            var j = new FolderJumper(d.File("lib/1"), (_, _) => throw new IOException("gone"));
            var r = j.JumpAsync(+1, d.File("lib/1/a.jpg")).GetAwaiter().GetResult();
            T.Eq(FolderJumpOutcome.Failed, r.Outcome);
            T.Eq("Couldn't read that folder", r.Message);
            T.True(r.ShouldBeep && !j.IsBusy, "not stuck busy after a failure");
            Same(d.File("lib/1"), j.Anchor);
        });

        T.Run("key repeat while a folder is still loading is ignored", () =>
        {
            using var d = Tree();
            using var release = new ManualResetEventSlim();
            var j = new FolderJumper(d.File("lib/1"), (_, _) => { release.Wait(); return null; });
            var first = j.JumpAsync(+1, d.File("lib/1/a.jpg"));
            bool wasBusy = j.IsBusy;
            // If repeats weren't ignored, the second jump would wait for the release too: give it a time limit instead
            // of hanging, and let the first one go either way.
            var secondTask = j.JumpAsync(+1, d.File("lib/1/a.jpg"));
            bool returnedAtOnce = secondTask.Wait(TimeSpan.FromSeconds(5));
            release.Set();
            T.True(wasBusy, "busy while searching");
            T.True(returnedAtOnce, "a repeat returns at once instead of starting a second search");
            var second = secondTask.GetAwaiter().GetResult();
            T.Eq(FolderJumpOutcome.Busy, second.Outcome);
            T.True(!second.ShouldBeep && second.Message == null, "silently ignored");
            T.Eq(FolderJumpOutcome.NotFound, first.GetAwaiter().GetResult().Outcome);
            T.True(!j.IsBusy);
        });

        T.Run("with subfolders on, a folder whose images are all in subfolders counts", () =>
        {
            using var d = Tree();
            d.Write("lib/2/deep/x.jpg", [1]);
            var j = new FolderJumper(d.File("lib/1"), scanner, SortOrder.Default, includeSubfolders: true);
            var r = j.JumpAsync(+1, d.File("lib/1/a.jpg")).GetAwaiter().GetResult();
            Same(d.File("lib/2"), r.Target!.Path);
            T.SeqEq(new[] { Full(d.File("lib/2/deep/x.jpg")) }, r.Target.Files);
        });
    }
}
