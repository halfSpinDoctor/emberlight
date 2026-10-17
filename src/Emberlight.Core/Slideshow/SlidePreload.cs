namespace Emberlight.Core;

/// <summary>Which slides the slideshow decodes ahead of time, and when.</summary>
public static class SlidePreload
{
    /// <summary>No preloading this soon (ms) after a navigation: while an arrow key is held down, the slides in between get skipped.</summary>
    public const long SettleMilliseconds = 200;

    /// <summary>True once the user has paused on a slide long enough for preloading to be worthwhile.</summary>
    public static bool IsSettled(long nowMs, long lastNavigationMs) => nowMs - lastNavigationMs >= SettleMilliseconds;

    /// <summary>The next <paramref name="ahead"/> slides, then the previous <paramref name="behind"/> (in that
    /// order, the order they're loaded), within the list. Defaults (2 ahead, 1 behind) are today's Windows
    /// values.</summary>
    public static List<T> Candidates<T>(IReadOnlyList<T> slides, int current, int ahead = 2, int behind = 1)
    {
        var wanted = new List<T>(ahead + behind);
        for (int i = 1; i <= ahead; i++)
            if (current + i < slides.Count) wanted.Add(slides[current + i]);
        for (int i = 1; i <= behind; i++)
            if (current - i >= 0 && current - i < slides.Count) wanted.Add(slides[current - i]);
        return wanted;
    }
}
