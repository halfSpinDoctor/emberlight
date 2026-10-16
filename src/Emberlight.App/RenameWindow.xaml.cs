using System.IO;
using System.Windows;
using System.Windows.Controls;
using Emberlight.Core;

namespace Emberlight.App;

/// <summary>
/// Asks for a file's new name. The name is checked as it is typed (<see cref="FileRename.Problem"/>), and a changed
/// extension is confirmed before the dialog closes; the rename itself is left to the caller.
/// </summary>
public partial class RenameWindow : Window
{
    private readonly string _path;
    private readonly Func<string, bool> _isShown;

    /// <param name="isShown">Whether the browser shows files with a given name (by its extension).</param>
    public RenameWindow(string path, Func<string, bool> isShown)
    {
        InitializeComponent();
        _path = path;
        _isShown = isShown;
        string name = Path.GetFileName(path);
        NameBox.Text = name;
        Loaded += (_, _) =>
        {
            NameBox.Focus();
            var (start, length) = FileRename.StemSelection(name);
            NameBox.Select(start, length);
        };
    }

    /// <summary>The name typed, as it should be used (only meaningful after the dialog returns true).</summary>
    public string NewName => FileRename.Clean(NameBox.Text);

    private void OnNameChanged(object sender, TextChangedEventArgs e)
    {
        string? problem = FileRename.Problem(_path, NameBox.Text);
        ProblemText.Text = problem ?? "";
        RenameButton.IsEnabled = problem == null;
    }

    private void OnRename(object sender, RoutedEventArgs e)
    {
        if (FileRename.Problem(_path, NameBox.Text) is { } problem)    // the folder may have changed since the last keystroke
        {
            ProblemText.Text = problem;
            return;
        }
        if (FileRename.ExtensionChangeQuestion(_path, NameBox.Text, _isShown(NewName)) is { } question
            && MessageBox.Show(this, question, "Emberlight", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            NameBox.Focus();
            return;
        }
        DialogResult = true;
    }
}
