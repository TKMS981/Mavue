using System.IO.Compression;
using Mavue.Core.Archives;

namespace Mavue.Core.Tests;

/// <summary>Decompressed bytes are counted, never trusted from the archive, and stopped at the limits (ARCHITECTURE §4.7.5).</summary>
[Trait("Category", "Archive")]
public class DecompressionGuardStreamTests
{
    private const long MiB = 1024L * 1024;

    private static MemoryStream Bytes(long count) => new(new byte[count]);

    private static async Task<int> ReadOnceAsync(Stream stream, byte[] buffer, CancellationToken cancellation) =>
        await stream.ReadAsync(buffer, cancellation);

    private static long Drain(Stream stream, int bufferSize = 81920)
    {
        var buffer = new byte[bufferSize];
        long total = 0;
        int n;
        while ((n = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            total += n;
        }

        return total;
    }

    [Fact]
    public void WithinLimits_ReadsEverything()
    {
        var totals = new DecompressionTotals();
        using var guard = new DecompressionGuardStream(Bytes(3 * MiB), compressedBytes: 3 * MiB, ArchiveLimits.Default, totals);

        Assert.Equal(3 * MiB, Drain(guard));
        Assert.Equal(3 * MiB, guard.BytesRead);
        Assert.Equal(3 * MiB, guard.Position);
        Assert.Equal(3 * MiB, totals.Bytes);
    }

    [Fact]
    public void EntryLimit_ExactlyReached_IsAllowed()
    {
        var limits = new ArchiveLimits { MaxEntryBytes = 1000, RatioCheckAfterBytes = long.MaxValue };
        using var guard = new DecompressionGuardStream(Bytes(1000), 1000, limits, new DecompressionTotals());

        Assert.Equal(1000, Drain(guard, 100));
    }

    [Fact]
    public void EntryLimit_Exceeded_Stops()
    {
        var limits = new ArchiveLimits { MaxEntryBytes = 1000, RatioCheckAfterBytes = long.MaxValue };
        using var guard = new DecompressionGuardStream(Bytes(1001), 1001, limits, new DecompressionTotals());

        var ex = Assert.Throws<ArchiveLimitExceededException>(() => Drain(guard, 100));
        Assert.Equal(nameof(ArchiveLimits.MaxEntryBytes), ex.Limit);
    }

    [Fact]
    public void ArchiveTotal_IsSharedByAllEntries()
    {
        var limits = new ArchiveLimits { MaxEntryBytes = 1000, MaxTotalBytes = 1500, RatioCheckAfterBytes = long.MaxValue };
        var totals = new DecompressionTotals();
        using (var first = new DecompressionGuardStream(Bytes(1000), 1000, limits, totals))
        {
            Assert.Equal(1000, Drain(first));
        }

        using var second = new DecompressionGuardStream(Bytes(1000), 1000, limits, totals);
        var ex = Assert.Throws<ArchiveLimitExceededException>(() => Drain(second, 100));
        Assert.Equal(nameof(ArchiveLimits.MaxTotalBytes), ex.Limit);
        Assert.InRange(totals.Bytes, 1501, 1600);
    }

    [Fact]
    public void AbnormalRatio_StopsAfterTheGracePeriod()
    {
        var limits = new ArchiveLimits { MaxCompressionRatio = 10, RatioCheckAfterBytes = 1000 };

        // 10 compressed bytes: ratio 10 is reached at 100 bytes, but the check only starts after 1000.
        using var guard = new DecompressionGuardStream(Bytes(5000), compressedBytes: 10, limits, new DecompressionTotals());
        var buffer = new byte[1000];
        Assert.Equal(1000, guard.Read(buffer, 0, buffer.Length));

        var ex = Assert.Throws<ArchiveLimitExceededException>(() => guard.Read(buffer, 0, 1));
        Assert.Equal(nameof(ArchiveLimits.MaxCompressionRatio), ex.Limit);
    }

    [Fact]
    public void WellCompressedSmallEntries_AreFine()
    {
        // 4 MiB of text compressed 1000× is below the grace period: allowed.
        using var guard = new DecompressionGuardStream(Bytes(4 * MiB), compressedBytes: 4 * 1024, ArchiveLimits.Default, new DecompressionTotals());
        Assert.Equal(4 * MiB, Drain(guard));
    }

    [Fact]
    public void RatioAtTheLimit_IsAllowed()
    {
        var limits = new ArchiveLimits { MaxCompressionRatio = 10, RatioCheckAfterBytes = 100 };
        using var guard = new DecompressionGuardStream(Bytes(1000), compressedBytes: 100, limits, new DecompressionTotals());
        Assert.Equal(1000, Drain(guard, 10));
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-5L)]
    public void UnknownCompressedSize_CountsAsOneByte(long compressed)
    {
        var limits = new ArchiveLimits { MaxCompressionRatio = 200, RatioCheckAfterBytes = 100 };
        using var guard = new DecompressionGuardStream(Bytes(1000), compressed, limits, new DecompressionTotals());
        Assert.Throws<ArchiveLimitExceededException>(() => Drain(guard, 50));
    }

