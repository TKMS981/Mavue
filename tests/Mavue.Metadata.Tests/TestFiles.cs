using System.Buffers.Binary;
using System.Text;
using Mavue.Core.Archives;

namespace Mavue.Metadata.Tests;

/// <summary>A TIFF entry to write: tag, type, count and the value's bytes (in the builder's byte order).</summary>
internal sealed record Field(ushort Tag, ushort Type, uint Count, byte[] Value);

/// <summary>Builds EXIF (TIFF) blocks and small image files byte by byte, so tests control every metadata block.</summary>
internal static class TestFiles
{
    public const ushort Ascii = 2;
    public const ushort Short = 3;
    public const ushort Long = 4;
    public const ushort Rational = 5;
    public const ushort SRational = 10;
    public const ushort Undefined = 7;
    public const ushort ByteType = 1;

    public static Field Text(ushort tag, string value, bool big = false) => new(tag, Ascii, (uint)Encoding.UTF8.GetByteCount(value) + 1, [.. Encoding.UTF8.GetBytes(value), 0]);

    public static Field U16(ushort tag, ushort value, bool big = false)
    {
        byte[] b = new byte[2];
        if (big)
        {
            BinaryPrimitives.WriteUInt16BigEndian(b, value);
        }
        else
        {
            BinaryPrimitives.WriteUInt16LittleEndian(b, value);
        }

        return new(tag, Short, 1, b);
    }

