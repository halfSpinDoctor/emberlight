using System.Diagnostics.CodeAnalysis;

namespace Emberlight.Core;

public enum JumpResult
{
    /// <summary>Moved to a different slide.</summary>
    Moved,
    /// <summary>Wrapped around the end (or start) because loop mode is on.</summary>
    Looped,
    /// <summary>Could not move (the original beeps here).</summary>
    Blocked,
}

/// <summary>Remembered zoom of a slide: scale factor plus the point (in image coordinates) at the view centre.</summary>
public readonly record struct ZoomInfo(double Zoom, double CenterX, double CenterY);

/// <summary>Per-file view adjustments the user made during a slideshow (rotation, flip, zoom).</summary>
public sealed class FileViewState
{
    public ImageOrientation? Orientation { get; set; }
    public ZoomInfo? Zoom { get; set; }
}

/// <summary>
/// The navigation/state half of SlideshowWindow, free of any UI: the file list (optionally
/// shuffled), current position, loop/random/auto-advance flags, and per-file view memory.
/// The WPF window drives this and renders whatever <see cref="CurrentFile"/> is.
/// </summary>
public sealed class SlideshowState
{
    private readonly Dictionary<string, FileViewState> _view = new(StringComparer.OrdinalIgnoreCase);

    public SlideshowState() : this(new RandomizableList<string>()) { }
    public SlideshowState(RandomizableList<string> files) => Files = files;

    public RandomizableList<string> Files { get; }

    /// <summary>
    /// Index of the current slide. Equals Files.Count when "past the end" (e.g. every remaining
    /// file was deleted), which the UI treats as a blank/end-of-show state.
    /// </summary>
    public int CurrentIndex { get; private set; }

    public bool Loop { get; set; }
    public bool RandomMode => Files.IsRandomized;
    public bool RerandomizeOnLoop { get; set; } = true;

    /// <summary>Seconds between automatic advances; 0 means off.</summary>
    public double AutoAdvanceSeconds { get; private set; }
    public bool Paused { get; private set; }

    public bool IsTimerActive => AutoAdvanceSeconds > 0 && !Paused;
    public bool IsPastEnd => CurrentIndex >= Files.Count;
    public string? CurrentFile => CurrentIndex < Files.Count ? Files[CurrentIndex] : null;

    /// <summary>Raised after the current slide changes (including loop wrap-around).</summary>
    public event Action? CurrentChanged;

    // ---- loading ------------------------------------------------------------------

    /// <summary>Loads files (already in display order). If random is true the list is shuffled with startIndex first.</summary>
    public void Load(IEnumerable<string> files, int startIndex = 0, bool random = false)
    {
        Files.SetItems(files);
        _view.Clear();
        CurrentIndex = Files.Count == 0 ? 0 : Math.Clamp(startIndex, 0, Files.Count - 1);
        if (random && Files.Count > 0)
        {
            Files.Randomize(CurrentIndex);
            CurrentIndex = 0;
        }
        CurrentChanged?.Invoke();
    }

    /// <summary>Turns shuffle on/off, keeping the current slide current.</summary>
    public void SetRandom(bool on)
    {
        if (on == Files.IsRandomized || Files.Count == 0) return;
        if (on)
        {
            if (CurrentIndex < Files.Count) { Files.Randomize(CurrentIndex); CurrentIndex = 0; }
            else Files.Randomize();
        }
        else
        {
            int ordered = CurrentIndex < Files.Count ? Files.OrderedIndexFromIndex(CurrentIndex) : 0;
            Files.Derandomize();
            CurrentIndex = ordered;
        }
    }

    // ---- navigation (port of jump:, jump:ordered:, jumpTo:) -------------------------------

    /// <summary>Go forward n slides (negative = back), honouring loop mode.</summary>
    public JumpResult Jump(int n)
    {
        int count = Files.Count;
        if (count == 0 || n == 0) return JumpResult.Blocked;
        if (n > 0) Paused = false;                 // going forward un-pauses auto-advance

        JumpResult result;
        if ((n > 0 && CurrentIndex + 1 >= count) || (n < 0 && CurrentIndex == 0))
        {
            if (Loop)
            {
                // Reshuffle each time we wrap forward through the end of a random slideshow.
                if (RandomMode && n > 0 && RerandomizeOnLoop) Files.Randomize();
                SetCurrent(n < 0 ? count - 1 : 0);
                result = JumpResult.Looped;
            }
            else
            {
                result = JumpResult.Blocked;
            }
        }
        else
        {
            if (n < 0 && -n > CurrentIndex) n = -CurrentIndex;
            else if (n > 0 && n >= count - CurrentIndex) n = count - 1 - CurrentIndex;
            SetCurrent(CurrentIndex + n);
            result = JumpResult.Moved;
        }

        if (n < 0 && AutoAdvanceSeconds > 0) Paused = true; // going backwards pauses auto-advance
        return result;
    }

