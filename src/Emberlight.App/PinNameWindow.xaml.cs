using System.Windows;
using Emberlight.Core;

namespace Emberlight.App;

/// <summary>
/// Asks for a pinned folder's name (Rename Pin). Starts with the label the pin shows now, all selected; what the result
/// means (unchanged, a new name, or empty for the automatic label) is decided by <see cref="PinnedFolders.Rename"/>.
/// </summary>
public partial class PinNameWindow : Window
{
    public PinNameWindow(string path, string shown)
    {
        InitializeComponent();
        NameBox.Text = shown;
        PathText.Text = path;
        HintText.Text = PinnedFolders.RenameHint;
        Loaded += (_, _) =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
        };
    }

    /// <summary>The text typed (only meaningful after the dialog returns true).</summary>
    public string NewName => NameBox.Text;

    private void OnOk(object sender, RoutedEventArgs e) => DialogResult = true;
}
