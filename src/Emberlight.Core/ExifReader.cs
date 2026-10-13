using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Emberlight.Core;

/// <summary>
/// Minimal, fully managed EXIF reader/patcher (replaces the bundled exiftags C library).
/// Understands EXIF in JPEG (APP1), TIFF (and TIFF-based RAW), and PNG (eXIf chunk).
/// Never throws on malformed input: damaged data just yields fewer tags (or null).
/// </summary>
public static class ExifReader
{
    private const int MaxEntriesPerIfd = 4096;
    private const int MaxThumbnailBytes = 8 * 1024 * 1024;

    // ---- public API ----------------------------------------------------------

    public static ExifData? Read(string path, bool includeThumbnail = false)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.RandomAccess);
            return Read(fs, includeThumbnail);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    public static ExifData? Read(Stream stream, bool includeThumbnail = false)
    {
        try
        {
            if (!stream.CanSeek) return null;
            stream.Position = 0;
            Span<byte> head = stackalloc byte[8];
            int n = stream.Read(head);
            if (n >= 2 && head[0] == 0xFF && head[1] == 0xD8)
                return ReadJpeg(stream, includeThumbnail);
            if (n >= 4 && IsTiffHeader(head))
                return ParseTiff(stream, 0, includeThumbnail);
            if (n >= 8 && head.SequenceEqual(PngSignature))
                return ReadPng(stream, includeThumbnail);
            return null;
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException or InvalidDataException
                                        or ArgumentException or OverflowException)
        {
            return null;
        }
    }

    /// <summary>EXIF orientation of a file, or 0 if none.</summary>
    public static ushort GetOrientation(string path) => Read(path)?.Orientation ?? 0;

    /// <summary>EXIF capture date of a file, if it has one.</summary>
    public static DateTime? GetDateTaken(string path) => Read(path)?.DateTaken;

    /// <summary>The embedded JPEG preview (IFD1 thumbnail), if the file has one.</summary>
    public static byte[]? GetEmbeddedThumbnail(string path) => Read(path, includeThumbnail: true)?.ThumbnailJpeg;

    /// <summary>
    /// Rewrites the EXIF orientation value in place without touching pixel data (a lossless
    /// "rotate"). Only works when the file already has an orientation tag. Returns false otherwise.
    /// </summary>
    public static bool TrySetOrientation(string path, int orientation, bool preserveModifiedTime)
    {
        if (orientation is < 1 or > 8) return false;
        try
        {
            var data = Read(path);
            if (data is null || data.OrientationFileOffset < 0) return false;

            DateTime lastWrite = File.GetLastWriteTimeUtc(path);
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
            {
                fs.Position = data.OrientationFileOffset;
                Span<byte> v = stackalloc byte[2];
                if (data.BigEndian) BinaryPrimitives.WriteUInt16BigEndian(v, (ushort)orientation);
                else BinaryPrimitives.WriteUInt16LittleEndian(v, (ushort)orientation);
                fs.Write(v);
            }
            if (preserveModifiedTime) File.SetLastWriteTimeUtc(path, lastWrite);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // ---- containers -----------------------------------------------------------

    private static readonly byte[] PngSignature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
    private static readonly byte[] ExifHeader = { (byte)'E', (byte)'x', (byte)'i', (byte)'f', 0, 0 };

    private static bool IsTiffHeader(ReadOnlySpan<byte> b)
        => (b[0] == 'I' && b[1] == 'I' && b[2] == 0x2A && b[3] == 0x00)
        || (b[0] == 'M' && b[1] == 'M' && b[2] == 0x00 && b[3] == 0x2A);

    private static ExifData? ReadJpeg(Stream s, bool thumb)
    {
        s.Position = 2; // past SOI
        Span<byte> two = stackalloc byte[2];
        Span<byte> hdr = stackalloc byte[6];
        while (true)
        {
            int b = s.ReadByte();
            if (b < 0) return null;
            if (b != 0xFF) continue;                    // resync on garbage
            int marker;
            do { marker = s.ReadByte(); } while (marker == 0xFF); // skip fill bytes
            if (marker < 0) return null;
            if (marker == 0xD9 || marker == 0xDA) return null;    // EOI or start of scan: no EXIF before pixels
            if (marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7) || marker == 0x00) continue; // no length

            if (s.Read(two) != 2) return null;
            int segLen = BinaryPrimitives.ReadUInt16BigEndian(two) - 2;
            if (segLen < 0) return null;
            long payloadStart = s.Position;

            if (marker == 0xE1 && segLen >= ExifHeader.Length + 8)
            {
                if (s.Read(hdr) == 6 && hdr.SequenceEqual(ExifHeader))
                {
                    var payload = new byte[segLen - ExifHeader.Length];
                    ReadExactly(s, payload);
                    using var ms = new MemoryStream(payload, writable: false);
                    var data = ParseTiff(ms, payloadStart + ExifHeader.Length, thumb);
                    if (data != null) return data;
                }
            }
            s.Position = payloadStart + segLen;
        }
    }

    private static ExifData? ReadPng(Stream s, bool thumb)
    {
        s.Position = 8;
        Span<byte> hdr = stackalloc byte[8];
        while (s.Read(hdr) == 8)
        {
            uint len = BinaryPrimitives.ReadUInt32BigEndian(hdr);
            string type = Encoding.ASCII.GetString(hdr.Slice(4, 4));
            long dataStart = s.Position;
            if (type == "eXIf" && len >= 8 && len < 16 * 1024 * 1024)
            {
                var payload = new byte[len];
                ReadExactly(s, payload);
                using var ms = new MemoryStream(payload, writable: false);
                return ParseTiff(ms, dataStart, thumb);
            }
            if (type == "IDAT" || type == "IEND") return null;
            s.Position = dataStart + len + 4; // skip data + CRC
        }
        return null;
    }

    private static void ReadExactly(Stream s, byte[] buffer)
    {
        int off = 0;
        while (off < buffer.Length)
        {
            int n = s.Read(buffer, off, buffer.Length - off);
            if (n <= 0) throw new EndOfStreamException();
            off += n;
        }
    }

    // ---- TIFF/EXIF structure ----------------------------------------------------

    private sealed class Tiff
    {
        public readonly Stream S;
        public readonly bool Big;
        public readonly long FileBase; // absolute file offset of the TIFF header (for orientation patching)
        private readonly byte[] _buf = new byte[8];

        public Tiff(Stream s, bool big, long fileBase) { S = s; Big = big; FileBase = fileBase; }

        public ushort U16(long pos)
        {
            Fill(pos, 2);
            return Big ? BinaryPrimitives.ReadUInt16BigEndian(_buf) : BinaryPrimitives.ReadUInt16LittleEndian(_buf);
        }

        public uint U32(long pos)
        {
            Fill(pos, 4);
            return Big ? BinaryPrimitives.ReadUInt32BigEndian(_buf) : BinaryPrimitives.ReadUInt32LittleEndian(_buf);
        }

        public byte[] Bytes(long pos, int len)
        {
            if (len < 0 || pos < 0 || pos + len > S.Length) throw new InvalidDataException("range");
            var b = new byte[len];
            S.Position = pos;
            ReadExactly(S, b);
            return b;
        }

        private void Fill(long pos, int n)
        {
            if (pos < 0 || pos + n > S.Length) throw new InvalidDataException("range");
            S.Position = pos;
            int off = 0;
            while (off < n)
            {
                int r = S.Read(_buf, off, n - off);
                if (r <= 0) throw new EndOfStreamException();
                off += r;
            }
        }
    }

    private readonly struct Entry
    {
        public readonly ushort Tag, Type;
        public readonly uint Count;
        public readonly long ValuePos; // position of the 4-byte value/offset field, relative to TIFF start

        public Entry(ushort tag, ushort type, uint count, long valuePos)
        { Tag = tag; Type = type; Count = count; ValuePos = valuePos; }

        public static int TypeSize(ushort type) => type switch
        {
            1 or 2 or 6 or 7 => 1,
            3 or 8 => 2,
            4 or 9 or 11 => 4,
            5 or 10 or 12 => 8,
            _ => 0,
        };
    }

    private static ExifData? ParseTiff(Stream s, long fileBase, bool wantThumb)
    {
        // `s` is positioned relative to the TIFF header: either the whole file (fileBase 0) or a
        // payload copy whose position 0 is the header.
        if (s.Length < 8) return null;
        Span<byte> h = stackalloc byte[8];
        s.Position = 0;
        if (s.Read(h) != 8 || !IsTiffHeader(h)) return null;
        bool big = h[0] == 'M';
        var t = new Tiff(s, big, fileBase);
        uint ifd0 = big ? BinaryPrimitives.ReadUInt32BigEndian(h[4..]) : BinaryPrimitives.ReadUInt32LittleEndian(h[4..]);
        if (ifd0 < 8) return null;

        var data = new ExifData { BigEndian = big };
        try
        {
            uint next = ReadIfd0(t, ifd0, data);
            if (wantThumb && next != 0) data.ThumbnailJpeg = ReadThumbnail(t, next);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or EndOfStreamException)
        {
            // keep whatever was parsed before the damage
        }
        return data;
    }

    private static List<Entry> ReadIfd(Tiff t, long offset, out uint nextIfd)
    {
        nextIfd = 0;
        var list = new List<Entry>();
        int count = t.U16(offset);
        if (count > MaxEntriesPerIfd) count = MaxEntriesPerIfd;
        for (int i = 0; i < count; i++)
        {
            long e = offset + 2 + 12L * i;
            list.Add(new Entry(t.U16(e), t.U16(e + 2), t.U32(e + 4), e + 8));
        }
        long nextPos = offset + 2 + 12L * count;
        if (nextPos + 4 <= t.S.Length) nextIfd = t.U32(nextPos);
        return list;
    }

    private static uint ReadIfd0(Tiff t, uint offset, ExifData d)
    {
        var entries = ReadIfd(t, offset, out uint next);
        uint exifIfd = 0, gpsIfd = 0;
        string? make = null, model = null;

        foreach (var e in entries)
        {
            switch (e.Tag)
            {
                case 0x010F: make = Ascii(t, e); break;
                case 0x0110: model = Ascii(t, e); break;
                case 0x0112:
                    if (e.Type == 3 && e.Count == 1)
                    {
                        ushort v = t.U16(e.ValuePos);
                        if (v is >= 1 and <= 8)
                        {
                            d.Orientation = v;
                            d.OrientationFileOffset = t.FileBase + e.ValuePos;
                        }
                    }
                    break;
                case 0x0131:
                    AddTag(d, "Software", Ascii(t, e), extended: true);
                    break;
                case 0x0132: d.DateTime = ParseDate(Ascii(t, e)); break;
                case 0x8769: exifIfd = t.U32(e.ValuePos); break;
                case 0x8825: gpsIfd = t.U32(e.ValuePos); break;
            }
        }

        var camera = string.Join(' ', new[] { make, model }.Where(x => !string.IsNullOrWhiteSpace(x)));
        if (camera.Length > 0)
            d._tags.Insert(0, new ExifTag("Camera", camera, false));

        if (exifIfd != 0) ReadExifIfd(t, exifIfd, d);
        if (gpsIfd != 0) ReadGpsIfd(t, gpsIfd, d);

        if (d.DateTaken is { } taken)
            d._tags.Insert(d._tags.Count > 0 && d._tags[0].Name == "Camera" ? 1 : 0,
                new ExifTag("Taken", taken.ToString("g", CultureInfo.CurrentCulture), false));
        return next;
    }

    private static void ReadExifIfd(Tiff t, uint offset, ExifData d)
    {
        List<Entry> entries;
        try { entries = ReadIfd(t, offset, out _); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or EndOfStreamException) { return; }

        ushort? width = null, height = null;
        foreach (var e in entries)
        {
            try
            {
                switch (e.Tag)
                {
                    case 0x829A: // ExposureTime
                        if (Rational(t, e) is { } et) AddTag(d, "Exposure", FormatExposure(et.Num, et.Den), false);
                        break;
                    case 0x829D: // FNumber
                        if (Rational(t, e) is { } fn && fn.Den != 0)
                            AddTag(d, "Aperture", "f/" + (fn.Num / (double)fn.Den).ToString("0.#", CultureInfo.InvariantCulture), false);
                        break;
                    case 0x8822: AddTag(d, "Exposure Program", ExposureProgram(t.U16(e.ValuePos)), true); break;
                    case 0x8827: AddTag(d, "ISO", t.U16(e.ValuePos).ToString(CultureInfo.InvariantCulture), false); break;
                    case 0x9003: d.DateTimeOriginal = ParseDate(Ascii(t, e)); break;
                    case 0x9004: d.DateTimeDigitized = ParseDate(Ascii(t, e)); break;
                    case 0x9204: // ExposureBiasValue (SRATIONAL)
                        if (SRational(t, e) is { } eb && eb.Den != 0)
                            AddTag(d, "Exposure Bias", (eb.Num / (double)eb.Den).ToString("+0.#;-0.#;0", CultureInfo.InvariantCulture) + " EV", false);
                        break;
                    case 0x9207: AddTag(d, "Metering", MeteringMode(t.U16(e.ValuePos)), true); break;
                    case 0x9209:
                        AddTag(d, "Flash", (t.U16(e.ValuePos) & 1) != 0 ? "Fired" : "Did not fire", false);
                        break;
                    case 0x920A: // FocalLength
                        if (Rational(t, e) is { } fl && fl.Den != 0)
                            AddTag(d, "Focal Length", (fl.Num / (double)fl.Den).ToString("0.#", CultureInfo.InvariantCulture) + " mm", false);
                        break;
                    case 0xA001:
                        {
                            ushort cs = t.U16(e.ValuePos);
                            AddTag(d, "Color Space", cs == 1 ? "sRGB" : cs == 0xFFFF ? "Uncalibrated" : cs.ToString(CultureInfo.InvariantCulture), true);
                            break;
                        }
                    case 0xA002: width = ShortOrLong(t, e); break;
                    case 0xA003: height = ShortOrLong(t, e); break;
                    case 0xA403: AddTag(d, "White Balance", t.U16(e.ValuePos) == 0 ? "Auto" : "Manual", true); break;
                    case 0xA434: AddTag(d, "Lens", Ascii(t, e), false); break;
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or EndOfStreamException)
            {
                // skip a bad tag, keep going
            }
        }
        if (width is { } w && height is { } hgt)
            AddTag(d, "Dimensions", $"{w} x {hgt}", true);
    }

    private static void ReadGpsIfd(Tiff t, uint offset, ExifData d)
    {
        try
        {
            var entries = ReadIfd(t, offset, out _);
            string? latRef = null, lonRef = null;
            double? lat = null, lon = null;
            foreach (var e in entries)
            {
                switch (e.Tag)
                {
                    case 1: latRef = Ascii(t, e); break;
                    case 2: lat = Dms(t, e); break;
                    case 3: lonRef = Ascii(t, e); break;
                    case 4: lon = Dms(t, e); break;
                }
            }
            if (lat is { } la && lon is { } lo)
            {
                string s = string.Create(CultureInfo.InvariantCulture,
                    $"{la:0.#####}\u00B0 {(latRef == "S" ? "S" : "N")}, {lo:0.#####}\u00B0 {(lonRef == "W" ? "W" : "E")}");
                AddTag(d, "GPS", s, true);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or EndOfStreamException) { }
    }

    private static byte[]? ReadThumbnail(Tiff t, uint ifd1Offset)
    {
        var entries = ReadIfd(t, ifd1Offset, out _);
        uint off = 0, len = 0;
        ushort compression = 6;
        foreach (var e in entries)
        {
            if (e.Tag == 0x0201) off = t.U32(e.ValuePos);
            else if (e.Tag == 0x0202) len = t.U32(e.ValuePos);
            else if (e.Tag == 0x0103) compression = t.U16(e.ValuePos);
        }
        if (compression != 6 || off == 0 || len < 4 || len > MaxThumbnailBytes) return null;
        var bytes = t.Bytes(off, (int)len);
        return bytes[0] == 0xFF && bytes[1] == 0xD8 ? bytes : null;
    }

    // ---- value helpers --------------------------------------------------------------

    private static void AddTag(ExifData d, string name, string? value, bool extended)
    {
        if (!string.IsNullOrWhiteSpace(value)) d._tags.Add(new ExifTag(name, value.Trim(), extended));
    }

    private static string? Ascii(Tiff t, Entry e)
    {
        if (e.Type != 2 || e.Count == 0 || e.Count > 4096) return null;
        long pos = e.Count <= 4 ? e.ValuePos : t.U32(e.ValuePos);
        var b = t.Bytes(pos, (int)e.Count);
        int end = Array.IndexOf(b, (byte)0);
        if (end < 0) end = b.Length;
        return Encoding.UTF8.GetString(b, 0, end).Trim();
    }

    private static (uint Num, uint Den)? Rational(Tiff t, Entry e)
    {
        if (e.Type != 5 || e.Count < 1) return null;
        long pos = t.U32(e.ValuePos);
        return (t.U32(pos), t.U32(pos + 4));
    }

    private static (int Num, int Den)? SRational(Tiff t, Entry e)
    {
        if (e.Type != 10 || e.Count < 1) return null;
        long pos = t.U32(e.ValuePos);
        return (unchecked((int)t.U32(pos)), unchecked((int)t.U32(pos + 4)));
    }

    private static ushort? ShortOrLong(Tiff t, Entry e)
    {
        if (e.Type == 3) return t.U16(e.ValuePos);
        if (e.Type == 4) { uint v = t.U32(e.ValuePos); return v <= ushort.MaxValue ? (ushort)v : null; }
        return null;
    }

    private static double? Dms(Tiff t, Entry e)
    {
        if (e.Type != 5 || e.Count < 3) return null;
        long pos = t.U32(e.ValuePos);
        double v = 0, div = 1;
        for (int i = 0; i < 3; i++)
        {
            uint n = t.U32(pos + 8L * i), dn = t.U32(pos + 8L * i + 4);
            if (dn == 0) return null;
            v += n / (double)dn / div;
            div *= 60;
        }
        return v;
    }

    private static string FormatExposure(uint num, uint den)
    {
        if (num == 0 || den == 0) return "0 s";
        double secs = num / (double)den;
        if (secs >= 1) return secs.ToString("0.#", CultureInfo.InvariantCulture) + " s";
        return "1/" + Math.Round(1.0 / secs).ToString(CultureInfo.InvariantCulture) + " s";
    }

    private static DateTime? ParseDate(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim().TrimEnd('\0');
        return System.DateTime.TryParseExact(s, "yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var dt) && dt.Year > 1 ? dt : null;
    }

    private static string ExposureProgram(ushort v) => v switch
    {
        1 => "Manual", 2 => "Program", 3 => "Aperture priority", 4 => "Shutter priority",
        5 => "Creative", 6 => "Action", 7 => "Portrait", 8 => "Landscape", _ => "Unknown",
    };

    private static string MeteringMode(ushort v) => v switch
    {
        1 => "Average", 2 => "Center-weighted", 3 => "Spot", 4 => "Multi-spot",
        5 => "Pattern", 6 => "Partial", _ => "Unknown",
    };
}
