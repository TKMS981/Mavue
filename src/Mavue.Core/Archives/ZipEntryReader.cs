using System.Buffers.Binary;
using System.IO.Compression;

namespace Mavue.Core.Archives;

/// <summary>Why one entry cannot be read (the rest of the archive still can).</summary>
public enum ArchiveEntryProblem
{
    /// <summary>Encrypted: Quick View does not ask for passwords.</summary>
    Encrypted = 0,

    /// <summary>A compression method other than stored or deflate (e.g. LZMA, bzip2, Zstandard, Deflate64).</summary>
    UnsupportedMethod,

    /// <summary>Wrong signature, data outside the file, invalid compressed data, size or CRC mismatch.</summary>
    Damaged,

    /// <summary>A folder has no data.</summary>
    Folder,
}

/// <summary>One entry cannot be read; the preview says why.</summary>
public sealed class ArchiveEntryException : IOException
{
    public ArchiveEntryException()
    {
    }

    public ArchiveEntryException(string message)
        : base(message)
    {
    }

    public ArchiveEntryException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public ArchiveEntryProblem Problem { get; init; }
}

/// <summary>
/// Decompresses one ZIP entry into memory, on demand (docs/ARCHITECTURE.md §4.7.5). Nothing is written to disk.
/// Every decompressed byte goes through <see cref="DecompressionGuardStream"/> (entry and archive totals, compression
/// ratio, cancellation); a declared size above the entry limit is refused before reading; the result must match the
/// declared size and CRC-32, or the entry is reported as damaged.
/// </summary>
public static class ZipEntryReader
{
    private const uint LocalHeader = 0x04034b50;
    private const int LocalHeaderLength = 30;
    private const int InitialCapacityLimit = 16 * 1024 * 1024; // the declared size is only a hint

    /// <summary>Reads <paramref name="entry"/> of the archive at <paramref name="archivePath"/>.</summary>
    public static ReadOnlyMemory<byte> ReadAll(string archivePath, ZipEntry entry, ArchiveLimits limits, DecompressionTotals totals, CancellationToken cancellation = default)
    {
        using var file = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 1 << 16, FileOptions.SequentialScan);
        return ReadAll(file, entry, limits, totals, cancellation);
    }

    /// <summary>Reads <paramref name="entry"/> from a readable, seekable stream of the whole archive.</summary>
    public static ReadOnlyMemory<byte> ReadAll(Stream archive, ZipEntry entry, ArchiveLimits limits, DecompressionTotals totals, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(archive);
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(totals);
        if (entry.IsDirectory)
        {
            throw new ArchiveEntryException("A folder has no data.") { Problem = ArchiveEntryProblem.Folder };
        }

        if (entry.IsEncrypted)
        {
            throw new ArchiveEntryException("The entry is encrypted.") { Problem = ArchiveEntryProblem.Encrypted };
        }

        if (!entry.IsSupportedMethod)
        {
            throw new ArchiveEntryException("Unsupported compression method.") { Problem = ArchiveEntryProblem.UnsupportedMethod };
        }

        if (entry.Size > limits.MaxEntryBytes)
        {
            throw new ArchiveLimitExceededException("The entry is larger than the preview limit.") { Limit = nameof(ArchiveLimits.MaxEntryBytes) };
        }

        if (entry.HasSuspiciousRatio(limits))
        {
            throw new ArchiveLimitExceededException("The entry has an abnormal compression ratio.") { Limit = nameof(ArchiveLimits.MaxCompressionRatio) };
        }

        long length = archive.Length;
        if (entry.LocalHeaderOffset < 0 || entry.LocalHeaderOffset > length - LocalHeaderLength)
        {
            throw Damaged("Local header outside the file.");
        }

        Span<byte> header = stackalloc byte[LocalHeaderLength];
        archive.Position = entry.LocalHeaderOffset;
        archive.ReadExactly(header);
        if (BinaryPrimitives.ReadUInt32LittleEndian(header) != LocalHeader)
        {
            throw Damaged("Wrong local header signature.");
        }

        long dataStart = entry.LocalHeaderOffset + LocalHeaderLength + BinaryPrimitives.ReadUInt16LittleEndian(header[26..]) + BinaryPrimitives.ReadUInt16LittleEndian(header[28..]);
        if (entry.CompressedSize < 0 || dataStart > length || entry.CompressedSize > length - dataStart)
        {
            throw Damaged("Entry data outside the file.");
        }

        var slice = new SliceStream(archive, dataStart, entry.CompressedSize);
        Stream decoded = entry.Method == ZipEntry.Deflate ? new DeflateStream(slice, CompressionMode.Decompress, leaveOpen: true) : slice;
        using var guard = new DecompressionGuardStream(decoded, entry.CompressedSize, limits, totals, leaveOpen: false, cancellation);
        var output = new MemoryStream((int)Math.Clamp(entry.Size, 0, InitialCapacityLimit));
        var crc = new Crc32();
        byte[] buffer = new byte[81920];
        try
        {
            int read;
            while ((read = guard.Read(buffer, 0, buffer.Length)) > 0)
            {
                crc.Append(buffer.AsSpan(0, read));
                output.Write(buffer, 0, read);
            }
        }
        catch (InvalidDataException ex)
        {
            throw new ArchiveEntryException("Invalid compressed data.", ex) { Problem = ArchiveEntryProblem.Damaged };
        }

        if (output.Length != entry.Size || crc.Value != entry.Crc)
        {
            throw Damaged("Size or CRC mismatch.");
        }

        return output.GetBuffer().AsMemory(0, (int)output.Length);
    }

    private static ArchiveEntryException Damaged(string message) => new(message) { Problem = ArchiveEntryProblem.Damaged };

    /// <summary>A read-only window [start, start + length) of the archive stream.</summary>
    private sealed class SliceStream : Stream
    {
        private readonly Stream _inner;
        private readonly long _start;
        private readonly long _length;
        private long _position;

        public SliceStream(Stream inner, long start, long length)
        {
            _inner = inner;
            _start = start;
            _length = length;
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => _length;

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            long remaining = _length - _position;
            if (remaining <= 0)
            {
                return 0;
            }

            if (buffer.Length > remaining)
            {
                buffer = buffer[..(int)remaining];
            }

            _inner.Position = _start + _position;
            int n = _inner.Read(buffer);
            _position += n;
            return n;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
