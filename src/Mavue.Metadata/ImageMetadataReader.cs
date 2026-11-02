using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Mavue.Metadata.Tiff;

namespace Mavue.Metadata;

/// <summary>
/// Reads the metadata of JPEG, PNG, WebP, GIF and TIFF-based files (docs/ARCHITECTURE.md §4.7.13) from a seekable
/// stream: where each metadata block is, the EXIF values, and what XMP and IPTC contain. Only headers and metadata
/// blocks are read; image data is skipped by seeking (except the optional end-of-image scan of a JPEG). Every size
/// read from the file is checked against what is available, and the amount kept in memory is bounded. Pure: no
/// Windows APIs, no XML parser (XMP is searched as text), no network.
/// </summary>
public static class ImageMetadataReader
{
    /// <summary>XMP and text payloads are read up to this size.</summary>
    private const int MaxTextBytes = 1024 * 1024;

    private const int MaxBlocks = 4096;

    /// <summary>Reads <paramref name="path"/> (shared read access).</summary>
    /// <param name="findTrailer">Also find data after the end of a JPEG image (reads through the compressed data).</param>
    public static ImageMetadata Read(string path, bool findTrailer = false)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024);
        return Read(stream, findTrailer);
    }

    public static ImageMetadata Read(Stream stream, bool findTrailer = false)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanSeek)
        {
            throw new ArgumentException("The stream must be seekable.", nameof(stream));
        }

        Span<byte> head = stackalloc byte[16];
        stream.Seek(0, SeekOrigin.Begin);
        int got = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
        head = head[..got];
        var builder = new Builder();
        try
        {
            if (head.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8]))
            {
                ReadJpeg(stream, builder, findTrailer);
                return builder.Build(MetadataContainer.Jpeg);
            }

            if (head.StartsWith((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]))
            {
                ReadPng(stream, builder);
                return builder.Build(MetadataContainer.Png);
            }

            if (head.Length >= 12 && head.StartsWith("RIFF"u8) && head[8..12].SequenceEqual("WEBP"u8))
            {
                ReadWebP(stream, builder);
                return builder.Build(MetadataContainer.WebP);
            }

            if (HeifReader.IsHeif(head))
            {
                ReadHeif(stream, builder);
                return builder.Build(MetadataContainer.Heif);
            }

            if (head.StartsWith("GIF87a"u8) || head.StartsWith("GIF89a"u8))
            {
                ReadGif(stream, builder);
                return builder.Build(MetadataContainer.Gif);
            }

            if (head.StartsWith("II*\0"u8) || head.StartsWith("MM\0*"u8))
            {
                ReadTiff(stream, builder);
                return builder.Build(MetadataContainer.Tiff);
            }
        }
        catch (EndOfStreamException)
        {
            // A truncated file: report what was found before the end.
            return builder.Build(builder.Container);
        }

        return ImageMetadata.Unsupported;
    }

    /// <summary>
    /// The offset of the JPEG end-of-image marker (its 0xFF byte) found by walking the scans after the first SOS
    /// segment, or -1. <paramref name="afterStartOfScan"/> is the offset just after the SOS segment's header.
    /// </summary>
    public static long FindJpegEnd(Stream stream, long afterStartOfScan)
    {
        ArgumentNullException.ThrowIfNull(stream);
        byte[] buffer = new byte[1 << 20];
        long position = afterStartOfScan;
        bool marker = false;
        Span<byte> length = stackalloc byte[2];
        stream.Seek(position, SeekOrigin.Begin);
        while (true)
        {
            int read = stream.Read(buffer, 0, buffer.Length);
            if (read <= 0)
            {
                return -1;
            }

            for (int i = 0; i < read; i++)
            {
                byte b = buffer[i];
                if (!marker)
                {
                    marker = b == 0xFF;
                    continue;
                }

                if (b == 0xFF)
                {
                    continue; // fill byte
                }

                marker = false;
                if (b == 0x00 || (b >= 0xD0 && b <= 0xD7))
                {
                    continue; // stuffed byte or restart marker: still entropy-coded data
                }

                long markerAt = position + i - 1;
                if (b == 0xD9)
                {
                    return markerAt;
                }

                // A segment between scans (DHT, DQT, SOS of a progressive image, DRI…): skip it by its length.
                stream.Seek(markerAt + 2, SeekOrigin.Begin);
                if (stream.ReadAtLeast(length, 2, throwOnEndOfStream: false) < 2)
                {
                    return -1;
                }

                position = markerAt + 2 + BinaryPrimitives.ReadUInt16BigEndian(length);
                stream.Seek(position, SeekOrigin.Begin);
                goto next;
            }

            position += read;
        next:;
        }
    }

    // ------------------------------------------------------------------ JPEG

    private static void ReadJpeg(Stream stream, Builder builder, bool findTrailer)
    {
        builder.Container = MetadataContainer.Jpeg;
        long position = 2;
        long length = stream.Length;
        Span<byte> marker = stackalloc byte[4];
        while (position + 4 <= length && builder.Count < MaxBlocks)
        {
            stream.Seek(position, SeekOrigin.Begin);
            stream.ReadExactly(marker);
            if (marker[0] != 0xFF)
            {
                return; // not a marker: damaged
            }

            byte code = marker[1];
            if (code == 0xFF)
            {
                position++; // fill byte
                continue;
            }

            if (code is 0x01 or (>= 0xD0 and <= 0xD7))
            {
                position += 2;
                continue;
            }

            if (code == 0xD9)
            {
                return;
            }

            int segment = BinaryPrimitives.ReadUInt16BigEndian(marker[2..]);
            if (segment < 2 || position + 2 + segment > length)
            {
                return;
            }

            long data = position + 4;
            int dataLength = segment - 2;
            if (code == 0xDA)
            {
                if (findTrailer)
                {
                    long end = FindJpegEnd(stream, position + 2 + segment);
                    if (end >= 0 && end + 2 < length)
                    {
                        builder.Add(new MetadataBlock(MetadataBlockKind.Trailer, end + 2, length - (end + 2)));
                    }
                }

                return;
            }

            if (code is >= 0xE0 and <= 0xEF or 0xFE)
            {
                ReadJpegSegment(stream, builder, code, data, dataLength);
            }

            position += 2 + segment;
        }
    }

    private static void ReadJpegSegment(Stream stream, Builder builder, byte code, long data, int length)
    {
        long segmentStart = data - 4;
        long segmentLength = length + 4;
        byte[] id = Read(stream, data, Math.Min(length, 40));
        ReadOnlySpan<byte> s = id;
        switch (code)
        {
            case 0xE0 when s.StartsWith("JFIF\0"u8):
                return; // the JFIF header itself (density) is not metadata; a JFIF thumbnail is rare and tiny
            case 0xE0 when s.StartsWith("JFXX\0"u8):
                builder.Add(new MetadataBlock(MetadataBlockKind.EmbeddedImage, segmentStart, segmentLength, "JFXX"));
                return;
            case 0xE0:
                return; // other APP0 headers (AVI1 of motion JPEG frames…) describe the image data
            case 0xE1 when s.StartsWith("Exif\0"u8):
                builder.Add(new MetadataBlock(MetadataBlockKind.Exif, segmentStart, segmentLength));
                builder.SetExif(TiffReader.Read(stream, data + 6, length - 6));
                return;
            case 0xE1 when s.StartsWith("http://ns.adobe.com/xap/1.0/\0"u8):
            {
                int prefix = "http://ns.adobe.com/xap/1.0/\0"u8.Length;
                builder.Add(new MetadataBlock(MetadataBlockKind.Xmp, segmentStart, segmentLength));
                builder.SetXmp(Encoding.UTF8.GetString(Read(stream, data + prefix, Math.Min(length - prefix, MaxTextBytes))), length - prefix);
                return;
            }

            case 0xE1 when s.StartsWith("http://ns.adobe.com/xmp/extension/\0"u8):
                builder.Add(new MetadataBlock(MetadataBlockKind.Xmp, segmentStart, segmentLength, "extended"));
                return;
            case 0xED when s.StartsWith("Photoshop 3.0\0"u8):
                builder.Add(new MetadataBlock(MetadataBlockKind.Iptc, segmentStart, segmentLength));
                builder.Iptc = ReadPhotoshop(Read(stream, data + 14, Math.Min(length - 14, MaxTextBytes)), length);
                return;
            case 0xE2 when s.StartsWith("ICC_PROFILE\0"u8):
                builder.Add(new MetadataBlock(MetadataBlockKind.IccProfile, segmentStart, segmentLength));
                return;
            case 0xE2 when s.StartsWith("MPF\0"u8):
                builder.Add(new MetadataBlock(MetadataBlockKind.EmbeddedImage, segmentStart, segmentLength, "MPF"));
                return;
            case 0xEE when s.StartsWith("Adobe"u8):
                return; // color transform flag: needed to decode the image
            case 0xFE:
                builder.Add(new MetadataBlock(MetadataBlockKind.Comment, segmentStart, segmentLength));
                builder.AddComment(Encoding.Latin1.GetString(Read(stream, data, Math.Min(length, 4096))));
                return;
            default:
                builder.Add(new MetadataBlock(MetadataBlockKind.Other, segmentStart, segmentLength, "APP" + (code - 0xE0).ToString(CultureInfo.InvariantCulture)));
                return;
        }
    }

    /// <summary>Photoshop image resources: the IPTC-NAA record (resource 0x0404) is read.</summary>
    private static IptcSummary ReadPhotoshop(byte[] data, long bytes)
    {
        ReadOnlySpan<byte> s = data;
        int at = 0;
        while (at + 12 <= s.Length && s[at..].StartsWith("8BIM"u8))
        {
            int id = BinaryPrimitives.ReadUInt16BigEndian(s[(at + 4)..]);
            int nameLength = s[at + 6];
            int nameField = (1 + nameLength + 1) & ~1;
            int sizeAt = at + 6 + nameField;
            if (sizeAt + 4 > s.Length)
            {
                break;
            }

            int size = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(s[sizeAt..]), int.MaxValue);
            int body = sizeAt + 4;
            if (size < 0 || body + size > s.Length)
            {
                break;
            }

            if (id == 0x0404)
            {
                return ReadIptc(s.Slice(body, size), bytes);
            }

            at = body + ((size + 1) & ~1);
        }

        return new IptcSummary(bytes, false, false, false, false, false);
    }

    private static IptcSummary ReadIptc(ReadOnlySpan<byte> s, long bytes)
    {
        bool creator = false, location = false, caption = false, keywords = false, copyright = false;
        int at = 0;
        while (at + 5 <= s.Length && s[at] == 0x1C)
        {
            int record = s[at + 1];
            int dataset = s[at + 2];
            int size = BinaryPrimitives.ReadUInt16BigEndian(s[(at + 3)..]);
            if ((size & 0x8000) != 0)
            {
                break; // extended length: not used for these datasets
            }

            if (record == 2)
            {
                creator |= dataset is 80 or 85 or 122;
                location |= dataset is 90 or 92 or 95 or 100 or 101;
                caption |= dataset is 5 or 105 or 120;
                keywords |= dataset == 25;
                copyright |= dataset == 116;
            }

            at += 5 + size;
        }

        return new IptcSummary(bytes, creator, location, caption, keywords, copyright);
    }

    // ------------------------------------------------------------------ PNG

    /// <summary>PNG chunks that describe how to show the image (kept by the stripper; not metadata).</summary>
    internal static readonly HashSet<string> PngImageChunks = new(StringComparer.Ordinal)
    {
        "IHDR", "PLTE", "IDAT", "IEND", "tRNS", "gAMA", "cHRM", "sRGB", "sBIT", "bKGD", "pHYs", "hIST", "sPLT", "acTL", "fcTL", "fdAT", "cICP", "mDCV", "cLLI",
    };

    private static void ReadPng(Stream stream, Builder builder)
    {
        builder.Container = MetadataContainer.Png;
        long position = 8;
        long length = stream.Length;
        Span<byte> header = stackalloc byte[8];
        while (position + 12 <= length && builder.Count < MaxBlocks)
        {
            stream.Seek(position, SeekOrigin.Begin);
            stream.ReadExactly(header);
            long size = BinaryPrimitives.ReadUInt32BigEndian(header);
            string type = Encoding.ASCII.GetString(header[4..8]);
            long data = position + 8;
            long total = 12 + size;
            if (size > int.MaxValue || position + total > length)
            {
                return;
            }

            switch (type)
            {
                case "IEND":
                    if (position + total < length)
                    {
                        builder.Add(new MetadataBlock(MetadataBlockKind.Trailer, position + total, length - (position + total)));
                    }

                    return;
                case "eXIf":
                    builder.Add(new MetadataBlock(MetadataBlockKind.Exif, position, total));
                    builder.SetExif(TiffReader.Read(stream, data, size));
                    break;
                case "iCCP":
                    builder.Add(new MetadataBlock(MetadataBlockKind.IccProfile, position, total));
                    break;
                case "tIME":
                    builder.Add(new MetadataBlock(MetadataBlockKind.Time, position, total));
                    break;
                case "tEXt" or "zTXt" or "iTXt":
                {
                    byte[] head = Read(stream, data, (int)Math.Min(size, 200));
                    int nul = Array.IndexOf(head, (byte)0);
                    string key = Encoding.Latin1.GetString(head, 0, nul < 0 ? head.Length : nul);
                    bool xmp = type == "iTXt" && key == "XML:com.adobe.xmp";
                    builder.Add(new MetadataBlock(xmp ? MetadataBlockKind.Xmp : MetadataBlockKind.Text, position, total, key));
                    if (xmp && nul >= 0 && nul + 2 < head.Length && head[nul + 1] == 0)
                    {
                        // Uncompressed iTXt: keyword, 0, compression flag 0, method, language tag, 0, translated keyword, 0, text.
                        byte[] body = Read(stream, data, (int)Math.Min(size, MaxTextBytes));
                        int text = SkipNulTerminated(body, SkipNulTerminated(body, nul + 3));
                        if (text > 0 && text <= body.Length)
                        {
                            builder.SetXmp(Encoding.UTF8.GetString(body, text, body.Length - text), size);
                        }
                    }
                    else if (!xmp)
                    {
                        builder.AddTextKey(key);
                    }

                    break;
                }

                default:
                    if (!PngImageChunks.Contains(type))
                    {
                        builder.Add(new MetadataBlock(MetadataBlockKind.Other, position, total, type));
                    }

                    break;
            }

            position += total;
        }
    }

    private static int SkipNulTerminated(byte[] data, int from)
    {
        if (from < 0 || from >= data.Length)
        {
            return -1;
        }

        int nul = Array.IndexOf(data, (byte)0, from);
        return nul < 0 ? -1 : nul + 1;
    }

    // ------------------------------------------------------------------ WebP

    internal static readonly HashSet<string> WebPImageChunks = new(StringComparer.Ordinal) { "VP8 ", "VP8L", "VP8X", "ALPH", "ANIM", "ANMF" };

    private static void ReadWebP(Stream stream, Builder builder)
    {
        builder.Container = MetadataContainer.WebP;
        Span<byte> header = stackalloc byte[8];
        stream.Seek(4, SeekOrigin.Begin);
        stream.ReadExactly(header[..4]);
        long riffEnd = Math.Min(stream.Length, 8L + BinaryPrimitives.ReadUInt32LittleEndian(header));
        long position = 12;
        while (position + 8 <= riffEnd && builder.Count < MaxBlocks)
        {
            stream.Seek(position, SeekOrigin.Begin);
            stream.ReadExactly(header);
            string type = Encoding.ASCII.GetString(header[..4]);
            long size = BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
            long total = 8 + size + (size & 1);
            if (position + 8 + size > riffEnd)
            {
                break;
            }

            long data = position + 8;
            switch (type)
            {
                case "EXIF":
                {
                    builder.Add(new MetadataBlock(MetadataBlockKind.Exif, position, total));
                    byte[] prefix = Read(stream, data, (int)Math.Min(size, 6));
                    long skip = prefix.AsSpan().StartsWith("Exif\0\0"u8) ? 6 : 0;
                    builder.SetExif(TiffReader.Read(stream, data + skip, size - skip));
                    break;
                }

                case "XMP ":
                    builder.Add(new MetadataBlock(MetadataBlockKind.Xmp, position, total));
                    builder.SetXmp(Encoding.UTF8.GetString(Read(stream, data, (int)Math.Min(size, MaxTextBytes))), size);
                    break;
                case "ICCP":
                    builder.Add(new MetadataBlock(MetadataBlockKind.IccProfile, position, total));
                    break;
                default:
                    if (!WebPImageChunks.Contains(type))
                    {
                        builder.Add(new MetadataBlock(MetadataBlockKind.Other, position, total, type.Trim()));
                    }

                    break;
            }

            position += total;
        }

        if (riffEnd < stream.Length)
        {
            builder.Add(new MetadataBlock(MetadataBlockKind.Trailer, riffEnd, stream.Length - riffEnd));
        }
    }

    // ------------------------------------------------------------------ GIF

    private static void ReadGif(Stream stream, Builder builder)
    {
        builder.Container = MetadataContainer.Gif;
        var reader = new BinaryReader(stream, Encoding.Latin1, leaveOpen: true);
        stream.Seek(10, SeekOrigin.Begin);
        byte flags = reader.ReadByte();
        stream.Seek(13 + ((flags & 0x80) != 0 ? 3 * (1 << ((flags & 7) + 1)) : 0), SeekOrigin.Begin);
        while (stream.Position < stream.Length && builder.Count < MaxBlocks)
        {
            long start = stream.Position;
            byte introducer = reader.ReadByte();
            if (introducer == 0x3B)
            {
                if (stream.Position < stream.Length)
                {
                    builder.Add(new MetadataBlock(MetadataBlockKind.Trailer, stream.Position, stream.Length - stream.Position));
                }

                return;
            }

            if (introducer == 0x2C)
            {
                stream.Seek(8, SeekOrigin.Current);
                byte local = reader.ReadByte();
                if ((local & 0x80) != 0)
                {
                    stream.Seek(3 * (1 << ((local & 7) + 1)), SeekOrigin.Current);
                }

                stream.Seek(1, SeekOrigin.Current); // LZW minimum code size
                SkipSubBlocks(reader, null);
                continue;
            }

            if (introducer != 0x21)
            {
                return; // damaged
            }

            byte label = reader.ReadByte();
            switch (label)
            {
                case 0xFE:
                {
                    var text = new MemoryStream();
                    SkipSubBlocks(reader, text);
                    builder.Add(new MetadataBlock(MetadataBlockKind.Comment, start, stream.Position - start));
                    builder.AddComment(Encoding.Latin1.GetString(text.GetBuffer(), 0, (int)Math.Min(text.Length, 4096)));
                    break;
                }

                case 0xFF:
                {
                    long blockStart = stream.Position;
                    int idLength = reader.ReadByte();
                    string id = Encoding.ASCII.GetString(reader.ReadBytes(Math.Min(idLength, 11)));
                    stream.Seek(blockStart, SeekOrigin.Begin);
                    SkipSubBlocks(reader, null);
                    if (id is not ("NETSCAPE2.0" or "ANIMEXTS1.0"))
                    {
                        builder.Add(new MetadataBlock(id == "XMP DataXMP" ? MetadataBlockKind.Xmp : MetadataBlockKind.Other, start, stream.Position - start, id));
                    }

                    break;
                }

                default:
                    SkipSubBlocks(reader, null); // graphic control, plain text
                    break;
            }
        }
    }

    internal static void SkipSubBlocks(BinaryReader reader, Stream? copy)
    {
        Stream stream = reader.BaseStream;
        while (true)
        {
            int size = reader.ReadByte();
            if (size == 0)
            {
                return;
            }

            if (stream.Position + size > stream.Length)
            {
                throw new EndOfStreamException();
            }

            if (copy is not null && copy.Length < 65536)
            {
                copy.Write(reader.ReadBytes(size));
            }
            else
            {
                stream.Seek(size, SeekOrigin.Current);
            }
        }
    }

    // ------------------------------------------------------------------ TIFF and camera raw

    private static void ReadTiff(Stream stream, Builder builder)
    {
        builder.Container = MetadataContainer.Tiff;
        TiffStructure? tiff = TiffReader.Read(stream, 0, stream.Length);
        if (tiff is null)
        {
            return;
        }

        builder.Add(new MetadataBlock(MetadataBlockKind.Exif, 0, 0, "TIFF"));
        builder.SetExif(tiff);
        if (tiff.Image[700] is { } xmp)
        {
            builder.Add(new MetadataBlock(MetadataBlockKind.Xmp, 0, xmp.Count));
            builder.SetXmp(Encoding.UTF8.GetString(xmp.Value), xmp.Count);
        }

        if (tiff.Image[33723] is { } iptc)
        {
            builder.Add(new MetadataBlock(MetadataBlockKind.Iptc, 0, iptc.Count));
            builder.Iptc = ReadIptc(iptc.Value, iptc.Count);
        }

        if (tiff.Image[34675] is { } icc)
        {
            builder.Add(new MetadataBlock(MetadataBlockKind.IccProfile, 0, icc.Count));
        }
    }

    // ------------------------------------------------------------------ HEIF and AVIF

    private static void ReadHeif(Stream stream, Builder builder)
    {
        builder.Container = MetadataContainer.Heif;
        (List<HeifItem> items, bool icc) = HeifReader.ReadItems(stream);
        foreach (HeifItem item in items)
        {
            if (item.Type == "Exif" && HeifReader.ReadItem(stream, item, 4 * 1024 * 1024) is { Length: > 8 } exif)
            {
                // The item starts with the offset of the TIFF header (after these 4 bytes), usually 6 ("Exif\0\0").
                long offset = BinaryPrimitives.ReadUInt32BigEndian(exif);
                if (offset < exif.Length - 4)
                {
                    builder.Add(new MetadataBlock(MetadataBlockKind.Exif, item.Extents[0].Offset, item.Length));
                    builder.SetExif(TiffReader.Read(new MemoryStream(exif), 4 + offset, exif.Length - 4 - offset));
                }
            }
            else if (item.Type == "mime" && item.ContentType?.Contains("rdf+xml", StringComparison.OrdinalIgnoreCase) == true &&
                HeifReader.ReadItem(stream, item, MaxTextBytes) is { } xmp)
            {
                builder.Add(new MetadataBlock(MetadataBlockKind.Xmp, item.Extents[0].Offset, item.Length));
                builder.SetXmp(Encoding.UTF8.GetString(xmp), item.Length);
            }
        }

        if (icc)
        {
            builder.Add(new MetadataBlock(MetadataBlockKind.IccProfile, 0, 0));
        }
    }

    // ------------------------------------------------------------------ shared

    private static byte[] Read(Stream stream, long offset, int count)
    {
        count = (int)Math.Max(0, Math.Min(count, stream.Length - offset));
        byte[] bytes = new byte[count];
        stream.Seek(offset, SeekOrigin.Begin);
        stream.ReadExactly(bytes);
        return bytes;
    }

    /// <summary>The EXIF values of a TIFF structure.</summary>
    public static ExifInfo ExifFrom(TiffStructure tiff)
    {
        ArgumentNullException.ThrowIfNull(tiff);
        TiffDirectory image = tiff.Image;
        TiffDirectory exif = tiff.Exif ?? new TiffDirectory(tiff.BigEndian);
        return new ExifInfo
        {
            Make = image.Ascii(0x010F),
            Model = image.Ascii(0x0110),
            Software = image.Ascii(0x0131),
            Artist = image.Ascii(0x013B),
            Copyright = image.Ascii(0x8298),
            Description = image.Ascii(0x010E),
            XpTitle = image.Ucs2(0x9C9B),
            XpComment = image.Ucs2(0x9C9C),
            XpAuthor = image.Ucs2(0x9C9D),
            XpKeywords = image.Ucs2(0x9C9E),
            XpSubject = image.Ucs2(0x9C9F),
            Orientation = image.Number(0x0112),
            DateTimeModified = ExifDate(image.Ascii(0x0132)),
            DateTimeOriginal = ExifDate(exif.Ascii(0x9003)),
            DateTimeDigitized = ExifDate(exif.Ascii(0x9004)),
            OffsetTimeOriginal = exif.Ascii(0x9011),
            ExposureTime = exif.Rational(0x829A) is { } t ? new RationalValue(t.Numerator, t.Denominator) : null,
            FNumber = exif.Real(0x829D),
            ExposureProgram = exif.Number(0x8822),
            Iso = exif.Number(0x8827),
            ExposureBias = exif.Real(0x9204),
            MeteringMode = exif.Number(0x9207),
            Flash = exif.Number(0x9209),
            FocalLength = exif.Real(0x920A),
            UserComment = exif.UserComment(0x9286),
            PixelWidth = exif.Number(0xA002),
            PixelHeight = exif.Number(0xA003),
            WhiteBalance = exif.Number(0xA403),
            FocalLength35mm = exif.Number(0xA405),
            ImageUniqueId = exif.Ascii(0xA420),
            OwnerName = exif.Ascii(0xA430),
            BodySerialNumber = exif.Ascii(0xA431) ?? image.Ascii(0xC62F), // DNG CameraSerialNumber
            LensMake = exif.Ascii(0xA433),
            LensModel = exif.Ascii(0xA434),
            LensSerialNumber = exif.Ascii(0xA435),
            MakerNoteBytes = exif[0x927C]?.Count ?? 0,
            ThumbnailBytes = tiff.ThumbnailBytes,
            HasGpsDirectory = tiff.Gps is { Entries.Count: > 0 },
            Gps = tiff.Gps is { } gps ? GpsFrom(gps) : null,
        };
    }

    private static GpsPosition? GpsFrom(TiffDirectory gps)
    {
        double? latitude = Degrees(gps, 2, gps.Ascii(1));
        double? longitude = Degrees(gps, 4, gps.Ascii(3));
        double? altitude = gps.Real(6) is { } a ? (gps.Number(5) == 1 ? -a : a) : null;
        DateTime? time = null;
        if (gps.Ascii(0x1D) is { } date && DateTime.TryParseExact(date, "yyyy:MM:dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime day) &&
            gps.Real(7, 0) is { } h && gps.Real(7, 1) is { } m && gps.Real(7, 2) is { } sec && h is >= 0 and < 24 && m is >= 0 and < 60 && sec is >= 0 and < 61)
        {
            time = DateTime.SpecifyKind(day.AddHours(h).AddMinutes(m).AddSeconds(sec), DateTimeKind.Utc);
        }

        double? direction = gps.Real(0x11);
        return latitude is null && longitude is null && altitude is null && time is null ? null : new GpsPosition(latitude, longitude, altitude, time, direction);
    }

    private static double? Degrees(TiffDirectory gps, ushort tag, string? reference)
    {
        if (gps.Real(tag, 0) is not { } d || gps.Real(tag, 1) is not { } m)
        {
            return null;
        }

        double s = gps.Real(tag, 2) ?? 0;
        double value = d + (m / 60) + (s / 3600);
        if (!double.IsFinite(value) || value > 180)
        {
            return null;
        }

        return reference is "S" or "W" ? -value : value;
    }

    /// <summary>"YYYY:MM:DD HH:MM:SS" (EXIF); null when blank or invalid ("0000:00:00 00:00:00").</summary>
    public static DateTime? ExifDate(string? text) =>
        text is not null && DateTime.TryParseExact(text.Trim(), ["yyyy:MM:dd HH:mm:ss", "yyyy:MM:dd HH:mm", "yyyy-MM-dd HH:mm:ss", "yyyy:MM:dd"], CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime value)
            ? value
            : null;

    /// <summary>What an XMP packet says, found by searching its text.</summary>
    public static XmpSummary SummarizeXmp(string xmp, long bytes)
    {
        ArgumentNullException.ThrowIfNull(xmp);
        bool Has(params string[] names) => names.Any(n => xmp.Contains(n, StringComparison.Ordinal));
        return new XmpSummary(
            bytes,
            XmpValue(xmp, "xmp:CreatorTool"),
            Has("xmpMM:History", "photoshop:History", "crs:History"),
            Has("exif:GPSLatitude", "exif:GPSLongitude", "photoshop:City", "photoshop:Country", "Iptc4xmpCore:Location", "Iptc4xmpExt:LocationShown", "Iptc4xmpExt:LocationCreated"),
            Has("dc:creator", "xmpRights:Owner", "photoshop:AuthorsPosition", "Iptc4xmpCore:CreatorContactInfo"),
            Has("dc:description", "dc:title", "dc:subject", "photoshop:Headline", "exif:UserComment"),
            Has("exif:DateTimeOriginal", "photoshop:DateCreated", "xmp:CreateDate", "exif:GPSTimeStamp"));
    }

    /// <summary>The value of a simple property written as an attribute (name="…") or an element (&lt;name&gt;…&lt;/name&gt;).</summary>
    private static string? XmpValue(string xmp, string name)
    {
        int at = xmp.IndexOf(name + "=\"", StringComparison.Ordinal);
        if (at >= 0)
        {
            int start = at + name.Length + 2;
            int end = xmp.IndexOf('"', start);
            return end > start ? System.Net.WebUtility.HtmlDecode(xmp[start..Math.Min(end, start + 200)]).Trim() : null;
        }

        at = xmp.IndexOf("<" + name + ">", StringComparison.Ordinal);
        if (at >= 0)
        {
            int start = at + name.Length + 2;
            int end = xmp.IndexOf('<', start);
            return end > start ? System.Net.WebUtility.HtmlDecode(xmp[start..Math.Min(end, start + 200)]).Trim() : null;
        }

        return null;
    }

    private sealed class Builder
    {
        private readonly List<MetadataBlock> _blocks = [];
        private readonly List<string> _textKeys = [];
        private readonly List<string> _comments = [];
        private TiffStructure? _exif;
        private XmpSummary? _xmp;

        public MetadataContainer Container { get; set; }

        public IptcSummary? Iptc { get; set; }

        public int Count => _blocks.Count;

        public void Add(MetadataBlock block) => _blocks.Add(block);

        public void SetExif(TiffStructure? tiff) => _exif ??= tiff;

        public void SetXmp(string text, long bytes) => _xmp ??= SummarizeXmp(text, bytes);

        public void AddTextKey(string key)
        {
            if (_textKeys.Count < 64)
            {
                _textKeys.Add(key);
            }
        }

        public void AddComment(string text)
        {
            text = text.Trim('\0', ' ', '\r', '\n');
            if (text.Length > 0 && _comments.Count < 16)
            {
                _comments.Add(text.Length > 500 ? text[..500] : text);
            }
        }

        public ImageMetadata Build(MetadataContainer container) =>
            new(container, _blocks, _exif is null ? null : ExifFrom(_exif), _exif, _xmp, Iptc, _textKeys, _comments);
    }
}
