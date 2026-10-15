namespace Emberlight.Core;

/// <summary>Which slides the slideshow decodes ahead of time, and when.</summary>
public static class SlidePreload
{
    /// <summary>No preloading this soon (ms) after a navigation: while an arrow key is held down, the slides in between get skipped.</summary>
    public const long SettleMilliseconds = 200;

    /// <summary>True once the user has paused on a slide long enough for preloading to be worthwhile.</summary>
    public static bool IsSettled(long nowMs, long lastNavigationMs) => nowMs - lastNavigationMs >= SettleMilliseconds;

    /// <summary>The next two slides, then the previous one (in that order, the order they're loaded), within the list.</summary>
    public static List<T> Candidates<T>(IReadOnlyList<T> slides, int current)
    {
        var wanted = new List<T>(3);
        if (current + 1 < slides.Count) wanted.Add(slides[current + 1]);
        if (current + 2 < slides.Count) wanted.Add(slides[current + 2]);
        if (current - 1 >= 0 && current - 1 < slides.Count) wanted.Add(slides[current - 1]);
        return wanted;
    }
}
