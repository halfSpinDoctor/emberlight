namespace Emberlight.Core;

/// <summary>
/// Geometry for the thumbnail grid. Cells are a uniform size, so which items are on screen can be
/// computed directly instead of asking WPF for every container; that keeps scrolling cheap even
/// with thousands of files.
/// </summary>
public static class ThumbnailGridMath
{
    /// <summary>Space around each cell (on every side).</summary>
    public const double CellMargin = 4;

    /// <summary>Height of the file name line under a thumbnail.</summary>
    public const double CaptionHeight = 20;

    /// <summary>How much the Larger/Smaller commands change the cell width.</summary>
    public const double SizeStep = 20;

    /// <summary>A cell width as the grid uses it: whole units, within the allowed range.</summary>
    public static double ClampCellWidth(double width)
        => Math.Clamp(Math.Round(width), AppSettings.MinThumbCellWidth, AppSettings.MaxThumbCellWidth);

    /// <summary>A cell's width or height including its margin on both sides.</summary>
    public static double OuterSize(double cellSize) => cellSize + 2 * CellMargin;

    /// <summary>Cell height for a given width: thumbnails are 4:3 boxes, plus a caption line when filenames are shown.</summary>
    public static double CellHeight(double cellWidth, bool showFilenames, double captionHeight = CaptionHeight)
        => Math.Round(cellWidth * 3 / 4) + (showFilenames ? captionHeight : 0);

    public static int Columns(double viewportWidth, double outerCellWidth)
        => Math.Max(1, (int)Math.Floor(viewportWidth / Math.Max(1, outerCellWidth)));

    public static int RowCount(int itemCount, int columns)
        => itemCount <= 0 ? 0 : (itemCount + Math.Max(1, columns) - 1) / Math.Max(1, columns);

    /// <summary>Total scrollable height of the grid.</summary>
    public static double ExtentHeight(int itemCount, int columns, double outerCellHeight)
        => RowCount(itemCount, columns) * outerCellHeight;

    /// <summary>Keeps a scroll offset inside [0, extent - viewport] (and repairs NaN/infinity).</summary>
    public static double ClampOffset(double offset, double extent, double viewport)
    {
        if (!double.IsFinite(offset)) offset = 0;
        return Math.Clamp(offset, 0, Math.Max(0, extent - viewport));
    }

    /// <summary>Top-left corner of an item's cell, in grid coordinates (before subtracting the scroll offset).</summary>
    public static (double X, double Y) CellOrigin(int index, int columns, double outerCellWidth, double outerCellHeight)
    {
        columns = Math.Max(1, columns);
        return (index % columns * outerCellWidth, index / columns * outerCellHeight);
    }

    /// <summary>
    /// The smallest scroll change that makes the item's row fully visible: the current offset if it
    /// already is, otherwise the row aligned to the nearest edge (or to the top if a row is taller than the viewport).
    /// </summary>
    public static double OffsetToReveal(int index, int columns, double outerCellHeight, double currentOffset, double viewportHeight)
    {
        double top = index / Math.Max(1, columns) * outerCellHeight;
        double bottom = top + outerCellHeight;
        if (top < currentOffset) return top;
        if (bottom > currentOffset + viewportHeight)
            return outerCellHeight >= viewportHeight ? top : bottom - viewportHeight;
        return currentOffset;
    }

    /// <summary>
    /// The window of items that should keep their decoded thumbnails: the visible range widened by
    /// <paramref name="margin"/> items each way and clamped to the list. Items outside it can release their bitmaps.
    /// </summary>
    public static (int First, int EndExclusive) RetainedRange(int itemCount, int first, int endExclusive, int margin)
    {
        if (itemCount <= 0) return (0, 0);
        int f = Math.Clamp(first - margin, 0, itemCount);
        int e = Math.Clamp(endExclusive + margin, f, itemCount);
        return (f, e);
    }

    /// <summary>Whole rows that fit in the viewport (at least 1); used for PageUp/PageDown.</summary>
    public static int VisibleRowCount(double viewportHeight, double outerCellHeight)
        => Math.Max(1, (int)Math.Floor(viewportHeight / Math.Max(1, outerCellHeight)));

    /// <summary>
    /// Indices [First, EndExclusive) of items that are visible (plus <paramref name="overscanRows"/> extra
    /// rows above and below). Always returns a valid, possibly empty, range.
    /// </summary>
    public static (int First, int EndExclusive) VisibleRange(
        int itemCount, double viewportWidth, double viewportHeight, double scrollOffsetY,
        double outerCellWidth, double outerCellHeight, int overscanRows = 1)
    {
        if (itemCount <= 0 || viewportHeight <= 0 || outerCellHeight <= 0) return (0, 0);
        int cols = Columns(viewportWidth, outerCellWidth);
        int firstRow = (int)Math.Floor(Math.Max(0, scrollOffsetY) / outerCellHeight) - overscanRows;
        int lastRow = (int)Math.Ceiling((Math.Max(0, scrollOffsetY) + viewportHeight) / outerCellHeight) + overscanRows;
        firstRow = Math.Max(0, firstRow);
        long first = (long)firstRow * cols;
        long end = (long)lastRow * cols;
        int f = (int)Math.Min(first, itemCount);
        int e = (int)Math.Min(Math.Max(end, f), itemCount);
        return (f, e);
    }
}