    [Fact]
    public void ZipBomb_FromARealZipArchive_IsStopped()
    {
        // 64 MiB of zeros deflates to about 64 KiB (ratio ~1000): stopped once 200× the compressed size is read,
        // long before the declared size is reached. Nothing is written to disk.
        using var zip = new MemoryStream();
        using (var archive = new ZipArchive(zip, ZipArchiveMode.Create, leaveOpen: true))
        {
            using Stream entry = archive.CreateEntry("zeros.bin", CompressionLevel.Optimal).Open();
            var zeros = new byte[MiB];
            for (int i = 0; i < 64; i++)
            {
                entry.Write(zeros);
            }
        }

        zip.Position = 0;
        using var read = new ZipArchive(zip, ZipArchiveMode.Read);
        ZipArchiveEntry bomb = read.Entries.Single();
        Assert.True(bomb.Length / Math.Max(1, bomb.CompressedLength) > 200);

        var totals = new DecompressionTotals();
        using var guard = new DecompressionGuardStream(bomb.Open(), bomb.CompressedLength, ArchiveLimits.Default, totals);
        var ex = Assert.Throws<ArchiveLimitExceededException>(() => Drain(guard));
        Assert.Equal(nameof(ArchiveLimits.MaxCompressionRatio), ex.Limit);
        long stopAt = (long)(ArchiveLimits.Default.MaxCompressionRatio * bomb.CompressedLength);
        Assert.InRange(guard.BytesRead, Math.Max(ArchiveLimits.Default.RatioCheckAfterBytes, stopAt), stopAt + 81920);
        Assert.True(guard.BytesRead < bomb.Length / 2);
    }

    [Fact]
    public void NormalZipEntry_IsReadCompletely()
    {
        var content = new byte[2 * MiB];
        new Random(7).NextBytes(content); // incompressible
        using var zip = new MemoryStream();
        using (var archive = new ZipArchive(zip, ZipArchiveMode.Create, leaveOpen: true))
        {
            using Stream entry = archive.CreateEntry("写真.jpg").Open();
            entry.Write(content);
        }

        zip.Position = 0;
        using var read = new ZipArchive(zip, ZipArchiveMode.Read);
        ZipArchiveEntry photo = read.Entries.Single();
        using var guard = new DecompressionGuardStream(photo.Open(), photo.CompressedLength, ArchiveLimits.Default, new DecompressionTotals());
        using var copy = new MemoryStream();
        guard.CopyTo(copy);
        Assert.Equal(content, copy.ToArray());
    }

