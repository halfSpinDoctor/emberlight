using Emberlight.Core;

namespace Emberlight.Tests;

/// <summary>Colour maths and the state behind the Preferences colour picker.</summary>
internal static class ColorTests
{
    static void Near(double expected, double actual, string? msg = null, double eps = 1e-9)
    {
        if (Math.Abs(expected - actual) > eps) throw new Exception($"{msg ?? "values differ"}: expected <{expected}> but was <{actual}>");
    }

    public static void RunAll()
    {
        T.Group("Colour text formatting");

        T.Run("opaque colours are written #RRGGBB, others #AARRGGBB, in capitals", () =>
        {
            T.Eq("#000000", ColorText.Format(0xFF000000));
            T.Eq("#FFA0B1", ColorText.Format(0xFFFFA0B1));
            T.Eq("#00000000", ColorText.Format(0x00000000));
            T.Eq("#80FF0000", ColorText.Format(0x80FF0000));
        });

        T.Run("join puts the four channels back together", () =>
        {
            T.Eq(0x80FF1020u, ColorText.Join(0x80, 0xFF, 0x10, 0x20));
            foreach (uint argb in new uint[] { 0, 0xFFFFFFFF, 0x12345678, 0xFF00FF00 })
            {
                var (a, r, g, b) = ColorText.Split(argb);
                T.Eq(argb, ColorText.Join(a, r, g, b));
            }
        });

        T.Run("formatted text reads back as the same colour", () =>
        {
            var rnd = new Random(7);
            for (int i = 0; i < 500; i++)
            {
                uint argb = (uint)rnd.NextInt64(0, 0x1_0000_0000);
                T.True(ColorText.TryParse(ColorText.Format(argb), out uint back) && back == argb, ColorText.Format(argb));
            }
        });

        T.Group("HSV");

        T.Run("primary, secondary and grey colours have the expected hue, saturation and value", () =>
        {
            void Check(byte r, byte g, byte b, double h, double s, double v)
            {
                var hsv = Hsv.FromRgb(r, g, b);
                Near(h, hsv.Hue, $"hue of {r},{g},{b}"); Near(s, hsv.Saturation, "saturation"); Near(v, hsv.Value, "value");
            }
            Check(255, 0, 0, 0, 1, 1);
            Check(0, 255, 0, 120, 1, 1);
            Check(0, 0, 255, 240, 1, 1);
            Check(255, 255, 0, 60, 1, 1);
            Check(0, 255, 255, 180, 1, 1);
            Check(255, 0, 255, 300, 1, 1);
            Check(255, 255, 255, 0, 0, 1);
            Check(0, 0, 0, 0, 0, 0);
            Check(128, 128, 128, 0, 0, 128 / 255.0);
            Check(255, 128, 0, 30.117647058823529, 1, 1);
        });

        T.Run("HSV to RGB: the corners of the hue circle and its wrap-around", () =>
        {
            T.Eq(((byte)255, (byte)0, (byte)0), new Hsv(0, 1, 1).ToRgb());
            T.Eq(((byte)255, (byte)0, (byte)0), new Hsv(360, 1, 1).ToRgb(), "360 is 0");
            T.Eq(((byte)0, (byte)255, (byte)0), new Hsv(120, 1, 1).ToRgb());
            T.Eq(((byte)0, (byte)0, (byte)255), new Hsv(240, 1, 1).ToRgb());
            T.Eq(((byte)128, (byte)128, (byte)128), new Hsv(77, 0, 128 / 255.0).ToRgb(), "no saturation: grey, any hue");
            T.Eq(((byte)0, (byte)0, (byte)0), new Hsv(200, 1, 0).ToRgb());
            T.Eq(((byte)255, (byte)128, (byte)128), new Hsv(0, 127 / 255.0, 1).ToRgb(), "pale red");
        });

        T.Run("brute-force: every 4th RGB value survives RGB -> HSV -> RGB exactly", () =>
        {
            for (int r = 0; r < 256; r += 4)
                for (int g = 0; g < 256; g += 4)
                    for (int b = 0; b < 256; b += 4)
                    {
                        var back = Hsv.FromRgb((byte)r, (byte)g, (byte)b).ToRgb();
                        if (back != ((byte)r, (byte)g, (byte)b)) throw new Exception($"{r},{g},{b} came back as {back}");
                    }
            T.Eq(((byte)255, (byte)255, (byte)255), Hsv.FromRgb(255, 255, 255).ToRgb());
        });

        T.Run("out-of-range values are clamped rather than producing garbage", () =>
        {
            T.Eq(((byte)255, (byte)255, (byte)255), new Hsv(0, -1, 2).ToRgb());
            T.Eq(new Hsv(0, 1, 1).ToRgb(), new Hsv(-360, 1, 1).ToRgb(), "negative hue wraps");
            T.Eq(new Hsv(90, 1, 1).ToRgb(), new Hsv(450, 1, 1).ToRgb(), "hue above 360 wraps");
            T.Eq(((byte)0, (byte)0, (byte)0), new Hsv(double.NaN, double.NaN, double.NaN).ToRgb());
        });

        T.Group("ColorPickerModel");

        T.Run("starts from a colour's text and gives it back", () =>
        {
            var m = new ColorPickerModel();
            T.True(m.TrySetText("#FF8000"));
            T.Eq("#FF8000", m.Text);
            T.Eq(0xFFFF8000u, m.Argb);
            T.Eq((byte)255, m.Alpha);
            T.True(m.TrySetText("  navy "), "colour names too");
            T.Eq("#000080", m.Text);
            T.True(m.TrySetText("#00000000"));
            T.Eq("#00000000", m.Text, "transparent keeps its alpha");
        });

        T.Run("text that isn't a colour leaves the picker as it was", () =>
        {
            var m = new ColorPickerModel();
            m.TrySetText("#123456");
            T.True(!m.TrySetText("#12345"));
            T.True(!m.TrySetText(""));
            T.True(!m.TrySetText(null));
            T.True(!m.TrySetText("sc#1,0,0,0"));
            T.Eq("#123456", m.Text);
        });

        T.Run("a new picker shows opaque black", () =>
        {
            var m = new ColorPickerModel();
            T.Eq("#000000", m.Text);
        });

        T.Run("dragging in the square sets saturation and brightness, clamped to the square", () =>
        {
            var m = new ColorPickerModel();
            m.TrySetText("#FF0000");
            m.SetSaturationValue(0.5, 1);
            T.Eq("#FF8080", m.Text);
            m.SetSaturationValue(1, 0.5);
            T.Eq("#800000", m.Text);
            m.SetSaturationValue(2, -1);
            Near(1, m.Saturation); Near(0, m.Value);
            T.Eq("#000000", m.Text);
        });

        T.Run("the hue bar changes the hue and keeps saturation and brightness", () =>
        {
            var m = new ColorPickerModel();
            m.TrySetText("#FF8080");
            m.SetHue(120);
            T.Eq("#80FF80", m.Text);
            m.SetHue(400);
            Near(360, m.Hue, "clamped to the bar");
            m.SetHue(-5);
            Near(0, m.Hue);
        });

        T.Run("greys and black keep the hue (and saturation) chosen before", () =>
        {
            var m = new ColorPickerModel();
            m.TrySetText("#0000FF");
            m.SetSaturationValue(0, 1);                         // dragged to the white edge
            T.Eq("#FFFFFF", m.Text);
            Near(240, m.Hue, "hue kept on the white edge");
            m.SetSaturationValue(1, 1);
            T.Eq("#0000FF", m.Text, "dragging back out finds blue again");

            m.SetSaturationValue(0.7, 0);                       // dragged to the black edge
            T.Eq("#000000", m.Text);
            m.SetSaturationValue(0.7, 1);
            Near(240, m.Hue); Near(0.7, m.Saturation, "saturation kept at black");

            m.TrySetText("#808080");                            // a grey typed in
            Near(240, m.Hue, "typing a grey keeps the hue too");
            m.TrySetText("#000000");
            Near(240, m.Hue);

            m.TrySetText("#3366CC");                            // saturation 0.75
            m.TrySetText("#000000");                            // black typed in
            Near(0.75, m.Saturation, "typing black keeps the saturation");
            Near(0, m.Value);
        });

        T.Run("NaN from the square or a bar is never stored", () =>
        {
            var m = new ColorPickerModel();
            m.TrySetText("#FF0000");
            m.SetSaturationValue(double.NaN, double.NaN);
            T.True(double.IsFinite(m.Saturation) && double.IsFinite(m.Value), "the ring needs real numbers to be placed");
            m.SetHue(double.NaN);
            T.True(double.IsFinite(m.Hue));
        });

        T.Run("opacity: 0 to 1 becomes alpha 0 to 255; the colour itself is kept", () =>
        {
            var m = new ColorPickerModel();
            m.TrySetText("#FF0000");
            m.SetAlpha(0.5);
            T.Eq((byte)128, m.Alpha);
            T.Eq("#80FF0000", m.Text);
            m.SetAlpha(0);
            T.Eq("#00FF0000", m.Text);
            m.SetAlpha(1);
            T.Eq("#FF0000", m.Text);
            m.SetAlpha(7);
            T.Eq((byte)255, m.Alpha, "clamped");
            m.SetAlpha(double.NaN);
            T.Eq((byte)255, m.Alpha, "NaN is ignored");
        });

        T.Run("opaque and solid colours report their parts for drawing the picker", () =>
        {
            var m = new ColorPickerModel();
            m.TrySetText("#80336699");
            T.Eq(0xFF336699u, m.OpaqueArgb, "the colour without its transparency");
            var (r, g, b) = m.HueColor;
            T.True(Hsv.FromRgb(r, g, b) is { Saturation: 1, Value: 1 }, "the square's hue at full strength");
            Near(m.Hue, Hsv.FromRgb(r, g, b).Hue, "same hue", 1);
        });

        T.Run("presets: all valid, all different, and include black, white and transparent", () =>
        {
            T.True(ColorPickerModel.Presets.Count >= 8);
            var parsed = ColorPickerModel.Presets.Select(p => ColorText.TryParse(p, out uint v) ? v : (uint?)null).ToList();
            T.True(parsed.All(v => v != null), "every preset parses");
            T.Eq(parsed.Count, parsed.Distinct().Count(), "no duplicates");
            T.True(parsed.Contains(0xFF000000u) && parsed.Contains(0xFFFFFFFFu) && parsed.Contains(0x00000000u));
            T.True(ColorPickerModel.Presets.All(p => p == ColorText.Format(ColorText.TryParse(p, out uint v) ? v : 0)),
                "presets are written the way the picker writes colours");
        });
    }
}
