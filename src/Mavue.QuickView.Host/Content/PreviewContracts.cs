using Mavue.Core.Settings;
using Mavue.Core.Viewing;
using Mavue.QuickView.Diagnostics;
using Mavue.QuickView.Host.Performance;
using Mavue.QuickView.Preview;
using Microsoft.UI.Dispatching;
using Microsoft.Windows.ApplicationModel.Resources;
using Windows.Foundation;

namespace Mavue.QuickView.Host.Content;

/// <summary>One file to show: what the controller hands to the content chosen for it (docs/ARCHITECTURE.md §4.7).</summary>
/// <param name="RequestId">Request the work is logged under (timing log).</param>
/// <param name="Path">Full path of the file.</param>
/// <param name="Facts">Attributes, size and format read before any decoding.</param>
/// <param name="AreaWidth">Image area in physical pixels.</param>
/// <param name="AreaHeight">Image area in physical pixels.</param>
/// <param name="ScaleMode">The image scale from the settings when the item was loaded.</param>
internal sealed record PreviewItem(long RequestId, string Path, FileFacts Facts, uint AreaWidth, uint AreaHeight, ImageScaleMode ScaleMode);

/// <summary>What a content forgets when the controller moves on.</summary>
internal enum ContentReset
{
    /// <summary>The same item is loaded again (new size, zoom refinement, rotation, page): stop playback only.</summary>
    Reload,

    /// <summary>Another item: also forget zoom, rotation and pages, and close documents.</summary>
    NewItem,

    /// <summary>Quick View closed: also release caches and displayed pixels (idle memory).</summary>
    Hidden,
}

/// <summary>How a content reacts when the window's image area changes size or DPI.</summary>
internal enum AreaResponse
{
    /// <summary>The content laid itself out again (nothing more to do).</summary>
    Handled,

    /// <summary>The content follows the area by itself (e.g. a scrolling document view).</summary>
    Ignore,

    /// <summary>Load the current item again if the area changed by more than a rounding difference.</summary>
    ReloadIfChanged,
}

/// <summary>
/// A replaceable component that shows one kind of content (<see cref="PreviewContentKind"/>) in the Quick View window.
/// One instance per kind lives for the whole process; it holds per-item state only while its item is shown and
/// gives it up in <see cref="Reset"/>. Heavy objects (decoders, players, documents) are created on first use.
/// All members run on the UI thread. Optional abilities are separate interfaces (zoom, pages, playback, selection,
/// prefetch), so the controller routes commands without knowing file types.
/// </summary>
internal interface IPreviewContent : IDisposable
{
    PreviewContentKind Kind { get; }

    /// <summary>Shows <paramref name="item"/>. Throws <see cref="OperationCanceledException"/> when superseded.</summary>
    Task ShowAsync(PreviewItem item, CancellationToken cancellation);

    /// <summary>
    /// Stops and forgets per-item state for <paramref name="scope"/>. Called on every content (idempotent), not
    /// only the active one, so nothing of a previous item can survive a switch.
    /// </summary>
    /// <param name="reason">Diagnostics (e.g. "next-item", "hidden").</param>
    void Reset(ContentReset scope, string reason);

    /// <summary>The image area changed while this content is shown.</summary>
    AreaResponse OnAreaChanged() => AreaResponse.ReloadIfChanged;

    /// <summary>A window of this content outside Quick View is open (e.g. the PDF presentation): do not hide on its activation.</summary>
    bool HasAuxiliaryWindow => false;
}

/// <summary>Zoom and quarter turns from the window (buttons, Ctrl+wheel, keys).</summary>
internal interface IViewTransformContent
{
    void Zoom(QuickViewZoomCommand command, int wheelDelta, Point? anchor);

    void Rotate(int quarterTurns);
}

/// <summary>Pages inside one file (PageUp/PageDown from the hook or the window, wheel, page buttons).</summary>
internal interface IPagedContent
{
    void StepPage(int delta, string source, long? inputQpc);
}

