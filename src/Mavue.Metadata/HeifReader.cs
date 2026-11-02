using System.Buffers.Binary;
using System.Text;

namespace Mavue.Metadata;

/// <summary>An item of a HEIF/AVIF file: where its bytes are (one or more extents in the file).</summary>
internal sealed record HeifItem(uint Id, string Type, string? ContentType, IReadOnlyList<(long Offset, long Length)> Extents)
{
    public long Length => Extents.Sum(e => e.Length);
}

/// <summary>
/// Metadata items of HEIF and AVIF files (ISO/IEC 23008-12, ISO base media file format; ARCHITECTURE §4.7.15): the
/// top-level <c>meta</c> box's item information (<c>iinf</c>) and locations (<c>iloc</c>) give the <c>Exif</c> item
/// (a TIFF block after a 4-byte offset) and XMP (<c>mime</c> items of type application/rdf+xml); <c>colr</c>
/// properties tell whether an ICC profile is embedded. Read only: box sizes, item counts and extents are checked
/// against the file and bounded; image data is never read.
/// </summary>
internal static class HeifReader
{
    private const int MaxItems = 4096;
    private const int MaxExtents = 64;
    private const long MaxMetaBytes = 16L * 1024 * 1024;

    /// <summary>True for an ISO BMFF file whose brands are HEIF or AVIF images (not videos, not Canon CR3).</summary>
    public static bool IsHeif(ReadOnlySpan<byte> head)
    {
        if (head.Length < 16 || !head.Slice(4, 4).SequenceEqual("ftyp"u8))
        {
            return false;
        }

        ReadOnlySpan<byte> major = head.Slice(8, 4);
        return major.SequenceEqual("heic"u8) || major.SequenceEqual("heix"u8) || major.SequenceEqual("heim"u8) || major.SequenceEqual("heis"u8)
            || major.SequenceEqual("mif1"u8) || major.SequenceEqual("msf1"u8) || major.SequenceEqual("avif"u8) || major.SequenceEqual("avis"u8);
    }

    /// <summary>The items of the file's top-level meta box, the offset of its idat payload (or -1), and whether an ICC profile is embedded.</summary>
    public static (List<HeifItem> Items, bool HasIcc) ReadItems(Stream stream)
    {
        var items = new List<HeifItem>();
        bool icc = false;
        long length = stream.Length;
        long position = 0;
        while (position + 8 <= length)
        {
            (string type, long start, long size) = Box(stream, position, length);
            if (size <= 0)
            {
                break;
            }

            if (type == "meta")
            {
                if (size > MaxMetaBytes)
                {
                    break;
                }

                (items, icc) = ReadMeta(stream, start + 4, position + size);
                break;
            }

            position += size;
        }

        return (items, icc);
    }

    /// <summary>The bytes of an item, concatenated (bounded by <paramref name="maxBytes"/>); null when out of range.</summary>
    public static byte[]? ReadItem(Stream stream, HeifItem item, long maxBytes)
    {
        if (item.Length > maxBytes || item.Length <= 0)
        {
            return null;
        }

        byte[] data = new byte[item.Length];
        int at = 0;
        foreach ((long offset, long extent) in item.Extents)
        {
            if (offset < 0 || offset + extent > stream.Length)
            {
                return null;
            }

            stream.Seek(offset, SeekOrigin.Begin);
            stream.ReadExactly(data, at, (int)extent);
            at += (int)extent;
        }

        return data;
    }

    private static (List<HeifItem>, bool) ReadMeta(Stream stream, long from, long end)
    {
        var types = new Dictionary<uint, (string Type, string? Content)>();
        var locations = new Dictionary<uint, List<(long, long)>>();
        long idat = -1;
        bool icc = false;
        long position = from;
        while (position + 8 <= end)
        {
            (string type, long start, long size) = Box(stream, position, end);
            if (size <= 0)
            {
                break;
            }

            byte[] body = Read(stream, start, position + size - start);
            switch (type)
            {
                case "iinf":
                    ReadInfo(body, types);
                    break;
                case "iloc":
                    ReadLocations(body, locations);
                    break;
                case "idat":
                    idat = start;
                    break;
                case "iprp":
                    icc = HasIccProfile(body);
                    break;
            }

            position += size;
        }

        var items = new List<HeifItem>();
        foreach ((uint id, (string itemType, string? content)) in types)
        {
            if (!locations.TryGetValue(id, out List<(long Offset, long Length)>? extents))
            {
                continue;
            }

            // Construction method 1 (offsets inside idat) was marked with a negative offset.
            var resolved = extents.Select(e => e.Offset < 0 ? (idat < 0 ? -1 : idat + (-e.Offset - 1), e.Length) : e).ToList();
            if (resolved.All(e => e.Item1 >= 0))
            {
                items.Add(new HeifItem(id, itemType, content, resolved));
            }
        }

        return (items, icc);
    }

