namespace Mavue.Core.Archives;

/// <summary>
/// Resource limits for previewing archives without extracting them (docs/ARCHITECTURE.md §4.7.5). The listing comes
/// from the central directory only; entries are decompressed one at a time, on demand, into memory, and every
/// decompressed byte is counted against these limits (sizes declared in the archive are never trusted).
/// Nothing is ever written to disk, so entry names cannot escape a target folder (Zip Slip); names are still checked
/// with <see cref="ArchiveEntryName"/> for display and for any later "save" operation.
/// </summary>
public sealed record ArchiveLimits
{
    private const long MiB = 1024L * 1024;

    /// <summary>Entries listed; larger archives are refused (a huge central directory alone can exhaust memory).</summary>
    public int MaxEntries { get; init; } = 200_000;

    /// <summary>Size of the central directory read for the listing.</summary>
    public long MaxCentralDirectoryBytes { get; init; } = 64 * MiB;

    /// <summary>Decompressed bytes read from one entry (e.g. an image shown from the archive).</summary>
    public long MaxEntryBytes { get; init; } = 256 * MiB;

    /// <summary>Decompressed bytes read from one archive while it is previewed (all entries together).</summary>
    public long MaxTotalBytes { get; init; } = 1024 * MiB;

    /// <summary>Decompressed ÷ compressed size above which decompression stops (zip bombs).</summary>
    public double MaxCompressionRatio { get; init; } = 200;

    /// <summary>The ratio is checked only after this many decompressed bytes (small, well-compressed text is fine).</summary>
    public long RatioCheckAfterBytes { get; init; } = 4 * MiB;

    /// <summary>Longest entry name accepted (UTF-16 code units).</summary>
    public int MaxNameLength { get; init; } = 1024;

    /// <summary>Deepest folder nesting accepted in entry names.</summary>
    public int MaxPathDepth { get; init; } = 64;

    /// <summary>Archives inside archives are listed as files, never opened (no recursion).</summary>
    public int MaxNesting { get; init; }

    public static ArchiveLimits Default { get; } = new();
}

/// <summary>A limit of <see cref="ArchiveLimits"/> was reached; the preview stops and says so (the process carries on).</summary>
public sealed class ArchiveLimitExceededException : IOException
{
    public ArchiveLimitExceededException()
    {
    }

    public ArchiveLimitExceededException(string message)
        : base(message)
    {
    }

    public ArchiveLimitExceededException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Which limit (for diagnostics; never contains archive content or names).</summary>
    public string Limit { get; init; } = string.Empty;
}
