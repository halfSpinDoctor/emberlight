using Emberlight.Core;

namespace Emberlight.Tests;

internal static class ViewTests
{
    static bool Near(double a, double b, double eps = 1e-6) => Math.Abs(a - b) <= eps;
    static void Approx(double expected, double actual, string? msg = null, double eps = 1e-6)
        => T.True(Near(expected, actual, eps), $"{msg ?? "value"}: expected {expected} but was {actual}");

    static ImageViewport Make(double vw, double vh, double iw, double ih, bool scaleUp = false, bool actual = false)
    {
        var v = new ImageViewport { ScaleUp = scaleUp, ShowActualSize = actual };
        v.SetView(vw, vh);
        v.SetImage(iw, ih);
        return v;
    }

    public static void RunAll()
    {
        T.Group("ZoomMath");
        T.Run("steps go up in half-powers of two, matching the original", () =>
        {
            var up = new List<double>(); double z = 1;
            for (int i = 0; i < 8; i++) { z = ZoomMath.NextZoomIn(z); up.Add(z); }
            T.SeqEq(new[] { 1.5, 2, 3, 4, 6, 8, 12, 16 }, up);
            var down = new List<double>(); z = 1;
            for (int i = 0; i < 6; i++) { z = ZoomMath.NextZoomOut(z); down.Add(z); }
            T.SeqEq(new[] { 0.75, 0.5, 0.375, 0.25, 0.1875, 0.125 }, down);
        });
        T.Run("off-grid values snap to the next step; in/out are inverses on the grid", () =>
        {
            Approx(1.5, ZoomMath.NextZoomIn(1.2)); Approx(1, ZoomMath.NextZoomOut(1.2));
            Approx(0.75, ZoomMath.NextZoomIn(0.6)); Approx(0.5, ZoomMath.NextZoomOut(0.6));
            Approx(1.5, ZoomMath.NextZoomIn(1.0000001)); Approx(1, ZoomMath.NextZoomOut(1.0000001));
            foreach (double s in new[] { 0.125, 0.375, 1, 1.5, 3, 6, 96, 256 })
            {
                Approx(s, ZoomMath.NextZoomOut(ZoomMath.NextZoomIn(s)), $"out(in({s}))");
                Approx(s, ZoomMath.NextZoomIn(ZoomMath.NextZoomOut(s)), $"in(out({s}))");
            }
            for (double s = 0.003; s < 500; s *= 1.37)
            {
                T.True(ZoomMath.NextZoomIn(s) > s, $"in({s}) grows");
                T.True(ZoomMath.NextZoomOut(s) < s, $"out({s}) shrinks");
            }
        });

        T.Group("ImageViewport");
        T.Run("default layout: shrink big images to fit, leave small ones alone", () =>
        {
            var v = Make(1000, 800, 4000, 3000);
            Approx(0.25, v.Scale); T.True(!v.IsZoomed); T.True(!v.CanPan);
            Approx(2000, v.CenterX); Approx(1500, v.CenterY);      // image-space centre
            Approx(500, v.TranslateX); Approx(400, v.TranslateY);  // centred in the 1000x800 view

            var small = Make(1000, 800, 200, 100);
            Approx(1, small.Scale);
            var scaled = Make(1000, 800, 200, 100, scaleUp: true);
            Approx(5, scaled.Scale);                              // width is the limiting side
            var tall = Make(1000, 800, 300, 1600);
            Approx(0.5, tall.Scale);
        });

        T.Run("actual-size and scale-up options interact like the original", () =>
        {
            T.True(Near(1, Make(1000, 800, 4000, 3000, actual: true).Scale), "big image shown at 100%");
            T.True(Make(1000, 800, 4000, 3000, actual: true).CanPan);
            T.True(Near(1, Make(1000, 800, 200, 100, actual: true).Scale), "small image at 100%");
            T.True(Near(5, Make(1000, 800, 200, 100, scaleUp: true, actual: true).Scale), "both on: small image scaled up to fit");
            T.True(Near(1, Make(1000, 800, 4000, 3000, scaleUp: true, actual: true).Scale), "both on: big image at 100%");
            // exactly view-sized is not "smaller", so 100% applies
            T.True(Near(1, Make(1000, 800, 1000, 800, scaleUp: true, actual: true).Scale));
        });

        T.Run("zoom in/out step through the grid and mark the viewport zoomed", () =>
        {
            var v = Make(1000, 800, 4000, 3000);   // fit scale 0.25
            T.True(v.ZoomIn()); Approx(0.375, v.Scale); T.True(v.IsZoomed);
            T.True(v.ZoomIn()); Approx(0.5, v.Scale);
            T.True(v.ZoomOut()); Approx(0.375, v.Scale);
            v.ZoomActualSize(); Approx(1, v.Scale); T.True(v.CanPan);
            v.Reset(); Approx(0.25, v.Scale); T.True(!v.IsZoomed);
        });

        T.Run("zoom limits", () =>
        {
            var v = Make(100, 100, 100, 100);
            int n = 0; while (v.ZoomIn() && n++ < 200) { }
            T.True(v.Scale >= 512 && v.Scale <= 768, $"stops around the cap, got {v.Scale}");
            T.True(!v.ZoomIn());
            var w = Make(100, 100, 100, 100);
            n = 0; while (w.ZoomOut() && n++ < 200) { }
            T.True(w.Scale < ZoomMath.MinZoom && w.Scale > ZoomMath.MinZoom / 3, $"min zoom reached, got {w.Scale}");
            T.True(!w.ZoomOut());
            T.True(!new ImageViewport().ZoomIn(), "no image: nothing to zoom");
        });

        T.Run("panning is limited to the image edges, and not possible when the image fits", () =>
        {
            var v = Make(1000, 800, 4000, 3000);
            v.ZoomActualSize();                       // 4000x3000 at 100% in a 1000x800 view
            v.PanBy(-100000, -100000);                // drag far towards the bottom-right
            Approx(4000 - 500, v.CenterX); Approx(3000 - 400, v.CenterY);
            v.PanBy(100000, 100000);                  // and back to the top-left
            Approx(500, v.CenterX); Approx(400, v.CenterY);
            Approx(0, v.ToView(0, 0).X, "image's left edge sits on the view's left edge");
            Approx(0, v.ToView(0, 0).Y);
            v.PanBy(-50, -30);                        // dragging left/up reveals the right/bottom
            Approx(550, v.CenterX); Approx(430, v.CenterY);

            var fits = Make(1000, 800, 400, 300);
            fits.PanBy(123, 456);
            Approx(200, fits.CenterX); Approx(150, fits.CenterY);
            T.True(!fits.CanPan);
        });

        T.Run("small image while zoomed in one axis stays centred in the other", () =>
        {
            var v = Make(1000, 800, 4000, 200);
            v.ZoomActualSize();
            v.PanBy(-300, -300);
            Approx(100, v.CenterY, "short axis fits, so it can't pan");
            Approx(2300, v.CenterX, "long axis panned 300px from the centre (2000)");
        });

        T.Run("zooming in while pinned to an edge keeps that edge pinned", () =>
        {
            var v = Make(1000, 800, 4000, 3000);
            v.ZoomActualSize(); v.PanBy(100000, 100000);          // top-left corner showing
            v.ZoomIn();                                          // 1 -> 1.5
            Approx(0, v.ToView(0, 0).X, "left edge still at the view's left");
            Approx(0, v.ToView(0, 0).Y, "top edge still at the view's top");
            v.PanBy(-100000, -100000);                            // bottom-right corner
            v.ZoomIn();
            Approx(1000, v.ToView(4000, 3000).X, "right edge still at the view's right");
            Approx(800, v.ToView(4000, 3000).Y, "bottom edge still at the view's bottom");
        });

        T.Run("zooming in from the middle keeps the middle", () =>
        {
            var v = Make(1000, 800, 4000, 3000);
            v.ZoomActualSize(); v.PanBy(-700, -500);
            var before = v.ToImage(500, 400);
            v.ZoomIn();
            var after = v.ToImage(500, 400);
            Approx(before.X, after.X); Approx(before.Y, after.Y);
        });

        T.Run("zoom-at-point keeps the image point under the pointer fixed", () =>
        {
            var v = Make(1000, 800, 4000, 3000);
            v.ZoomActualSize(); v.PanBy(-700, -500);
            var p = v.ToImage(300, 600);
            T.True(v.ZoomBy(0.5, 300, 600));
            Approx(1.5, v.Scale);
            var q = v.ToView(p.X, p.Y);
            Approx(300, q.X, "x"); Approx(600, q.Y, "y");
            T.True(v.ZoomBy(-0.5, 300, 600));
            Approx(0.75, v.Scale);
        });

        T.Run("view and image coordinates are inverses", () =>
        {
            var v = Make(1000, 800, 4000, 3000);
            v.ZoomActualSize(); v.PanBy(-321, -123);
            foreach (var (x, y) in new[] { (0.0, 0.0), (500.0, 400.0), (999.0, 799.0) })
            {
                var i = v.ToImage(x, y); var b = v.ToView(i.X, i.Y);
                Approx(x, b.X); Approx(y, b.Y);
            }
        });

        T.Run("resizing the view re-fits the default layout and re-clamps a zoomed one", () =>
        {
            var v = Make(1000, 800, 4000, 3000);
            v.SetView(2000, 1500);
            Approx(0.5, v.Scale);
            v.SetView(1000, 800);
            v.ZoomActualSize(); v.PanBy(-100000, -100000);
            v.SetView(1600, 1200);                                // bigger view while panned to the corner
            Approx(4000 - 800, v.CenterX); Approx(3000 - 600, v.CenterY);
        });

        T.Run("orientation change keeps the zoom but recentres", () =>
        {
            var v = Make(1000, 800, 4000, 3000);
            v.ZoomActualSize(); v.PanBy(-500, -500);
            v.SetOrientedSize(3000, 4000);
            Approx(1, v.Scale); T.True(v.IsZoomed);
            Approx(1500, v.CenterX); Approx(2000, v.CenterY);
        });

        T.Run("zoom info round-trips; garbage values are tolerated", () =>
        {
            var v = Make(1000, 800, 4000, 3000);
            T.True(v.GetZoomInfo() == null);
            v.ZoomActualSize(); v.PanBy(-250, -125);
            var z = v.GetZoomInfo()!.Value;
            var w = Make(1000, 800, 4000, 3000);
            w.ApplyZoomInfo(z);
            Approx(v.Scale, w.Scale); Approx(v.CenterX, w.CenterX); Approx(v.CenterY, w.CenterY);
            w.ApplyZoomInfo(null); T.True(!w.IsZoomed);
            w.ApplyZoomInfo(new ZoomInfo(double.NaN, 1, 1)); T.True(!w.IsZoomed);
            w.ApplyZoomInfo(new ZoomInfo(-3, 1, 1)); T.True(!w.IsZoomed);
            w.ApplyZoomInfo(new ZoomInfo(2, double.PositiveInfinity, double.NaN));
            T.True(double.IsFinite(w.CenterX) && double.IsFinite(w.CenterY));
        });

        T.Run("degenerate sizes never produce NaN or exceptions", () =>
        {
            var v = new ImageViewport();
            T.Eq(1.0, v.Scale);
            v.SetView(0, 0); v.SetImage(0, 0); v.PanBy(5, 5); v.ZoomActualSize(); v.Reset();
            T.True(double.IsFinite(v.TranslateX) && double.IsFinite(v.TranslateY));
            v.SetView(500, 500); v.SetImage(0, 100);
            T.True(double.IsFinite(v.Scale));
            v.SetImage(100, 100); v.SetView(0, 0);
            T.True(double.IsFinite(v.Scale) && double.IsFinite(v.TranslateX));
        });

        T.Run("fuzz: any sequence of operations keeps the view inside the image", () =>
        {
            var rng = new Random(77);
            for (int round = 0; round < 40; round++)
            {
                var v = Make(rng.Next(100, 2000), rng.Next(100, 1500), rng.Next(50, 6000), rng.Next(50, 6000),
                             scaleUp: rng.Next(2) == 0, actual: rng.Next(2) == 0);
                for (int step = 0; step < 150; step++)
                {
                    switch (rng.Next(8))
                    {
                        case 0: v.ZoomIn(); break;
                        case 1: v.ZoomOut(); break;
                        case 2: v.ZoomActualSize(); break;
                        case 3: v.PanBy(rng.Next(-800, 800), rng.Next(-800, 800)); break;
                        case 4: v.ZoomBy(rng.NextDouble() - 0.4, rng.Next((int)v.ViewWidth), rng.Next((int)v.ViewHeight)); break;
                        case 5: v.SetView(rng.Next(100, 2000), rng.Next(100, 1500)); break;
                        case 6: v.Reset(); break;
                        default: if (rng.Next(3) == 0) v.SetOrientedSize(v.ImageHeight, v.ImageWidth); break;
                    }
                    double s = v.Scale;
                    T.True(double.IsFinite(s) && s > 0, $"scale finite ({s})");
                    double halfW = v.ViewWidth / (2 * s), halfH = v.ViewHeight / (2 * s);
                    if (v.ImageWidth > 2 * halfW) T.True(v.CenterX >= halfW - 1e-6 && v.CenterX <= v.ImageWidth - halfW + 1e-6, "x in range");
                    else Approx(v.ImageWidth / 2, v.CenterX, "x centred");
                    if (v.ImageHeight > 2 * halfH) T.True(v.CenterY >= halfH - 1e-6 && v.CenterY <= v.ImageHeight - halfH + 1e-6, "y in range");
                    else Approx(v.ImageHeight / 2, v.CenterY, "y centred");
                    T.True(double.IsFinite(v.TranslateX) && double.IsFinite(v.TranslateY));
                }
            }
        });

        // ---------------------------------------------------------------------------------
        T.Group("ThumbnailGridMath");
        T.Run("cell height and columns", () =>
        {
            Approx(90, ThumbnailGridMath.CellHeight(120, false));
            Approx(110, ThumbnailGridMath.CellHeight(120, true));
            T.Eq(5, ThumbnailGridMath.Columns(1000, 200));
            T.Eq(1, ThumbnailGridMath.Columns(50, 200), "always at least one column");
        });
        T.Run("visible range covers the viewport plus overscan and clamps to the item count", () =>
        {
            // 5 columns of 200px, rows 100px tall, 1000x300 viewport
            var (f, e) = ThumbnailGridMath.VisibleRange(1000, 1000, 300, 0, 200, 100, overscanRows: 0);
            T.Eq(0, f); T.Eq(15, e);                               // rows 0-2
            (f, e) = ThumbnailGridMath.VisibleRange(1000, 1000, 300, 250, 200, 100, overscanRows: 0);
            T.Eq(10, f); T.Eq(30, e);                              // rows 2..5 (partially visible rows included)
            (f, e) = ThumbnailGridMath.VisibleRange(1000, 1000, 300, 250, 200, 100, overscanRows: 2);
            T.Eq(0, f); T.Eq(40, e);
            (f, e) = ThumbnailGridMath.VisibleRange(12, 1000, 300, 0, 200, 100);
            T.Eq(0, f); T.Eq(12, e, "clamped to count");
            (f, e) = ThumbnailGridMath.VisibleRange(12, 1000, 300, 99999, 200, 100, overscanRows: 0);
            T.Eq(12, f); T.Eq(12, e, "scrolled past the end: empty but valid");
            (f, e) = ThumbnailGridMath.VisibleRange(0, 1000, 300, 0, 200, 100);
            T.Eq(0, f); T.Eq(0, e);
            (f, e) = ThumbnailGridMath.VisibleRange(50, 1000, 0, 0, 200, 100);
            T.Eq(0, e, "zero-height viewport shows nothing");
        });
        T.Run("every item on screen is inside the computed range (brute-force cross-check)", () =>
        {
            var rng = new Random(5);
            for (int i = 0; i < 300; i++)
            {
                int count = rng.Next(1, 400);
                double vw = rng.Next(100, 1600), vh = rng.Next(80, 900), off = rng.Next(0, 5000);
                double cw = rng.Next(60, 300), ch = rng.Next(60, 300);
                int cols = ThumbnailGridMath.Columns(vw, cw);
                var (f, e) = ThumbnailGridMath.VisibleRange(count, vw, vh, off, cw, ch, overscanRows: 0);
                for (int idx = 0; idx < count; idx++)
                {
                    double top = idx / cols * ch, bottom = top + ch;
                    bool visible = bottom > off && top < off + vh;
                    if (visible) T.True(idx >= f && idx < e, $"item {idx} is visible but outside [{f},{e})");
                }
            }
        });

        T.Group("ThumbnailGridMath (virtualization)");
        T.Run("rows, extent and cell positions", () =>
        {
            T.Eq(0, ThumbnailGridMath.RowCount(0, 5)); T.Eq(1, ThumbnailGridMath.RowCount(5, 5));
            T.Eq(2, ThumbnailGridMath.RowCount(6, 5)); T.Eq(3, ThumbnailGridMath.RowCount(11, 5));
            T.Eq(1, ThumbnailGridMath.RowCount(1, 0), "zero columns treated as one") ;
            Approx(300, ThumbnailGridMath.ExtentHeight(11, 5, 100));
            Approx(0, ThumbnailGridMath.ExtentHeight(0, 5, 100));
            T.Eq((0.0, 0.0), ThumbnailGridMath.CellOrigin(0, 5, 200, 100));
            T.Eq((800.0, 0.0), ThumbnailGridMath.CellOrigin(4, 5, 200, 100));
            T.Eq((0.0, 100.0), ThumbnailGridMath.CellOrigin(5, 5, 200, 100));
            T.Eq((400.0, 200.0), ThumbnailGridMath.CellOrigin(12, 5, 200, 100));
        });
        T.Run("clamping scroll offsets", () =>
        {
            Approx(0, ThumbnailGridMath.ClampOffset(-50, 1000, 300));
            Approx(700, ThumbnailGridMath.ClampOffset(5000, 1000, 300));
            Approx(250, ThumbnailGridMath.ClampOffset(250, 1000, 300));
            Approx(0, ThumbnailGridMath.ClampOffset(100, 200, 300), "content shorter than viewport can't scroll");
            Approx(0, ThumbnailGridMath.ClampOffset(double.NaN, 1000, 300));
            Approx(0, ThumbnailGridMath.ClampOffset(double.PositiveInfinity, 1000, 300));
        });
        T.Run("reveal: no scroll if already visible, minimal scroll otherwise", () =>
        {
            // rows 100px tall, viewport 300px tall (3 rows), 5 columns
            Approx(100, ThumbnailGridMath.OffsetToReveal(7, 5, 100, 100, 300), "row 1 fully visible at offset 100");
            Approx(0, ThumbnailGridMath.OffsetToReveal(2, 5, 100, 150, 300), "row 0 is above: align to top");
            Approx(300, ThumbnailGridMath.OffsetToReveal(25, 5, 100, 0, 300), "row 5 (500-600) is below: its bottom edge goes to the viewport bottom");
            Approx(50, ThumbnailGridMath.OffsetToReveal(7, 5, 100, 50, 300));
            Approx(400, ThumbnailGridMath.OffsetToReveal(20, 5, 100, 0, 50), "row taller than the viewport aligns to its top");
        });
        T.Run("reveal puts any item fully on screen (brute-force cross-check)", () =>
        {
            var rng = new Random(11);
            for (int i = 0; i < 2000; i++)
            {
                int cols = rng.Next(1, 12), count = rng.Next(1, 600), idx = rng.Next(count);
                double ch = rng.Next(40, 250), vh = ch * rng.Next(1, 8) + rng.Next(0, (int)ch);
                double extent = ThumbnailGridMath.ExtentHeight(count, cols, ch);
                double start = ThumbnailGridMath.ClampOffset(rng.Next(0, 100000), extent, vh);
                double next = ThumbnailGridMath.ClampOffset(ThumbnailGridMath.OffsetToReveal(idx, cols, ch, start, vh), extent, vh);
                double top = idx / cols * ch;
                T.True(top >= next - 1e-9 && top + ch <= next + vh + 1e-9, $"item {idx} visible after reveal (offset {next}, row top {top}, vh {vh})");
                T.True(ThumbnailGridMath.ClampOffset(next, extent, vh) == next);
                // and the realised range always contains it
                var (f, e) = ThumbnailGridMath.VisibleRange(count, cols * 100.0, vh, next, 100, ch, overscanRows: 0);
                T.True(idx >= f && idx < e, $"item {idx} inside VisibleRange [{f},{e})");
            }
        });
        T.Run("retained window widens the visible range and clamps to the list", () =>
        {
            T.Eq((80, 220), ThumbnailGridMath.RetainedRange(1000, 100, 200, 20));
            T.Eq((0, 130), ThumbnailGridMath.RetainedRange(1000, 10, 100, 30), "clamped at the start");
            T.Eq((970, 1000), ThumbnailGridMath.RetainedRange(1000, 990, 1000, 20), "clamped at the end");
            T.Eq((0, 50), ThumbnailGridMath.RetainedRange(50, 0, 10, 500), "small list: everything");
            T.Eq((0, 0), ThumbnailGridMath.RetainedRange(0, 0, 0, 10), "empty list");
            T.Eq((50, 50), ThumbnailGridMath.RetainedRange(50, 99, 120, 0), "scrolled past the end: empty but valid");
        });
        T.Run("page size", () =>
        {
            T.Eq(3, ThumbnailGridMath.VisibleRowCount(320, 100));
            T.Eq(1, ThumbnailGridMath.VisibleRowCount(50, 100));
            T.Eq(1, ThumbnailGridMath.VisibleRowCount(0, 100));
        });

        // ---------------------------------------------------------------------------------
        T.Group("FileTransfer");
        T.Run("unique names never collide", () =>
        {
            using var d = new TempDir();
            T.Eq(d.File("a.jpg"), FileTransfer.GetUniquePath(d.Path, "a.jpg"));
            d.Write("a.jpg", new byte[1]);
            T.Eq(d.File("a (2).jpg"), FileTransfer.GetUniquePath(d.Path, "a.jpg"));
            d.Write("a (2).jpg", new byte[1]);
            T.Eq(d.File("a (3).jpg"), FileTransfer.GetUniquePath(d.Path, "a.jpg"));
        });
        T.Run("copy keeps the original and never overwrites", () =>
        {
            using var d = new TempDir();
            string src = d.Write("src/p.jpg", new byte[] { 1, 2, 3 });
            string dest1 = FileTransfer.CopyInto(src, d.File("out"));
            string dest2 = FileTransfer.CopyInto(src, d.File("out"));
            T.True(File.Exists(src)); T.Eq("p.jpg", Path.GetFileName(dest1)); T.Eq("p (2).jpg", Path.GetFileName(dest2));
            T.SeqEq(new byte[] { 1, 2, 3 }, File.ReadAllBytes(dest2));
        });
        T.Run("move relocates, renames on conflict, and is a no-op in the same folder", () =>
        {
            using var d = new TempDir();
            string a = d.Write("src/p.jpg", new byte[] { 9 });
            d.Write("dst/p.jpg", new byte[] { 7 });
            string moved = FileTransfer.MoveInto(a, d.File("dst"));
            T.True(!File.Exists(a)); T.Eq("p (2).jpg", Path.GetFileName(moved));
            T.SeqEq(new byte[] { 7 }, File.ReadAllBytes(d.File("dst/p.jpg")), "existing file untouched");
            T.Eq(moved, FileTransfer.MoveInto(moved, d.File("dst")));
            T.True(File.Exists(moved));
        });

        // ---------------------------------------------------------------------------------
        T.Group("FolderWatcher");
        T.Run("folder changes: created, deleted and renamed folders report their parent once", () =>
        {
            string root = Path.Combine(Path.GetTempPath(), "phx-watch"), sub = Path.Combine(root, "sub");
            var dirs = new HashSet<string>(PathUtil.Comparer) { Path.Combine(root, "new"), Path.Combine(sub, "deeper"), Path.Combine(root, "renamed-to") };
            var files = new HashSet<string>(PathUtil.Comparer) { Path.Combine(root, "x.jpg") };
            var changed = FolderWatcher.ChangedFolders(
                [Path.Combine(root, "new"), Path.Combine(root, "gone"), Path.Combine(root, "x.jpg"), Path.Combine(root, "y.jpg"),
                 Path.Combine(sub, "deeper"), Path.Combine(root, "renamed-from"), Path.Combine(root, "renamed-to")],
                dirs.Contains, p => dirs.Contains(p) || files.Contains(p));
            T.SeqEq(new[] { root, sub }, changed.OrderBy(x => x.Length));
        });
        T.Run("folder changes: files alone report no folder", () =>
        {
            string root = Path.Combine(Path.GetTempPath(), "phx-watch");
            T.Eq(0, FolderWatcher.ChangedFolders([Path.Combine(root, "a.jpg"), Path.Combine(root, "gone.png")], _ => false, _ => false).Count);
        });
        T.Run("reports a subfolder being created and deleted (for the folder tree)", () =>
        {
            using var d = new TempDir();
            var scanner = new DirectoryScanner(new ImageFileTypes());
            using var w = new FolderWatcher(scanner.ShouldShowFile, TimeSpan.FromMilliseconds(250));
            var folders = new List<string>();
            var got = new ManualResetEventSlim();
            w.FoldersChanged += f => { lock (folders) folders.AddRange(f); got.Set(); };
            T.True(w.Watch(d.Path, false));
            Directory.CreateDirectory(d.File("new folder"));
            T.True(got.Wait(TimeSpan.FromSeconds(10)), "creating a folder is reported");
            lock (folders) { T.True(folders.All(f => PathUtil.SamePath(f, Path.GetFullPath(d.Path))), string.Join(", ", folders)); folders.Clear(); }
            got.Reset();
            Directory.Delete(d.File("new folder"));
            T.True(got.Wait(TimeSpan.FromSeconds(10)), "deleting a folder is reported");
            lock (folders) T.True(folders.Count > 0 && folders.All(f => PathUtil.SamePath(f, Path.GetFullPath(d.Path))));
        });
        T.Run("reports new, modified and deleted files after the quiet period", () =>
        {
            using var d = new TempDir();
            string existing = d.Write("old.jpg", new byte[] { 1 });
            string doomed = d.Write("doomed.jpg", new byte[] { 1 });
            var scanner = new DirectoryScanner(new ImageFileTypes());
            using var w = new FolderWatcher(scanner.ShouldShowFile, TimeSpan.FromMilliseconds(250));
            var changed = new List<string>(); var deleted = new List<string>();
            var got = new ManualResetEventSlim();
            w.FilesChanged += (c, del) => { lock (changed) { changed.AddRange(c); deleted.AddRange(del); } got.Set(); };
            T.True(w.Watch(d.Path, false), "watch started");

            File.WriteAllBytes(d.File("new.jpg"), new byte[] { 2 });
            File.WriteAllBytes(d.File("ignored.txt"), new byte[] { 2 });
            File.AppendAllText(existing, "x");
            File.Delete(doomed);
            T.True(got.Wait(TimeSpan.FromSeconds(10)), "got a change notification");
            Thread.Sleep(600);                                    // let any trailing batch land

            lock (changed)
            {
                var names = changed.Select(Path.GetFileName).Distinct().OrderBy(x => x).ToList();
                T.SeqEq(new[] { "new.jpg", "old.jpg" }, names, "changed");
                T.SeqEq(new[] { "doomed.jpg" }, deleted.Select(Path.GetFileName).Distinct(), "deleted");
            }
        });
        T.Run("bursts are coalesced into one notification", () =>
        {
            using var d = new TempDir();
            var scanner = new DirectoryScanner(new ImageFileTypes());
            using var w = new FolderWatcher(scanner.ShouldShowFile, TimeSpan.FromMilliseconds(500));
            int batches = 0; var all = new List<string>();
            w.FilesChanged += (c, _) => { lock (all) { batches++; all.AddRange(c); } };
            T.True(w.Watch(d.Path, false));
            for (int i = 0; i < 12; i++) { File.WriteAllBytes(d.File($"f{i}.jpg"), new byte[] { 1 }); Thread.Sleep(20); }
            Thread.Sleep(2500);
            lock (all)
            {
                T.Eq(12, all.Select(Path.GetFileName).Distinct().Count(), "all files reported");
                T.True(batches <= 2, $"coalesced (batches: {batches})");
            }
        });
        T.Run("ignores subfolders unless asked, and picks them up when asked", () =>
        {
            using var d = new TempDir();
            Directory.CreateDirectory(d.File("sub"));
            var scanner = new DirectoryScanner(new ImageFileTypes());
            foreach (bool recurse in new[] { false, true })
            {
                using var w = new FolderWatcher(scanner.ShouldShowFile, TimeSpan.FromMilliseconds(250));
                var seen = new List<string>();
                w.FilesChanged += (c, _) => { lock (seen) seen.AddRange(c); };
                T.True(w.Watch(d.Path, recurse));
                string name = recurse ? "deep-yes.jpg" : "deep-no.jpg";
                File.WriteAllBytes(d.File("sub/" + name), new byte[] { 1 });
                Thread.Sleep(1800);
                lock (seen) T.Eq(recurse, seen.Any(p => Path.GetFileName(p) == name), $"recursive={recurse}");
            }
        });
        T.Run("refuses missing folders and recursive watches of a drive root; stop is idempotent", () =>
        {
            using var d = new TempDir();
            using var w = new FolderWatcher(_ => true);
            T.True(!w.Watch(d.File("does-not-exist"), false));
            T.True(!w.Watch(Path.GetPathRoot(Path.GetFullPath(d.Path))!, true));
            T.True(w.Watch(d.Path, false) && w.IsWatching);
            w.Stop(); w.Stop(); T.True(!w.IsWatching);
            w.Dispose(); w.Dispose();
        });
        T.Run("reports when the watched folder disappears", () =>
        {
            using var d = new TempDir();
            string sub = d.File("watched"); Directory.CreateDirectory(sub);
            using var w = new FolderWatcher(_ => true, TimeSpan.FromMilliseconds(250));
            var gone = new ManualResetEventSlim();
            w.RootChanged += () => gone.Set();
            T.True(w.Watch(sub, false));
            Directory.Delete(sub);
            T.True(gone.Wait(TimeSpan.FromSeconds(10)), "root-changed raised");
        });
    }
}
