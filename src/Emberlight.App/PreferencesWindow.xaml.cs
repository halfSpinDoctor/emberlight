using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Emberlight.App.Services;
using Emberlight.Core;

namespace Emberlight.App;

public partial class PreferencesWindow : Window
{
    private readonly AppSettings _settings;
    private readonly ImageFileTypes _types;
    private readonly Dictionary<string, CheckBox> _typeBoxes = new();

    /// <summary>True if the set of enabled file types was changed (so the file list must be rebuilt).</summary>
    public bool FileTypesChanged { get; private set; }

    public PreferencesWindow(AppSettings settings, ImageFileTypes types)
    {
        InitializeComponent();
        _settings = settings;
        _types = types;

        // ---- load current values into the controls ----
        StartLast.IsChecked = settings.StartupOption == StartupFolder.LastFolder;
        StartPictures.IsChecked = settings.StartupOption == StartupFolder.PicturesFolder;
        PicturesPath.Text = string.IsNullOrWhiteSpace(settings.PicturesFolderPath)
            ? Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)
            : settings.PicturesFolderPath;
        AutoRotate.IsChecked = settings.AutoRotateByOrientationTag;
        PreserveModDate.IsChecked = settings.PreserveModifiedDateOnEdit;
        OpenDoSlideshow.IsChecked = settings.OpenFilesDoSlideshow;
        OpenSubfolders.IsChecked = settings.StartupSlideshowSubfolders;

        foreach (var (field, label) in SortOrder.Choices)
        {
            var choice = new ComboBoxItem { Content = label, Tag = field };
            SortFieldBox.Items.Add(choice);
            if (field == settings.SortOrder.Key) SortFieldBox.SelectedItem = choice;
        }
        SortDescendingBox.IsChecked = settings.SortOrder.Descending;
        ShowFilenames.IsChecked = settings.ShowFilenames;
        IncludeSubfolders.IsChecked = settings.IncludeSubfolders;
        SubfoldersAlways.IsChecked = settings.IncludeSubfoldersMode == SubfolderMode.Always;
        SubfoldersReset.IsChecked = settings.IncludeSubfoldersMode == SubfolderMode.ResetOnNewTree;
        InfoPanel.IsChecked = settings.InfoPanelVisible;
        MoreExif.IsChecked = settings.InfoPanelMoreExif;

        FullScreen.IsChecked = settings.SlideshowDefaultFullScreen;
        AutoAdvance.IsChecked = settings.SlideshowAutoAdvance;
        AutoSeconds.Text = PreferencesInput.FormatSeconds(settings.SlideshowAutoAdvanceSeconds, CultureInfo.CurrentCulture);
        Loop.IsChecked = settings.SlideshowLoop;
        RandomOrder.IsChecked = settings.SlideshowRandom;
        Rerandomize.IsChecked = settings.RerandomizeOnLoop;
        SuppressLoop.IsChecked = settings.SuppressLoopIndicator;
        ScaleUp.IsChecked = settings.SlideshowScaleUp;
        ActualSize.IsChecked = settings.SlideshowActualSize;
        FitWindow.IsChecked = settings.SlideshowWindowFitToImage;
        PlayGifs.IsChecked = settings.SlideshowPlayGifs;
        GifsFinish.IsChecked = settings.SlideshowGifsFinish;
        BackgroundColor.Text = settings.SlideshowBackgroundColor;
        TransparentColor.Text = settings.TransparentImageBackgroundColor;

        foreach (var ext in types.AllExtensions)
        {
            var box = new CheckBox { Content = ext, IsChecked = types.IsEnabled(ext), Margin = new Thickness(0, 0, 0, 4) };
            _typeBoxes[ext] = box;
            FileTypesPanel.Children.Add(box);
        }
    }

    private void OnBrowsePictures(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Choose the startup folder" };
        if (Directory.Exists(PicturesPath.Text)) dlg.InitialDirectory = PicturesPath.Text;
        if (dlg.ShowDialog(this) == true)
        {
            PicturesPath.Text = dlg.FolderName;
            StartPictures.IsChecked = true;
        }
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        // validate first, so an error leaves everything unchanged
        if (!PreferencesInput.TryParseSeconds(AutoSeconds.Text, CultureInfo.CurrentCulture, out double seconds))
        {
            MessageBox.Show(this, PreferencesInput.SecondsError, "Preferences",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            AutoSeconds.Focus();
            return;
        }
        if (!PreferencesInput.ColorsValid(BackgroundColor.Text, TransparentColor.Text))
        {
            MessageBox.Show(this, PreferencesInput.ColorError, "Preferences",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _settings.StartupOption = StartPictures.IsChecked == true ? StartupFolder.PicturesFolder : StartupFolder.LastFolder;
        _settings.PicturesFolderPath = PicturesPath.Text.Trim();
        _settings.AutoRotateByOrientationTag = AutoRotate.IsChecked == true;
        _settings.PreserveModifiedDateOnEdit = PreserveModDate.IsChecked == true;
        _settings.OpenFilesDoSlideshow = OpenDoSlideshow.IsChecked == true;
        _settings.StartupSlideshowSubfolders = OpenSubfolders.IsChecked == true;

        if (SortFieldBox.SelectedItem is ComboBoxItem { Tag: SortField field })
            _settings.SortOrder = new SortOrder(field, SortDescendingBox.IsChecked == true);
        _settings.ShowFilenames = ShowFilenames.IsChecked == true;
        _settings.IncludeSubfolders = IncludeSubfolders.IsChecked == true;
        _settings.IncludeSubfoldersMode = SubfoldersAlways.IsChecked == true ? SubfolderMode.Always : SubfolderMode.ResetOnNewTree;
        _settings.InfoPanelVisible = InfoPanel.IsChecked == true;
        _settings.InfoPanelMoreExif = MoreExif.IsChecked == true;

        _settings.SlideshowDefaultFullScreen = FullScreen.IsChecked == true;
        _settings.SlideshowAutoAdvance = AutoAdvance.IsChecked == true;
        _settings.SlideshowAutoAdvanceSeconds = seconds;
        _settings.SlideshowLoop = Loop.IsChecked == true;
        _settings.SlideshowRandom = RandomOrder.IsChecked == true;
        _settings.RerandomizeOnLoop = Rerandomize.IsChecked == true;
        _settings.SuppressLoopIndicator = SuppressLoop.IsChecked == true;
        _settings.SlideshowScaleUp = ScaleUp.IsChecked == true;
        _settings.SlideshowActualSize = ActualSize.IsChecked == true;
        _settings.SlideshowWindowFitToImage = FitWindow.IsChecked == true;
        _settings.SlideshowPlayGifs = PlayGifs.IsChecked == true;
        _settings.SlideshowGifsFinish = GifsFinish.IsChecked == true;
        _settings.SlideshowBackgroundColor = BackgroundColor.Text.Trim();
        _settings.TransparentImageBackgroundColor = TransparentColor.Text.Trim();

        foreach (var (ext, box) in _typeBoxes)
        {
            bool enabled = box.IsChecked == true;
            if (enabled != _types.IsEnabled(ext))
            {
                _types.SetEnabled(ext, enabled);
                FileTypesChanged = true;
            }
        }
        _settings.IgnoredFileTypes = _types.DisabledExtensions.ToList();

        DialogResult = true;
    }
}
