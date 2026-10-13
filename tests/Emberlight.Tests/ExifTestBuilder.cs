using System.Text;

namespace Emberlight.Tests;

/// <summary>Synthesizes TIFF/EXIF blocks (and JPEG/PNG wrappers) so the parser can be tested without sample photos.</summary>
internal sealed class ExifTestBuilder
{
    public sealed record Entry(ushort Tag, ushort Type, uint Count, byte[] Data);
    public sealed class Ifd { public List<Entry> Entries = new(); }

    private readonly bool _big;
    public ExifTestBuilder(bool bigEndian) => _big = bigEndian;

    // ---- value constructors (data is already in the file's byte order) ----
    public byte[] B16(int v) => _big ? new[] { (byte)(v >> 8), (byte)v } : new[] { (byte)v, (byte)(v >> 8) };
    public byte[] B32(uint v) => _big
        ? new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v }
        : new[] { (byte)v, (byte)(v >> 8), (byte)(v >> 16), (byte)(v >> 24) };

    public Entry Short(ushort tag, int v) => new(tag, 3, 1, B16(v));
    public Entry Long(ushort tag, uint v) => new(tag, 4, 1, B32(v));
    public Entry Ascii(ushort tag, string s) { var b = Encoding.ASCII.GetBytes(s + "\0"); return new(tag, 2, (uint)b.Length, b); }
    public Entry Rational(ushort tag, uint n, uint d) => new(tag, 5, 1, B32(n).Concat(B32(d)).ToArray());
    public Entry SRational(ushort tag, int n, int d) => new(tag, 10, 1, B32(unchecked((uint)n)).Concat(B32(unchecked((uint)d))).ToArray());
    public Entry Rationals(ushort tag, params (uint n, uint d)[] v)
        => new(tag, 5, (uint)v.Length, v.SelectMany(x => B32(x.n).Concat(B32(x.d))).ToArray());

    /// <summary>Lays out IFD0 (+ optional Exif/GPS sub-IFDs and an IFD1 thumbnail) and returns the TIFF block.</summary>
    public byte[] BuildTiff(Ifd ifd0, Ifd? exif = null, Ifd? gps = null, byte[]? thumb = null)
    {
        var e0 = new List<Entry>(ifd0.Entries);
        int placeholders0 = e0.Count;
        if (exif != null) e0.Add(Long(0x8769, 0));
        if (gps != null) e0.Add(Long(0x8825, 0));
        var e1 = new List<Entry>();
        if (thumb != null) { e1.Add(Short(0x0103, 6)); e1.Add(Long(0x0201, 0)); e1.Add(Long(0x0202, (uint)thumb.Length)); }

        static int IfdSize(int n) => 2 + 12 * n + 4;
        uint pos = 8;
        uint off0 = pos; pos += (uint)IfdSize(e0.Count);
        uint offExif = 0, offGps = 0, off1 = 0;
        if (exif != null) { offExif = pos; pos += (uint)IfdSize(exif.Entries.Count); }
        if (gps != null) { offGps = pos; pos += (uint)IfdSize(gps.Entries.Count); }
        if (thumb != null) { off1 = pos; pos += (uint)IfdSize(e1.Count); }
        uint dataStart = pos;

        var data = new List<byte>();
        uint thumbOff = 0;
        if (thumb != null) { thumbOff = dataStart; data.AddRange(thumb); if (data.Count % 2 == 1) data.Add(0); }

        // patch pointer entries now that offsets are known
        int k = placeholders0;
        if (exif != null) e0[k++] = Long(0x8769, offExif);
        if (gps != null) e0[k++] = Long(0x8825, offGps);
        if (thumb != null) e1[1] = Long(0x0201, thumbOff);

        var outp = new List<byte>();
        outp.AddRange(new byte[] { (byte)(_big ? 'M' : 'I'), (byte)(_big ? 'M' : 'I') });
        outp.AddRange(B16(42));
        outp.AddRange(B32(off0));

        void WriteIfd(List<Entry> entries, uint next)
        {
            outp.AddRange(B16(entries.Count));
            foreach (var e in entries)
            {
                outp.AddRange(B16(e.Tag)); outp.AddRange(B16(e.Type)); outp.AddRange(B32(e.Count));
                if (e.Data.Length <= 4)
                {
                    outp.AddRange(e.Data);
                    for (int i = e.Data.Length; i < 4; i++) outp.Add(0);
                }
                else
                {
                    outp.AddRange(B32(dataStart + (uint)data.Count));
                    data.AddRange(e.Data);
                    if (data.Count % 2 == 1) data.Add(0);
                }
            }
            outp.AddRange(B32(next));
        }

        WriteIfd(e0, off1);
        if (exif != null) WriteIfd(exif.Entries, 0);
        if (gps != null) WriteIfd(gps.Entries, 0);
        if (thumb != null) WriteIfd(e1, 0);
        // thumbnail bytes were pre-placed at the start of `data`, so they land at dataStart as promised
        outp.AddRange(data);
        return outp.ToArray();
    }

    public static byte[] WrapInJpeg(byte[] tiff, bool withJfifFirst = true)
    {
        var o = new List<byte> { 0xFF, 0xD8 };
        if (withJfifFirst)
        {
            // APP0 "JFIF" segment, so the reader has to skip a segment before finding APP1
            var jfif = new byte[] { (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0, 1, 1, 0, 0, 1, 0, 1, 0, 0 };
            o.AddRange(new byte[] { 0xFF, 0xE0, (byte)((jfif.Length + 2) >> 8), (byte)(jfif.Length + 2) });
            o.AddRange(jfif);
        }
        int len = 2 + 6 + tiff.Length;
        o.AddRange(new byte[] { 0xFF, 0xE1, (byte)(len >> 8), (byte)len });
        o.AddRange(new byte[] { (byte)'E', (byte)'x', (byte)'i', (byte)'f', 0, 0 });
        o.AddRange(tiff);
        o.AddRange(new byte[] { 0xFF, 0xDA, 0x00, 0x02, 0x11, 0x22, 0x33, 0xFF, 0xD9 });
        return o.ToArray();
    }

    public static byte[] WrapInPng(byte[] tiff)
    {
        var o = new List<byte> { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        void Chunk(string type, byte[] body)
        {
            o.AddRange(new[] { (byte)(body.Length >> 24), (byte)(body.Length >> 16), (byte)(body.Length >> 8), (byte)body.Length });
            o.AddRange(Encoding.ASCII.GetBytes(type));
            o.AddRange(body);
            o.AddRange(new byte[] { 0, 0, 0, 0 }); // CRC is not checked by the reader
        }
        Chunk("IHDR", new byte[13]);
        Chunk("eXIf", tiff);
        Chunk("IDAT", new byte[] { 1, 2, 3 });
        Chunk("IEND", Array.Empty<byte>());
        return o.ToArray();
    }

    /// <summary>A realistic-looking set of tags used by several tests.</summary>
    public byte[] BuildSample(int orientation = 6, bool includeOrientation = true, byte[]? thumb = null)
    {
        var ifd0 = new Ifd();
        ifd0.Entries.Add(Ascii(0x010F, "Canon"));
        ifd0.Entries.Add(Ascii(0x0110, "EOS 5D"));
        if (includeOrientation) ifd0.Entries.Add(Short(0x0112, orientation));
        ifd0.Entries.Add(Ascii(0x0131, "TestSoft 1.0"));
        ifd0.Entries.Add(Ascii(0x0132, "2020:01:02 03:04:05"));

        var exif = new Ifd();
        exif.Entries.Add(Rational(0x829A, 1, 250));
        exif.Entries.Add(Rational(0x829D, 28, 10));
        exif.Entries.Add(Short(0x8827, 400));
        exif.Entries.Add(Ascii(0x9003, "2021:06:07 08:09:10"));
        exif.Entries.Add(SRational(0x9204, -2, 3));
        exif.Entries.Add(Short(0x9209, 1));
        exif.Entries.Add(Rational(0x920A, 50, 1));
        exif.Entries.Add(Long(0xA002, 4000));
        exif.Entries.Add(Short(0xA003, 3000));
        exif.Entries.Add(Ascii(0xA434, "EF50mm f/1.8"));

        var gps = new Ifd();
        gps.Entries.Add(Ascii(1, "N"));
        gps.Entries.Add(Rationals(2, (40, 1), (42, 1), (46, 1)));
        gps.Entries.Add(Ascii(3, "W"));
        gps.Entries.Add(Rationals(4, (74, 1), (0, 1), (22, 1)));

        return BuildTiff(ifd0, exif, gps, thumb);
    }
}
