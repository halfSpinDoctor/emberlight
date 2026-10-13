using System.ComponentModel;
using System.Windows.Media;
using Emberlight.App.Controls;
using Emberlight.App.Services;
using Emberlight.Core;

namespace Emberlight.App.ViewModels;

/// <summary>One cell in the thumbnail grid.</summary>
public sealed class ThumbnailItem : INotifyPropertyChanged
{
    /// <summary>Whether thumbnails are shown upright according to their EXIF orientation tag. Set from preferences.</summary>
    public static bool AutoRotate { get; set; } = true;

    private ImageSource? _thumbnail = ImageService.LoadingPlaceholder;
    private Transform _orientationTransform = Transform.Identity;

    public ThumbnailItem(string path)
    {
        Path = path;
        FileName = System.IO.Path.GetFileName(path);
    }

    public string Path { get; }
    public string FileName { get; }

    public ImageSource? Thumbnail => _thumbnail;
    public Transform OrientationTransform => _orientationTransform;

    /// <summary>What was decoded for this item (null until loaded, or if the file couldn't be read).</summary>
    public LoadedImage? Info { get; private set; }
    public bool IsLoaded { get; private set; }

    // Bookkeeping for ThumbnailLoader. Touched from worker threads, hence volatile.
    public volatile bool IsQueued;
    public volatile int WantedStamp = -1;
    public int Generation;

    public string ToolTipText
    {
        get
        {
            if (Info == null) return FileName;
            return $"{FileName}\n{Info.PixelWidth} \u00D7 {Info.PixelHeight}   {FileSizeFormatter.Format((ulong)Info.FileSize)}";
        }
    }

    public void SetLoaded(LoadedImage? image)
    {
        Info = image;
        IsLoaded = true;
        IsQueued = false;
        if (image == null)
        {
            _thumbnail = ImageService.BrokenPlaceholder;
            _orientationTransform = Transform.Identity;
        }
        else
        {
            _thumbnail = image.Bitmap;
            _orientationTransform = AutoRotate ? OrientationTransforms.For(image.ExifOrientation) : Transform.Identity;
        }
        RaiseAll();
    }

    /// <summary>Forget the thumbnail so it will be reloaded (file changed, or thumbnail size changed).</summary>
    public void ResetThumbnail()
    {
        IsLoaded = false;
        IsQueued = false;
        Info = null;
        _thumbnail = ImageService.LoadingPlaceholder;
        _orientationTransform = Transform.Identity;
        RaiseAll();
    }

    /// <summary>Re-applies the auto-rotate preference to an already loaded thumbnail.</summary>
    public void RefreshOrientation()
    {
        if (Info == null) return;
        _orientationTransform = AutoRotate ? OrientationTransforms.For(Info.ExifOrientation) : Transform.Identity;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(OrientationTransform)));
    }

    private void RaiseAll()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Thumbnail)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(OrientationTransform)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ToolTipText)));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
