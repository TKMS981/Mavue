using System.Runtime.InteropServices;
using Mavue.Core.Formats;
using Mavue.Core.Viewing;
using Mavue.Image.Gif;
using Mavue.Image.Wic;
using Mavue.QuickView.Diagnostics;
using Mavue.QuickView.Host.Presentation;
using Mavue.QuickView.Preview;
using Mavue.QuickView.Shell;
using Mavue.Viewer;
using Mavue.Viewer.Playback;
using Mavue.Viewer.Rendering;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Mavue.QuickView.Host.Content;

/// <summary>
/// Raster images through Windows Imaging Component, with staged display: the cached shell thumbnail first, then the
/// full-quality decode exactly at the displayed size (never enlarged), swapped in when ready. Also animated GIF
/// (first frame, then a player), PDF pages rendered as images when PDFium is missing (pages with PageUp/PageDown),
/// zoom and rotation, the decoded-preview cache and neighbor prefetching.
/// </summary>
internal sealed class RasterContent : IPreviewContent, IViewTransformContent, IPagedContent, IAnimatedContent, IPrefetchingContent, IPixelSourceContent
{
    private readonly PreviewServices _s;
    private readonly IPreviewHost _host;
    private readonly SurfaceImageView _view;

    // Decoded items for instant stepping (neighbors decoded ahead, and items already shown). Cleared when Quick View
    // closes to keep idle memory low; the budget follows the performance tier.
    private readonly PreviewCache<DecodedImage> _cache;

    // PDF pages without PDFium: the current page of the shown PDF.
    private readonly PdfPageCursor _pdfPages = new();

    // Animated GIF: at most one player, stopped whenever the shown content changes.
    private GifPlayer? _gif;
    private DispatcherQueueTimer? _zoomTimer;

    public RasterContent(PreviewServices services, IPreviewHost host)
    {
        _s = services;
        _host = host;
        _view = services.ImageView;
        _cache = new PreviewCache<DecodedImage>(
            services.Resources.Policy.CacheBudgetBytes,
            d => d.Bitmap is { } b ? (long)b.PixelWidth * b.PixelHeight * 4 : 0,
            d => d.Bitmap?.Dispose());
        services.Resources.PolicyChanged += policy => _cache.BudgetBytes = policy.CacheBudgetBytes;
        services.Settings.ScaleModeChanged += _cache.Clear; // decoded for the other mode's box
    }

    public PreviewContentKind Kind => PreviewContentKind.Raster;

    /// <summary>A decoded still image is shown (not an animation, a page image or a placeholder): its file's pixels.</summary>
    public string? PixelSourcePath => _host.IsVisible && _view.Shown == SurfaceImageKind.Raster && _gif is null ? _host.CurrentPath : null;

    /// <summary>Decode/display plan for one item (see <see cref="DisplaySizing"/>).</summary>
    /// <param name="DisplayElementSize">Element size for a zoomed item (the bitmap may be smaller and stretched); null: the bitmap's own size.</param>
    private readonly record struct Plan(DecodeBox Box, (double Width, double Height)? ExpectedElementSize, bool Scrollable, double? PdfActualScale, (double Width, double Height)? DisplayElementSize = null)
    {
        public static Plan Unknown => new(new DecodeBox(1, 1), null, false, null);
    }

