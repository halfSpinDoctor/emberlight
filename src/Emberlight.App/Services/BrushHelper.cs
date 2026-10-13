using System.Windows.Media;

namespace Emberlight.App.Services;

public static class BrushHelper
{
    /// <summary>Parses "#RRGGBB" / "#AARRGGBB" / a named colour into a frozen brush, or returns <paramref name="fallback"/>.</summary>
    public static Brush? FromString(string? text, Brush? fallback)
    {
        if (string.IsNullOrWhiteSpace(text)) return fallback;
        try
        {
            if (ColorConverter.ConvertFromString(text.Trim()) is Color c)
            {
                var b = new SolidColorBrush(c);
                b.Freeze();
                return b;
            }
        }
        catch (FormatException) { }
        return fallback;
    }

    public static bool IsValid(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        try { return ColorConverter.ConvertFromString(text.Trim()) is Color; }
        catch (FormatException) { return false; }
    }
}
