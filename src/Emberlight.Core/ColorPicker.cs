namespace Emberlight.Core;

/// <summary>A colour as hue (degrees, 0-360), saturation and value (brightness), both 0-1.</summary>
public readonly record struct Hsv(double Hue, double Saturation, double Value)
{
    public static Hsv FromRgb(byte r, byte g, byte b)
    {
        int max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        double delta = max - min;
        double hue = 0;
        if (delta > 0)
        {
            if (max == r) hue = 60 * ((g - b) / delta);
            else if (max == g) hue = 60 * ((b - r) / delta + 2);
            else hue = 60 * ((r - g) / delta + 4);
            if (hue < 0) hue += 360;
        }
        return new Hsv(hue, max == 0 ? 0 : delta / max, max / 255.0);
    }

    /// <summary>The RGB bytes. The hue wraps around (360 is 0); saturation and value are clamped to 0-1.</summary>
    public (byte R, byte G, byte B) ToRgb()
    {
        double h = double.IsFinite(Hue) ? (Hue % 360 + 360) % 360 : 0;
        double s = Clamp01(Saturation), v = Clamp01(Value);
        double c = v * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - c;
        var (r, g, b) = (int)(h / 60) switch
        {
            0 => (c, x, 0.0),
            1 => (x, c, 0.0),
            2 => (0.0, c, x),
            3 => (0.0, x, c),
            4 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        return (ToByte(r + m), ToByte(g + m), ToByte(b + m));
    }

    internal static double Clamp01(double d) => double.IsNaN(d) ? 0 : Math.Clamp(d, 0, 1);

    private static byte ToByte(double unit) => (byte)Math.Round(Clamp01(unit) * 255);
}

/// <summary>
/// The state behind the Preferences colour picker: hue, saturation, brightness and opacity, as the hue bar, the
/// saturation/brightness square and the opacity bar set them. Kept as HSV rather than RGB so the hue survives
/// dragging to the grey or black edge of the square (and typing a grey), and dragging back out finds the same colour.
/// </summary>
public sealed class ColorPickerModel
{
    /// <summary>The swatches offered above the square: neutrals first (the usual choices behind photos), then a few
    /// dark tints, transparent, and some brighter colours.</summary>
    public static IReadOnlyList<string> Presets { get; } =
    [
        "#000000", "#1A1A1A", "#333333", "#4D4D4D", "#808080", "#C0C0C0", "#FFFFFF",
        "#00000000", "#1C2833", "#2E4053", "#3B2F2F", "#F5F0E6", "#D63226", "#FEAC2D",
    ];

    public double Hue { get; private set; }
    public double Saturation { get; private set; }
    public double Value { get; private set; }
    public byte Alpha { get; private set; } = 255;

    /// <summary>The picked colour as 0xAARRGGBB.</summary>
    public uint Argb
    {
        get
        {
            var (r, g, b) = new Hsv(Hue, Saturation, Value).ToRgb();
            return ColorText.Join(Alpha, r, g, b);
        }
    }

    /// <summary>The picked colour without its transparency (for the opacity bar's opaque end).</summary>
    public uint OpaqueArgb => Argb | 0xFF000000;

    /// <summary>The current hue at full saturation and brightness (the square's base colour).</summary>
    public (byte R, byte G, byte B) HueColor => new Hsv(Hue, 1, 1).ToRgb();

    public string Text => ColorText.Format(Argb);

    /// <summary>Shows the colour typed as <paramref name="text"/>. False (and no change) if it isn't a colour.</summary>
    public bool TrySetText(string? text)
    {
        if (!ColorText.TryParse(text, out uint argb)) return false;
        SetArgb(argb);
        return true;
    }

    /// <summary>
    /// Shows <paramref name="argb"/>. A grey has no hue and black has no saturation either, so those keep the ones
    /// already chosen.
    /// </summary>
    public void SetArgb(uint argb)
    {
        var (a, r, g, b) = ColorText.Split(argb);
        var hsv = Hsv.FromRgb(r, g, b);
        Alpha = a;
        Value = hsv.Value;
        if (hsv.Value == 0) return;                      // black: keep hue and saturation
        Saturation = hsv.Saturation;
        if (hsv.Saturation > 0) Hue = hsv.Hue;           // grey: keep hue
    }

    /// <summary>The square: saturation left to right, brightness bottom to top, each 0-1 (clamped).</summary>
    public void SetSaturationValue(double saturation, double value)
    {
        Saturation = Hsv.Clamp01(saturation);
        Value = Hsv.Clamp01(value);
    }

    /// <summary>The hue bar, in degrees (clamped to 0-360).</summary>
    public void SetHue(double degrees)
    {
        if (!double.IsNaN(degrees)) Hue = Math.Clamp(degrees, 0, 360);
    }

    /// <summary>The opacity bar, 0 (transparent) to 1 (opaque).</summary>
    public void SetAlpha(double opacity)
    {
        if (!double.IsNaN(opacity)) Alpha = (byte)Math.Round(Math.Clamp(opacity, 0, 1) * 255);
    }
}
