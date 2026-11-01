using Mavue.Core.Viewing;

namespace Mavue.QuickView.Host.Content;

/// <summary>
/// The contents of the Quick View window, one per <see cref="PreviewContentKind"/>. Adding a file type means adding
/// a kind and its rule (<see cref="PreviewContentRouting"/>) and registering one content here.
/// </summary>
internal sealed class PreviewContentSet : IDisposable
{
    private readonly Dictionary<PreviewContentKind, IPreviewContent> _byKind;

    // Reset order matters only for diagnostics: animation stops before media (as before the split).
    private readonly IPreviewContent[] _all;

    public PreviewContentSet(PreviewServices services, IPreviewHost host)
    {
        _all =
        [
            new RasterContent(services, host),
            new MediaContent(services, host),
            new PdfDocumentContent(services, host),
            new SvgContent(services, host),
            new ArchiveContent(services, host),
            new TextContent(services, host),
            new MarkdownContent(services, host),
            new HtmlContent(services, host),
            Universal = new UniversalContent(services, host),
        ];
        _byKind = _all.ToDictionary(c => c.Kind);
        foreach (IPreviewContent content in _all)
        {
            // Only the kinds the plan names decode neighbors ahead (PreviewPrefetchPlan, unit-tested).
            System.Diagnostics.Debug.Assert(
                (content is IPrefetchingContent) == PreviewPrefetchPlan.PreparesNeighbors(content.Kind),
                $"{content.Kind}: IPrefetchingContent does not match PreviewPrefetchPlan.PreparesNeighbors");
        }
    }

    public IPreviewContent this[PreviewContentKind kind] => _byKind[kind];

    /// <summary>The information card, also the fallback for items another content cannot show.</summary>
    public UniversalContent Universal { get; }

    /// <summary>A content of the process has a window of its own open (e.g. the PDF presentation).</summary>
    public bool AnyAuxiliaryWindow => _all.Any(c => c.HasAuxiliaryWindow);

    /// <summary>Resets every content (see <see cref="IPreviewContent.Reset"/>).</summary>
    public void Reset(ContentReset scope, string reason)
    {
        foreach (IPreviewContent content in _all)
        {
            content.Reset(scope, reason);
        }
    }

    public void Dispose()
    {
        foreach (IPreviewContent content in _all)
        {
            content.Dispose();
        }
    }
}
