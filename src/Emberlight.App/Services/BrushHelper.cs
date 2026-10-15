using System.Windows.Media;
using Emberlight.Core;

namespace Emberlight.App.Services;

public static class BrushHelper
{
    /// <summary>Turns "#RRGGBB" / "#AARRGGBB" / a named colour (read by <see cref="ColorText"/>) into a frozen brush, or returns <paramref name="fallback"/>.</summary>
    public static Brush? FromString(string? text, Brush? fallback)
    {
        if (!ColorText.TryParse(text, out uint argb)) return fallback;
        var (a, r, g, b) = ColorText.Split(argb);
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }
}
