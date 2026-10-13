using Emberlight.Core;

namespace Emberlight.Tests;

internal static class ExifTests
{
    public static void RunAll()
    {
        T.Group("ExifReader");

        foreach (bool big in new[] { false, true })
        {
            string endian = big ? "big-endian" : "little-endian";

            T.Run($"parses camera/exposure/date/GPS tags from a JPEG ({endian})", () =>
            {
                var b = new ExifTestBuilder(big);
                var jpeg = ExifTestBuilder.WrapInJpeg(b.BuildSample());
                var d = ExifReader.Read(new MemoryStream(jpeg));
                T.True(d != null, "parsed");
                T.Eq((ushort)6, d!.Orientation);
                T.Eq(new DateTime(2021, 6, 7, 8, 9, 10), d.DateTimeOriginal!.Value);
                T.Eq(new DateTime(2020, 1, 2, 3, 4, 5), d.DateTime!.Value);
                T.Eq(new DateTime(2021, 6, 7, 8, 9, 10), d.DateTaken!.Value, "original wins over modified");

                string Tag(string n) => d.Tags.First(t => t.Name == n).Value;
                T.Eq("Canon EOS 5D", Tag("Camera"));
                T.Eq("1/250 s", Tag("Exposure"));
                T.Eq("f/2.8", Tag("Aperture"));
                T.Eq("400", Tag("ISO"));
                T.Eq("50 mm", Tag("Focal Length"));
                T.Eq("Fired", Tag("Flash"));
                T.Eq("-0.7 EV", Tag("Exposure Bias"));
                T.Eq("EF50mm f/1.8", Tag("Lens"));
                T.Eq("4000 x 3000", Tag("Dimensions"));
                T.Eq("TestSoft 1.0", Tag("Software"));
                T.Eq("40.71278\u00B0 N, 74.00611\u00B0 W", Tag("GPS"));
                T.True(d.Tags.First(t => t.Name == "Dimensions").Extended);
                T.True(!d.Tags.First(t => t.Name == "Exposure").Extended);
            });
        }

        T.Run("basic display string omits extended tags", () =>
        {
            var d = ExifReader.Read(new MemoryStream(ExifTestBuilder.WrapInJpeg(new ExifTestBuilder(false).BuildSample())))!;
            string basic = d.ToDisplayString(false), more = d.ToDisplayString(true);
            T.True(basic.Contains("Exposure: 1/250 s") && !basic.Contains("GPS") && !basic.Contains("Dimensions"));
            T.True(more.Contains("GPS") && more.Contains("Dimensions"));
        });

        T.Run("embedded thumbnail is extracted only when requested", () =>
        {
            var fake = new byte[] { 0xFF, 0xD8, 1, 2, 3, 4, 5, 6, 7, 0xFF, 0xD9 };
            var jpeg = ExifTestBuilder.WrapInJpeg(new ExifTestBuilder(false).BuildSample(thumb: fake));
            T.True(ExifReader.Read(new MemoryStream(jpeg))!.ThumbnailJpeg == null);
            T.SeqEq(fake, ExifReader.Read(new MemoryStream(jpeg), includeThumbnail: true)!.ThumbnailJpeg!);
            // a "thumbnail" that isn't a JPEG is rejected
            var junk = ExifTestBuilder.WrapInJpeg(new ExifTestBuilder(true).BuildSample(thumb: new byte[] { 1, 2, 3, 4, 5 }));
            T.True(ExifReader.Read(new MemoryStream(junk), true)!.ThumbnailJpeg == null);
        });

        T.Run("APP1 need not be the first segment", () =>
        {
            var withJfif = ExifTestBuilder.WrapInJpeg(new ExifTestBuilder(false).BuildSample(), withJfifFirst: true);
            var without = ExifTestBuilder.WrapInJpeg(new ExifTestBuilder(false).BuildSample(), withJfifFirst: false);
            T.Eq((ushort)6, ExifReader.Read(new MemoryStream(withJfif))!.Orientation);
            T.Eq((ushort)6, ExifReader.Read(new MemoryStream(without))!.Orientation);
        });

        T.Run("reads TIFF files and PNG eXIf chunks", () =>
        {
            var tiff = new ExifTestBuilder(false).BuildSample(orientation: 8);
            T.Eq((ushort)8, ExifReader.Read(new MemoryStream(tiff))!.Orientation);
            var png = ExifTestBuilder.WrapInPng(new ExifTestBuilder(true).BuildSample(orientation: 3));
            var d = ExifReader.Read(new MemoryStream(png))!;
            T.Eq((ushort)3, d.Orientation);
            T.Eq(new DateTime(2021, 6, 7, 8, 9, 10), d.DateTaken!.Value);
        });

        T.Run("files without EXIF, and non-images, give null/zero", () =>
        {
            T.True(ExifReader.Read(new MemoryStream(new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 })) == null);
            T.True(ExifReader.Read(new MemoryStream(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 })) == null);
            T.True(ExifReader.Read(new MemoryStream(Array.Empty<byte>())) == null);
            using var d = new TempDir();
            T.Eq((ushort)0, ExifReader.GetOrientation(d.File("missing.jpg")));
            T.True(ExifReader.GetDateTaken(d.File("missing.jpg")) == null);
            // orientation tag absent -> 0
            var noOrient = ExifTestBuilder.WrapInJpeg(new ExifTestBuilder(false).BuildSample(includeOrientation: false));
            T.Eq((ushort)0, ExifReader.Read(new MemoryStream(noOrient))!.Orientation);
        });

        T.Run("invalid orientation values are ignored", () =>
        {
            foreach (int bad in new[] { 0, 9, 255 })
            {
                var jpeg = ExifTestBuilder.WrapInJpeg(new ExifTestBuilder(false).BuildSample(orientation: bad));
                T.Eq((ushort)0, ExifReader.Read(new MemoryStream(jpeg))!.Orientation, $"value {bad}");
            }
        });

        // ---- lossless orientation rewrite -----------------------------------------------
        foreach (var container in new[] { "jpeg-LE", "jpeg-BE", "tiff-LE", "png-BE" })
        {
            T.Run($"TrySetOrientation rewrites exactly the two orientation bytes ({container})", () =>
            {
                bool big = container.EndsWith("BE");
                var tiff = new ExifTestBuilder(big).BuildSample(orientation: 6);
                byte[] file = container.StartsWith("jpeg") ? ExifTestBuilder.WrapInJpeg(tiff)
                            : container.StartsWith("png") ? ExifTestBuilder.WrapInPng(tiff) : tiff;
                using var dir = new TempDir();
                string path = dir.Write("img.bin", file);
                File.SetLastWriteTimeUtc(path, new DateTime(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc));

                T.True(ExifReader.TrySetOrientation(path, 3, preserveModifiedTime: true));
                var after = File.ReadAllBytes(path);
                T.Eq(file.Length, after.Length, "length unchanged");
                int diffs = 0;
                for (int i = 0; i < file.Length; i++) if (file[i] != after[i]) diffs++;
                T.True(diffs is >= 1 and <= 2, $"only the orientation value changed (changed bytes: {diffs})");
                T.Eq((ushort)3, ExifReader.GetOrientation(path));
                T.Eq(new DateTime(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc), File.GetLastWriteTimeUtc(path), "mtime preserved");
                // other tags are still intact
                T.Eq("Canon EOS 5D", ExifReader.Read(path)!.Tags.First(t => t.Name == "Camera").Value);

                // setting it again, this time letting the mtime change
                T.True(ExifReader.TrySetOrientation(path, 1, preserveModifiedTime: false));
                T.Eq((ushort)1, ExifReader.GetOrientation(path));
            });
        }

        T.Run("TrySetOrientation refuses bad input and files it can't patch", () =>
        {
            using var dir = new TempDir();
            string withTag = dir.Write("a.jpg", ExifTestBuilder.WrapInJpeg(new ExifTestBuilder(false).BuildSample(orientation: 6)));
            string noTag = dir.Write("b.jpg", ExifTestBuilder.WrapInJpeg(new ExifTestBuilder(false).BuildSample(includeOrientation: false)));
            string junk = dir.Write("c.jpg", new byte[] { 1, 2, 3 });
            var before = File.ReadAllBytes(withTag);
            T.True(!ExifReader.TrySetOrientation(withTag, 0, false));
            T.True(!ExifReader.TrySetOrientation(withTag, 9, false));
            T.SeqEq(before, File.ReadAllBytes(withTag), "file untouched");
            T.True(!ExifReader.TrySetOrientation(noTag, 3, false));
            T.True(!ExifReader.TrySetOrientation(junk, 3, false));
            T.True(!ExifReader.TrySetOrientation(dir.File("missing.jpg"), 3, false));
        });

        T.Run("never throws on truncated data (every prefix length)", () =>
        {
            foreach (bool big in new[] { false, true })
            {
                var full = ExifTestBuilder.WrapInJpeg(new ExifTestBuilder(big).BuildSample(thumb: new byte[] { 0xFF, 0xD8, 1, 2, 3, 0xFF, 0xD9 }));
                for (int len = 0; len <= full.Length; len++)
                    ExifReader.Read(new MemoryStream(full, 0, len), includeThumbnail: true);
                var tiff = new ExifTestBuilder(big).BuildSample();
                for (int len = 0; len <= tiff.Length; len++)
                    ExifReader.Read(new MemoryStream(tiff, 0, len), includeThumbnail: true);
                var png = ExifTestBuilder.WrapInPng(new ExifTestBuilder(big).BuildSample());
                for (int len = 0; len <= png.Length; len++)
                    ExifReader.Read(new MemoryStream(png, 0, len), includeThumbnail: true);
            }
        });

        T.Run("never throws or hangs on randomly corrupted data", () =>
        {
            var rng = new Random(12345);
            var originals = new[]
            {
                ExifTestBuilder.WrapInJpeg(new ExifTestBuilder(false).BuildSample(thumb: new byte[] { 0xFF, 0xD8, 9, 9, 0xFF, 0xD9 })),
                ExifTestBuilder.WrapInJpeg(new ExifTestBuilder(true).BuildSample()),
                new ExifTestBuilder(false).BuildSample(),
                ExifTestBuilder.WrapInPng(new ExifTestBuilder(true).BuildSample()),
            };
            var sw = System.Diagnostics.Stopwatch.StartNew();
            for (int iter = 0; iter < 4000; iter++)
            {
                var data = (byte[])originals[iter % originals.Length].Clone();
                int flips = rng.Next(1, 6);
                for (int f = 0; f < flips; f++) data[rng.Next(data.Length)] = (byte)rng.Next(256);
                ExifReader.Read(new MemoryStream(data), includeThumbnail: true);
            }
            T.True(sw.Elapsed < TimeSpan.FromSeconds(20), "parsing stays fast on garbage");
        });
    }
}
