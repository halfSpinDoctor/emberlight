using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Emberlight.Core;

namespace Emberlight.App.Services;

/// <summary>
/// Plays an animated GIF on the UI thread: <see cref="GifCompositor"/> (Core) composes each frame, and this writes it
/// into one <see cref="Bitmap"/> that the viewer keeps showing, so zoom, pan and rotation carry on undisturbed.
/// </summary>
public sealed class GifPlayer : IDisposable
{
    private readonly GifCompositor _compositor;
    private readonly DispatcherTimer _timer = new(DispatcherPriority.Render);

    public WriteableBitmap Bitmap { get; }

    public GifPlayer(GifAnimation animation)
    {
        _compositor = new GifCompositor(animation);
        Bitmap = new WriteableBitmap(animation.Width, animation.Height, 96, 96, PixelFormats.Bgra32, null);
        Show();
        _timer.Tick += (_, _) =>
        {
            _compositor.Advance();
            Show();
            _timer.Interval = _compositor.CurrentFrame.Delay;
        };
    }

    public void Start()
    {
        _timer.Interval = _compositor.CurrentFrame.Delay;
        _timer.Start();
    }

    public void Dispose() => _timer.Stop();

    private void Show() => Bitmap.WritePixels(new Int32Rect(0, 0, Bitmap.PixelWidth, Bitmap.PixelHeight),
        _compositor.Canvas, _compositor.Stride, 0);
}
