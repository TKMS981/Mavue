using System.Buffers.Binary;
using System.Text;

namespace Mavue.Core.Archives;

/// <summary>Why an archive as a whole cannot be listed.</summary>
public enum ArchiveProblem
{
    /// <summary>No ZIP end-of-central-directory record: not a ZIP file (or cut off before its end).</summary>
    NotAnArchive = 0,

    /// <summary>The structure is inconsistent (sizes or offsets outside the file, wrong signatures).</summary>
    Damaged,

    /// <summary>A split or spanned archive (.z01, .zip.001 …): only the last part was given.</summary>
    MultiVolume,
}

/// <summary>The archive cannot be listed; the preview says why and Quick View carries on.</summary>
public sealed class ArchiveFormatException : IOException
{
    public ArchiveFormatException()
    {
    }

    public ArchiveFormatException(string message)
        : base(message)
    {
    }

    public ArchiveFormatException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public ArchiveProblem Problem { get; init; }
}

/// <summary>
/// One entry of a ZIP central directory. Sizes, names and dates are as declared by the archive and are never trusted
/// for anything but display and early refusal: reading counts what is actually decompressed
/// (<see cref="DecompressionGuardStream"/>) and checks the CRC.
/// </summary>
/// <param name="Index">Position in the central directory.</param>
/// <param name="Name">The name as stored (decoded), with either separator.</param>
/// <param name="Path">"/"-separated name without empty and "." segments (<see cref="ArchiveEntryName.Normalize"/>).</param>
/// <param name="NameCheck">Result of <see cref="ArchiveEntryName.Check"/>; unsafe names are listed with a warning.</param>
/// <param name="IsDirectory">A folder entry (name ends with a separator).</param>
/// <param name="CompressedSize">Declared compressed size.</param>
/// <param name="Size">Declared uncompressed size.</param>
/// <param name="Crc">Declared CRC-32 of the uncompressed data.</param>
/// <param name="Method">Compression method (0 stored, 8 deflate; others are listed but not read).</param>
/// <param name="IsEncrypted">Encrypted (traditional or AES): listed, never read (Quick View does not ask for passwords).</param>
/// <param name="Modified">Declared modification time (local), when valid.</param>
/// <param name="LocalHeaderOffset">Offset of the local header in the file (prefix data such as a self-extractor included).</param>
public sealed record ZipEntry(
    int Index,
    string Name,
    string Path,
    ArchiveNameCheck NameCheck,
    bool IsDirectory,
    long CompressedSize,
    long Size,
    uint Crc,
    ushort Method,
    bool IsEncrypted,
    DateTime? Modified,
    long LocalHeaderOffset)
{
    public const ushort Stored = 0;
    public const ushort Deflate = 8;

    /// <summary>Quick View can decompress this method (stored or deflate).</summary>
    public bool IsSupportedMethod => Method is Stored or Deflate;

    /// <summary>The entry's data can be read (a file, not encrypted, supported method).</summary>
    public bool IsReadable => !IsDirectory && !IsEncrypted && IsSupportedMethod;

    /// <summary>Declared size ÷ compressed size is beyond the decompression limit (a likely decompression bomb).</summary>
    public bool HasSuspiciousRatio(ArchiveLimits limits) =>
        Size > limits.RatioCheckAfterBytes && Size > limits.MaxCompressionRatio * Math.Max(1, CompressedSize);

    /// <summary>The last segment of <see cref="Path"/> (the file or folder name).</summary>
    public string FileName => Path.Length == 0 ? Name : Path[(Path.LastIndexOf('/') + 1)..];
}

/// <summary>
/// The listing of a ZIP archive, read from its central directory only (docs/ARCHITECTURE.md §4.7.5): nothing is
/// decompressed and nothing is written. The entry count and the directory size are checked against
/// <see cref="ArchiveLimits"/> before the directory is read. Supports ZIP64, prefix data (self-extracting archives),
/// UTF-8 names (flag or Info-ZIP Unicode path field) and legacy names in the system's OEM code page
/// (CP932 on Japanese Windows), as Explorer does.
/// </summary>
public sealed class ZipDirectory
{
    private const uint EndOfCentralDirectory = 0x06054b50;
    private const uint Zip64EndOfCentralDirectory = 0x06064b50;
    private const uint Zip64Locator = 0x07064b50;
    private const uint CentralHeader = 0x02014b50;
    private const int EndRecordLength = 22;
    private const int CentralHeaderLength = 46;

