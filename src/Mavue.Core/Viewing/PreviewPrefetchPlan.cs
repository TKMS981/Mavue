namespace Mavue.Core.Viewing;

/// <summary>
/// What Quick View prepares ahead of navigation (docs/ARCHITECTURE.md §4.7.4): which neighbor files, in which order,
/// and which contents decode a neighbor at all. Pure; the Quick View host applies it after the current item is shown.
/// </summary>
public static class PreviewPrefetchPlan
{
    /// <summary>
    /// Neighbor files to prepare, in order: the next one first (the likely direction), then the previous one when
    /// the tier allows two. Missing neighbors are skipped.
    /// </summary>
    public static IReadOnlyList<string> NeighborOrder(PreviewResourcePolicy policy, string? previous, string? next)
    {
        ArgumentNullException.ThrowIfNull(policy);
        string?[] candidates = policy.PrefetchNeighbors switch
        {
            <= 0 => [],
            1 => [next],
            _ => [next, previous],
        };
        return candidates.OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>
    /// A neighbor may be prepared: a regular local file (never a cloud placeholder, which would download, or a
    /// folder) no larger than the tier allows. Its content decides the rest (e.g. image dimensions).
    /// </summary>
    public static bool MayPrefetch(FileFacts facts, PreviewResourcePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(policy);
        return facts.Access == PreviewAccess.Allowed && facts.Length <= policy.MaxPrefetchFileBytes;
    }

    /// <summary>
    /// Contents that decode a neighbor ahead: raster images only. The PDF document view renders its pages on demand,
    /// SVG is drawn by XAML when shown (Windows Imaging Component cannot decode it), media opens on demand and
    /// messages have nothing to prepare.
    /// </summary>
    public static bool PreparesNeighbors(PreviewContentKind kind) => kind == PreviewContentKind.Raster;
}
