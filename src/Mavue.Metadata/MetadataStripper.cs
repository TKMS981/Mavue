using System.Buffers.Binary;
using System.Text;
using Mavue.Core.Archives;
using Mavue.Metadata.Tiff;

namespace Mavue.Metadata;

/// <summary>What a copy without private metadata contains.</summary>
/// <param name="RemovedBytes">Bytes of metadata (and data after the image) left out.</param>
/// <param name="WroteExif">A minimal EXIF block with the kept values (orientation…) was written.</param>
public sealed record StripResult(MetadataContainer Container, long RemovedBytes, bool WroteExif);

/// <summary>
/// Copies an image without its private metadata and without re-encoding (ARCHITECTURE §4.7.13): the compressed image
/// data is copied unchanged, only metadata blocks are left out, and a minimal EXIF block is written for what
/// <see cref="StripOptions"/> keeps (always the orientation, so the picture stays upright). JPEG, PNG, WebP and GIF.
/// Data after the end of the image (motion-photo videos, vendor trailers) is not copied. The source is only read.
/// </summary>
public static class MetadataStripper
{
    public static bool CanStrip(MetadataContainer container) =>
        container is MetadataContainer.Jpeg or MetadataContainer.Png or MetadataContainer.WebP or MetadataContainer.Gif;

    /// <param name="exifSource">The EXIF to take kept values from instead of the input's own (a converted image: the
    /// values come from the original HEIF or raw file; its orientation is already applied to the pixels).</param>
    /// <exception cref="NotSupportedException">The format is not JPEG, PNG, WebP or GIF.</exception>
    /// <exception cref="InvalidDataException">The file is damaged.</exception>
    public static StripResult Strip(Stream input, Stream output, StripOptions options, Tiff.TiffStructure? exifSource = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(options);
        ImageMetadata metadata = ImageMetadataReader.Read(input);
        byte[]? exif = (exifSource ?? metadata.ExifStructure) is { } tiff ? ExifWriter.Write(tiff, options) : null;
        long before = output.Position;
        StripResult result = metadata.Container switch
        {
            MetadataContainer.Jpeg => Jpeg(input, output, options, exif),
            MetadataContainer.Png => Png(input, output, options, exif),
            MetadataContainer.WebP => WebP(input, output, options, exif),
            MetadataContainer.Gif => Gif(input, output),
            _ => throw new NotSupportedException("Metadata can be removed from JPEG, PNG, WebP and GIF files only."),
        };
        return result with { RemovedBytes = Math.Max(0, input.Length - (output.Position - before)) };
    }

    // ------------------------------------------------------------------ JPEG

    private static StripResult Jpeg(Stream input, Stream output, StripOptions options, byte[]? exif)
    {
        output.Write([0xFF, 0xD8]);
        long position = 2;
        long length = input.Length;
        bool exifWritten = false;
        bool first = true;
        Span<byte> marker = stackalloc byte[4];
        while (position + 4 <= length)
        {
            input.Seek(position, SeekOrigin.Begin);
            input.ReadExactly(marker);
            if (marker[0] != 0xFF)
            {
                throw new InvalidDataException("A JPEG marker was expected.");
            }

            byte code = marker[1];
            if (code == 0xFF)
            {
                position++;
                continue;
            }

            if (code is 0x01 or (>= 0xD0 and <= 0xD7))
            {
                output.Write(marker[..2]);
                position += 2;
                continue;
            }

            if (code == 0xD9)
            {
                output.Write([0xFF, 0xD9]);
                return new StripResult(MetadataContainer.Jpeg, 0, exifWritten);
            }

            int segment = BinaryPrimitives.ReadUInt16BigEndian(marker[2..]);
            if (segment < 2 || position + 2 + segment > length)
            {
                throw new InvalidDataException("A JPEG segment runs past the end of the file.");
            }

            // The new EXIF goes right after SOI, or after a leading JFIF APP0 (both standards want to be first).
            bool jfif = code == 0xE0 && first && StartsWith(input, position + 4, "JFIF\0"u8);
            if (!exifWritten && exif is not null && !jfif)
            {
                WriteJpegExif(output, exif);
                exifWritten = true;
            }

            first = false;
            if (code == 0xDA)
            {
                long end = ImageMetadataReader.FindJpegEnd(input, position + 2 + segment);
                long stop = end >= 0 ? end + 2 : length;
                Copy(input, output, position, stop - position);
                if (end < 0)
                {
                    output.Write([0xFF, 0xD9]); // truncated file: close the image
                }

                return new StripResult(MetadataContainer.Jpeg, 0, exifWritten);
            }

            if (KeepJpegSegment(input, code, position + 4, options))
            {
                Copy(input, output, position, 2 + segment);
            }

            if (jfif && !exifWritten && exif is not null)
            {
                WriteJpegExif(output, exif);
                exifWritten = true;
            }

            position += 2 + segment;
        }

        throw new InvalidDataException("The JPEG image data was not found.");
    }

