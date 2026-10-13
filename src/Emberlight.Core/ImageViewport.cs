namespace Emberlight.Core;

/// <summary>Zoom step helpers. Port of DYImageView's zoomIn/zoomOut stepping.</summary>
public static class ZoomMath
{
    public const double MaxZoom = 512;
    public const double MinZoom = 0.002;

    // The original steps through "half powers of two": ..., 0.5, 0.75, 1, 1.5, 2, 3, 4, 6, 8, ...
    private static double Step(int n) => n % 2 != 0 ? 1.5 * Math.Pow(2, (n - 1) / 2) : Math.Pow(2, n / 2);

    /// <summary>The smallest step strictly greater than <paramref name="current"/>.</summary>
    public static double NextZoomIn(double current)
    {
        int n = (int)Math.Floor(2 * Math.Log2(current));
        double f;
        do { n++; f = Step(n); } while (f <= current);
        return f;
    }

    /// <summary>The largest step strictly smaller than <paramref name="current"/>.</summary>
    public static double NextZoomOut(double current)
    {
        int n = (int)Math.Ceiling(2 * Math.Log2(current));
        double f;
        do { n--; f = Step(n); } while (f >= current);
        return f;
    }
}

/// <summary>
/// Zoom/pan/fit model for one displayed image. Pure math, no UI types, so it can be unit tested.
/// Port of the layout logic in DYImageView.calculateRectsAndSetNeedsDisplay plus zoomIn/zoomOut/zoomBy.
///
/// All lengths are in device pixels: the view size is the control's pixel size, and the image size
/// is the *oriented* image's pixel size (width/height already swapped for 90°/270° rotations).
/// "Scale" is device pixels per image pixel. The visible point is described by (CenterX, CenterY),
/// the image-space point shown at the middle of the view.
/// </summary>
public sealed class ImageViewport
{
    private double _zoom;   // 0 = default (fit / actual size); otherwise an explicit scale
    private double _cx, _cy;

    public double ViewWidth { get; private set; }
    public double ViewHeight { get; private set; }
    public double ImageWidth { get; private set; }
    public double ImageHeight { get; private set; }

    /// <summary>Allow small images to be enlarged to fill the view by default.</summary>
    public bool ScaleUp { get => _scaleUp; set { _scaleUp = value; Clamp(); } }
    /// <summary>Show images at 100% by default (scaled down only if you turn this off). See remarks in <see cref="Scale"/>.</summary>
    public bool ShowActualSize { get => _showActualSize; set { _showActualSize = value; Clamp(); } }
    private bool _scaleUp, _showActualSize;

    public double CenterX => _cx;
    public double CenterY => _cy;

    /// <summary>True once the user has explicitly zoomed (as opposed to the default fit/actual-size layout).</summary>
    public bool IsZoomed => _zoom != 0;

    public bool HasImage => ImageWidth > 0 && ImageHeight > 0 && ViewWidth > 0 && ViewHeight > 0;

    /// <summary>Device pixels per image pixel currently in effect.</summary>
    public double Scale
    {
        get
        {
            if (_zoom != 0) return _zoom;
            if (!HasImage) return 1;
            bool strictlySmaller = ImageWidth < ViewWidth && ImageHeight < ViewHeight;
            // With both options on, small images are scaled up to fit while big ones show at 100%.
            if (_showActualSize && !(_scaleUp && strictlySmaller)) return 1;
            if (!_scaleUp && ImageWidth <= ViewWidth && ImageHeight <= ViewHeight) return 1;
            return Math.Min(ViewWidth / ImageWidth, ViewHeight / ImageHeight);
        }
    }

    /// <summary>Size of the image on screen at the current scale.</summary>
    public (double Width, double Height) ScaledSize => (ImageWidth * Scale, ImageHeight * Scale);

    /// <summary>True when part of the image is off-screen, i.e. dragging can pan it.</summary>
    public bool CanPan
    {
        get
        {
            var (w, h) = ScaledSize;
            return w > ViewWidth + 0.5 || h > ViewHeight + 0.5;
        }
    }

    /// <summary>Translation (device px) that places the image's centre where it belongs, given a transform that scales about the image centre.</summary>
    public double TranslateX => ViewWidth / 2 - (_cx - ImageWidth / 2) * Scale;
    public double TranslateY => ViewHeight / 2 - (_cy - ImageHeight / 2) * Scale;

    // ---- setup -----------------------------------------------------------------------

    public void SetView(double width, double height)
    {
        ViewWidth = Math.Max(0, width);
        ViewHeight = Math.Max(0, height);
        Clamp();
    }

    /// <summary>A new image: forget any zoom and centre it.</summary>
    public void SetImage(double width, double height)
    {
        ImageWidth = Math.Max(0, width);
        ImageHeight = Math.Max(0, height);
        _zoom = 0;
        _cx = ImageWidth / 2;
        _cy = ImageHeight / 2;
        Clamp();
    }

    /// <summary>The oriented size changed (rotation/flip): keep the zoom level but re-centre.</summary>
    public void SetOrientedSize(double width, double height)
    {
        ImageWidth = Math.Max(0, width);
        ImageHeight = Math.Max(0, height);
        _cx = ImageWidth / 2;
        _cy = ImageHeight / 2;
        Clamp();
    }

