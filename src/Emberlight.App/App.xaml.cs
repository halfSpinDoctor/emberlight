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

        // Files/folders passed on the command line ("Open with", drag onto the exe) go straight to a slideshow when
        // that preference is on. The browser opens behind it, so closing the slideshow returns to the browser instead
        // of quitting. StartupArgs.Plan (Core) decides.
        var plan = StartupArgs.Plan(e.Args, Settings);
        var browser = new MainWindow(Settings, plan.BrowserPath);
        browser.Show();
        if (plan.Slideshow is { } show)
            browser.ShowSlideshow(show.Files, show.StartIndex, Settings.StartupSlideshowSubfolders, show.Folder,
                Settings.SlideshowDefaultFullScreen);
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(e.Exception.Message, "Emberlight", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;   // keep the app alive; a failed thumbnail or file operation shouldn't take everything down
    }
}
