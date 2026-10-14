using System.Windows;
using System.Windows.Threading;
using Emberlight.App.Services;
using Emberlight.Core;

namespace Emberlight.App;

public partial class App : Application
{
    public static AppSettings Settings { get; private set; } = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandledException;

        Settings = SettingsStore.Load();
        ImageService.InitPlaceholders();

        // Files/folders passed on the command line ("Open with", drag onto the exe) go straight to a slideshow.
        // The browser opens behind it, so closing the slideshow returns to the browser instead of quitting.
        if (e.Args.Length > 0 && Settings.OpenFilesDoSlideshow)
        {
            var types = new ImageFileTypes();
            types.ApplyIgnored(Settings.IgnoredFileTypes);
            var scanner = new DirectoryScanner(types);
            var comparer = FileComparers.For(Settings.SortOrder);
            var target = StartupArgs.Resolve(e.Args, scanner, comparer, Settings.StartupSlideshowSubfolders);
            if (target != null && target.Files.Count > 0)
            {
                var browser = new MainWindow(Settings, target.Folder ?? target.Files[target.StartIndex]);
                browser.Show();
                browser.ShowSlideshow(target.Files, target.StartIndex, Settings.StartupSlideshowSubfolders, target.Folder,
                    Settings.SlideshowDefaultFullScreen);
                return;
            }
        }

        new MainWindow(Settings, e.Args.FirstOrDefault()).Show();
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(e.Exception.Message, "Emberlight", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;   // keep the app alive; a failed thumbnail or file operation shouldn't take everything down
    }
}