    public async Task ShowAsync(PreviewItem item, CancellationToken cancellation)
    {
        (long requestId, string path, FileFacts facts) = (item.RequestId, item.Path, item.Facts);
        (uint areaWidth, uint areaHeight) = (item.AreaWidth, item.AreaHeight);

        // Display box: decode exactly the pixels that will be shown (no second resample in the UI),
        // never enlarging small images; actual-size mode decodes the source size.
        _s.Window.Surface.ImageMayHaveTransparency = FileFormatKinds.MayHaveTransparency(facts.Format); // checkerboard behind it
        Plan plan = await PlanAsync(path, facts, item.ScaleMode, areaWidth, areaHeight, cancellation);
        uint viewportWidth = plan.Box.Width;
        uint viewportHeight = plan.Box.Height;

        // A zoomed item is decoded for its zoom only (the cache holds the sizes navigation needs).
        bool cacheable = facts.Access == PreviewAccess.Allowed && _s.Options.Decoder == DecoderMode.WinRt && _view.ViewZoom is null;
        int page = facts.Format == FileFormat.Pdf ? _pdfPages.Index : 0;
        PreviewKey key = PreviewKey.Create(path, facts.Length, facts.LastWriteUtc, viewportWidth, viewportHeight, page);
        if (cacheable && _cache.TryGet(key, out DecodedImage? cached) && cached?.Bitmap is not null)
        {
            await ShowFullAsync(requestId, facts, cached, "cache", plan, cancellation);
            await StartGifAsync(requestId, path, facts, plan, areaWidth, areaHeight, cancellation);
            _host.StartPrefetch(areaWidth, areaHeight);
            return;
        }

        // Cached shell thumbnail (never extracts, never hydrates cloud files).
        int thumbnailSize = (int)Math.Min(1024, Math.Max(areaWidth, areaHeight));

        // The shell thumbnail of a PDF shows its first page; other pages go straight to the rendered page.
        Task<BgraImage?> thumbnailTask = page > 0
            ? Task.FromResult<BgraImage?>(null)
            : _s.ShellThread.InvokeAsync(() => ShellThumbnail.TryGetCached(path, thumbnailSize));

        if (facts.Access == PreviewAccess.CloudPlaceholder)
        {
            _s.Window.Surface.ShowStatusOnly(_s.Strings.GetString("QuickView_Error_CloudPlaceholder"));
            _host.Display.MarkNotice(requestId); // nothing is decoded: a cached thumbnail may join the notice, "loading" may not
            await ShowThumbnailAsync(requestId, thumbnailTask, plan, null, cancellation);
            return;
        }

        if (_s.Options.Decoder == DecoderMode.Xaml && facts.Format != FileFormat.Pdf)
        {
            _s.Window.Surface.SetImageBox(null, scrollable: false); // comparison path keeps the original "fit" layout
            Task xamlThumbnail = ShowThumbnailAsync(requestId, thumbnailTask, Plan.Unknown, () => _s.Window.Surface.HasFullImage, cancellation);
            await LoadWithXamlDecoderAsync(requestId, path, facts, areaWidth, areaHeight, cancellation);
            await xamlThumbnail;
            return;
        }

        // Full quality in the background; swapped in when ready.
        Task<DecodedImage> fullTask = facts.Format == FileFormat.Pdf
            ? PdfRendering.RenderAsync(path, viewportWidth, viewportHeight, cancellation, plan.PdfActualScale, page)
            : ImageDecoding.DecodeAsync(path, viewportWidth, viewportHeight, Interpolation(_s.Options.Interpolation), PreferWic(facts), cancellation);

        Task thumbnailShown = ShowThumbnailAsync(requestId, thumbnailTask, plan, () => _s.Window.Surface.HasFullImage, cancellation);
        DecodedImage full = await fullTask;

        // Keep the work even if the user already moved on: stepping back is then instant.
        if (full.Reason is null && cacheable)
        {
            _cache.Add(key, full);
        }

        cancellation.ThrowIfCancellationRequested();

        if (full.Reason is { } reason)
        {
            string message = ReasonToResource(reason);
            _s.Window.Surface.ShowStatusOnly(_s.Strings.GetString(message));
            _host.Display.MarkMessage(requestId); // a thumbnail lookup still running must not cover or replace it
            _s.Timeline.Mark(requestId, "full-skipped", QuickViewTimeline.Now, new Dictionary<string, object?> { ["reason"] = full.Reason });

            // Not an image after all (or one that cannot be decoded): Universal Preview's card with the reason.
            await _host.ShowInfoCardAsync(item, message, cancellation);
            await thumbnailShown;
            return;
        }

        await ShowFullAsync(requestId, facts, full, full.Decoder, plan, cancellation);
        await StartGifAsync(requestId, path, facts, plan, areaWidth, areaHeight, cancellation);
        _host.StartPrefetch(areaWidth, areaHeight);
    }

