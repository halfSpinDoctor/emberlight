using System.Globalization;

namespace Emberlight.Core;

/// <summary>Checks the Preferences window's text fields before anything is changed.</summary>
public static class PreferencesInput
{
    /// <summary>The longest auto-advance time accepted, in seconds (an hour).</summary>
    public const double MaxAutoAdvanceSeconds = 3600;

    public const string SecondsError = "Enter the auto-advance time as a number of seconds greater than 0.";
    public const string ColorError = "Colours must look like #RRGGBB (or #AARRGGBB, or a colour name such as Black).";

    /// <summary>The auto-advance time as typed in the user's number format: more than 0, at most an hour.</summary>
    public static bool TryParseSeconds(string? text, IFormatProvider culture, out double seconds)
        => double.TryParse(text, NumberStyles.Float, culture, out seconds) && seconds > 0 && seconds <= MaxAutoAdvanceSeconds;

    /// <summary>How the seconds field shows a saved value (up to two decimals, in the user's number format).</summary>
    public static string FormatSeconds(double seconds, IFormatProvider culture) => seconds.ToString("0.##", culture);

    /// <summary>Both colour fields hold colours <see cref="ColorText"/> can read.</summary>
    public static bool ColorsValid(params string?[] colors) => colors.All(ColorText.IsValid);
}

/// <summary>What the browser has to redo after Preferences' OK.</summary>
public enum SortFollowUp
{
    /// <summary>The sort order didn't change.</summary>
    None,
    /// <summary>Just record the new order: the rescan that follows sorts with it anyway.</summary>
    SetOrder,
    /// <summary>Re-sort the files already on screen.</summary>
    Resort,
}

/// <param name="Rescan">The file list itself changes (different file types or Include subfolders), so read the folder again.</param>
public readonly record struct PreferencesFollowUp(bool Rescan, SortFollowUp Sort)
{
    /// <summary>
    /// Decides the follow-up from what changed: a rescan when the enabled file types or Include subfolders
    /// changed, and for a new sort order either a re-sort or, when rescanning anyway, only the new order.
    /// </summary>
    public static PreferencesFollowUp From(bool fileTypesChanged, bool subfoldersChanged, SortOrder oldSort, SortOrder newSort)
    {
        bool rescan = fileTypesChanged || subfoldersChanged;
        var sort = newSort == oldSort ? SortFollowUp.None : rescan ? SortFollowUp.SetOrder : SortFollowUp.Resort;
        return new PreferencesFollowUp(rescan, sort);
    }
}