    public static Field Rationals(ushort tag, bool big, params (int N, int D)[] values)
    {
        byte[] b = new byte[8 * values.Length];
        for (int i = 0; i < values.Length; i++)
        {
            if (big)
            {
                BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(i * 8), values[i].N);
                BinaryPrimitives.WriteInt32BigEndian(b.AsSpan((i * 8) + 4), values[i].D);
            }
            else
            {
                BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(i * 8), values[i].N);
                BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan((i * 8) + 4), values[i].D);
            }
        }

        return new(tag, Rational, (uint)values.Length, b);
    }

    public static Field Bytes(ushort tag, byte[] value) => new(tag, Undefined, (uint)value.Length, value);

    /// <summary>A full EXIF block: camera, lens, settings, dates, author, serials, owner, maker note, GPS and a thumbnail.</summary>
    public static byte[] RichExif(bool big = false, int orientation = 6)
    {
        Field[] image =
        [
            Text(0x010F, "Canon"), Text(0x0110, "Canon EOS R5"), U16(0x0112, (ushort)orientation, big), Text(0x0131, "Adobe Lightroom 7.0"),
            Text(0x0132, "2026:10:02 09:00:00"), Text(0x013B, "Taro Yamada"), Text(0x8298, "(c) Taro"), Text(0x010E, "Family trip"),
        ];
        Field[] exif =
        [
            Rationals(0x829A, big, (1, 250)), Rationals(0x829D, big, (28, 10)), U16(0x8827, 400, big), Text(0x9003, "2026:10:01 14:03:22"),
            Text(0x9011, "+09:00"), Rationals(0x920A, big, (35, 1)), U16(0xA405, 52, big), Text(0xA430, "Taro Yamada"), Text(0xA431, "012345678"),
            Text(0xA434, "RF24-70mm F2.8 L IS USM"), Text(0xA435, "LENS9876"), Bytes(0x927C, new byte[2000]), U16(0xA001, 1, big),
        ];
        Field[] gps =
        [
            Text(0x0001, "N"), Rationals(0x0002, big, (35, 1), (40, 1), (52, 1)), Text(0x0003, "E"), Rationals(0x0004, big, (139, 1), (45, 1), (36, 1)),
            new(0x0005, ByteType, 1, [0]), Rationals(0x0006, big, (40, 1)),
        ];
        return Tiff(big, image, exif, gps, thumbnailBytes: 1500);
    }

    /// <summary>A TIFF block with IFD0, optional EXIF and GPS sub-directories and an IFD1 thumbnail of the given size.</summary>
    public static byte[] Tiff(bool big, Field[] image, Field[]? exif = null, Field[]? gps = null, int thumbnailBytes = 0)
    {
        var ifd0 = image.ToList();
        if (exif is not null)
        {
            ifd0.Add(new(0x8769, Long, 1, new byte[4]));
        }

        if (gps is not null)
        {
            ifd0.Add(new(0x8825, Long, 1, new byte[4]));
        }

        List<Field>[] dirs = [Sorted(ifd0), Sorted(exif ?? []), Sorted(gps ?? []), thumbnailBytes > 0 ? [new(0x0201, Long, 1, new byte[4]), new(0x0202, Long, 1, Le32((uint)thumbnailBytes, big))] : []];
        int[] offsets = new int[4];
        int position = 8;
        for (int d = 0; d < 4; d++)
        {
            if (dirs[d].Count == 0)
            {
                continue;
            }

            offsets[d] = position;
            position += 2 + (12 * dirs[d].Count) + 4 + dirs[d].Where(f => f.Value.Length > 4).Sum(f => (f.Value.Length + 1) & ~1);
        }

        int thumbnailAt = position;
        byte[] o = new byte[position + thumbnailBytes];
        o[0] = o[1] = (byte)(big ? 'M' : 'I');
        W16(o, 2, 42, big);
        W32(o, 4, 8, big);
        for (int d = 0; d < 4; d++)
        {
            if (dirs[d].Count == 0)
            {
                continue;
            }

            int table = offsets[d];
            int values = table + 2 + (12 * dirs[d].Count) + 4;
            W16(o, table, (ushort)dirs[d].Count, big);
            for (int i = 0; i < dirs[d].Count; i++)
            {
                Field f = dirs[d][i];
                int e = table + 2 + (12 * i);
                W16(o, e, f.Tag, big);
                W16(o, e + 2, f.Type, big);
                W32(o, e + 4, f.Count, big);
                uint pointer = f.Tag switch
                {
                    0x8769 when d == 0 => (uint)offsets[1],
                    0x8825 when d == 0 => (uint)offsets[2],
                    0x0201 when d == 3 => (uint)thumbnailAt,
                    _ => 0,
                };
                if (pointer != 0)
                {
                    W32(o, e + 8, pointer, big);
                }
                else if (f.Value.Length <= 4)
                {
                    f.Value.CopyTo(o, e + 8);
                }
                else
                {
                    W32(o, e + 8, (uint)values, big);
                    f.Value.CopyTo(o, values);
                    values += (f.Value.Length + 1) & ~1;
                }
            }

            int next = d == 0 && dirs[3].Count > 0 ? offsets[3] : 0;
            W32(o, table + 2 + (12 * dirs[d].Count), (uint)next, big);
        }

        return o;
    }

    public const string Xmp = "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF><rdf:Description xmp:CreatorTool=\"Adobe Photoshop 27.0\" " +
        "exif:GPSLatitude=\"35,40.8N\" photoshop:City=\"Tokyo\"><dc:creator><rdf:Seq><rdf:li>Taro</rdf:li></rdf:Seq></dc:creator>" +
        "<xmpMM:History>…</xmpMM:History></rdf:Description></rdf:RDF></x:xmpmeta>";

    /// <summary>A JPEG with every kind of metadata; the "image" is fake compressed data with stuffing and restart markers.</summary>
    public static byte[] Jpeg(byte[]? exif, bool xmp = true, bool iptc = true, bool icc = true, bool mpf = true, bool comment = true, bool adobe = true, byte[]? trailer = null, bool jfif = true)
    {
        var o = new MemoryStream();
        o.Write([0xFF, 0xD8]);
        if (jfif)
        {
            Segment(o, 0xE0, [.. "JFIF\0"u8, 1, 2, 0, 0, 72, 0, 72, 0, 0]);
        }

        if (exif is not null)
        {
            Segment(o, 0xE1, [.. "Exif\0\0"u8, .. exif]);
        }

        if (xmp)
        {
            Segment(o, 0xE1, [.. "http://ns.adobe.com/xap/1.0/\0"u8, .. Encoding.UTF8.GetBytes(Xmp)]);
        }

        if (icc)
        {
            Segment(o, 0xE2, [.. "ICC_PROFILE\0"u8, 1, 1, .. new byte[128]]);
        }

        if (mpf)
        {
            Segment(o, 0xE2, [.. "MPF\0"u8, .. new byte[40]]);
        }

        if (iptc)
        {
            // Photoshop resource 0x0404 with IPTC: by-line (2:80), city (2:90), caption (2:120).
            byte[] iim = [0x1C, 2, 80, 0, 4, .. "Taro"u8, 0x1C, 2, 90, 0, 5, .. "Tokyo"u8, 0x1C, 2, 120, 0, 3, .. "Hi!"u8];
            byte[] resource = [.. "8BIM"u8, 0x04, 0x04, 0, 0, 0, 0, 0, (byte)iim.Length, .. iim];
            if ((iim.Length & 1) == 1)
            {
                resource = [.. resource, 0];
            }

            Segment(o, 0xED, [.. "Photoshop 3.0\0"u8, .. resource]);
        }

        if (adobe)
        {
            Segment(o, 0xEE, [.. "Adobe"u8, 0, 100, 0, 0, 0, 0, 1]);
        }

        if (comment)
        {
            Segment(o, 0xFE, "secret note"u8.ToArray());
        }

        Segment(o, 0xDB, new byte[65]); // DQT
        Segment(o, 0xC0, [8, 0, 16, 0, 16, 1, 1, 0x11, 0]); // SOF0 16×16, one component
        Segment(o, 0xC4, new byte[20]); // DHT
        Segment(o, 0xDA, [1, 1, 0, 0, 63, 0]); // SOS
        o.Write([0x12, 0xFF, 0x00, 0x34, 0xFF, 0xD0, 0x56, 0x78]); // data, stuffed 0xFF, RST0
        Segment(o, 0xC4, new byte[10]); // a table between scans (progressive)
        Segment(o, 0xDA, [1, 1, 0, 0, 63, 0]);
        o.Write([0x9A, 0xBC]);
        o.Write([0xFF, 0xD9]);
        if (trailer is not null)
        {
            o.Write(trailer);
        }

        return o.ToArray();
    }

    public static void Segment(Stream o, byte marker, byte[] payload)
    {
        o.Write([0xFF, marker, (byte)((payload.Length + 2) >> 8), (byte)(payload.Length + 2)]);
        o.Write(payload);
    }

    /// <summary>A PNG with text, XMP, EXIF, ICC, time and an unknown chunk (valid CRCs; the image data is fake).</summary>
    public static byte[] Png(byte[]? exif, bool trailer = false)
    {
        var o = new MemoryStream();
        o.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        Chunk(o, "IHDR", [0, 0, 0, 16, 0, 0, 0, 16, 8, 6, 0, 0, 0]);
        Chunk(o, "iCCP", [.. "icc\0"u8, 0, .. new byte[30]]);
        Chunk(o, "tEXt", [.. "Author\0"u8, .. "Taro"u8]);
        Chunk(o, "iTXt", [.. "XML:com.adobe.xmp\0"u8, 0, 0, 0, 0, .. Encoding.UTF8.GetBytes(Xmp)]);
        Chunk(o, "tIME", [7, 234, 10, 1, 12, 0, 0]);
        if (exif is not null)
        {
            Chunk(o, "eXIf", exif);
        }

        Chunk(o, "zzZz", new byte[8]); // unknown ancillary
        Chunk(o, "IDAT", new byte[20]);
        Chunk(o, "IEND", []);
        if (trailer)
        {
            o.Write("hidden"u8);
        }

        return o.ToArray();
    }

    public static void Chunk(Stream o, string type, byte[] data)
    {
        byte[] header = new byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)data.Length);
        Encoding.ASCII.GetBytes(type, header.AsSpan(4));
        o.Write(header);
        o.Write(data);
        var crc = new Crc32();
        crc.Append(header.AsSpan(4));
        crc.Append(data);
        byte[] c = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(c, crc.Value);
        o.Write(c);
    }

    /// <summary>An extended WebP: VP8X (ICC, EXIF, XMP flags), ICCP, VP8, EXIF, XMP and an unknown chunk.</summary>
    public static byte[] WebP(byte[]? exif)
    {
        var body = new MemoryStream();
        RiffChunk(body, "VP8X", [0x20 | 0x08 | 0x04, 0, 0, 0, 15, 0, 0, 15, 0, 0]);
        RiffChunk(body, "ICCP", new byte[21]);
        RiffChunk(body, "VP8 ", new byte[31]);
        if (exif is not null)
        {
            RiffChunk(body, "EXIF", exif);
        }

        RiffChunk(body, "XMP ", Encoding.UTF8.GetBytes(Xmp));
        RiffChunk(body, "ABCD", new byte[4]);
        var o = new MemoryStream();
        o.Write("RIFF"u8);
        byte[] size = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(size, (uint)(4 + body.Length));
        o.Write(size);
        o.Write("WEBP"u8);
        body.Position = 0;
        body.CopyTo(o);
        return o.ToArray();
    }

    public static void RiffChunk(Stream o, string type, byte[] data)
    {
        o.Write(Encoding.ASCII.GetBytes(type));
        byte[] size = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(size, (uint)data.Length);
        o.Write(size);
        o.Write(data);
        if ((data.Length & 1) == 1)
        {
            o.WriteByte(0);
        }
    }

    /// <summary>An animated GIF: global palette, NETSCAPE loop, a comment, an XMP extension, two frames, trailer junk.</summary>
    public static byte[] Gif()
    {
        var o = new MemoryStream();
        o.Write("GIF89a"u8);
        o.Write([2, 0, 2, 0, 0x80, 0, 0]); // 2×2, global color table of 2 entries
        o.Write([0, 0, 0, 255, 255, 255]);
        o.Write([0x21, 0xFF, 11, .. "NETSCAPE2.0"u8, 3, 1, 0, 0, 0]);
        o.Write([0x21, 0xFE, 6, .. "secret"u8, 0]);
        o.Write([0x21, 0xFF, 11, .. "XMP DataXMP"u8, 4, .. "<x/>"u8, 0]);
        for (int frame = 0; frame < 2; frame++)
        {
            o.Write([0x21, 0xF9, 4, 0, 10, 0, 0, 0]);
            o.Write([0x2C, 0, 0, 0, 0, 2, 0, 2, 0, 0]);
            o.Write([2, 2, 0x4C, 0x01, 0]);
        }

        o.Write([0x3B]);
        o.Write("junk"u8);
        return o.ToArray();
    }

    private static List<Field> Sorted(IEnumerable<Field> fields) => [.. fields.OrderBy(f => f.Tag)];

    private static byte[] Le32(uint value, bool big)
    {
        byte[] b = new byte[4];
        if (big)
        {
            BinaryPrimitives.WriteUInt32BigEndian(b, value);
        }
        else
        {
            BinaryPrimitives.WriteUInt32LittleEndian(b, value);
        }

        return b;
    }

    private static void W16(byte[] o, int at, ushort v, bool big)
    {
        if (big)
        {
            BinaryPrimitives.WriteUInt16BigEndian(o.AsSpan(at), v);
        }
        else
        {
            BinaryPrimitives.WriteUInt16LittleEndian(o.AsSpan(at), v);
        }
    }

    private static void W32(byte[] o, int at, uint v, bool big)
    {
        if (big)
        {
            BinaryPrimitives.WriteUInt32BigEndian(o.AsSpan(at), v);
        }
        else
        {
            BinaryPrimitives.WriteUInt32LittleEndian(o.AsSpan(at), v);
        }
    }
}
