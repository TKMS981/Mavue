using Mavue.Core.Viewing;
using Mavue.QuickView.Diagnostics;
using Mavue.QuickView.Host.Content;

namespace Mavue.QuickView.Host.Performance;

/// <summary>
/// Prepares what the user is likely to look at next, only while Quick View is visible and only after the current item
/// is on screen: first within the item (e.g. neighboring pages), then the neighbor files (next first), each by the
/// content its file routes to. Bounded by <see cref="PreviewResourcePolicy"/>; cancelled by any navigation or by
/// closing. Failures are ignored (prefetch is an optimization; neighbors may be broken or hostile files).
/// </summary>
internal sealed class NeighborPrefetcher : IDisposable
{
    private readonly PreviewContentSet _contents;
    private readonly ResidentResources _resources;
    private readonly QuickViewTimeline _timeline;
    private readonly Func<Task<(string? Previous, string? Next)>> _neighbors;
    private readonly Func<IPreviewContent?> _active;
    private readonly bool _pdfDocumentView;
    private CancellationTokenSource? _prefetch;

    /// <param name="neighbors">Previous and next item (the multi-selection, or Explorer's view around the focused item).</param>
    /// <param name="active">The content showing the current item.</param>
    /// <param name="pdfDocumentView">PDFs open in the document view (routing, see <see cref="PreviewContentRouting"/>).</param>
    public NeighborPrefetcher(PreviewContentSet contents, ResidentResources resources, QuickViewTimeline timeline, Func<Task<(string? Previous, string? Next)>> neighbors, Func<IPreviewContent?> active, bool pdfDocumentView)
    {
        _contents = contents;
        _resources = resources;
        _timeline = timeline;
        _neighbors = neighbors;
        _active = active;
        _pdfDocumentView = pdfDocumentView;
    }

    public void Cancel() => _prefetch?.Cancel();

    public async void Start(uint areaWidth, uint areaHeight)
    {
        _prefetch?.Cancel();
        _prefetch?.Dispose();
        _prefetch = new CancellationTokenSource();
        CancellationToken cancellation = _prefetch.Token;
        try
        {
            if (_active() is IPrefetchingContent own)
            {
                await own.PrefetchWithinAsync(areaWidth, areaHeight, cancellation);
            }

            PreviewResourcePolicy policy = _resources.Policy;
            if (policy.PrefetchNeighbors <= 0)
            {
                return;
            }

            (string? previous, string? next) = await _neighbors();
            foreach (string path in PreviewPrefetchPlan.NeighborOrder(policy, previous, next))
            {
                cancellation.ThrowIfCancellationRequested();
                FileFacts facts = await Task.Run(() => FileFacts.Read(path), cancellation);
                if (!PreviewPrefetchPlan.MayPrefetch(facts, policy))
                {
                    continue;
                }

                PreviewContentKind kind = PreviewContentRouting.Choose(facts, _pdfDocumentView);
                if (PreviewPrefetchPlan.PreparesNeighbors(kind) && _contents[kind] is IPrefetchingContent content)
                {
                    await content.PrefetchAsync(path, facts, areaWidth, areaHeight, cancellation);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Superseded by navigation or closing.
        }
        catch (Exception ex)
        {
            _timeline.Mark(0, "prefetch-error", QuickViewTimeline.Now, new Dictionary<string, object?> { ["type"] = ex.GetType().Name });
        }
    }

    public void Dispose()
    {
        _prefetch?.Cancel();
        _prefetch?.Dispose();
        _prefetch = null;
    }
}