    public void Reset(ContentReset scope, string reason)
    {
        StopGif();
        if (scope == ContentReset.Reload)
        {
            return;
        }

        _pdfPages.Reset(); // another file starts at its first page
        _zoomTimer?.Stop();
        _view.Reset();
        if (scope == ContentReset.Hidden)
        {
            _view.ReleaseDisplayedBitmap();
            _cache.Clear();
        }
    }

    public void Dispose()
    {
        StopGif();
        _zoomTimer?.Stop();
        _cache.Dispose();
    }

    /// <summary>Zoom applied at once; a raster image or page image is decoded again at the new size once zooming pauses.</summary>
    public void Zoom(QuickViewZoomCommand command, int wheelDelta, Point? anchor)
    {
        if (!_host.IsVisible || _host.CurrentPath is null)
        {
            return;
        }

        if (_view.Zoom(command, wheelDelta, anchor, _s.Settings.ScaleMode, _host.ImageArea(), _host.DisplayScale(), _host.CurrentRequest))
        {
            _zoomTimer ??= CreateZoomTimer();
            _zoomTimer.Stop();
            _zoomTimer.Start();
        }
    }

    /// <summary>Turns the image, GIF or page a quarter (the file is not changed) and draws it again.</summary>
    public void Rotate(int quarterTurns)
    {
        if (!_host.IsVisible || _host.CurrentPath is null || !SurfaceImageView.IsRotatable(_view.Shown))
        {
            return;
        }

        _view.Orientation = quarterTurns > 0 ? _view.Orientation.RotateClockwise() : _view.Orientation.RotateCounterClockwise();
        long id = SelectionWorker.NextRequestId();
        _s.Timeline.Mark(id, "rotate", QuickViewTimeline.Now, new Dictionary<string, object?> { ["quarterTurns"] = _view.Orientation.QuarterTurns });
        _host.MarkNavigation(id, "rotate");
        _host.ShowCurrentAgain(id);
    }

    /// <summary>Previous/next page of a PDF shown as page images. Past the first or last page nothing happens.</summary>
    public void StepPage(int delta, string source, long? inputQpc)
    {
        if (!_host.IsVisible || _host.CurrentPath is null || !_pdfPages.IsMultiPage)
        {
            return;
        }

        if (!_pdfPages.TryStep(delta))
        {
            _s.Timeline.Mark(_host.CurrentRequest, "pdf-page-edge", QuickViewTimeline.Now, new Dictionary<string, object?>
            {
                ["index"] = _pdfPages.Index,
                ["count"] = _pdfPages.Count,
                ["source"] = source,
            });
            return;
        }

        long id = SelectionWorker.NextRequestId();
        if (inputQpc is { } input)
        {
            _s.Timeline.Mark(id, "nav-input", input);
        }

        _s.Timeline.Mark(id, "pdf-page", QuickViewTimeline.Now, new Dictionary<string, object?>
        {
            ["index"] = _pdfPages.Index,
            ["count"] = _pdfPages.Count,
            ["source"] = source,
        });
        _host.MarkNavigation(id, "pdf-page");
        UpdatePageControls();
        _s.Window.Surface.ScrollToTop();
        _host.ShowCurrentAgain(id);
    }

    public void ToggleAnimation() => _gif?.TogglePause();

    /// <summary>A multi-page PDF shown as images: the next and previous pages first (page turns are the likely next step).</summary>
    public async Task PrefetchWithinAsync(uint areaWidth, uint areaHeight, CancellationToken cancellation)
    {
        if (!_pdfPages.IsMultiPage || _host.CurrentPath is not { } document)
        {
            return;
        }

        int[] pages = [_pdfPages.Index + 1, _pdfPages.Index - 1];
        int count = _pdfPages.Count;
        foreach (int neighborPage in pages.Where(p => p >= 0 && p < count))
        {
            await PrefetchPdfPageAsync(document, neighborPage, areaWidth, areaHeight, cancellation);
        }
    }