    private static bool KeepJpegSegment(Stream input, byte code, long data, StripOptions options) => code switch
    {
        0xE0 => !StartsWith(input, data, "JFXX\0"u8), // JFIF (density) and AVI1 stay; JFXX thumbnails go
        0xE2 => options.KeepColorProfile && StartsWith(input, data, "ICC_PROFILE\0"u8),
        0xEE => StartsWith(input, data, "Adobe"u8), // color transform: needed to decode
        >= 0xE1 and <= 0xEF => false, // EXIF, XMP, IPTC, MPF, vendor and unknown application data
        0xFE => false, // comments
        _ => true, // tables, frame headers, restart interval…
    };

    private static void WriteJpegExif(Stream output, byte[] tiff)
    {
        int length = 2 + 6 + tiff.Length;
        if (length > 0xFFFF)
        {
            return; // cannot happen for the few kept tags; never write a broken segment
        }

        output.Write([0xFF, 0xE1, (byte)(length >> 8), (byte)length]);
        output.Write("Exif\0\0"u8);
        output.Write(tiff);
    }

    // ------------------------------------------------------------------ PNG

    private static StripResult Png(Stream input, Stream output, StripOptions options, byte[]? exif)
    {
        output.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        long position = 8;
        long length = input.Length;
        bool exifWritten = false;
        Span<byte> header = stackalloc byte[8];
        while (position + 12 <= length)
        {
            input.Seek(position, SeekOrigin.Begin);
            input.ReadExactly(header);
            long size = BinaryPrimitives.ReadUInt32BigEndian(header);
            string type = Encoding.ASCII.GetString(header[4..8]);
            long total = 12 + size;
            if (size > int.MaxValue || position + total > length)
            {
                throw new InvalidDataException("A PNG chunk runs past the end of the file.");
            }

            if (!exifWritten && exif is not null && type is "IDAT" or "IEND")
            {
                WritePngChunk(output, "eXIf", exif);
                exifWritten = true;
            }

            bool critical = char.IsAsciiLetterUpper(type[0]);
            bool keep = ImageMetadataReader.PngImageChunks.Contains(type) || (type == "iCCP" && options.KeepColorProfile) || (critical && type is not ("eXIf" or "tEXt" or "zTXt" or "iTXt" or "tIME"));
            if (keep)
            {
                Copy(input, output, position, total);
            }

            if (type == "IEND")
            {
                return new StripResult(MetadataContainer.Png, 0, exifWritten);
            }

            position += total;
        }

        throw new InvalidDataException("The PNG file has no IEND chunk.");
    }

