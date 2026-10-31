namespace Mavue.Core.Archives;

/// <summary>Decompressed bytes of one archive preview, shared by all entry streams (thread-safe).</summary>
public sealed class DecompressionTotals
{
    private long _bytes;

    public long Bytes => Interlocked.Read(ref _bytes);

    internal long Add(long count) => Interlocked.Add(ref _bytes, count);
}

/// <summary>
/// Read-only wrapper around a decompressing stream that counts what it actually produces and stops with
/// <see cref="ArchiveLimitExceededException"/> at the entry limit, the archive total or an abnormal compression ratio.
/// The declared uncompressed size is never trusted. Cancellation is checked on every read.
/// </summary>
public sealed class DecompressionGuardStream : Stream
{
    private readonly Stream _inner;
    private readonly long _compressedBytes;
    private readonly ArchiveLimits _limits;
    private readonly DecompressionTotals _totals;
    private readonly CancellationToken _cancellation;
    private readonly bool _leaveOpen;
    private long _read;

    /// <param name="inner">The entry's decompressing stream.</param>
    /// <param name="compressedBytes">Compressed size from the archive (used only for the ratio).</param>
    /// <param name="limits">Limits to enforce.</param>
    /// <param name="totals">Running total for the whole archive.</param>
    /// <param name="leaveOpen">Do not dispose <paramref name="inner"/>.</param>
    /// <param name="cancellation">Stops reading when the preview moves on.</param>
    public DecompressionGuardStream(Stream inner, long compressedBytes, ArchiveLimits limits, DecompressionTotals totals, bool leaveOpen = false, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(totals);
        _inner = inner;
        _compressedBytes = Math.Max(0, compressedBytes);
        _limits = limits;
        _totals = totals;
        _cancellation = cancellation;
        _leaveOpen = leaveOpen;
    }

    /// <summary>Decompressed bytes read from this entry so far.</summary>
    public long BytesRead => _read;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => _read;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        _cancellation.ThrowIfCancellationRequested();
        int n = _inner.Read(buffer);
        Account(n);
        return n;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_cancellation, cancellationToken);
        linked.Token.ThrowIfCancellationRequested();
        int n = await _inner.ReadAsync(buffer, linked.Token).ConfigureAwait(false);
        Account(n);
        return n;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_leaveOpen)
        {
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }

    private void Account(int n)
    {
        if (n <= 0)
        {
            return;
        }

        _read += n;
        long total = _totals.Add(n);
        if (_read > _limits.MaxEntryBytes)
        {
            throw new ArchiveLimitExceededException("The entry is larger than the preview limit.") { Limit = nameof(ArchiveLimits.MaxEntryBytes) };
        }

        if (total > _limits.MaxTotalBytes)
        {
            throw new ArchiveLimitExceededException("The archive expands beyond the preview limit.") { Limit = nameof(ArchiveLimits.MaxTotalBytes) };
        }

        if (_read > _limits.RatioCheckAfterBytes && _read > _limits.MaxCompressionRatio * Math.Max(1, _compressedBytes))
        {
            throw new ArchiveLimitExceededException("The entry has an abnormal compression ratio.") { Limit = nameof(ArchiveLimits.MaxCompressionRatio) };
        }
    }
}