    /// <summary>Decodes a neighbor at the size it would be shown, if it is modest (header size checked before decoding).</summary>
    public async Task PrefetchAsync(string path, FileFacts facts, uint areaWidth, uint areaHeight, CancellationToken cancellation)
    {
        if (facts.Format == FileFormat.Pdf && PdfSession.IsAvailable)
        {
            return; // PDFs open in the document view, which renders on demand
        }

        ImageScaleMode scaleMode = _s.Settings.ScaleMode;
        Plan plan = await PlanAsync(path, facts, scaleMode, areaWidth, areaHeight, cancellation);
        PreviewKey key = PreviewKey.Create(path, facts.Length, facts.LastWriteUtc, plan.Box.Width, plan.Box.Height);
        PreviewResourcePolicy policy = _s.Resources.Policy;
        long pixelBudget = scaleMode == ImageScaleMode.ActualSize ? policy.MaxActualSizePrefetchPixels : policy.MaxPrefetchSourcePixels;
        if (_cache.Contains(key))
        {
            return;
        }

        DecodedImage decoded = facts.Format == FileFormat.Pdf
            ? await PdfRendering.RenderAsync(path, plan.Box.Width, plan.Box.Height, cancellation, plan.PdfActualScale)
            : await ImageDecoding.DecodeAsync(path, plan.Box.Width, plan.Box.Height, Interpolation(_s.Options.Interpolation), PreferWic(facts), cancellation, pixelBudget);
        if (decoded.Reason is null && _host.IsVisible)
        {
            _cache.Add(key, decoded);
            _s.Timeline.Mark(0, "prefetched", QuickViewTimeline.Now, new Dictionary<string, object?>
            {
                ["decodeMs"] = decoded.DecodeMilliseconds,
                ["format"] = facts.Format.ToString(),
                ["cacheBytes"] = _cache.Bytes,
                ["cacheCount"] = _cache.Count,
            });
        }
        else
        {
            decoded.Bitmap?.Dispose();
        }
    }

    private void UpdatePageControls() => _host.SetPaging(_pdfPages.IsMultiPage, _pdfPages.CanStep(-1), _pdfPages.CanStep(+1));

    private DispatcherQueueTimer CreateZoomTimer()
    {
        DispatcherQueueTimer timer = _s.Dispatcher.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(180);
        timer.IsRepeating = false;
        timer.Tick += (_, _) =>
        {
            if (_host.IsVisible && _host.CurrentPath is not null && _view.Shown is SurfaceImageKind.Raster or SurfaceImageKind.Pdf)
            {
                long id = SelectionWorker.NextRequestId();
                _host.MarkNavigation(id, "zoom-refine");
                _host.ShowCurrentAgain(id);
            }
        };
        return timer;
    }

    /// <summary>
    /// Reads the image size from the header (no pixel decoding; never for cloud placeholders, which would
    /// download) and decides the decode box and the element size for the placeholder.
    /// </summary>
    private async Task<Plan> PlanAsync(string path, FileFacts facts, ImageScaleMode mode, uint areaWidth, uint areaHeight, CancellationToken cancellation)
    {
        if (_view.ViewZoom is not null || !_view.Orientation.IsIdentity)
        {
            return await PlanViewAsync(path, facts, mode, areaWidth, areaHeight, cancellation);
        }

        double rasterization = _host.DisplayScale();
        if (facts.Format == FileFormat.Pdf)
        {
            bool actual = mode == ImageScaleMode.ActualSize;
            return new Plan(new DecodeBox(areaWidth, areaHeight), null, actual, actual ? rasterization : null);
        }

        (uint Width, uint Height)? source = facts.Access == PreviewAccess.Allowed
            ? await Task.Run(() => WicPreviewDecoder.TryReadDimensions(path), cancellation)
            : null;
        if (source is not { } size)
        {
            return new Plan(new DecodeBox(areaWidth, areaHeight), null, false, null);
        }

        DecodeBox box = DisplaySizing.DecodeBoxFor(mode, areaWidth, areaHeight, size.Width, size.Height);
        (uint expectedWidth, uint expectedHeight) = DisplaySizing.ExpectedDisplayPixels(mode, areaWidth, areaHeight, size.Width, size.Height);
        bool scrollable = mode == ImageScaleMode.ActualSize && !box.ActualSizeRefused;
        return new Plan(box, DisplaySizing.ElementSize(expectedWidth, expectedHeight, rasterization), scrollable, null);
    }

