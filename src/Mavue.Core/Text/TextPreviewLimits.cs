using Mavue.Core.Viewing;

namespace Mavue.Core.Text;

/// <summary>
/// Upper bounds for text, code, Markdown and HTML previews (docs/ARCHITECTURE.md §4.7.12). Nothing reads a text file
/// whole: the line index is built in the background up to <see cref="MaxIndexedBytes"/> and
/// <see cref="MaxCheckpoints"/>, and only visible lines are read, decoded and colored. Markdown and HTML are read
/// whole for rendering and therefore have their own, smaller limits (above them the source is shown as text).
/// </summary>
/// <param name="MaxIndexedBytes">Lines are indexed (and shown) up to this many bytes of the file.</param>
/// <param name="MaxCheckpoints">Index entries kept (12 bytes each; one per 16 lines or per 64 KiB).</param>
/// <param name="MaxLineChars">Longer lines are cut at this many characters for display.</param>
/// <param name="MaxMarkdownBytes">Larger Markdown files are shown as source text.</param>
/// <param name="MaxHtmlBytes">Larger HTML files are shown as source text.</param>
/// <param name="MaxCachedLines">Decoded lines kept while scrolling.</param>
public sealed record TextPreviewLimits(
    long MaxIndexedBytes,
    int MaxCheckpoints,
    int MaxLineChars,
    long MaxMarkdownBytes,
    long MaxHtmlBytes,
    int MaxCachedLines)
{
    private const long MiB = 1024L * 1024;

    /// <summary>Bytes read at the start of a file to detect its encoding and whether it is text at all.</summary>
    public const int SampleBytes = 64 * 1024;

    /// <summary>Lines up to this long (in bytes) are split from the index without an extra checkpoint.</summary>
    public const int CheckpointByteSpan = 64 * 1024;

    /// <summary>One checkpoint at least every this many lines.</summary>
    public const int CheckpointLineStride = 16;

    public static TextPreviewLimits High { get; } = new(2048 * MiB, 4_000_000, 10_000, 4 * MiB, 16 * MiB, 6_000);

    public static TextPreviewLimits Balanced { get; } = new(1024 * MiB, 2_000_000, 10_000, 2 * MiB, 8 * MiB, 4_000);

    public static TextPreviewLimits Low { get; } = new(512 * MiB, 1_000_000, 5_000, 1 * MiB, 4 * MiB, 2_000);

    public static TextPreviewLimits For(PerformanceTier tier) => tier switch
    {
        PerformanceTier.Low => Low,
        PerformanceTier.Balanced => Balanced,
        _ => High,
    };
}
