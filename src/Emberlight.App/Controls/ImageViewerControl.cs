using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Emberlight.Core;

namespace Emberlight.App.Controls;

/// <summary>
/// Displays one picture with fit/zoom/pan/rotate/flip. All the layout decisions come from the
/// (unit-tested) <see cref="ImageViewport"/> in the core library; this control only turns them into a
/// WPF transform and routes mouse input.
///
/// The picture lives in a Border that is exactly pixel-sized (1 DIP per image pixel) and is positioned
/// with a single RenderTransform: centre at origin, mirror, rotate, scale, translate. Because the
/// Border has the *full* image size, a reduced-resolution bitmap can be shown in it and swapped for the
/// full one later without disturbing zoom or pan.
/// </summary>
public sealed class ImageViewerControl : Grid
{
    private readonly Canvas _canvas = new() { ClipToBounds = true };
    private readonly Border _host = new() { Visibility = Visibility.Collapsed, SnapsToDevicePixels = false };
    private readonly Image _image = new() { Stretch = Stretch.Fill };
    private readonly ImageViewport _viewport = new();

    private int _pixelWidth, _pixelHeight;
    private ImageOrientation _orientation = ImageOrientation.Identity;

    private bool _trackingClick, _dragMoved;
    private Point _downPoint, _lastPoint;
    private int _downClickCount;