    private static void WritePngChunk(Stream output, string type, byte[] data)
    {
        Span<byte> header = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)data.Length);
        Encoding.ASCII.GetBytes(type, header[4..]);
        output.Write(header);
        output.Write(data);
        var crc = new Crc32();
        crc.Append(header[4..]);
        crc.Append(data);
        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc.Value);
        output.Write(crcBytes);
    }

    // ------------------------------------------------------------------ WebP

    private static StripResult WebP(Stream input, Stream output, StripOptions options, byte[]? exif)
    {
        Span<byte> header = stackalloc byte[8];
        input.Seek(4, SeekOrigin.Begin);
        input.ReadExactly(header[..4]);
        long riffEnd = Math.Min(input.Length, 8L + BinaryPrimitives.ReadUInt32LittleEndian(header));
        var chunks = new List<(string Type, long Offset, long Size)>();
        long position = 12;
        while (position + 8 <= riffEnd)
        {
            input.Seek(position, SeekOrigin.Begin);
            input.ReadExactly(header);
            string type = Encoding.ASCII.GetString(header[..4]);
            long size = BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
            if (position + 8 + size > riffEnd)
            {
                throw new InvalidDataException("A WebP chunk runs past the end of the file.");
            }

            chunks.Add((type, position, size));
            position += 8 + size + (size & 1);
        }

        bool extended = chunks.Any(c => c.Type == "VP8X");
        bool keepIcc = options.KeepColorProfile && extended && chunks.Any(c => c.Type == "ICCP");
        bool writeExif = extended && exif is not null; // the simple format cannot carry metadata at all
        var body = new MemoryStream();
        foreach ((string type, long offset, long size) in chunks)
        {
            if (type == "VP8X")
            {
                byte[] vp8x = new byte[8 + size];
                input.Seek(offset, SeekOrigin.Begin);
                input.ReadExactly(vp8x);
                byte flags = vp8x[8];
                flags = (byte)(flags & ~(0x20 | 0x08 | 0x04)); // ICC, EXIF, XMP
                flags |= (byte)((keepIcc ? 0x20 : 0) | (writeExif ? 0x08 : 0));
                vp8x[8] = flags;
                body.Write(vp8x);
                Pad(body, size);
            }
            else if (ImageMetadataReader.WebPImageChunks.Contains(type) || (type == "ICCP" && keepIcc))
            {
                Copy(input, body, offset, 8 + size + (size & 1));
            }
        }

        if (writeExif)
        {
            Span<byte> chunk = stackalloc byte[8];
            "EXIF"u8.CopyTo(chunk);
            BinaryPrimitives.WriteUInt32LittleEndian(chunk[4..], (uint)exif!.Length);
            body.Write(chunk);
            body.Write(exif);
            Pad(body, exif.Length);
        }

        Span<byte> riff = stackalloc byte[12];
        "RIFF"u8.CopyTo(riff);
        BinaryPrimitives.WriteUInt32LittleEndian(riff[4..], (uint)(4 + body.Length));
        "WEBP"u8.CopyTo(riff[8..]);
        output.Write(riff);
        body.Position = 0;
        body.CopyTo(output);
        return new StripResult(MetadataContainer.WebP, 0, writeExif);
    }

    private static void Pad(Stream stream, long size)
    {
        if ((size & 1) != 0)
        {
            stream.WriteByte(0);
        }
    }

    // ------------------------------------------------------------------ GIF

    private static StripResult Gif(Stream input, Stream output)
    {
        var reader = new BinaryReader(input, Encoding.Latin1, leaveOpen: true);
        input.Seek(10, SeekOrigin.Begin);
        byte flags = reader.ReadByte();
        long headerLength = 13 + ((flags & 0x80) != 0 ? 3 * (1 << ((flags & 7) + 1)) : 0);
        Copy(input, output, 0, headerLength);
        input.Seek(headerLength, SeekOrigin.Begin);
        while (input.Position < input.Length)
        {
            long start = input.Position;
            byte introducer = reader.ReadByte();
            if (introducer == 0x3B)
            {
                output.WriteByte(0x3B);
                return new StripResult(MetadataContainer.Gif, 0, false);
            }

            if (introducer == 0x2C)
            {
                input.Seek(8, SeekOrigin.Current);
                byte local = reader.ReadByte();
                if ((local & 0x80) != 0)
                {
                    input.Seek(3 * (1 << ((local & 7) + 1)), SeekOrigin.Current);
                }

                input.Seek(1, SeekOrigin.Current);
                ImageMetadataReader.SkipSubBlocks(reader, null);
                long end = input.Position;
                Copy(input, output, start, end - start);
                input.Seek(end, SeekOrigin.Begin);
                continue;
            }

            if (introducer != 0x21)
            {
                throw new InvalidDataException("A GIF block was expected.");
            }

            byte label = reader.ReadByte();
            bool keep = label is 0xF9 or 0x01;
            if (label == 0xFF)
            {
                long blockStart = input.Position;
                int idLength = reader.ReadByte();
                string id = Encoding.ASCII.GetString(reader.ReadBytes(Math.Min(idLength, 11)));
                input.Seek(blockStart, SeekOrigin.Begin);
                keep = id is "NETSCAPE2.0" or "ANIMEXTS1.0"; // the loop count of an animation
            }

            ImageMetadataReader.SkipSubBlocks(reader, null);
            long blockEnd = input.Position;
            if (keep)
            {
                Copy(input, output, start, blockEnd - start);
                input.Seek(blockEnd, SeekOrigin.Begin);
            }
        }

        output.WriteByte(0x3B); // truncated file: close it
        return new StripResult(MetadataContainer.Gif, 0, false);
    }

    // ------------------------------------------------------------------ shared

    private static bool StartsWith(Stream input, long offset, ReadOnlySpan<byte> prefix)
    {
        if (offset + prefix.Length > input.Length)
        {
            return false;
        }

        Span<byte> bytes = stackalloc byte[32];
        input.Seek(offset, SeekOrigin.Begin);
        input.ReadExactly(bytes[..prefix.Length]);
        return bytes[..prefix.Length].SequenceEqual(prefix);
    }

    private static void Copy(Stream input, Stream output, long offset, long count)
    {
        input.Seek(offset, SeekOrigin.Begin);
        byte[] buffer = new byte[(int)Math.Min(count, 1 << 20)];
        while (count > 0)
        {
            int read = input.Read(buffer, 0, (int)Math.Min(buffer.Length, count));
            if (read <= 0)
            {
                throw new InvalidDataException("The file ended early.");
            }

            output.Write(buffer, 0, read);
            count -= read;
        }
    }
}