/// <summary>Content that plays (pausing it before another application takes the screen).</summary>
internal interface IPlaybackContent
{
    void PauseIfPlaying();
}

/// <summary>
/// Content whose pixels the pixel inspector can read from the file (docs/ARCHITECTURE.md §4.7.8). The path is null
/// while the shown item has no readable pixels (e.g. an animation is playing, a cloud-only file, a PDF page).
/// </summary>
internal interface IPixelSourceContent
{
    string? PixelSourcePath { get; }
}

/// <summary>Content with an animation toggled by Enter (animated GIF).</summary>
internal interface IAnimatedContent
{
    void ToggleAnimation();
}

/// <summary>Content with a user selection that Ctrl+C copies instead of the file (PDF text).</summary>
internal interface ISelectionContent
{
    bool HasSelection { get; }

    Task CopySelectionAsync();
}

/// <summary>Content that can prepare neighbors ahead of navigation (bounded by <see cref="PreviewResourcePolicy"/>).</summary>
internal interface IPrefetchingContent
{
    /// <summary>Within the shown item (e.g. the next and previous page), before neighbors.</summary>
    Task PrefetchWithinAsync(uint areaWidth, uint areaHeight, CancellationToken cancellation);

    /// <summary>A neighbor file that routes to this content.</summary>
    Task PrefetchAsync(string path, FileFacts facts, uint areaWidth, uint areaHeight, CancellationToken cancellation);
}

/// <summary>
/// What contents may ask of the controller: the current request, staged-display bookkeeping, the info line,
/// page controls and re-showing the current item. Implemented by <see cref="QuickViewController"/>.
/// </summary>
internal interface IPreviewHost
{
    bool IsVisible { get; }

    string? CurrentPath { get; }

    /// <summary>Request of the item on screen (thread-safe read).</summary>
    long CurrentRequest { get; }

    /// <summary>
    /// What the current item has put on screen: contents record their content (no stale-image clearing needed) and
    /// their final messages (no later thumbnail or "loading" replaces them).
    /// </summary>
    ItemDisplayState Display { get; }

    /// <summary>Area the current item was planned for (physical pixels).</summary>
    (uint Width, uint Height) PlannedArea { get; set; }

    /// <summary>A move inside the content (a PDF page) becomes the current request.</summary>
    void AdoptRequest(long requestId);

    /// <summary>Image area of the window in physical pixels (real client size and DPI, not the last layout).</summary>
    (uint Width, uint Height) ImageArea();

    /// <summary>Physical pixels per device-independent pixel on the window's monitor.</summary>
    double DisplayScale();

    /// <summary>Sets the info line; the position in a multi-selection is prefixed.</summary>
    void SetInfo(string text);

    /// <summary>Page buttons and PageUp/PageDown interception for the shown item.</summary>
    void SetPaging(bool multiPage, bool canPrevious, bool canNext);

    void MarkNavigation(long requestId, string source);

    /// <summary>Loads the current item again under <paramref name="requestId"/> (keeps zoom, rotation and page).</summary>
    void ShowCurrentAgain(long requestId);

    /// <summary>Starts decoding neighbors (and pages) ahead, after the current item is on screen.</summary>
    void StartPrefetch(uint areaWidth, uint areaHeight);

    /// <summary>
    /// The item cannot be shown as content: Universal Preview shows its information card with
    /// <paramref name="messageResource"/> (a string resource) and becomes the active content.
    /// The caller has already recorded the message (<see cref="ItemDisplayState.MarkMessage"/>).
    /// </summary>
    Task ShowInfoCardAsync(PreviewItem item, string messageResource, CancellationToken cancellation);
}

/// <summary>Services shared by all contents (created once by the controller).</summary>
internal sealed record PreviewServices(
    QuickViewWindow Window,
    QuickViewTimeline Timeline,
    DispatcherQueue Dispatcher,
    HostOptions Options,
    ResourceLoader Strings,
    StaThread ShellThread,
    QuickViewSettingsStore Settings,
    ResidentResources Resources,
    SurfaceImageView ImageView);