    public ImageViewerControl()
    {
        Background = Brushes.Transparent;   // a Grid with no background doesn't receive mouse input
        ClipToBounds = true;
        Focusable = false;
        _host.Child = _image;
        _canvas.Children.Add(_host);
        Children.Add(_canvas);
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.HighQuality);
    }

    // ---- public state ---------------------------------------------------------------------

    public ImageViewport Viewport => _viewport;
    public bool HasImage => _pixelWidth > 0 && _pixelHeight > 0;

    /// <summary>Colour shown behind transparent areas of the picture.</summary>
    public Brush? ImageBackground { get => _host.Background; set => _host.Background = value; }

    public bool ScaleUp { get => _viewport.ScaleUp; set { _viewport.ScaleUp = value; UpdateTransform(); } }
    public bool ActualSize { get => _viewport.ShowActualSize; set { _viewport.ShowActualSize = value; UpdateTransform(); } }

    public ImageOrientation Orientation
    {
        get => _orientation;
        set
        {
            if (_orientation == value) return;
            _orientation = value;
            var (w, h) = value.ApplyToSize(_pixelWidth, _pixelHeight);
            _viewport.SetOrientedSize(w, h);
            UpdateTransform();
        }
    }

    /// <summary>Raised whenever the layout changes for any reason (resize, zoom, rotate...).</summary>
    public event EventHandler? ViewChanged;
    /// <summary>Raised when the user changed zoom/pan with the mouse (so the owner can remember it).</summary>
    public event EventHandler? UserViewChanged;
    /// <summary>A click that should count as "advance" (argument = click count). Dragging doesn't raise it.</summary>
    public event EventHandler<int>? Clicked;

    // ---- content --------------------------------------------------------------------------

    public void SetImage(BitmapSource bitmap, int pixelWidth, int pixelHeight, ImageOrientation orientation, ZoomInfo? zoom)
    {
        _pixelWidth = pixelWidth;
        _pixelHeight = pixelHeight;
        _orientation = orientation;
        _image.Source = bitmap;
        _host.Width = pixelWidth;
        _host.Height = pixelHeight;
        _host.Visibility = Visibility.Visible;
        var (w, h) = orientation.ApplyToSize(pixelWidth, pixelHeight);
        _viewport.SetImage(w, h);
        _viewport.ApplyZoomInfo(zoom);
        SyncViewSize();
    }

    /// <summary>Swaps in a different-resolution bitmap of the same picture without touching zoom or pan.</summary>
    public void ReplaceBitmap(BitmapSource bitmap) => _image.Source = bitmap;

    public void Clear()
    {
        _image.Source = null;
        _host.Visibility = Visibility.Collapsed;
        _pixelWidth = _pixelHeight = 0;
        _viewport.SetImage(0, 0);
        UpdateTransform();
    }

    // ---- zoom / pan commands ------------------------------------------------------------------

    public bool ZoomIn() { bool ok = _viewport.ZoomIn(); UpdateTransform(); return ok; }
    public bool ZoomOut() { bool ok = _viewport.ZoomOut(); UpdateTransform(); return ok; }
    public void ZoomActualSize() { _viewport.ZoomActualSize(); UpdateTransform(); }
    public void ResetView() { _viewport.Reset(); UpdateTransform(); }

    /// <summary>Drags the picture by the given amount in device pixels.</summary>
    public void PanBy(double dxDevice, double dyDevice) { _viewport.PanBy(dxDevice, dyDevice); UpdateTransform(); }

    // ---- layout ---------------------------------------------------------------------------------

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        SyncViewSize();
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        SyncViewSize();
    }

    private double Dpi => VisualTreeHelper.GetDpi(this).DpiScaleX;

    private void SyncViewSize()
    {
        double dpi = Dpi;
        _viewport.SetView(ActualWidth * dpi, ActualHeight * dpi);
        UpdateTransform();
    }

    private void UpdateTransform()
    {
        if (!HasImage)
        {
            Cursor = Cursors.Arrow;
            ViewChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        double dpi = Dpi;
        double s = _viewport.Scale;

        var group = new TransformGroup();
        group.Children.Add(new TranslateTransform(-_pixelWidth / 2.0, -_pixelHeight / 2.0)); // image centre -> origin
        if (_orientation.Flipped) group.Children.Add(new ScaleTransform(-1, 1));               // mirror
        if (_orientation.RotationCw != 0) group.Children.Add(new RotateTransform(_orientation.RotationCw)); // clockwise
        group.Children.Add(new ScaleTransform(s / dpi, s / dpi));                              // device px -> DIPs
        group.Children.Add(new TranslateTransform(_viewport.TranslateX / dpi, _viewport.TranslateY / dpi));
        _host.RenderTransform = group;

        // Crisp pixels when zoomed well in; smooth resampling otherwise.
        RenderOptions.SetBitmapScalingMode(_image, s >= 3 ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality);
        Cursor = _viewport.CanPan ? Cursors.Hand : Cursors.Arrow;
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    // ---- mouse ------------------------------------------------------------------------------------

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (!HasImage || !_viewport.CanPan)
        {
            Clicked?.Invoke(this, e.ClickCount);
            e.Handled = true;
            return;
        }
        // Zoomed in: this might be the start of a drag, so decide on mouse-up.
        _trackingClick = true;
        _dragMoved = false;
        _downClickCount = e.ClickCount;
        _downPoint = _lastPoint = e.GetPosition(this);
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_trackingClick || e.LeftButton != MouseButtonState.Pressed) return;
        var p = e.GetPosition(this);
        double dx = p.X - _lastPoint.X, dy = p.Y - _lastPoint.Y;
        if (Math.Abs(dx) + Math.Abs(dy) < 1) return;
        if (Math.Abs(p.X - _downPoint.X) > 3 || Math.Abs(p.Y - _downPoint.Y) > 3) _dragMoved = true;
        double dpi = Dpi;
        PanBy(dx * dpi, dy * dpi);
        _lastPoint = p;
        UserViewChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_trackingClick) return;
        _trackingClick = false;
        ReleaseMouseCapture();
        if (!_dragMoved) Clicked?.Invoke(this, _downClickCount);
        e.Handled = true;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (!HasImage) return;
        double dpi = Dpi;

        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            var p = e.GetPosition(this);
            if (_viewport.ZoomBy(e.Delta > 0 ? 0.25 : -0.2, p.X * dpi, p.Y * dpi))
            {
                UpdateTransform();
                UserViewChanged?.Invoke(this, EventArgs.Empty);
            }
            e.Handled = true;
        }
        else if (_viewport.CanPan)
        {
            double d = e.Delta / 120.0 * 128 * dpi;   // the original scrolls 128 px per wheel notch
            if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) PanBy(d, 0); else PanBy(0, d);
            UserViewChanged?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
        }
    }
}