    /// <summary>Back to the default fit (or actual-size) layout, centred.</summary>
    public void Reset()
    {
        _zoom = 0;
        _cx = ImageWidth / 2;
        _cy = ImageHeight / 2;
        Clamp();
    }

    // ---- zooming -----------------------------------------------------------------------

    /// <summary>Returns false when already at the maximum zoom (the original beeps).</summary>
    public bool ZoomIn()
    {
        if (!HasImage) return false;
        if (_zoom >= ZoomMath.MaxZoom) return false;
        double old = Scale;
        double halfW = ViewWidth / (2 * old), halfH = ViewHeight / (2 * old);
        const double eps = 0.5 / 1024; // tolerate float fuzz

        // If the view is pushed against an edge, keep that edge pinned while zooming in.
        double newCx = _cx, newCy = _cy;
        if (ImageWidth * old > ViewWidth)
        {
            if (_cx - halfW <= eps) newCx = 0;
            else if (_cx + halfW >= ImageWidth - eps) newCx = ImageWidth;
        }
        else newCx = ImageWidth / 2;
        if (ImageHeight * old > ViewHeight)
        {
            if (_cy - halfH <= eps) newCy = 0;
            else if (_cy + halfH >= ImageHeight - eps) newCy = ImageHeight;
        }
        else newCy = ImageHeight / 2;

        _zoom = Math.Min(ZoomMath.NextZoomIn(old), ZoomMath.MaxZoom * 1.5);
        _cx = newCx; _cy = newCy;
        Clamp();
        return true;
    }

    /// <summary>Returns false when already at the minimum zoom.</summary>
    public bool ZoomOut()
    {
        if (!HasImage) return false;
        if (_zoom != 0 && _zoom < ZoomMath.MinZoom) return false;
        _zoom = ZoomMath.NextZoomOut(Scale);
        Clamp();
        return true;
    }

    public void ZoomActualSize()
    {
        if (!HasImage) return;
        if (_zoom != 1) { _zoom = 1; Clamp(); }
    }

    /// <summary>
    /// Zooms by a relative amount (e.g. 0.1 = 10% bigger, -0.1 = 10% smaller) keeping the image point
    /// under (viewX, viewY) in place where possible. For wheel/pinch zooming.
    /// </summary>
    public bool ZoomBy(double magnification, double viewX, double viewY)
    {
        if (!HasImage) return false;
        double cur = Scale;
        if ((magnification < 0 && cur < ZoomMath.MinZoom) || (magnification > 0 && cur >= ZoomMath.MaxZoom)) return false;
        double factor = 1.0 + magnification;
        if (factor <= 0) return false;
        var p = ToImage(viewX, viewY);
        double ns = Math.Clamp(cur * factor, ZoomMath.MinZoom / 2, ZoomMath.MaxZoom);
        _cx = p.X - (viewX - ViewWidth / 2) / ns;
        _cy = p.Y - (viewY - ViewHeight / 2) / ns;
        _zoom = ns;
        Clamp();
        return true;
    }

    // ---- panning -------------------------------------------------------------------------

    /// <summary>Drags the picture by (dx, dy) device pixels (content follows the pointer).</summary>
    public void PanBy(double dxView, double dyView)
    {
        if (!HasImage) return;
        double s = Scale;
        _cx -= dxView / s;
        _cy -= dyView / s;
        Clamp();
    }

    // ---- coordinate mapping ----------------------------------------------------------------

    public (double X, double Y) ToView(double imageX, double imageY)
    {
        double s = Scale;
        return (ViewWidth / 2 + (imageX - _cx) * s, ViewHeight / 2 + (imageY - _cy) * s);
    }

    public (double X, double Y) ToImage(double viewX, double viewY)
    {
        double s = Scale;
        return (_cx + (viewX - ViewWidth / 2) / s, _cy + (viewY - ViewHeight / 2) / s);
    }

    // ---- per-slide memory ---------------------------------------------------------------------

    /// <summary>The current zoom to remember for this slide, or null while in the default layout.</summary>
    public ZoomInfo? GetZoomInfo() => _zoom == 0 ? null : new ZoomInfo(_zoom, _cx, _cy);

    public void ApplyZoomInfo(ZoomInfo? info)
    {
        if (info is not { } z || !double.IsFinite(z.Zoom) || z.Zoom <= 0)
        {
            Reset();
            return;
        }
        _zoom = Math.Clamp(z.Zoom, ZoomMath.MinZoom / 2, ZoomMath.MaxZoom * 1.5);
        _cx = double.IsFinite(z.CenterX) ? z.CenterX : ImageWidth / 2;
        _cy = double.IsFinite(z.CenterY) ? z.CenterY : ImageHeight / 2;
        Clamp();
    }

    // ---------------------------------------------------------------------------------------------

    private void Clamp()
    {
        if (!HasImage)
        {
            _cx = ImageWidth / 2;
            _cy = ImageHeight / 2;
            return;
        }
        double s = Scale;
        double halfW = ViewWidth / (2 * s), halfH = ViewHeight / (2 * s);
        _cx = ImageWidth <= 2 * halfW ? ImageWidth / 2 : Math.Clamp(_cx, halfW, ImageWidth - halfW);
        _cy = ImageHeight <= 2 * halfH ? ImageHeight / 2 : Math.Clamp(_cy, halfH, ImageHeight - halfH);
    }
}