    [Fact]
    public void Cancellation_StopsTheNextRead()
    {
        using var cts = new CancellationTokenSource();
        using var guard = new DecompressionGuardStream(Bytes(1000), 1000, ArchiveLimits.Default, new DecompressionTotals(), cancellation: cts.Token);
        var buffer = new byte[100];
        Assert.Equal(100, guard.Read(buffer, 0, buffer.Length));

        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => guard.Read(buffer, 0, buffer.Length));
    }

    [Fact]
    public async Task ReadAsync_CountsAndHonorsBothTokens()
    {
        var limits = new ArchiveLimits { MaxEntryBytes = 150, RatioCheckAfterBytes = long.MaxValue };
        using var owner = new CancellationTokenSource();
        using var guard = new DecompressionGuardStream(Bytes(1000), 1000, limits, new DecompressionTotals(), cancellation: owner.Token);
        var buffer = new byte[100];

        Assert.Equal(100, await guard.ReadAsync(buffer, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArchiveLimitExceededException>(() => ReadOnceAsync(guard, buffer, TestContext.Current.CancellationToken));

        using var caller = new CancellationTokenSource();
        await caller.CancelAsync();
        using var other = new DecompressionGuardStream(Bytes(10), 10, ArchiveLimits.Default, new DecompressionTotals());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ReadOnceAsync(other, buffer, caller.Token));

        await owner.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ReadOnceAsync(guard, buffer, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Totals_AreThreadSafe()
    {
        var totals = new DecompressionTotals();
        var limits = new ArchiveLimits { RatioCheckAfterBytes = long.MaxValue };
        Parallel.For(0, 32, _ =>
        {
            using var guard = new DecompressionGuardStream(Bytes(100_000), 100_000, limits, totals);
            Drain(guard, 997);
        });
        Assert.Equal(32 * 100_000L, totals.Bytes);
    }

    [Fact]
    public void IsReadOnlyAndForwardOnly()
    {
        using var guard = new DecompressionGuardStream(Bytes(10), 10, ArchiveLimits.Default, new DecompressionTotals());
        Assert.True(guard.CanRead);
        Assert.False(guard.CanSeek);
        Assert.False(guard.CanWrite);
        Assert.Throws<NotSupportedException>(() => guard.Length);
        Assert.Throws<NotSupportedException>(() => guard.Position = 0);
        Assert.Throws<NotSupportedException>(() => guard.Seek(0, SeekOrigin.Begin));
        Assert.Throws<NotSupportedException>(() => guard.SetLength(0));
        Assert.Throws<NotSupportedException>(() => guard.Write(new byte[1], 0, 1));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void Dispose_ClosesTheInnerStreamUnlessLeftOpen(bool leaveOpen, bool expectDisposed)
    {
        var inner = Bytes(10);
        new DecompressionGuardStream(inner, 10, ArchiveLimits.Default, new DecompressionTotals(), leaveOpen).Dispose();
        Assert.Equal(expectDisposed, !inner.CanRead);
    }

    [Fact]
    public void LimitException_IsAnIOException_WithoutContent()
    {
        var limits = new ArchiveLimits { MaxEntryBytes = 1, RatioCheckAfterBytes = long.MaxValue };
        using var guard = new DecompressionGuardStream(Bytes(10), 10, limits, new DecompressionTotals());
        IOException ex = Assert.ThrowsAny<IOException>(() => Drain(guard));
        Assert.IsType<ArchiveLimitExceededException>(ex);
    }

    [Fact]
    public void NullArguments_Throw()
    {
        Assert.Throws<ArgumentNullException>(() => new DecompressionGuardStream(null!, 1, ArchiveLimits.Default, new DecompressionTotals()));
        Assert.Throws<ArgumentNullException>(() => new DecompressionGuardStream(Bytes(1), 1, null!, new DecompressionTotals()));
        Assert.Throws<ArgumentNullException>(() => new DecompressionGuardStream(Bytes(1), 1, ArchiveLimits.Default, null!));
    }

    [Fact]
    public void DefaultLimits_AreTheDocumentedValues()
    {
        ArchiveLimits limits = ArchiveLimits.Default;
        Assert.Equal(200_000, limits.MaxEntries);
        Assert.Equal(64 * MiB, limits.MaxCentralDirectoryBytes);
        Assert.Equal(256 * MiB, limits.MaxEntryBytes);
        Assert.Equal(1024 * MiB, limits.MaxTotalBytes);
        Assert.Equal(200, limits.MaxCompressionRatio);
        Assert.Equal(4 * MiB, limits.RatioCheckAfterBytes);
        Assert.Equal(1024, limits.MaxNameLength);
        Assert.Equal(64, limits.MaxPathDepth);
        Assert.Equal(0, limits.MaxNesting); // archives in archives are never opened
    }
}
