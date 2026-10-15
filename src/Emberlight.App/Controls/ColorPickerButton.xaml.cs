using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Media;
using Emberlight.Core;

namespace Emberlight.App.Controls;

/// <summary>
/// A colour swatch that opens a small picker: preset swatches, a saturation/brightness square, a hue bar and an
/// opacity bar. <see cref="Text"/> is meant to be bound two-way to a colour text box, so the box and the picker
/// stay in step. All the colour logic is <see cref="ColorPickerModel"/> (Core); this class draws it and routes the
/// mouse and keyboard.
/// </summary>
public partial class ColorPickerButton : UserControl
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(ColorPickerButton),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            (d, _) => ((ColorPickerButton)d).OnTextChanged()));

    /// <summary>The colour as text: whatever was typed, or what the picker wrote (#RRGGBB or #AARRGGBB).</summary>
    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    private enum DragTarget { None, Square, Hue, Alpha }

    private const double KeyStep = 0.01;          // arrow keys move 1% of a bar or the square; Shift moves 10%

    private readonly ColorPickerModel _model = new();
    private bool _valid = true;
    private bool _writing;                         // Text is being set from the picker, not typed
    private DragTarget _drag;

    public ColorPickerButton()
    {
        InitializeComponent();
        foreach (string preset in ColorPickerModel.Presets)
        {
            var swatch = new Button
            {
                Style = (Style)Resources["PresetSwatch"],
                Background = BrushFor(ColorText.TryParse(preset, out uint argb) ? argb : 0),
                ToolTip = preset == "#00000000" ? "Transparent" : preset,
                Tag = argb,
            };
            AutomationProperties.SetName(swatch, preset);
            swatch.Click += (_, _) => { _model.SetArgb((uint)swatch.Tag); Commit(); };
            PresetPanel.Children.Add(swatch);
        }

        foreach (var (area, target) in new[] { (SquareArea, DragTarget.Square), (HueBar, DragTarget.Hue), (AlphaBar, DragTarget.Alpha) })
        {
            area.MouseLeftButtonDown += (_, e) => BeginDrag(area, target, e);
            area.MouseMove += (_, e) => { if (_drag == target) ApplyPointer(area, target, e.GetPosition(area)); };
            area.MouseLeftButtonUp += (_, _) => EndDrag(area);
            area.LostMouseCapture += (_, _) => _drag = DragTarget.None;
            area.KeyDown += (_, e) => e.Handled = Nudge(target, e.Key, (Keyboard.Modifiers & ModifierKeys.Shift) != 0);
            area.SizeChanged += (_, _) => RefreshPicker();
        }

        // StaysOpen=False closes the popup when the button is pressed again; don't let that same click reopen it.
        PickerPopup.Opened += (_, _) =>
        {
            Toggle.IsHitTestVisible = false;
            RefreshPicker();
            SquareArea.Focus();
        };
        PickerPopup.Closed += (_, _) => Toggle.IsHitTestVisible = true;
        PreviewKeyDown += OnPreviewKeyDown;
        RefreshSwatch();
    }

    // ---- text <-> picker -----------------------------------------------------------------------------

    private void OnTextChanged()
    {
        if (_writing) return;                      // our own write coming back through the binding
        _valid = _model.TrySetText(Text);          // invalid text leaves the picker on the last good colour
        RefreshSwatch();
        RefreshPicker();
    }

    /// <summary>The picker changed the colour: write it into <see cref="Text"/> (and so into the text box).</summary>
    private void Commit()
    {
        _writing = true;
        try { Text = _model.Text; }
        finally { _writing = false; }
        _valid = true;
        RefreshSwatch();
        RefreshPicker();
    }

    // ---- drawing ---------------------------------------------------------------------------------------

    private void RefreshSwatch()
    {
        SwatchFill.Background = _valid ? BrushFor(_model.Argb) : null;
        InvalidMark.Visibility = _valid ? Visibility.Collapsed : Visibility.Visible;
        Toggle.ToolTip = _valid ? $"{_model.Text}: click to pick a colour" : "Not a colour yet: click to pick one, or type #RRGGBB";
    }

    private void RefreshPicker()
    {
        if (!PickerPopup.IsOpen) return;
        var (hr, hg, hb) = _model.HueColor;
        SquareHue.Fill = Frozen(new SolidColorBrush(Color.FromRgb(hr, hg, hb)));
        Place(SquareThumb, _model.Saturation * SquareArea.ActualWidth - SquareThumb.Width / 2,
                           (1 - _model.Value) * SquareArea.ActualHeight - SquareThumb.Height / 2);
        Canvas.SetLeft(HueThumb, _model.Hue / 360 * HueBar.ActualWidth - HueThumb.Width / 2);

        uint opaque = _model.OpaqueArgb;
        var (_, r, g, b) = ColorText.Split(opaque);
        AlphaGradient.Background = Frozen(new LinearGradientBrush(Color.FromArgb(0, r, g, b), Color.FromRgb(r, g, b), 0));
        Canvas.SetLeft(AlphaThumb, _model.Alpha / 255.0 * AlphaBar.ActualWidth - AlphaThumb.Width / 2);

        PreviewFill.Background = BrushFor(_model.Argb);
        HexText.Text = _model.Text;
    }

    private static void Place(UIElement e, double left, double top)
    {
        Canvas.SetLeft(e, left);
        Canvas.SetTop(e, top);
    }

    private static Brush BrushFor(uint argb)
    {
        var (a, r, g, b) = ColorText.Split(argb);
        return Frozen(new SolidColorBrush(Color.FromArgb(a, r, g, b)));
    }

    private static T Frozen<T>(T brush) where T : Freezable
    {
        brush.Freeze();
        return brush;
    }

    // ---- mouse ----------------------------------------------------------------------------------------

    private void BeginDrag(FrameworkElement area, DragTarget target, MouseButtonEventArgs e)
    {
        area.Focus();
        _drag = target;
        area.CaptureMouse();
        ApplyPointer(area, target, e.GetPosition(area));
        e.Handled = true;
    }

    private void EndDrag(FrameworkElement area)
    {
        _drag = DragTarget.None;
        if (area.IsMouseCaptured) area.ReleaseMouseCapture();
    }

    private void ApplyPointer(FrameworkElement area, DragTarget target, Point p)
    {
        if (area.ActualWidth <= 0 || area.ActualHeight <= 0) return;
        double x = p.X / area.ActualWidth, y = p.Y / area.ActualHeight;
        switch (target)
        {
            case DragTarget.Square: _model.SetSaturationValue(x, 1 - y); break;
            case DragTarget.Hue: _model.SetHue(x * 360); break;
            case DragTarget.Alpha: _model.SetAlpha(x); break;
        }
        Commit();
    }

    // ---- keyboard -------------------------------------------------------------------------------------

    /// <summary>Arrow keys move the focused bar or the square's thumb. Returns true if the key was used.</summary>
    private bool Nudge(DragTarget target, Key key, bool big)
    {
        double step = big ? KeyStep * 10 : KeyStep;
        double dx = key switch { Key.Left => -step, Key.Right => step, _ => 0 };
        double dy = key switch { Key.Up => step, Key.Down => -step, _ => 0 };
        if (dx == 0 && dy == 0) return false;
        switch (target)
        {
            case DragTarget.Square: _model.SetSaturationValue(_model.Saturation + dx, _model.Value + dy); break;
            case DragTarget.Hue: _model.SetHue(_model.Hue + (dx + dy) * 360); break;
            case DragTarget.Alpha: _model.SetAlpha(_model.Alpha / 255.0 + dx + dy); break;
        }
        Commit();
        return true;
    }

    /// <summary>
    /// Esc and Enter close the picker. Handling them here also stops them reaching the dialog's Cancel and OK
    /// buttons, which would otherwise close Preferences.
    /// </summary>
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!PickerPopup.IsOpen || e.Key is not (Key.Escape or Key.Enter)) return;
        PickerPopup.IsOpen = false;
        Toggle.Focus();
        e.Handled = true;
    }
}
