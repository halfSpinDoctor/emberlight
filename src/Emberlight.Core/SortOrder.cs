namespace Emberlight.Core;

public enum SortField
{
    Name = 1,
    DateModified = 2,
    ExifDate = 3,
    /// <summary>Windows has no "date added"; the file creation time is the closest equivalent.</summary>
    DateAdded = 4,
    Type = 5,
    Size = 6,
    FilePath = 7,
}

/// <summary>A sort key plus direction. Round-trips to the original app's signed-integer encoding.</summary>
public readonly record struct SortOrder(SortField Key, bool Descending)
{
    public static SortOrder Default => new(SortField.Name, false);

    /// <summary>Original encoding: 1-7 for the key, negative for descending.</summary>
    public static SortOrder FromLegacy(int value)
    {
        bool desc = value < 0;
        int k = Math.Abs(value);
        return new SortOrder(k is >= 1 and <= 7 ? (SortField)k : SortField.Name, desc);
    }

    public int ToLegacy() => Descending ? -(int)Key : (int)Key;

    public SortOrder Reversed() => this with { Descending = !Descending };

    /// <summary>Whether sorting has to open every file (for its dates or size), which can take a while.</summary>
    public bool NeedsFileInspection => Key is not (SortField.Name or SortField.FilePath or SortField.Type);
}
