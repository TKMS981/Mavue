using System.Buffers.Binary;

namespace Mavue.Metadata.Tiff;

/// <summary>The directories of an EXIF/TIFF structure.</summary>
/// <param name="BigEndian">"MM" byte order.</param>
/// <param name="Image">IFD0 (the main image: camera, software, artist, copyright, orientation).</param>
/// <param name="Exif">The EXIF sub-directory (exposure, dates, lens, serial numbers, maker note), when present.</param>
/// <param name="Gps">The GPS sub-directory, when present.</param>
/// <param name="Thumbnail">IFD1 (the embedded thumbnail), when present.</param>
/// <param name="ThumbnailBytes">Length of the embedded JPEG thumbnail (0 when none).</param>
public sealed record TiffStructure(bool BigEndian, TiffDirectory Image, TiffDirectory? Exif, TiffDirectory? Gps, TiffDirectory? Thumbnail, long ThumbnailBytes);

/// <summary>
/// Reads TIFF directories (the EXIF block of JPEG/PNG/WebP, and TIFF-based files such as camera raw images) from a
/// seekable stream without loading the file. Hostile input is bounded: entry counts, value sizes and the number of
/// directories are limited, loops between directories are detected, offsets outside the data are ignored. Values
/// larger than <see cref="MaxValueBytes"/> (maker notes, embedded images) are recorded with their count but no bytes.
/// </summary>
public static class TiffReader
{
    /// <summary>Tags that point to sub-directories.</summary>
    public const ushort ExifPointer = 0x8769;
    public const ushort GpsPointer = 0x8825;
    public const ushort InteropPointer = 0xA005;

    /// <summary>Values larger than this are not read (their presence and size are kept).</summary>
    public const int MaxValueBytes = 256 * 1024;

    private const int MaxEntries = 1024;

    /// <summary>Reads the structure that starts at <paramref name="start"/> ("II*\0" or "MM\0*"); null when it is not TIFF.</summary>
    /// <param name="length">Bytes available from <paramref name="start"/> (the EXIF block, or the file).</param>
    public static TiffStructure? Read(Stream stream, long start, long length)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (length < 8)
        {
            return null;
        }

        Span<byte> header = stackalloc byte[8];
        if (!ReadAt(stream, start, header))
        {
            return null;
        }

        bool big;
        if (header[0] == 'I' && header[1] == 'I' && header[2] == 42 && header[3] == 0)
        {
            big = false;
        }
        else if (header[0] == 'M' && header[1] == 'M' && header[2] == 0 && header[3] == 42)
        {
            big = true;
        }
        else
        {
            return null;
        }

        var context = new Context(stream, start, length, big);
        uint first = U32(header[4..], big);
        (TiffDirectory? image, uint next) = context.ReadDirectory(first);
        if (image is null)
        {
            return null;
        }

        TiffDirectory? exif = image.Number(ExifPointer) is { } exifOffset ? context.ReadDirectory((uint)exifOffset).Directory : null;
        TiffDirectory? gps = image.Number(GpsPointer) is { } gpsOffset ? context.ReadDirectory((uint)gpsOffset).Directory : null;
        TiffDirectory? thumbnail = next != 0 ? context.ReadDirectory(next).Directory : null;
        long thumbnailBytes = thumbnail?.Number(0x0202) ?? 0; // JPEGInterchangeFormatLength
        return new TiffStructure(big, image, exif, gps, thumbnail, thumbnailBytes);
    }

    private static bool ReadAt(Stream stream, long offset, Span<byte> buffer)
    {
        if (offset < 0 || offset + buffer.Length > stream.Length)
        {
            return false;
        }

        stream.Seek(offset, SeekOrigin.Begin);
        return stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false) == buffer.Length;
    }

    private static ushort U16(ReadOnlySpan<byte> b, bool big) => big ? BinaryPrimitives.ReadUInt16BigEndian(b) : BinaryPrimitives.ReadUInt16LittleEndian(b);

    private static uint U32(ReadOnlySpan<byte> b, bool big) => big ? BinaryPrimitives.ReadUInt32BigEndian(b) : BinaryPrimitives.ReadUInt32LittleEndian(b);

    private sealed class Context(Stream stream, long start, long length, bool big)
    {
        private readonly HashSet<uint> _visited = [];

        public (TiffDirectory? Directory, uint Next) ReadDirectory(uint offset)
        {
            if (offset < 8 || offset + 2 > length || !_visited.Add(offset) || _visited.Count > 16)
            {
                return (null, 0);
            }

            Span<byte> countBytes = stackalloc byte[2];
            if (!ReadAt(stream, start + offset, countBytes))
            {
                return (null, 0);
            }

            int count = U16(countBytes, big);
            if (count == 0 || count > MaxEntries || offset + 2 + (count * 12L) + 4 > length)
            {
                return (null, 0);
            }

            byte[] table = new byte[(count * 12) + 4];
            if (!ReadAt(stream, start + offset + 2, table))
            {
                return (null, 0);
            }

            var directory = new TiffDirectory(big);
            for (int i = 0; i < count; i++)
            {
                ReadOnlySpan<byte> e = table.AsSpan(i * 12, 12);
                ushort tag = U16(e, big);
                var type = (TiffType)U16(e[2..], big);
                uint n = U32(e[4..], big);
                int size = TiffEntry.SizeOf(type);
                if (size == 0)
                {
                    continue; // unknown type: skip (TIFF readers must)
                }

                long bytes = (long)size * n;
                byte[] value;
                if (bytes <= 4)
                {
                    value = e.Slice(8, (int)bytes).ToArray();
                }
                else if (bytes > MaxValueBytes)
                {
                    value = []; // too large to keep (maker notes, embedded previews): presence and count only
                }
                else
                {
                    uint at = U32(e[8..], big);
                    value = new byte[bytes];
                    if (at + bytes > length || !ReadAt(stream, start + at, value))
                    {
                        continue; // points outside the data
                    }
                }

                directory.Entries[tag] = new TiffEntry(tag, type, n, value);
            }

            return (directory, U32(table.AsSpan(count * 12), big));
        }
    }
}