    /// <summary>
    /// Plan for an item the user zoomed or turned in the window: the factor comes from the view zoom (or the
    /// setting), the decode box is the displayed size (the source size when enlarged), and the element gets the
    /// displayed size even when the bitmap is smaller (the screen stretches it).
    /// </summary>
    private async Task<Plan> PlanViewAsync(string path, FileFacts facts, ImageScaleMode mode, uint areaWidth, uint areaHeight, CancellationToken cancellation)
    {
        double rasterization = _host.DisplayScale();
        ZoomSetting zoom = _view.ViewZoom ?? ZoomSetting.For(mode);
        ViewOrientation orientation = _view.Orientation;
        (uint swappedWidth, uint swappedHeight) = orientation.Oriented(areaWidth, areaHeight);
        if (facts.Format == FileFormat.Pdf)
        {
            if (_view.Shown != SurfaceImageKind.Pdf || _view.ShownSource.Width <= 0)
            {
                return new Plan(new DecodeBox(swappedWidth, swappedHeight), null, false, null);
            }

            (double ow, double oh) = orientation.Oriented(_view.ShownSource.Width, _view.ShownSource.Height);
            double pdfFactor = ViewerZoom.FactorFor(zoom, ow, oh, areaWidth, areaHeight, allowEnlarge: true);
            (uint pw, uint ph) = ViewerZoom.DisplaySize(ow, oh, pdfFactor);
            (double, double) pdfElement = DisplaySizing.ElementSize(pw, ph, rasterization);
            return new Plan(new DecodeBox(swappedWidth, swappedHeight), pdfElement, pw > areaWidth + 1 || ph > areaHeight + 1, rasterization * pdfFactor, pdfElement);
        }

        (uint Width, uint Height)? source = facts.Access == PreviewAccess.Allowed
            ? await Task.Run(() => WicPreviewDecoder.TryReadDimensions(path), cancellation)
            : null;
        if (source is not { } size)
        {
            return new Plan(new DecodeBox(swappedWidth, swappedHeight), null, false, null);
        }

        (uint sw, uint sh) = orientation.Oriented(size.Width, size.Height);
        double factor = ViewerZoom.FactorFor(zoom, sw, sh, areaWidth, areaHeight);
        (uint dw, uint dh) = ViewerZoom.DisplaySize(size.Width, size.Height, factor);
        (uint decodeWidth, uint decodeHeight) = ViewerZoom.DecodeSize(size.Width, size.Height, dw, dh, DisplaySizing.MaxActualSizePixels);
        (uint displayWidth, uint displayHeight) = orientation.Oriented(dw, dh);
        (double, double) element = DisplaySizing.ElementSize(displayWidth, displayHeight, rasterization);
        return new Plan(new DecodeBox(decodeWidth, decodeHeight), element, displayWidth > areaWidth + 1 || displayHeight > areaHeight + 1, null, element);
    }

