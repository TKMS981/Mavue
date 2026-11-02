using System.Buffers.Binary;
using System.Text;

namespace Mavue.Metadata.Tiff;

/// <summary>TIFF field types (TIFF 6.0, plus the IFD type of TIFF/EP).</summary>
public enum TiffType : ushort
{
    Byte = 1,
    Ascii = 2,
    Short16 = 3,
    Long32 = 4,
    Rational = 5,
    SByte = 6,
    Undefined = 7,
    SShort16 = 8,
    SLong32 = 9,
    SRational = 10,
    SingleFloat = 11,
    Double64 = 12,
    Ifd = 13,
}

/// <summary>One directory entry with its value bytes exactly as stored (in the file's byte order).</summary>
public sealed record TiffEntry(ushort Tag, TiffType Type, uint Count, byte[] Value)
{
    /// <summary>Bytes per value of a type (0 for unknown types).</summary>
    public static int SizeOf(TiffType type) => type switch
    {
        TiffType.Byte or TiffType.Ascii or TiffType.SByte or TiffType.Undefined => 1,
        TiffType.Short16 or TiffType.SShort16 => 2,
        TiffType.Long32 or TiffType.SLong32 or TiffType.SingleFloat or TiffType.Ifd => 4,
        TiffType.Rational or TiffType.SRational or TiffType.Double64 => 8,
        _ => 0,
    };
}

/// <summary>An image file directory: its entries by tag (the last one wins when a tag repeats).</summary>
public sealed class TiffDirectory
{
    public TiffDirectory(bool bigEndian) => BigEndian = bigEndian;

    public bool BigEndian { get; }

    public Dictionary<ushort, TiffEntry> Entries { get; } = [];

    public TiffEntry? this[ushort tag] => Entries.GetValueOrDefault(tag);

    public string? Ascii(ushort tag)
    {
        if (this[tag] is not { } entry || entry.Value.Length == 0)
        {
            return null;
        }

        // ASCII is officially 7-bit; in practice cameras and editors write UTF-8 (or Latin-1). Trailing NULs end it.
        ReadOnlySpan<byte> bytes = entry.Value;
        int end = bytes.IndexOf((byte)0);
        bytes = end >= 0 ? bytes[..end] : bytes;
        string text = System.Text.Unicode.Utf8.IsValid(bytes) ? Encoding.UTF8.GetString(bytes) : Encoding.Latin1.GetString(bytes);
        text = text.Trim();
        return text.Length == 0 ? null : text;
    }

    /// <summary>A SHORT, LONG or BYTE value (the first one).</summary>
    public long? Number(ushort tag, int index = 0)
    {
        if (this[tag] is not { } entry || index >= entry.Count)
        {
            return null;
        }

        int size = TiffEntry.SizeOf(entry.Type);
        if (size == 0 || entry.Value.Length < (index + 1) * size)
        {
            return null;
        }

        ReadOnlySpan<byte> v = entry.Value.AsSpan(index * size, size);
        return entry.Type switch
        {
            TiffType.Byte or TiffType.Undefined => v[0],
            TiffType.SByte => (sbyte)v[0],
            TiffType.Short16 => BigEndian ? BinaryPrimitives.ReadUInt16BigEndian(v) : BinaryPrimitives.ReadUInt16LittleEndian(v),
            TiffType.SShort16 => BigEndian ? BinaryPrimitives.ReadInt16BigEndian(v) : BinaryPrimitives.ReadInt16LittleEndian(v),
            TiffType.Long32 or TiffType.Ifd => BigEndian ? BinaryPrimitives.ReadUInt32BigEndian(v) : BinaryPrimitives.ReadUInt32LittleEndian(v),
            TiffType.SLong32 => BigEndian ? BinaryPrimitives.ReadInt32BigEndian(v) : BinaryPrimitives.ReadInt32LittleEndian(v),
            _ => null,
        };
    }

    /// <summary>A RATIONAL or SRATIONAL value as (numerator, denominator); null when absent or the denominator is 0.</summary>
    public (long Numerator, long Denominator)? Rational(ushort tag, int index = 0)
    {
        if (this[tag] is not { Type: TiffType.Rational or TiffType.SRational } entry || index >= entry.Count || entry.Value.Length < (index + 1) * 8)
        {
            return null;
        }

        ReadOnlySpan<byte> v = entry.Value.AsSpan(index * 8, 8);
        long n, d;
        if (entry.Type == TiffType.Rational)
        {
            n = BigEndian ? BinaryPrimitives.ReadUInt32BigEndian(v) : BinaryPrimitives.ReadUInt32LittleEndian(v);
            d = BigEndian ? BinaryPrimitives.ReadUInt32BigEndian(v[4..]) : BinaryPrimitives.ReadUInt32LittleEndian(v[4..]);
        }
        else
        {
            n = BigEndian ? BinaryPrimitives.ReadInt32BigEndian(v) : BinaryPrimitives.ReadInt32LittleEndian(v);
            d = BigEndian ? BinaryPrimitives.ReadInt32BigEndian(v[4..]) : BinaryPrimitives.ReadInt32LittleEndian(v[4..]);
        }

        return d == 0 ? null : (n, d);
    }

    public double? Real(ushort tag, int index = 0) =>
        Rational(tag, index) is { } r ? (double)r.Numerator / r.Denominator : Number(tag, index);

    /// <summary>UCS-2 little-endian text of the Windows XP tags (XPTitle, XPComment…), stored as BYTE arrays.</summary>
    public string? Ucs2(ushort tag)
    {
        if (this[tag] is not { } entry || entry.Value.Length < 2)
        {
            return null;
        }

        string text = Encoding.Unicode.GetString(entry.Value, 0, entry.Value.Length & ~1).TrimEnd('\0').Trim();
        return text.Length == 0 ? null : text;
    }

    /// <summary>EXIF UserComment: an 8-byte character code ("ASCII\0\0\0", "UNICODE\0", "JIS\0\0\0\0\0") and the text.</summary>
    public string? UserComment(ushort tag)
    {
        if (this[tag] is not { } entry || entry.Value.Length <= 8)
        {
            return null;
        }

        ReadOnlySpan<byte> code = entry.Value.AsSpan(0, 8);
        ReadOnlySpan<byte> body = entry.Value.AsSpan(8);
        string text = code.StartsWith("UNICODE"u8)
            ? (BigEndian ? Encoding.BigEndianUnicode : Encoding.Unicode).GetString(body[..(body.Length & ~1)])
            : System.Text.Unicode.Utf8.IsValid(body) ? Encoding.UTF8.GetString(body) : Encoding.Latin1.GetString(body);
        text = text.TrimEnd('\0', ' ').Trim();
        return text.Length == 0 ? null : text;
    }
}
