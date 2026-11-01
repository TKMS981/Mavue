using System.Buffers;
using System.Runtime.InteropServices;

namespace Mavue.Core.Text;

/// <summary>Which line breaks a file uses (for the info line).</summary>
[Flags]
public enum LineBreakKinds
{
    None = 0,
    CrLf = 1,
    Lf = 2,
    Cr = 4,
}

/// <summary>
/// Where lines start in a text file, built once in the background without keeping the text (docs/ARCHITECTURE.md
/// §4.7.12). Only checkpoints are stored: the byte offset of every 16th line, and of every line that starts more than
/// 64 KiB after the previous checkpoint, so reading any line scans at most 15 short lines or 64 KiB. Line breaks are
/// LF, CRLF and a lone CR, searched in whole code units of the encoding. Memory is 12 bytes per checkpoint and is
/// bounded by <see cref="TextPreviewLimits.MaxCheckpoints"/>; indexing stops at
/// <see cref="TextPreviewLimits.MaxIndexedBytes"/>. One thread builds; any thread may read while it does.
/// </summary>
public sealed class TextLineIndex
{
    private const int ChunkSize = 1 << 16;
    private const int BlockSize = 1 << 20;

    private readonly TextEncodingInfo _encoding;
    private readonly int _maxCheckpoints;
    private readonly long _maxBytes;
    private readonly long[][] _offsets;
    private readonly int[][] _lines;
    private int _checkpoints;
    private int _lineCount;
    private long _indexedBytes;
    private long _longestLineBytes;
    private int _breaks;
    private volatile bool _complete;
    private volatile bool _truncated;

    public TextLineIndex(TextEncodingInfo encoding, TextPreviewLimits limits)
    {
        ArgumentNullException.ThrowIfNull(encoding);
        ArgumentNullException.ThrowIfNull(limits);
        _encoding = encoding;
        _maxCheckpoints = Math.Max(1, limits.MaxCheckpoints);
        _maxBytes = limits.MaxIndexedBytes;
        int chunks = (_maxCheckpoints + ChunkSize - 1) / ChunkSize;
        _offsets = new long[chunks][];
        _lines = new int[chunks][];
        AddCheckpoint(0, encoding.PreambleLength);
        _lineCount = 1;
    }

    /// <summary>Lines whose start is known (the last one may still be growing while indexing).</summary>
    public int LineCount => Volatile.Read(ref _lineCount);

    /// <summary>The whole file (or everything up to the limits) has been indexed.</summary>
    public bool IsComplete => _complete;

    /// <summary>Indexing stopped at a limit: later lines are not shown.</summary>
    public bool IsTruncated => _truncated;

    /// <summary>Bytes of the file indexed so far.</summary>
    public long IndexedBytes => Interlocked.Read(ref _indexedBytes);

    /// <summary>The longest line seen so far, in bytes (an estimate of the horizontal extent).</summary>
    public long LongestLineBytes => Interlocked.Read(ref _longestLineBytes);

    public LineBreakKinds LineBreaks => (LineBreakKinds)Volatile.Read(ref _breaks);

    /// <summary>Memory held by the checkpoints (diagnostics).</summary>
    public long MemoryBytes => (long)Volatile.Read(ref _checkpoints) * (sizeof(long) + sizeof(int));