    private async Task ShowFullAsync(long requestId, FileFacts facts, DecodedImage full, string decoder, Plan plan, CancellationToken cancellation)
    {
        SoftwareBitmap display = SoftwareBitmapPixels.Orient(full.Bitmap!, _view.Orientation); // a private copy (see SurfaceImageView)
        ImageSource source;
        try
        {
            source = await CreateSourceAsync(display);
            cancellation.ThrowIfCancellationRequested();
        }
        catch
        {
            display.Dispose();
            throw;
        }

        double scale = _host.DisplayScale();
        (double Width, double Height) element = plan.DisplayElementSize ?? DisplaySizing.ElementSize((uint)display.PixelWidth, (uint)display.PixelHeight, scale);
        _s.Window.Surface.SetImageBox(element, plan.Scrollable);
        _s.Window.Surface.SetFullImage(source);
        bool pdf = full.PageCount > 0;
        _view.RecordShown(
            pdf ? SurfaceImageKind.Pdf : SurfaceImageKind.Raster,
            pdf ? (full.SourceWidth * scale, full.SourceHeight * scale) : (full.SourceWidth, full.SourceHeight),
            element.Width,
            scale);
        _view.SetDisplayedBitmap(display);
        _host.Display.MarkContent(requestId);
        if (full.PageCount > 0)
        {
            _pdfPages.SetCount(full.PageCount);
            UpdatePageControls();
        }

        _host.SetInfo(PreviewInfoText.For(_s.Strings, facts, full) + (plan.Box.ActualSizeRefused ? " · " + _s.Strings.GetString("QuickView_ActualSizeTooLarge") : string.Empty));
        (uint plannedWidth, uint plannedHeight) = _host.PlannedArea;
        _s.Timeline.Mark(requestId, "full-set", QuickViewTimeline.Now, new Dictionary<string, object?>
        {
            ["sourceWidth"] = full.SourceWidth,
            ["sourceHeight"] = full.SourceHeight,
            ["decodedWidth"] = full.Bitmap!.PixelWidth,
            ["decodedHeight"] = full.Bitmap.PixelHeight,
            ["decodeMs"] = decoder == "cache" ? 0 : full.DecodeMilliseconds,
            ["decoder"] = decoder,
            ["areaWidth"] = plannedWidth,
            ["areaHeight"] = plannedHeight,
            ["layoutAreaWidth"] = _s.Window.LayoutImageAreaPixels().Width,
            ["layoutAreaHeight"] = _s.Window.LayoutImageAreaPixels().Height,
            ["dpi"] = NativeMethods.GetDpiForWindow(_s.Window.Handle),
            ["pageIndex"] = full.PageIndex,
            ["pageCount"] = full.PageCount,
        });
        await QuickViewPresence.NextFrameAsync();
        if (!cancellation.IsCancellationRequested)
        {
            _s.Timeline.Mark(requestId, "full-visible", QuickViewTimeline.Now);
        }
    }

    private async Task ShowThumbnailAsync(long requestId, Task<BgraImage?> thumbnailTask, Plan plan, Func<bool>? superseded, CancellationToken cancellation)
    {
        BgraImage? thumbnail;
        try
        {
            thumbnail = await thumbnailTask;
        }
        catch (Exception ex) when (ex is COMException or OperationCanceledException or OverflowException)
        {
            thumbnail = null;
        }

        if (cancellation.IsCancellationRequested || superseded?.Invoke() == true)
        {
            return;
        }

        if (!_host.Display.MayShowThumbnail(requestId))
        {
            // The item's own message (e.g. "unsupported") arrived first: neither the thumbnail nor "loading" replaces it.
            _s.Timeline.Mark(requestId, "thumbnail-suppressed", QuickViewTimeline.Now, new Dictionary<string, object?> { ["cached"] = thumbnail is not null });
            return;
        }

        _s.Timeline.Mark(requestId, "thumbnail", QuickViewTimeline.Now, new Dictionary<string, object?>
        {
            ["cached"] = thumbnail is not null,
            ["width"] = thumbnail?.Width ?? 0,
            ["height"] = thumbnail?.Height ?? 0,
        });
        if (thumbnail is null)
        {
            if (_host.Display.MayShowLoading(requestId))
            {
                _s.Window.Surface.SetStatus(_s.Strings.GetString("QuickView_Loading"));
            }

            return;
        }

        _s.Window.Surface.SetImageBox(plan.ExpectedElementSize, plan.Scrollable);
        _s.Window.SetThumbnail(thumbnail);
        _host.Display.MarkContent(requestId);
        await QuickViewPresence.NextFrameAsync();
        if (!cancellation.IsCancellationRequested && superseded?.Invoke() != true)
        {
            _s.Timeline.Mark(requestId, "thumbnail-visible", QuickViewTimeline.Now);
        }
    }