    private static void ReadInfo(ReadOnlySpan<byte> s, Dictionary<uint, (string, string?)> types)
    {
        if (s.Length < 6)
        {
            return;
        }

        int version = s[0];
        int at = 4;
        long count = version == 0 ? BinaryPrimitives.ReadUInt16BigEndian(s[at..]) : BinaryPrimitives.ReadUInt32BigEndian(s[at..]);
        at += version == 0 ? 2 : 4;
        for (long i = 0; i < Math.Min(count, MaxItems) && at + 8 <= s.Length; i++)
        {
            uint size = BinaryPrimitives.ReadUInt32BigEndian(s[at..]);
            if (size < 8 || at + size > s.Length || !s.Slice(at + 4, 4).SequenceEqual("infe"u8))
            {
                break;
            }

            ReadOnlySpan<byte> infe = s.Slice(at + 8, (int)size - 8);
            at += (int)size;
            if (infe.Length < 12 || infe[0] < 2)
            {
                continue; // versions 0 and 1 have no item type
            }

            int p = 4;
            uint id = infe[0] == 2 ? BinaryPrimitives.ReadUInt16BigEndian(infe[p..]) : BinaryPrimitives.ReadUInt32BigEndian(infe[p..]);
            p += infe[0] == 2 ? 2 : 4;
            p += 2; // protection index
            if (p + 4 > infe.Length)
            {
                continue;
            }

            string type = Encoding.ASCII.GetString(infe.Slice(p, 4));
            p += 4;
            string? content = null;
            if (type == "mime")
            {
                int nameEnd = infe[p..].IndexOf((byte)0); // item name
                if (nameEnd >= 0)
                {
                    p += nameEnd + 1;
                    int contentEnd = infe[p..].IndexOf((byte)0);
                    content = Encoding.ASCII.GetString(contentEnd >= 0 ? infe.Slice(p, contentEnd) : infe[p..]);
                }
            }

            types[id] = (type, content);
        }
    }

    private static void ReadLocations(ReadOnlySpan<byte> s, Dictionary<uint, List<(long, long)>> locations)
    {
        if (s.Length < 8)
        {
            return;
        }

        int version = s[0];
        int at = 4;
        int offsetSize = s[at] >> 4, lengthSize = s[at] & 0xF, baseSize = s[at + 1] >> 4, indexSize = version is 1 or 2 ? s[at + 1] & 0xF : 0;
        at += 2;
        long count = version < 2 ? BinaryPrimitives.ReadUInt16BigEndian(s[at..]) : BinaryPrimitives.ReadUInt32BigEndian(s[at..]);
        at += version < 2 ? 2 : 4;
        for (long i = 0; i < Math.Min(count, MaxItems); i++)
        {
            if (!Take(s, ref at, version < 2 ? 2 : 4, out ulong id))
            {
                return;
            }

            int method = 0;
            if (version is 1 or 2)
            {
                if (!Take(s, ref at, 2, out ulong flags))
                {
                    return;
                }

                method = (int)(flags & 0xF);
            }

            if (!Take(s, ref at, 2, out _) || !Take(s, ref at, baseSize, out ulong baseOffset) || !Take(s, ref at, 2, out ulong extentCount))
            {
                return;
            }

            var extents = new List<(long, long)>();
            for (ulong e = 0; e < extentCount; e++)
            {
                if (!Take(s, ref at, indexSize, out _) || !Take(s, ref at, offsetSize, out ulong offset) || !Take(s, ref at, lengthSize, out ulong length))
                {
                    return;
                }

                if (extents.Count < MaxExtents && offset + baseOffset < long.MaxValue / 2 && length < long.MaxValue / 2)
                {
                    long absolute = (long)(baseOffset + offset);
                    extents.Add((method == 1 ? -(absolute + 1) : absolute, (long)length));
                }
            }

            if (method is 0 or 1)
            {
                locations[(uint)id] = extents;
            }
        }
    }

    private static bool HasIccProfile(ReadOnlySpan<byte> iprp)
    {
        // iprp → ipco → colr boxes; colour_type 'prof' or 'rICC' carries an ICC profile.
        for (int at = 0; at + 12 <= iprp.Length; at++)
        {
            if (iprp.Slice(at, 4).SequenceEqual("colr"u8) && (iprp.Slice(at + 4, 4).SequenceEqual("prof"u8) || iprp.Slice(at + 4, 4).SequenceEqual("rICC"u8)))
            {
                return true;
            }
        }

        return false;
    }

    private static bool Take(ReadOnlySpan<byte> s, ref int at, int size, out ulong value)
    {
        value = 0;
        if (size == 0)
        {
            return true;
        }

        if (size is not (2 or 4 or 8) || at + size > s.Length)
        {
            return false;
        }

        value = size switch
        {
            2 => BinaryPrimitives.ReadUInt16BigEndian(s[at..]),
            4 => BinaryPrimitives.ReadUInt32BigEndian(s[at..]),
            _ => BinaryPrimitives.ReadUInt64BigEndian(s[at..]),
        };
        at += size;
        return true;
    }

    /// <summary>The box at <paramref name="position"/>: its type, where its payload starts and its total size (0 when invalid).</summary>
    private static (string Type, long Start, long Size) Box(Stream stream, long position, long end)
    {
        Span<byte> header = stackalloc byte[16];
        stream.Seek(position, SeekOrigin.Begin);
        if (stream.ReadAtLeast(header, 16, throwOnEndOfStream: false) < 8)
        {
            return (string.Empty, 0, 0);
        }

        long size = BinaryPrimitives.ReadUInt32BigEndian(header);
        string type = Encoding.ASCII.GetString(header.Slice(4, 4));
        long start = position + 8;
        if (size == 1)
        {
            size = (long)Math.Min(BinaryPrimitives.ReadUInt64BigEndian(header[8..]), long.MaxValue);
            start += 8;
        }
        else if (size == 0)
        {
            size = end - position;
        }

        return size < start - position || position + size > end ? (type, start, 0) : (type, start, size);
    }

    private static byte[] Read(Stream stream, long offset, long count)
    {
        byte[] data = new byte[Math.Max(0, Math.Min(count, MaxMetaBytes))];
        stream.Seek(offset, SeekOrigin.Begin);
        stream.ReadExactly(data);
        return data;
    }
}
