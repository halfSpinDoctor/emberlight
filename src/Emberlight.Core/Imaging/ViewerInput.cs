namespace Emberlight.Core;

/// <summary>
/// How mouse and keypad input on the picture turns into zoom and pan amounts. The viewer control only
/// reads the input and passes these numbers to <see cref="ImageViewport"/>. Lengths are in device pixels
/// unless the name says otherwise.
/// </summary>
public static class ViewerInput
{
    /// <summary>Wheel delta of one notch (Windows' WHEEL_DELTA).</summary>
    private const double WheelNotch = 120;

    /// <summary>The original scrolls 128 px per wheel notch when the picture can pan.</summary>
    private const double PanPerNotch = 128;

    /// <summary>Ctrl+wheel: 25% bigger per notch up, 20% smaller per notch down (so up then down is back to the start).</summary>
    private const double WheelZoomIn = 0.25, WheelZoomOut = -0.2;

    /// <summary>A press that moves further than this (in DIPs, on either axis) is a drag, not a click.</summary>
    private const double DragThreshold = 3;

    /// <summary>From this scale up, pixels are drawn as crisp squares instead of being smoothed.</summary>
    private const double PixelatedScale = 3;

    /// <summary>The relative zoom for one Ctrl+wheel event (any upward delta zooms in, anything else zooms out).</summary>
    public static double WheelZoomStep(int wheelDelta) => wheelDelta > 0 ? WheelZoomIn : WheelZoomOut;

    /// <summary>
    /// How far a plain wheel event pans the picture: <see cref="PanPerNotch"/> per notch, scaled to device
    /// pixels, along x with Shift held and along y otherwise. Wheel up moves the picture down (shows the top).
    /// </summary>
    public static (double Dx, double Dy) WheelPan(int wheelDelta, double dpiScale, bool horizontal)
    {
        double d = wheelDelta / WheelNotch * PanPerNotch * dpiScale;
        return horizontal ? (d, 0) : (0, d);
    }

    /// <summary>Pointer movement too small to be worth panning for (under one DIP in total).</summary>
    public static bool IsNegligibleMove(double dx, double dy) => Math.Abs(dx) + Math.Abs(dy) < 1;

    /// <summary>Whether the pointer has left the click zone around where the button went down (DIPs).</summary>
    public static bool IsDrag(double downX, double downY, double x, double y)
        => Math.Abs(x - downX) > DragThreshold || Math.Abs(y - downY) > DragThreshold;

    /// <summary>Nearest-neighbour scaling (sharp pixels) when zoomed well in; smooth resampling otherwise.</summary>
    public static bool UseNearestNeighbour(double scale) => scale >= PixelatedScale;

    /// <summary>
    /// The pan for a numeric-keypad key, laid out like the keypad: 7 8 9 on top, 1 2 3 at the bottom.
    /// A key reveals that side of the picture by half a view, so the picture itself moves the other way
    /// (4 moves it right, 8 moves it down). Null for 5 (the original beeps) and for anything not 1-9.
    /// </summary>
    public static (double Dx, double Dy)? KeypadPan(int digit, double viewWidth, double viewHeight)
    {
        if (digit is < 1 or > 9 or 5) return null;
        int col = (digit - 1) % 3, row = (digit - 1) / 3;            // 1-3 bottom row, 7-9 top row
        double w = viewWidth / 2, h = viewHeight / 2;
        double dx = col == 0 ? +w : col == 2 ? -w : 0;
        double dy = row == 2 ? +h : row == 0 ? -h : 0;
        return (dx, dy);
    }
}
