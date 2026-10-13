using System.Windows.Media;
using Emberlight.Core;

namespace Emberlight.App.Controls;

/// <summary>WPF transforms for the eight image orientations (mirror first, then rotate clockwise).</summary>
public static class OrientationTransforms
{
    private static readonly Transform?[] Cache = new Transform?[9];

    /// <summary>A frozen transform suitable for an element's LayoutTransform.</summary>
    public static Transform For(ImageOrientation o)
    {
        int key = o.ToExif();
        if (Cache[key] is { } cached) return cached;

        Transform t;
        if (o.RotationCw == 0 && !o.Flipped)
        {
            t = Transform.Identity;
        }
        else
        {
            var g = new TransformGroup();
            if (o.Flipped) g.Children.Add(new ScaleTransform(-1, 1));
            if (o.RotationCw != 0) g.Children.Add(new RotateTransform(o.RotationCw));
            g.Freeze();
            t = g;
        }
        Cache[key] = t;
        return t;
    }
}