    /// <summary>
    /// With <paramref name="ordered"/> and random mode on, steps to the next/previous slide in the
    /// *original* order (and stops auto-advance); otherwise behaves like <see cref="Jump(int)"/>.
    /// </summary>
    public JumpResult Jump(int n, bool ordered)
    {
        if (!(ordered && RandomMode)) return Jump(n);

        SetAutoAdvance(0);
        if (CurrentIndex >= Files.Count) return JumpResult.Blocked;
        int target = n < 0 ? Files.IndexOfPreviousInOrder(CurrentIndex) : Files.IndexOfNextInOrder(CurrentIndex);
        if (target == RandomizableList<string>.NotFound) return JumpResult.Blocked;
        SetCurrent(target);
        return JumpResult.Moved;
    }

    public JumpResult JumpTo(int index)
    {
        if (Files.Count == 0) return JumpResult.Blocked;
        SetCurrent(Math.Clamp(index, 0, Files.Count - 1));
        return JumpResult.Moved;
    }

    /// <summary>Back to the first slide (a step back, so it pauses auto-advance). Blocked if already there.</summary>
    public JumpResult JumpToFirst() => CurrentIndex > 0 ? Jump(-CurrentIndex) : JumpResult.Blocked;

    public JumpResult JumpToLast() => JumpTo(Files.Count - 1);

    /// <summary>
    /// Space: pauses a running auto-advance (returns null), otherwise moves on one slide, in the original order with
    /// <paramref name="inOrder"/>.
    /// </summary>
    public JumpResult? PauseOrNext(bool inOrder)
    {
        if (IsTimerActive) { Pause(); return null; }
        return Jump(1, inOrder);
    }

    // ---- auto-advance ---------------------------------------------------------------------

    public void SetAutoAdvance(double seconds)
    {
        AutoAdvanceSeconds = Math.Max(0, seconds);
        Paused = false;
    }

    /// <summary>
    /// Starts auto-advance from a digit key. If it was off or paused this first moves on one slide, so the first
    /// interval isn't spent looking at a slide already seen; returns that step's result, or null if there was none.
    /// </summary>
    public JumpResult? StartAutoAdvance(double seconds)
    {
        JumpResult? step = IsTimerActive ? null : Jump(1);
        SetAutoAdvance(seconds);
        return step;
    }

    /// <summary>
    /// The auto-advance timer fired. False at the last slide of a show that doesn't loop: auto-advance is then switched
    /// off. Otherwise the caller moves on with <c>Jump(1)</c>.
    /// </summary>
    public bool ContinueAutoAdvance()
    {
        if (CurrentIndex + 1 >= Files.Count && !Loop)
        {
            SetAutoAdvance(0);
            return false;
        }
        return true;
    }

    /// <summary>
    /// Time until the next automatic advance: the set interval, or <paramref name="playThrough"/> if that is longer (an
    /// animated GIF gets to play through at least once).
    /// </summary>
    public TimeSpan AutoAdvanceInterval(TimeSpan? playThrough)
    {
        var interval = TimeSpan.FromSeconds(AutoAdvanceSeconds);
        return playThrough is { } p && p > interval ? p : interval;
    }

    public void Pause() { if (AutoAdvanceSeconds > 0) Paused = true; }

    // ---- list changes (file watcher / delete) ------------------------------------------------

    /// <summary>Removes a slide, keeping the current slide sensible. Returns false if the path isn't present.</summary>
    public bool RemoveFile(string path)
    {
        int idx = Files.IndexOf(path);
        if (idx < 0) return false;
        bool removedCurrent = idx == CurrentIndex;
        Files.RemoveAt(idx);
        _view.Remove(path);
        if (idx < CurrentIndex) CurrentIndex--;                 // same file is still current, just shifted
        else if (CurrentIndex > Files.Count) CurrentIndex = Files.Count;
        if (removedCurrent) CurrentChanged?.Invoke();           // the slide on screen changed (or ran out)
        return true;
    }

    /// <summary>Adds a newly appeared file at its sorted position. While shuffled it is placed just after the current slide.</summary>
    public void AddFile(string path, IComparer<string> comparer)
    {
        int before = Files.Count;
        int idx = Files.Insert(path, comparer, Math.Min(CurrentIndex + 1, before));
        if (Files.Count > before && idx <= CurrentIndex && CurrentIndex < before)
            CurrentIndex++;
    }

    // ---- per-file view memory ----------------------------------------------------------------

    public FileViewState GetViewState(string path)
    {
        if (!_view.TryGetValue(path, out var s)) _view[path] = s = new FileViewState();
        return s;
    }

    public bool TryGetViewState(string path, [MaybeNullWhen(false)] out FileViewState state) => _view.TryGetValue(path, out state);

    public void ResetViewState(string path) => _view.Remove(path);

    private void SetCurrent(int index)
    {
        CurrentIndex = index;
        CurrentChanged?.Invoke();
    }
}