    /// <summary>
    /// Reads <paramref name="stream"/> from its start to the end (or a limit), recording line starts.
    /// <paramref name="progress"/> is called on this thread after the first 1 MiB, then about every 4 MiB, and at the end.
    /// </summary>
    public void Build(Stream stream, CancellationToken cancellation, Action? progress = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        int unit = _encoding.UnitSize;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(BlockSize);
        try
        {
            stream.Seek(_encoding.PreambleLength, SeekOrigin.Begin);
            long position = _encoding.PreambleLength;
            long lineStart = position;
            long lastCheckpoint = position;
            bool pendingCr = false;
            long nextProgress = position + (4L * 1024 * 1024);
            while (true)
            {
                cancellation.ThrowIfCancellationRequested();
                long remaining = _maxBytes - position;
                if (remaining <= 0)
                {
                    _truncated = stream.Length > position;
                    break;
                }

                int want = (int)Math.Min(BlockSize, remaining);
                want -= want % unit;
                int read = stream.ReadAtLeast(buffer.AsSpan(0, want), want, throwOnEndOfStream: false);
                read -= read % unit; // a cut code unit at the very end of the file is not a line break
                if (read <= 0)
                {
                    break;
                }

                ReadOnlySpan<byte> block = buffer.AsSpan(0, read);
                int at = 0;
                if (pendingCr)
                {
                    pendingCr = false;
                    if (IsUnit(block, 0, '\n'))
                    {
                        at = unit; // the LF of a CRLF split across blocks
                        lineStart = position + unit;
                        Record(LineBreakKinds.CrLf);
                    }
                    else
                    {
                        Record(LineBreakKinds.Cr);
                    }

                    if (!StartLine(lineStart, ref lastCheckpoint))
                    {
                        _truncated = true;
                        return;
                    }
                }

                while (at < read)
                {
                    int found = FindBreak(block[at..], unit);
                    if (found < 0)
                    {
                        break;
                    }

                    int breakAt = at + found;
                    long breakPosition = position + breakAt;
                    NoteLineLength(breakPosition - lineStart);
                    if (IsUnit(block, breakAt, '\r'))
                    {
                        if (breakAt + unit >= read)
                        {
                            pendingCr = true; // the next block tells CR from CRLF
                            at = read;
                            lineStart = position + read;
                            break;
                        }

                        if (IsUnit(block, breakAt + unit, '\n'))
                        {
                            Record(LineBreakKinds.CrLf);
                            at = breakAt + (2 * unit);
                        }
                        else
                        {
                            Record(LineBreakKinds.Cr);
                            at = breakAt + unit;
                        }
                    }
                    else
                    {
                        Record(LineBreakKinds.Lf);
                        at = breakAt + unit;
                    }

                    lineStart = position + at;
                    if (!StartLine(lineStart, ref lastCheckpoint))
                    {
                        position += at;
                        Interlocked.Exchange(ref _indexedBytes, position);
                        _truncated = true;
                        return;
                    }
                }

                bool first = position == _encoding.PreambleLength;
                position += read;
                Interlocked.Exchange(ref _indexedBytes, position);
                if (first || position >= nextProgress)
                {
                    // After the first block (the first screen can be shown), then about every 4 MiB.
                    nextProgress = position + (4L * 1024 * 1024);
                    progress?.Invoke();
                }
            }

            if (pendingCr)
            {
                Record(LineBreakKinds.Cr);
                StartLine(lineStart, ref lastCheckpoint);
            }

            NoteLineLength(position - lineStart);

            // A file that ends with a line break has no further (empty) line to show.
            if (LineCount > 1 && lineStart == position)
            {
                Volatile.Write(ref _lineCount, LineCount - 1);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            _complete = true;
            progress?.Invoke();
        }
    }

    /// <summary>The greatest checkpoint at or before <paramref name="line"/>: its line number and byte offset.</summary>
    public (int Line, long Offset) CheckpointAtOrBefore(int line)
    {
        int count = Volatile.Read(ref _checkpoints);
        int low = 0, high = count - 1;
        while (low < high)
        {
            int mid = (low + high + 1) >> 1;
            if (LineAt(mid) <= line)
            {
                low = mid;
            }
            else
            {
                high = mid - 1;
            }
        }

        return (LineAt(low), OffsetAt(low));
    }

    /// <summary>The byte offset of <paramref name="line"/> when it is a checkpoint.</summary>
    public long? CheckpointOf(int line)
    {
        (int found, long offset) = CheckpointAtOrBefore(line);
        return found == line ? offset : null;
    }

    private bool StartLine(long offset, ref long lastCheckpoint)
    {
        int line = _lineCount; // only this thread writes
        if (line % TextPreviewLimits.CheckpointLineStride == 0 || offset - lastCheckpoint >= TextPreviewLimits.CheckpointByteSpan)
        {
            if (_checkpoints >= _maxCheckpoints)
            {
                return false;
            }

            AddCheckpoint(line, offset);
            lastCheckpoint = offset;
        }

        Volatile.Write(ref _lineCount, line + 1);
        return true;
    }

    private void AddCheckpoint(int line, long offset)
    {
        int index = _checkpoints;
        int chunk = index / ChunkSize;
        _offsets[chunk] ??= new long[ChunkSize];
        _lines[chunk] ??= new int[ChunkSize];
        _offsets[chunk][index % ChunkSize] = offset;
        _lines[chunk][index % ChunkSize] = line;
        Volatile.Write(ref _checkpoints, index + 1);
    }

    private long OffsetAt(int index) => _offsets[index / ChunkSize][index % ChunkSize];

    private int LineAt(int index) => _lines[index / ChunkSize][index % ChunkSize];

    private void NoteLineLength(long length)
    {
        if (length > _longestLineBytes)
        {
            Interlocked.Exchange(ref _longestLineBytes, length);
        }
    }

    private void Record(LineBreakKinds kind) => _breaks |= (int)kind;

    private bool IsUnit(ReadOnlySpan<byte> block, int at, char c) => UnitIs(block, at, c, _encoding.UnitSize, _encoding.BigEndian);

    private int FindBreak(ReadOnlySpan<byte> block, int unit) => FindLineBreak(block, unit, _encoding.BigEndian);

    /// <summary>Index (in bytes, unit-aligned) of the first CR or LF code unit, or -1. Windows runs little endian, so
    /// a code unit read as a native integer has the file's low byte first.</summary>
    internal static int FindLineBreak(ReadOnlySpan<byte> block, int unit, bool bigEndian)
    {
        switch (unit)
        {
            case 1:
                return block.IndexOfAny((byte)'\n', (byte)'\r');
            case 2:
            {
                ReadOnlySpan<ushort> units = MemoryMarshal.Cast<byte, ushort>(block[..(block.Length & ~1)]);
                int i = bigEndian ? units.IndexOfAny((ushort)0x0A00, (ushort)0x0D00) : units.IndexOfAny((ushort)0x000A, (ushort)0x000D);
                return i < 0 ? -1 : i * 2;
            }

            default:
            {
                ReadOnlySpan<uint> units = MemoryMarshal.Cast<byte, uint>(block[..(block.Length & ~3)]);
                int i = bigEndian ? units.IndexOfAny(0x0A000000u, 0x0D000000u) : units.IndexOfAny(0x0000000Au, 0x0000000Du);
                return i < 0 ? -1 : i * 4;
            }
        }
    }

    internal static bool UnitIs(ReadOnlySpan<byte> block, int at, char c, int unit, bool bigEndian)
    {
        if (at + unit > block.Length)
        {
            return false;
        }

        return unit switch
        {
            1 => block[at] == c,
            2 => bigEndian ? block[at] == 0 && block[at + 1] == c : block[at] == c && block[at + 1] == 0,
            _ => bigEndian
                ? block[at] == 0 && block[at + 1] == 0 && block[at + 2] == 0 && block[at + 3] == c
                : block[at] == c && block[at + 1] == 0 && block[at + 2] == 0 && block[at + 3] == 0,
        };
    }
}