    private ZipDirectory(IReadOnlyList<ZipEntry> entries, long archiveLength, string? legacyEncoding)
    {
        Entries = entries;
        ArchiveLength = archiveLength;
        LegacyNameEncoding = legacyEncoding;
    }

    public IReadOnlyList<ZipEntry> Entries { get; }

    public long ArchiveLength { get; }

    /// <summary>The encoding chosen for names without the UTF-8 flag (diagnostics), or null when all were ASCII/UTF-8-flagged.</summary>
    public string? LegacyNameEncoding { get; }

    /// <summary>Reads the listing of the ZIP file at <paramref name="path"/> (shared read; the file is never locked for writers).</summary>
    public static ZipDirectory Read(string path, ArchiveLimits? limits = null, CancellationToken cancellation = default)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 1);
        return Read(stream, limits, null, cancellation);
    }

    /// <param name="stream">A readable, seekable stream of the whole archive.</param>
    /// <param name="limits">Limits (default <see cref="ArchiveLimits.Default"/>).</param>
    /// <param name="legacyCodePage">Code page for names without the UTF-8 flag (null: the system's OEM code page).</param>
    /// <param name="cancellation">Checked while entries are parsed.</param>
    public static ZipDirectory Read(Stream stream, ArchiveLimits? limits = null, int? legacyCodePage = null, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        limits ??= ArchiveLimits.Default;
        long length = stream.Length;
        if (length < EndRecordLength)
        {
            throw NotAnArchive();
        }

        // The end record: the last signature whose comment fits in the file (the comment is at most 65,535 bytes).
        int tailLength = (int)Math.Min(length, EndRecordLength + ushort.MaxValue);
        byte[] tail = ReadAt(stream, length - tailLength, tailLength);
        int end = -1;
        for (int p = tailLength - EndRecordLength; p >= 0; p--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(p)) == EndOfCentralDirectory &&
                p + EndRecordLength + BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(p + 20)) <= tailLength)
            {
                end = p;
                break;
            }
        }

        if (end < 0)
        {
            throw NotAnArchive();
        }

        long endPosition = length - tailLength + end;
        ReadOnlySpan<byte> record = tail.AsSpan(end, EndRecordLength);
        uint disk = BinaryPrimitives.ReadUInt16LittleEndian(record[4..]);
        uint directoryDisk = BinaryPrimitives.ReadUInt16LittleEndian(record[6..]);
        long declaredCount = BinaryPrimitives.ReadUInt16LittleEndian(record[10..]);
        long directorySize = BinaryPrimitives.ReadUInt32LittleEndian(record[12..]);
        long directoryOffset = BinaryPrimitives.ReadUInt32LittleEndian(record[16..]);
        long directoryEnd = endPosition; // where the central directory is expected to end

        bool zip64 = declaredCount == ushort.MaxValue || directorySize == uint.MaxValue || directoryOffset == uint.MaxValue ||
            disk == ushort.MaxValue || directoryDisk == ushort.MaxValue;
        if (endPosition >= 20)
        {
            byte[] locator = ReadAt(stream, endPosition - 20, 20);
            if (BinaryPrimitives.ReadUInt32LittleEndian(locator) == Zip64Locator)
            {
                zip64 = true;
                if (BinaryPrimitives.ReadUInt32LittleEndian(locator.AsSpan(16)) > 1)
                {
                    throw MultiVolume();
                }

                long recordOffset = (long)Math.Min(BinaryPrimitives.ReadUInt64LittleEndian(locator.AsSpan(8)), long.MaxValue);
                long recordPosition = FindZip64Record(stream, recordOffset, endPosition - 20 - 56);
                byte[] zip64Record = ReadAt(stream, recordPosition, 56);
                disk = BinaryPrimitives.ReadUInt32LittleEndian(zip64Record.AsSpan(16));
                directoryDisk = BinaryPrimitives.ReadUInt32LittleEndian(zip64Record.AsSpan(20));
                declaredCount = (long)Math.Min(BinaryPrimitives.ReadUInt64LittleEndian(zip64Record.AsSpan(32)), long.MaxValue);
                directorySize = (long)Math.Min(BinaryPrimitives.ReadUInt64LittleEndian(zip64Record.AsSpan(40)), long.MaxValue);
                directoryOffset = (long)Math.Min(BinaryPrimitives.ReadUInt64LittleEndian(zip64Record.AsSpan(48)), long.MaxValue);
                directoryEnd = recordPosition;
            }
            else if (zip64)
            {
                throw Damaged("ZIP64 locator missing.");
            }
        }

        if (disk != 0 || directoryDisk != 0)
        {
            throw MultiVolume();
        }

        if (declaredCount > limits.MaxEntries)
        {
            throw new ArchiveLimitExceededException("The archive has too many entries to preview.") { Limit = nameof(ArchiveLimits.MaxEntries) };
        }

        if (directorySize > limits.MaxCentralDirectoryBytes)
        {
            throw new ArchiveLimitExceededException("The archive's directory is too large to preview.") { Limit = nameof(ArchiveLimits.MaxCentralDirectoryBytes) };
        }

        // Prefix data (a self-extractor, a script) shifts every offset by the same amount: the directory really ends
        // where the end record starts.
        long directoryStart = directoryEnd - directorySize;
        if (directoryStart < 0 || directoryOffset > directoryStart)
        {
            throw Damaged("Central directory outside the file.");
        }

        long shift = directoryStart - directoryOffset;
        byte[] directory = ReadAt(stream, directoryStart, (int)directorySize);
        List<ZipEntry> entries = ParseEntries(directory, shift, length, limits, legacyCodePage, out string? legacyEncoding, cancellation);
        return new ZipDirectory(entries, length, legacyEncoding);
    }

    private static List<ZipEntry> ParseEntries(byte[] directory, long shift, long length, ArchiveLimits limits, int? legacyCodePage, out string? legacyEncoding, CancellationToken cancellation)
    {
        var raw = new List<RawEntry>();
        int p = 0;
        while (p + CentralHeaderLength <= directory.Length)
        {
            ReadOnlySpan<byte> h = directory.AsSpan(p);
            if (BinaryPrimitives.ReadUInt32LittleEndian(h) != CentralHeader)
            {
                break; // digital signature / zip64 records may follow; anything else ends the listing
            }

            if ((raw.Count & 1023) == 0)
            {
                cancellation.ThrowIfCancellationRequested();
            }

            if (raw.Count >= limits.MaxEntries)
            {
                throw new ArchiveLimitExceededException("The archive has too many entries to preview.") { Limit = nameof(ArchiveLimits.MaxEntries) };
            }

            ushort madeBy = BinaryPrimitives.ReadUInt16LittleEndian(h[4..]);
            ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(h[8..]);
            ushort method = BinaryPrimitives.ReadUInt16LittleEndian(h[10..]);
            ushort dosTime = BinaryPrimitives.ReadUInt16LittleEndian(h[12..]);
            ushort dosDate = BinaryPrimitives.ReadUInt16LittleEndian(h[14..]);
            uint crc = BinaryPrimitives.ReadUInt32LittleEndian(h[16..]);
            long compressed = BinaryPrimitives.ReadUInt32LittleEndian(h[20..]);
            long size = BinaryPrimitives.ReadUInt32LittleEndian(h[24..]);
            int nameLength = BinaryPrimitives.ReadUInt16LittleEndian(h[28..]);
            int extraLength = BinaryPrimitives.ReadUInt16LittleEndian(h[30..]);
            int commentLength = BinaryPrimitives.ReadUInt16LittleEndian(h[32..]);
            uint externalAttributes = BinaryPrimitives.ReadUInt32LittleEndian(h[38..]);
            long offset = BinaryPrimitives.ReadUInt32LittleEndian(h[42..]);
            int total = CentralHeaderLength + nameLength + extraLength + commentLength;
            if (p + total > directory.Length)
            {
                throw Damaged("Central directory entry cut off.");
            }

            byte[] name = h.Slice(CentralHeaderLength, nameLength).ToArray();
            ReadOnlySpan<byte> extra = h.Slice(CentralHeaderLength + nameLength, extraLength);
            string? unicodeName = null;
            DateTime? modified = DosTime(dosDate, dosTime);
            ParseExtra(extra, name, ref size, ref compressed, ref offset, ref unicodeName, ref modified);

            bool isDirectory = name.Length > 0 && name[^1] is (byte)'/' or (byte)'\\';
            byte host = (byte)(madeBy >> 8);
            if (!isDirectory && size == 0 && host is 0 or 10 or 11 or 14 && (externalAttributes & 0x10) != 0)
            {
                isDirectory = true; // MS-DOS directory attribute
            }

            raw.Add(new RawEntry(name, (flags & 0x800) != 0, unicodeName, isDirectory, compressed, size, crc, method,
                (flags & 1) != 0 || method == 99, modified, offset + shift));
            p += total;
        }

        if (raw.Count == 0 && directory.Length > 0)
        {
            throw Damaged("No central directory entries.");
        }

        Encoding? legacy = ChooseLegacyEncoding(raw, legacyCodePage);
        legacyEncoding = legacy?.WebName;
        var entries = new List<ZipEntry>(raw.Count);
        for (int i = 0; i < raw.Count; i++)
        {
            RawEntry r = raw[i];
            string name = r.UnicodeName ?? (r.Utf8 || legacy is null ? Encoding.UTF8.GetString(r.Name) : legacy.GetString(r.Name));
            bool readable = r.Offset >= 0 && r.Offset <= length - 30;
            entries.Add(new ZipEntry(
                i,
                name,
                ArchiveEntryName.Normalize(name),
                ArchiveEntryName.Check(name, limits),
                r.IsDirectory,
                r.Compressed,
                r.Size,
                r.Crc,
                r.Method,
                r.Encrypted,
                r.Modified,
                readable ? r.Offset : -1));
        }

        return entries;
    }

    /// <summary>ZIP64 sizes and offset, the Info-ZIP Unicode path (when its CRC matches the stored name) and the Unix modification time.</summary>
    private static void ParseExtra(ReadOnlySpan<byte> extra, byte[] name, ref long size, ref long compressed, ref long offset, ref string? unicodeName, ref DateTime? modified)
    {
        while (extra.Length >= 4)
        {
            ushort id = BinaryPrimitives.ReadUInt16LittleEndian(extra);
            int fieldLength = BinaryPrimitives.ReadUInt16LittleEndian(extra[2..]);
            if (4 + fieldLength > extra.Length)
            {
                return;
            }

            ReadOnlySpan<byte> field = extra.Slice(4, fieldLength);
            switch (id)
            {
                case 0x0001:
                    // Only the values that are 0xFFFFFFFF in the header are present, in this order.
                    if (size == uint.MaxValue && field.Length >= 8)
                    {
                        size = ClampToLong(BinaryPrimitives.ReadUInt64LittleEndian(field));
                        field = field[8..];
                    }

                    if (compressed == uint.MaxValue && field.Length >= 8)
                    {
                        compressed = ClampToLong(BinaryPrimitives.ReadUInt64LittleEndian(field));
                        field = field[8..];
                    }

                    if (offset == uint.MaxValue && field.Length >= 8)
                    {
                        offset = ClampToLong(BinaryPrimitives.ReadUInt64LittleEndian(field));
                    }

                    break;
                case 0x7075 when field.Length > 5 && field[0] == 1:
                    if (BinaryPrimitives.ReadUInt32LittleEndian(field[1..]) == Crc32.Compute(name))
                    {
                        try
                        {
                            unicodeName = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(field[5..]);
                        }
                        catch (DecoderFallbackException)
                        {
                            // Keep the stored name.
                        }
                    }

                    break;
                case 0x5455 when field.Length >= 5 && (field[0] & 1) != 0:
                    int unix = BinaryPrimitives.ReadInt32LittleEndian(field[1..]);
                    if (unix > 0)
                    {
                        modified = DateTimeOffset.FromUnixTimeSeconds(unix).LocalDateTime;
                    }

                    break;
            }

            extra = extra[(4 + fieldLength)..];
        }
    }

    /// <summary>
    /// Names without the UTF-8 flag: UTF-8 when every such name is valid UTF-8 (macOS and many tools), else the
    /// legacy code page when every name decodes in it, else CP437 (the ZIP default). Null when all are ASCII.
    /// </summary>
    private static Encoding? ChooseLegacyEncoding(List<RawEntry> entries, int? legacyCodePage)
    {
        List<byte[]> names = entries.Where(e => !e.Utf8 && e.UnicodeName is null && e.Name.Any(b => b >= 0x80)).Select(e => e.Name).ToList();
        if (names.Count == 0)
        {
            return null;
        }

        var utf8 = new UTF8Encoding(false, throwOnInvalidBytes: true);
        if (DecodesAll(utf8, names))
        {
            return Encoding.UTF8;
        }

        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        int codePage = legacyCodePage ?? System.Globalization.CultureInfo.CurrentCulture.TextInfo.OEMCodePage;
        try
        {
            Encoding strict = Encoding.GetEncoding(codePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
            if (DecodesAll(strict, names))
            {
                return Encoding.GetEncoding(codePage);
            }
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            // Unknown code page: fall through to CP437.
        }

        return Encoding.GetEncoding(437);
    }

    private static bool DecodesAll(Encoding strict, List<byte[]> names)
    {
        try
        {
            foreach (byte[] name in names)
            {
                _ = strict.GetCharCount(name);
            }

            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    private static long FindZip64Record(Stream stream, long declared, long expected)
    {
        foreach (long candidate in new[] { declared, expected })
        {
            if (candidate >= 0 && candidate <= stream.Length - 56 &&
                BinaryPrimitives.ReadUInt32LittleEndian(ReadAt(stream, candidate, 4)) == Zip64EndOfCentralDirectory)
            {
                return candidate;
            }
        }

        throw Damaged("ZIP64 end record missing.");
    }

    private static DateTime? DosTime(ushort date, ushort time)
    {
        int year = 1980 + (date >> 9);
        int month = (date >> 5) & 0xF;
        int day = date & 0x1F;
        int hour = time >> 11;
        int minute = (time >> 5) & 0x3F;
        int second = (time & 0x1F) * 2;
        if (month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month) || hour > 23 || minute > 59 || second > 59)
        {
            return null;
        }

        return new DateTime(year, month, day, hour, minute, second, DateTimeKind.Local);
    }

    private static long ClampToLong(ulong value) => value > long.MaxValue ? long.MaxValue : (long)value;

    private static byte[] ReadAt(Stream stream, long position, int count)
    {
        if (position < 0 || position + count > stream.Length)
        {
            throw Damaged("Read outside the file.");
        }

        var buffer = new byte[count];
        stream.Position = position;
        stream.ReadExactly(buffer);
        return buffer;
    }

    private static ArchiveFormatException NotAnArchive() => new("Not a ZIP archive.") { Problem = ArchiveProblem.NotAnArchive };

    private static ArchiveFormatException MultiVolume() => new("Split or spanned ZIP archive.") { Problem = ArchiveProblem.MultiVolume };

    private static ArchiveFormatException Damaged(string message) => new(message) { Problem = ArchiveProblem.Damaged };

    private sealed record RawEntry(byte[] Name, bool Utf8, string? UnicodeName, bool IsDirectory, long Compressed, long Size, uint Crc, ushort Method, bool Encrypted, DateTime? Modified, long Offset);
}