    /// <summary>
    /// After a GIF's first frame is on screen: if the file has several frames, replace it with an animation.
    /// The static first frame stays when the file has one frame, cannot be read as an animation, or is very large.
    /// </summary>
    private async Task StartGifAsync(long requestId, string path, FileFacts facts, Plan plan, uint areaWidth, uint areaHeight, CancellationToken cancellation)
    {
        if (facts.Format != FileFormat.Gif || facts.Access != PreviewAccess.Allowed)
        {
            return;
        }

        GifAnimationReader? reader = await GifAnimationReader.OpenAsync(path, cancellation);
        if (reader is null)
        {
            return;
        }

        // Larger canvases keep the static first frame (copying a bigger canvas every frame would load the UI thread).
        if (reader.FrameCount < 2 || (long)reader.Width * reader.Height > _s.Resources.Policy.MaxAnimatedGifPixels ||
            cancellation.IsCancellationRequested || !_host.IsVisible || _host.Display.ShownRequest != requestId)
        {
            reader.Dispose();
            return;
        }

        StopGif();
        var player = new GifPlayer(reader, (name, detail) => _s.Timeline.Mark(requestId, name, QuickViewTimeline.Now, detail), _view.Orientation);
        _gif = player;

        // The animation is sized from the logical screen (the first frame may cover only part of it).
        ImageScaleMode scaleMode = _s.Settings.ScaleMode;
        (uint displayWidth, uint displayHeight) = DisplaySizing.ExpectedDisplayPixels(scaleMode, areaWidth, areaHeight, (uint)reader.Width, (uint)reader.Height);
        bool scrollable = plan.Scrollable;
        if (_view.ViewZoom is not null || !_view.Orientation.IsIdentity)
        {
            (int ow, int oh) = _view.Orientation.Oriented(reader.Width, reader.Height);
            double factor = ViewerZoom.FactorFor(_view.ViewZoom ?? ZoomSetting.For(scaleMode), ow, oh, areaWidth, areaHeight);
            (displayWidth, displayHeight) = ViewerZoom.DisplaySize(ow, oh, factor);
            scrollable = displayWidth > areaWidth + 1 || displayHeight > areaHeight + 1;
        }

        double scale = _host.DisplayScale();
        (double Width, double Height) element = DisplaySizing.ElementSize(displayWidth, displayHeight, scale);
        _s.Window.Surface.SetImageBox(element, scrollable);
        _view.RecordShown(SurfaceImageKind.Gif, (reader.Width, reader.Height), element.Width, scale);
        _s.Window.Surface.SetFullImage(player.Source);
        _view.ReleaseDisplayedBitmap(); // the still frame's source was just replaced; free its pixels too
        player.Start();
    }

    private void StopGif()
    {
        _gif?.Dispose();
        _gif = null;
    }

    private async Task PrefetchPdfPageAsync(string path, int page, uint areaWidth, uint areaHeight, CancellationToken cancellation)
    {
        FileFacts facts = await Task.Run(() => FileFacts.Read(path), cancellation);
        if (facts.Access != PreviewAccess.Allowed || facts.Format != FileFormat.Pdf)
        {
            return;
        }

        Plan plan = await PlanAsync(path, facts, _s.Settings.ScaleMode, areaWidth, areaHeight, cancellation);
        PreviewKey key = PreviewKey.Create(path, facts.Length, facts.LastWriteUtc, plan.Box.Width, plan.Box.Height, page);
        if (_cache.Contains(key))
        {
            return;
        }

        DecodedImage decoded = await PdfRendering.RenderAsync(path, plan.Box.Width, plan.Box.Height, cancellation, plan.PdfActualScale, page);
        if (decoded.Reason is null && _host.IsVisible)
        {
            _cache.Add(key, decoded);
            _s.Timeline.Mark(0, "prefetched", QuickViewTimeline.Now, new Dictionary<string, object?>
            {
                ["decodeMs"] = decoded.DecodeMilliseconds,
                ["pdfPage"] = page,
                ["cacheBytes"] = _cache.Bytes,
                ["cacheCount"] = _cache.Count,
            });
        }
        else
        {
            decoded.Bitmap?.Dispose();
        }
    }

