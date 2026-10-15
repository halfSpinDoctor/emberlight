namespace Emberlight.Core;

/// <summary>A window's position and size, in the platform's screen units (device-independent pixels on Windows).</summary>
public readonly record struct WindowBounds(double Left, double Top, double Width, double Height);

public static class WindowFit
{
    public const double MinWidth = 320, MinHeight = 240;

    /// <summary>The share of the work area a window may grow to.</summary>
    public const double MaxShare = 0.95;

    /// <summary>
    /// Resizes a windowed slideshow to show a picture at its actual size (a preference), keeping the window's centre
    /// where it was. The window stays at least 320 x 240 and at most 95% of the work area.
    /// </summary>
    /// <param name="chrome">Window size minus the picture area (title bar, borders).</param>
    /// <param name="dpiScale">Device pixels per screen unit.</param>
    public static WindowBounds FitToImage(WindowBounds window, (double Width, double Height) chrome, (double Width, double Height) workArea,
        int pixelWidth, int pixelHeight, ImageOrientation orientation, double dpiScale)
    {
        var (w, h) = orientation.ApplyToSize(pixelWidth, pixelHeight);
        double newW = Math.Clamp(w / dpiScale + chrome.Width, MinWidth, Math.Max(MinWidth, workArea.Width * MaxShare));
        double newH = Math.Clamp(h / dpiScale + chrome.Height, MinHeight, Math.Max(MinHeight, workArea.Height * MaxShare));
        return new WindowBounds(window.Left + (window.Width - newW) / 2, window.Top + (window.Height - newH) / 2, newW, newH);
    }
}
