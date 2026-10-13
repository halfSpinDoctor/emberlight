using System.ComponentModel;
using System.Windows;
using Emberlight.Core;

namespace Emberlight.App.ViewModels;

/// <summary>
/// Grid appearance shared by every thumbnail cell (bound from the item template). Keeping it in one
/// observable object means changing the thumbnail size is a single property change rather than
/// touching thousands of items.
/// </summary>
public sealed class ViewSettings : INotifyPropertyChanged
{
    /// <summary>Margin around each cell; the item template uses the same value.</summary>
    public const double CellMargin = 4;
    public const double CaptionHeight = 20;

    private double _cellWidth = 120;
    private bool _showFilenames = true;

    public double CellWidth
    {
        get => _cellWidth;
        set
        {
            double v = Math.Clamp(Math.Round(value), AppSettings.MinThumbCellWidth, AppSettings.MaxThumbCellWidth);
            if (v == _cellWidth) return;
            _cellWidth = v;
            Raise(nameof(CellWidth));
            Raise(nameof(CellHeight));
            Raise(nameof(OuterCellWidth));
            Raise(nameof(OuterCellHeight));
        }
    }

    public bool ShowFilenames
    {
        get => _showFilenames;
        set
        {
            if (value == _showFilenames) return;
            _showFilenames = value;
            Raise(nameof(ShowFilenames));
            Raise(nameof(FilenameVisibility));
            Raise(nameof(CellHeight));
            Raise(nameof(OuterCellHeight));
        }
    }

    public double CellHeight => ThumbnailGridMath.CellHeight(_cellWidth, _showFilenames, CaptionHeight);
    public Visibility FilenameVisibility => _showFilenames ? Visibility.Visible : Visibility.Collapsed;

    public double OuterCellWidth => _cellWidth + 2 * CellMargin;
    public double OuterCellHeight => CellHeight + 2 * CellMargin;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