    /// <summary>
    /// Comparison path: XAML BitmapImage with DecodePixelWidth/Height. Decoding happens inside the XAML
    /// image pipeline once the source is in the live tree; completion is signalled by ImageOpened.
    /// </summary>
    private async Task LoadWithXamlDecoderAsync(long requestId, string path, FileFacts facts, uint viewportWidth, uint viewportHeight, CancellationToken cancellation)
    {
        long start = QuickViewTimeline.Now;
        IRandomAccessStream stream = await ImageDecoding.OpenReadAsync(path);
        try
        {
            // Header only: dimensions for the decode size and the safety check.
            (uint width, uint height) = await Task.Run(
                async () =>
                {
                    BitmapDecoder header = await BitmapDecoder.CreateAsync(stream);
                    return (header.OrientedPixelWidth, header.OrientedPixelHeight);
                },
                cancellation);
            if (PreviewSafetyPolicy.CheckDimensions(width, height) != PreviewAccess.Allowed)
            {
                _s.Window.Surface.ShowStatusOnly(_s.Strings.GetString("QuickView_Error_TooLarge"));
                _host.Display.MarkMessage(requestId);
                return;
            }

            stream.Seek(0);
            (uint fitWidth, uint fitHeight) = PreviewSizing.FitWithin(width, height, viewportWidth, viewportHeight);
            var image = new BitmapImage
            {
                DecodePixelType = DecodePixelType.Physical,
                DecodePixelWidth = (int)fitWidth,
                DecodePixelHeight = (int)fitHeight,
            };
            var opened = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            image.ImageOpened += (_, _) => opened.TrySetResult(true);
            image.ImageFailed += (_, _) => opened.TrySetResult(false);
            await image.SetSourceAsync(stream);
            _s.Window.Surface.SetFullImagePending(image);
            bool ok = await opened.Task;
            cancellation.ThrowIfCancellationRequested();
            if (!ok)
            {
                _s.Window.Surface.ShowStatusOnly(_s.Strings.GetString("QuickView_Error_Failed"));
                _host.Display.MarkMessage(requestId);
                return;
            }

            _s.Window.Surface.CommitFullImage();
            _host.SetInfo(PreviewInfoText.For(_s.Strings, facts, new DecodedImage(null, width, height, 0, "xaml-bitmapimage", 0, null)));
            _s.Timeline.Mark(requestId, "full-set", QuickViewTimeline.Now, new Dictionary<string, object?>
            {
                ["sourceWidth"] = width,
                ["sourceHeight"] = height,
                ["decodedWidth"] = fitWidth,
                ["decodedHeight"] = fitHeight,
                ["decodeMs"] = (QuickViewTimeline.Now - start) * 1000.0 / System.Diagnostics.Stopwatch.Frequency,
                ["decoder"] = "xaml-bitmapimage",
            });
            await QuickViewPresence.NextFrameAsync();
            if (!cancellation.IsCancellationRequested)
            {
                _s.Timeline.Mark(requestId, "full-visible", QuickViewTimeline.Now);
            }
        }
        finally
        {
            stream.Dispose();
        }
    }

    private static async Task<ImageSource> CreateSourceAsync(SoftwareBitmap bitmap)
    {
        var source = new SoftwareBitmapSource();
        await source.SetBitmapAsync(bitmap);
        return source;
    }

    private static string ReasonToResource(string reason) => reason switch
    {
        "no-codec" => "QuickView_Error_Unsupported",
        "too-large" => "QuickView_Error_TooLarge",
        "password" => "QuickView_Error_PdfPassword",
        _ => "QuickView_Error_Failed",
    };

    private static BitmapInterpolationMode Interpolation(string name) => name switch
    {
        "linear" => BitmapInterpolationMode.Linear,
        "cubic" => BitmapInterpolationMode.Cubic,
        "nearest" => BitmapInterpolationMode.NearestNeighbor,
        _ => BitmapInterpolationMode.Fant,
    };

    /// <summary>JPEG goes through WIC directly (measured faster); other formats were as fast or faster via WinRT.</summary>
    private bool PreferWic(FileFacts facts) => _s.Options.UseWicForJpeg && facts.Format == FileFormat.Jpeg;
}
