using System.Windows.Threading;
using Emberlight.App.ViewModels;
using Emberlight.Core;

namespace Emberlight.App.Services;

/// <summary>
/// The WPF side of thumbnail loading: <see cref="ThumbnailScheduler{TItem, TImage}"/> (Core) decides what to
/// decode and when; this supplies the WIC decoder, the file check for cached thumbnails, and delivery on the
/// UI thread through the Dispatcher.
/// </summary>
public sealed class ThumbnailLoader : IDisposable
{
    private readonly ThumbnailScheduler<ThumbnailItem, LoadedImage> _scheduler;

    public ThumbnailLoader(Dispatcher dispatcher, int workerCount = 0)
    {
        _scheduler = new ThumbnailScheduler<ThumbnailItem, LoadedImage>(
            (path, size) => ImageService.LoadThumbnail(path, size, UseEmbeddedPreviews),
            (path, image) => image.Info.MatchesFile(path),
            (item, image) => item.SetLoaded(image),
            action => _ = dispatcher.BeginInvoke(DispatcherPriority.Background, action),
            workerCount: workerCount > 0 ? workerCount : ThumbnailScheduler<ThumbnailItem, LoadedImage>.DefaultWorkerCount);
    }

    /// <summary>Use the JPEG's embedded EXIF preview when it's big enough (much faster than decoding the photo).</summary>
    public bool UseEmbeddedPreviews { get; set; } = true;

    public int DecodeSize => _scheduler.DecodeSize;

    /// <summary>Forget everything queued (e.g. when navigating to another folder).</summary>
    public void Reset() => _scheduler.Reset();

    /// <summary>Changes the decode size. Returns true if it changed (callers should then reset item thumbnails).</summary>
    public bool Configure(int decodeSize) => _scheduler.Configure(decodeSize);

    public void Invalidate(string path) => _scheduler.Invalidate(path);

    /// <summary>UI thread only. Asks for the thumbnails of items [first, end), the cells on or near the screen.</summary>
    public void RequestRange(IReadOnlyList<ThumbnailItem> items, int first, int end) => _scheduler.RequestRange(items, first, end);

    public void Dispose() => _scheduler.Dispose();
}
