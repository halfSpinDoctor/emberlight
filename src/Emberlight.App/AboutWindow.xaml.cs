using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Navigation;
using Emberlight.App.Services;
using Emberlight.Core;

namespace Emberlight.App;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        var info = typeof(AboutWindow).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
        VersionText.Text = "Version " + BuildVersion.Parse(info?.InformationalVersion);
        // The 256 px icon scaled down smoothly; app.ico set in the XAML stays as the fallback.
        if (ImageService.LoadLargestAppIcon() is { } icon) AppIcon.Source = icon;
    }

    private void OnLink(object sender, RequestNavigateEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception) { /* no default browser: nothing useful to do */ }
        e.Handled = true;
    }
}
