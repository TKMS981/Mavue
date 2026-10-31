namespace Mavue.Core.Viewing;

/// <summary>
/// How much work Quick View does ahead of the user (Adaptive Performance). Chosen automatically from the machine;
/// <see cref="High"/> keeps the limits measured on the development PC (docs/QUICKVIEW-POC.md §10).
/// </summary>
public enum PerformanceTier
{
    /// <summary>Few cores, little memory or energy saver: smallest cache, only the next item is prefetched.</summary>
    Low = 0,

    /// <summary>Mid-range machines and remote sessions.</summary>
    Balanced,

    /// <summary>Plenty of memory and cores.</summary>
    High,
}

/// <summary>What the tier decision is based on (read cheaply; no polling).</summary>
/// <param name="LogicalProcessors">Logical processors.</param>
/// <param name="TotalMemoryBytes">Installed physical memory (0 when unknown).</param>
/// <param name="EnergySaver">Windows energy saver (battery saver) is on.</param>
/// <param name="RemoteSession">The session is shown through Remote Desktop.</param>
public readonly record struct SystemResourceSnapshot(int LogicalProcessors, long TotalMemoryBytes, bool EnergySaver, bool RemoteSession);

/// <summary>
/// Upper bounds for Quick View's resident work: the decoded-preview cache, what is decoded ahead, and the size of
/// content that is animated or read whole. One instance applies at a time; values never depend on the file.
/// </summary>
/// <param name="Tier">The tier these limits belong to.</param>
/// <param name="CacheBudgetBytes">Decoded previews kept for stepping back and forth (cleared when Quick View closes).</param>
/// <param name="PrefetchNeighbors">Neighbors decoded ahead: 2 = next and previous, 1 = next only, 0 = none.</param>
/// <param name="MaxPrefetchFileBytes">Larger neighbor files are not decoded ahead.</param>
/// <param name="MaxPrefetchSourcePixels">Larger neighbor images are not decoded ahead (header size).</param>
/// <param name="MaxActualSizePrefetchPixels">The same in actual-size mode, which decodes the full source.</param>
/// <param name="MaxAnimatedGifPixels">Larger GIF canvases keep their static first frame.</param>
/// <param name="MaxWholeFileReadBytes">Content read into memory as a whole (SVG today) is refused above this.</param>
public sealed record PreviewResourcePolicy(
    PerformanceTier Tier,
    long CacheBudgetBytes,
    int PrefetchNeighbors,
    long MaxPrefetchFileBytes,
    long MaxPrefetchSourcePixels,
    long MaxActualSizePrefetchPixels,
    long MaxAnimatedGifPixels,
    long MaxWholeFileReadBytes)
{
    private const long MiB = 1024L * 1024;

    /// <summary>The measured limits of the PoC (unchanged behavior on capable machines).</summary>
    public static PreviewResourcePolicy High { get; } = new(PerformanceTier.High, 192 * MiB, 2, 64 * MiB, 40_000_000, 16_000_000, 8_000_000, 64 * MiB);

    public static PreviewResourcePolicy Balanced { get; } = new(PerformanceTier.Balanced, 128 * MiB, 2, 64 * MiB, 24_000_000, 12_000_000, 8_000_000, 64 * MiB);

    public static PreviewResourcePolicy Low { get; } = new(PerformanceTier.Low, 64 * MiB, 1, 32 * MiB, 12_000_000, 8_000_000, 4_000_000, 32 * MiB);

    public static PreviewResourcePolicy For(PerformanceTier tier) => tier switch
    {
        PerformanceTier.Low => Low,
        PerformanceTier.Balanced => Balanced,
        _ => High,
    };

    /// <summary>
    /// The tier for a machine. Low: under 6 GiB of memory, two or fewer logical processors, or energy saver on.
    /// Balanced: under 12 GiB, four or fewer logical processors, or a remote session. Otherwise High.
    /// Unknown memory (0) does not lower the tier.
    /// </summary>
    public static PerformanceTier SelectTier(SystemResourceSnapshot system)
    {
        const long GiB = 1024L * MiB;
        bool knownMemory = system.TotalMemoryBytes > 0;
        if (system.EnergySaver || (system.LogicalProcessors is > 0 and <= 2) || (knownMemory && system.TotalMemoryBytes < 6 * GiB))
        {
            return PerformanceTier.Low;
        }

        if (system.RemoteSession || (system.LogicalProcessors is > 0 and <= 4) || (knownMemory && system.TotalMemoryBytes < 12 * GiB))
        {
            return PerformanceTier.Balanced;
        }

        return PerformanceTier.High;
    }
}
