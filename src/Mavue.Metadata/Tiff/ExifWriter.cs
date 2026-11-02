using System.Buffers.Binary;

namespace Mavue.Metadata.Tiff;

/// <summary>
/// Writes a minimal EXIF (TIFF) block that keeps only chosen tags of an existing one, with their values copied byte
/// for byte in the original byte order (ARCHITECTURE §4.7.13). Everything not on the lists below is dropped: maker
/// notes, serial numbers, owner names, comments, software, thumbnails (no IFD1) and unknown tags.
/// </summary>
public static class ExifWriter
{
    private static readonly ushort[] Orientation = [0x0112];
    private static readonly ushort[] CameraImageTags = [0x010F, 0x0110];
    private static readonly ushort[] AuthorTags = [0x013B, 0x8298];
    private static readonly ushort[] CameraExifTags = [0x829A, 0x829D, 0x8822, 0x8827, 0x8830, 0x9204, 0x9205, 0x9207, 0x9208, 0x9209, 0x920A, 0xA402, 0xA403, 0xA405, 0xA406, 0xA432, 0xA433, 0xA434];
    private static readonly ushort[] TimeExifTags = [0x9003, 0x9004, 0x9010, 0x9011, 0x9012];
    private static readonly ushort[] AlwaysExifTags = [0x9000, 0xA001]; // EXIF version, color space (how to read the colors)
    private static readonly ushort[] LocationGpsTags = [0x0000, 0x0001, 0x0002, 0x0003, 0x0004, 0x0005, 0x0006, 0x0010, 0x0011, 0x0012];
    private static readonly ushort[] LocationTimeGpsTags = [0x0007, 0x001D];

    /// <summary>The TIFF bytes of the kept tags, or null when nothing worth keeping is left.</summary>
    public static byte[]? Write(TiffStructure source, StripOptions options)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(options);
        var image = new List<TiffEntry>();
        var exif = new List<TiffEntry>();
        var gps = new List<TiffEntry>();

        if (options.KeepOrientation && source.Image.Number(0x0112) is { } orientation && orientation is > 1 and <= 8)
        {
            Take(source.Image, Orientation, image);
        }

        if (!options.RemoveCameraDetails)
        {
            Take(source.Image, CameraImageTags, image);
            Take(source.Exif, CameraExifTags, exif);
        }

        if (!options.RemoveAuthorCopyright)
        {
            Take(source.Image, AuthorTags, image);
        }

        if (!options.RemoveCaptureTime)
        {
            Take(source.Exif, TimeExifTags, exif);
        }

        if (!options.RemoveLocation)
        {
            Take(source.Gps, LocationGpsTags, gps);
            if (!options.RemoveCaptureTime)
            {
                Take(source.Gps, LocationTimeGpsTags, gps);
            }
        }

        if (image.Count == 0 && exif.Count == 0 && gps.Count == 0)
        {
            return null;
        }

        if (exif.Count > 0)
        {
            Take(source.Exif, AlwaysExifTags, exif);
        }

        return Serialize(source.BigEndian, image, exif, gps);
    }

    private static void Take(TiffDirectory? from, ushort[] tags, List<TiffEntry> to)
    {
        if (from is null)
        {
            return;
        }

        foreach (ushort tag in tags)
        {
            // Values that were too large to read (count without bytes) cannot be copied: dropped.
            if (from[tag] is { } entry && entry.Value.Length == TiffEntry.SizeOf(entry.Type) * entry.Count && to.All(e => e.Tag != tag))
            {
                to.Add(entry);
            }
        }
    }

    private static byte[] Serialize(bool big, List<TiffEntry> image, List<TiffEntry> exif, List<TiffEntry> gps)
    {
        // Pointer entries are LONG values (4 bytes, inline); their values are filled in once the layout is known.
        var imageDirectory = new List<TiffEntry>(image);
        if (exif.Count > 0)
        {
            imageDirectory.Add(new TiffEntry(TiffReader.ExifPointer, TiffType.Long32, 1, new byte[4]));
        }

        if (gps.Count > 0)
        {
            imageDirectory.Add(new TiffEntry(TiffReader.GpsPointer, TiffType.Long32, 1, new byte[4]));
        }

        List<TiffEntry>[] directories = [Sorted(imageDirectory), Sorted(exif), Sorted(gps)];
        int[] offsets = new int[3];
        int position = 8;
        for (int d = 0; d < 3; d++)
        {
            if (directories[d].Count == 0)
            {
                continue;
            }

            offsets[d] = position;
            position += Size(directories[d]);
        }

        byte[] output = new byte[position];
        Span<byte> o = output;
        o[0] = o[1] = (byte)(big ? 'M' : 'I');
        U16(o[2..], 42, big);
        U32(o[4..], 8, big);
        for (int d = 0; d < 3; d++)
        {
            List<TiffEntry> entries = directories[d];
            if (entries.Count == 0)
            {
                continue;
            }

            int table = offsets[d];
            int values = table + 2 + (12 * entries.Count) + 4;
            U16(o[table..], (ushort)entries.Count, big);
            for (int i = 0; i < entries.Count; i++)
            {
                TiffEntry entry = entries[i];
                Span<byte> e = o.Slice(table + 2 + (12 * i), 12);
                U16(e, entry.Tag, big);
                U16(e[2..], (ushort)entry.Type, big);
                U32(e[4..], entry.Count, big);
                if (entry.Tag == TiffReader.ExifPointer && d == 0)
                {
                    U32(e[8..], (uint)offsets[1], big);
                }
                else if (entry.Tag == TiffReader.GpsPointer && d == 0)
                {
                    U32(e[8..], (uint)offsets[2], big);
                }
                else if (entry.Value.Length <= 4)
                {
                    entry.Value.CopyTo(e[8..]);
                }
                else
                {
                    U32(e[8..], (uint)values, big);
                    entry.Value.CopyTo(o[values..]);
                    values += (entry.Value.Length + 1) & ~1;
                }
            }

            U32(o[(table + 2 + (12 * entries.Count))..], 0, big); // no next directory (no thumbnail)
        }

        return output;
    }

    private static List<TiffEntry> Sorted(List<TiffEntry> entries) => [.. entries.OrderBy(e => e.Tag)];

    private static int Size(List<TiffEntry> entries) =>
        2 + (12 * entries.Count) + 4 + entries.Where(e => e.Value.Length > 4).Sum(e => (e.Value.Length + 1) & ~1);

    private static void U16(Span<byte> b, ushort v, bool big)
    {
        if (big)
        {
            BinaryPrimitives.WriteUInt16BigEndian(b, v);
        }
        else
        {
            BinaryPrimitives.WriteUInt16LittleEndian(b, v);
        }
    }

    private static void U32(Span<byte> b, uint v, bool big)
    {
        if (big)
        {
            BinaryPrimitives.WriteUInt32BigEndian(b, v);
        }
        else
        {
            BinaryPrimitives.WriteUInt32LittleEndian(b, v);
        }
    }
}
