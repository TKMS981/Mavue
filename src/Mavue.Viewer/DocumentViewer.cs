using Mavue.Core.Formats;
using Mavue.Core.Viewing;
using Mavue.Image.Gif;
using Mavue.Image.Wic;
using Mavue.Pdf;
using Mavue.Pdf.Pdfium;
using Mavue.Viewer.Controls;
using Mavue.Viewer.Playback;
using Mavue.Viewer.Rendering;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Mavue.Viewer;

/// <summary>What the viewer currently shows.</summary>
public enum ViewerContentKind
{
    None,
    Loading,
    Image,

    /// <summary>An animated GIF that is playing.</summary>
    AnimatedImage,
    Pdf,
    Video,
    Audio,

    /// <summary>A message instead of content (see <see cref="ViewerState.Message"/>).</summary>
    Message,
}

/// <summary>Texts the viewer shows; the host supplies them in the user's language.</summary>
public enum ViewerMessage
{
    Loading,
    NotAFile,
    Unsupported,
    TooLarge,
    PdfPassword,
    Failed,
    MediaFailed,
    MediaPlaying,
    MediaPaused,
    MediaEnded,
}

/// <summary>Snapshot for the host's title, toolbar and information line.</summary>
/// <param name="Path">The open file, or null.</param>
/// <param name="Facts">File metadata (null before it is read).</param>
/// <param name="Kind">What is on screen.</param>
/// <param name="SourceWidth">Image size (PDF: page size in device-independent pixels; video: picture size).</param>
/// <param name="SourceHeight">Image size (PDF: page size in device-independent pixels; video: picture size).</param>
/// <param name="PageIndex">Zero-based PDF page.</param>
/// <param name="PageCount">PDF pages, 0 otherwise.</param>
/// <param name="Duration">Video/audio length.</param>
/// <param name="Message">Set when <paramref name="Kind"/> is <see cref="ViewerContentKind.Message"/>.</param>
/// <param name="Zoom">Display pixels per source pixel (1 = actual size); 0 when the content cannot zoom.</param>
/// <param name="ZoomMode">How the zoom is chosen.</param>
/// <param name="Orientation">How the content is turned on screen.</param>
/// <param name="CanRotate">The content can be turned and mirrored.</param>
/// <param name="IsPaused">An animated GIF is paused.</param>
/// <param name="Frame">Zero-based GIF frame on screen (-1 when not an animation).</param>
/// <param name="FrameCount">Frames of an animated GIF.</param>
/// <param name="PdfLayout">How the pages of a PDF are arranged.</param>
public sealed record ViewerState(
    string? Path,
    FileFacts? Facts,
    ViewerContentKind Kind,
    uint SourceWidth = 0,
    uint SourceHeight = 0,
    int PageIndex = 0,
    int PageCount = 0,
    TimeSpan Duration = default,
    ViewerMessage? Message = null,
    double Zoom = 0,
    ZoomMode ZoomMode = ZoomMode.Fit,
    ViewOrientation Orientation = default,
    bool CanRotate = false,
    bool IsPaused = false,
    int Frame = -1,
    int FrameCount = 0,
    PdfLayoutMode PdfLayout = PdfLayoutMode.Continuous)
{
    public static readonly ViewerState Empty = new(null, null, ViewerContentKind.None);

    /// <summary>The content can be zoomed (images, GIFs, PDF pages and SVG drawings).</summary>
    public bool CanZoom => Zoom > 0;
}

/// <summary>
/// Opens one file at a time on a <see cref="ViewerSurface"/> (Mavue.App): images decoded for the area (never
/// enlarged by default), SVG drawings, animated GIFs, PDF pages, video and audio. Opening another file, closing, or
/// disposing cancels any decoding in flight, stops GIF and media playback, and releases bitmaps, players and files.
/// All members are used on the UI thread.
/// <para>
/// Zoom: a change is shown at once by stretching what is on screen (<see cref="ViewerSurface.ZoomImage"/>); shortly
/// after, the image is decoded again (or the page rendered again) at the new size so it is sharp. Raster images are
/// never decoded beyond their own pixels or <see cref="DisplaySizing.MaxActualSizePixels"/>; above that the screen
/// stretches them. Rotation and mirroring apply to the decoded pixels (the file is never changed) and reset when
/// another file opens.
/// </para>
/// </summary>
public sealed class DocumentViewer : IDisposable
{
    /// <summary>GIFs above this size keep their first frame (copying every frame would load the UI thread).</summary>
    private const long MaxAnimatedGifPixels = 8_000_000;

    /// <summary>SVG documents larger than this are not opened (they are read into memory).</summary>
    private const long MaxSvgBytes = 64L * 1024 * 1024;

    private readonly ViewerSurface _surface;
    private readonly Func<ViewerMessage, string> _text;
    private readonly DispatcherQueue _dispatcher;
    private readonly PdfPageCursor _pages = new();
    private readonly DispatcherQueueTimer _areaTimer;
    private readonly DispatcherQueueTimer _refineTimer;

    private CancellationTokenSource? _loading;
    private GifPlayer? _gif;
    private MediaSession? _media;
    private PdfDocumentSession? _pdf;

    // PDF with PDFium: the shared session (document, view, search, outline, selection), also used by Quick View.
    private readonly PdfSession _pdfSession = new();
    private PdfLayoutMode _pdfMode = PdfLayoutMode.Continuous;
    private SoftwareBitmap? _displayedBitmap;
    private IRandomAccessStream? _svgStream;
    private (uint Width, uint Height) _plannedArea;
    private ImageScaleMode _scaleMode = ImageScaleMode.FitNoUpscale;
    private ZoomSetting _zoom = ZoomSetting.Fit;
    private ViewOrientation _orientation;
    private Shown? _shown;
    private bool _disposed;
    private PlaybackTrace? _trace;

    /// <param name="surface">Where content is shown.</param>
    /// <param name="text">Localized texts for loading, errors and media states.</param>
    public DocumentViewer(ViewerSurface surface, Func<ViewerMessage, string> text)
    {
        ArgumentNullException.ThrowIfNull(surface);
        ArgumentNullException.ThrowIfNull(text);
        _surface = surface;
        _text = text;
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _areaTimer = CreateTimer(150, OnAreaSettled);
        _refineTimer = CreateTimer(180, Refine);
        _surface.AreaChanged += OnAreaChanged;
        _pdfSession.ViewChanged += () =>
        {
            if (PdfiumShown)
            {
                PublishPdf();
            }
        };
        _pdfSession.UriRequested += uri => UriRequested?.Invoke(uri);
        _pdfSession.SelectionChanged += () => TextSelectionChanged?.Invoke();
    }

    /// <summary>Kinds of zoomable content.</summary>
    private enum ShownKind
    {
        Raster,
        Gif,
        Pdf,
        Svg,
    }

    /// <summary>Raised on the UI thread whenever <see cref="State"/> changes.</summary>
    public event Action<ViewerState>? StateChanged;

    public ViewerState State { get; private set; } = ViewerState.Empty;

    /// <summary>Optional diagnostics for GIF and media playback, zoom and errors.</summary>
    public PlaybackTrace? Trace
    {
        get => _trace;
        set
        {
            _trace = value;
            _pdfSession.Trace = value;
        }
    }

    /// <summary>A link to a web address in a PDF was clicked (http, https or mailto); the host decides how to open it.</summary>
    public event Action<string>? UriRequested;

    /// <summary>
    /// Asks for the password of an encrypted PDF: (file name, the previous attempt was wrong) → the password, or null to
    /// cancel. Without a handler an encrypted PDF shows a message. Passwords are never stored or traced.
    /// </summary>
    public Func<string, bool, Task<string?>>? PasswordProvider { get; set; }

    /// <summary>The text selection of a PDF changed.</summary>
    public event Action? TextSelectionChanged;

    /// <summary>
    /// How PDF pages are arranged (single page, continuous, two pages). Applies to the open PDF and the next ones.
    /// </summary>
    public PdfLayoutMode PdfLayout
    {
        get => _pdfMode;
        set
        {
            _pdfMode = value;
            if (PdfiumShown)
            {
                _pdfSession.SetLayout(value);
                PublishPdf();
            }
        }
    }

    /// <summary>True when a PDF is shown by PDFium (text, search, links and outline are available).</summary>
    public bool HasPdfText => PdfiumShown;

    /// <summary>True when text of the PDF is selected.</summary>
    public bool HasTextSelection => PdfiumShown && _pdfSession.HasSelection;

    /// <summary>The PDF session (search, outline, selection, thumbnails) for the host's PDF controls.</summary>
    public PdfSession Pdf => _pdfSession;

    private bool PdfiumShown => _pdfSession.IsOpen && _surface.ShowsDocument;

    /// <summary>
    /// How an opened image or page is sized at first: fit without enlarging (default) or actual size. Changing it
    /// also applies to what is shown now.
    /// </summary>
    public ImageScaleMode ScaleMode
    {
        get => _scaleMode;
        set
        {
            if (_scaleMode != value)
            {
                _scaleMode = value;
                SetZoom(ZoomSetting.For(value));
            }
        }
    }

    /// <summary>True when a multi-page PDF is shown and a step by <paramref name="delta"/> pages is possible.</summary>
    public bool CanStepPage(int delta) => State.Kind == ViewerContentKind.Pdf && (PdfiumShown ? _pdfSession.CanStepPage(delta) : _pages.CanStep(delta));

    /// <summary>True when a video or audio file is open.</summary>
    public bool HasMedia => _media is not null;

    /// <summary>True when an animated GIF is playing or paused.</summary>
    public bool HasAnimation => _gif is not null;

    /// <summary>Opens <paramref name="path"/>, replacing whatever was shown (first page for a PDF).</summary>
    public Task OpenAsync(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ObjectDisposedException.ThrowIf(_disposed, this);
        _pages.Reset();
        _surface.ResetWheel();
        StopPlayback();
        ClosePdf();
        ClosePdfium();
        _surface.ClearMedia();
        _surface.SetStatus(string.Empty);
        _zoom = ZoomSetting.For(_scaleMode);
        _orientation = ViewOrientation.Identity;
        _shown = null;
        _refineTimer.Stop();
        Publish(new ViewerState(path, null, ViewerContentKind.Loading));
        CancellationToken cancellation = BeginLoad();
        _ = ClearStaleImageAsync(cancellation);
        return LoadAsync(path, cancellation);
    }

    /// <summary>Turns the PDF page by <paramref name="delta"/>; returns false when there is no such page.</summary>
    public Task<bool> StepPageAsync(int delta)
    {
        if (PdfiumShown)
        {
            return Task.FromResult(_pdfSession.StepPage(delta));
        }

        return CanStepPage(delta) ? GoToPageAsync(_pages.Index + delta) : Task.FromResult(false);
    }

    /// <summary>Shows page <paramref name="pageIndex"/> (zero-based) of the PDF; false when there is no such page.</summary>
    public async Task<bool> GoToPageAsync(int pageIndex)
    {
        if (PdfiumShown)
        {
            if (pageIndex < 0 || pageIndex >= _pdfSession.PageCount)
            {
                return false;
            }

            _pdfSession.GoToPage(pageIndex);
            return true;
        }

        if (State.Kind != ViewerContentKind.Pdf || State.Path is not { } path || State.Facts is not { } facts ||
            pageIndex < 0 || pageIndex >= _pages.Count || pageIndex == _pages.Index)
        {
            return false;
        }

        _pages.TryStep(pageIndex - _pages.Index);
        CancellationToken cancellation = BeginLoad();
        await GuardAsync(() => ShowPdfPageAsync(path, facts, scrollToTop: true, cancellation), cancellation);
        return true;
    }

    /// <summary>
    /// Renders a thumbnail of page <paramref name="pageIndex"/> of the open PDF that fits
    /// <paramref name="maxWidth"/> × <paramref name="maxHeight"/> pixels; null when no PDF is open.
    /// </summary>
    public async Task<SoftwareBitmap?> RenderPageThumbnailAsync(int pageIndex, uint maxWidth, uint maxHeight, CancellationToken cancellation)
    {
        if (_pdfSession.IsOpen)
        {
            return await _pdfSession.RenderThumbnailAsync(pageIndex, maxWidth, maxHeight, cancellation);
        }

        if (_pdf is not { } pdf || pageIndex < 0 || pageIndex >= pdf.PageCount)
        {
            return null;
        }

        try
        {
            (double width, double height) = await pdf.PageSizeAsync(pageIndex, cancellation);
            double scale = Math.Min(maxWidth / Math.Max(1, width), maxHeight / Math.Max(1, height));
            (uint w, uint h) = ViewerZoom.DisplaySize(Math.Max(1, width), Math.Max(1, height), scale);
            DecodedImage page = await pdf.RenderAsync(pageIndex, w, h, ViewOrientation.Identity, cancellation);
            return page.Bitmap;
        }
        catch (ObjectDisposedException)
        {
            return null; // the document closed meanwhile
        }
    }

    /// <summary>
    /// The mouse wheel over the surface: turns the PDF page once a notch has accumulated (at the edges when the page
    /// scrolls). Returns true when the wheel was used for a page turn.
    /// </summary>
    public bool HandleWheel(int wheelDelta)
    {
        if (PdfiumShown)
        {
            return _pdfSession.HandleWheel(wheelDelta);
        }

        if (State.Kind != ViewerContentKind.Pdf || !_pages.IsMultiPage || _surface.WheelPageStep(wheelDelta) is not { } step)
        {
            return false;
        }

        _ = StepPageAsync(step);
        return true;
    }

    /// <summary>Play/pause, seek, volume for the open video or audio file; false when none is open.</summary>
    public bool Execute(MediaCommand command)
    {
        if (_media is not { } media)
        {
            return false;
        }

        (TimeSpan position, double volume) = media.Execute(command);
        Trace?.Invoke("media-command", new Dictionary<string, object?>
        {
            ["command"] = command.ToString(),
            ["positionMs"] = Math.Round(position.TotalMilliseconds),
            ["volume"] = Math.Round(volume, 2),
            ["player"] = media.Id,
        });
        return true;
    }

    /// <summary>Pauses a playing video or audio file (e.g. before handing the file to another app).</summary>
    public void PauseMedia()
    {
        if (_media is { IsPlaying: true })
        {
            Execute(MediaCommand.TogglePlay);
        }
    }

    /// <summary>Pauses or resumes an animated GIF; false when none is shown.</summary>
    public bool ToggleAnimation()
    {
        if (_gif is not { } gif)
        {
            return false;
        }

        gif.TogglePause();
        return true;
    }

    /// <summary>Pauses an animated GIF and shows the next (+1) or previous (-1) frame; false when none is shown.</summary>
    public bool StepFrame(int delta)
    {
        if (_gif is not { } gif)
        {
            return false;
        }

        _ = gif.StepAsync(delta);
        return true;
    }

    /// <summary>Zooms in one step (keyboard, toolbar), around the center or <paramref name="anchor"/>.</summary>
    public void ZoomIn(Point? anchor = null) => ZoomTo(ViewerZoom.StepIn(State.Zoom), anchor);

    /// <summary>Zooms out one step.</summary>
    public void ZoomOut(Point? anchor = null) => ZoomTo(ViewerZoom.StepOut(State.Zoom), anchor);

    /// <summary>Ctrl + mouse wheel: zooms around the pointer.</summary>
    public void ZoomWheel(int wheelDelta, Point anchor) => ZoomTo(ViewerZoom.Wheel(State.Zoom, wheelDelta), anchor);

    /// <summary>Shows the content at <paramref name="factor"/> (1 = actual size).</summary>
    public void ZoomTo(double factor, Point? anchor = null) => ApplyZoom(new ZoomSetting(ZoomMode.Custom, factor), anchor, keepWhenNotShown: false);

    /// <summary>Fit, fit width or a factor; applies to what is shown now, or to the file being opened (until another file opens).</summary>
    public void SetZoom(ZoomSetting setting) => ApplyZoom(setting, null, keepWhenNotShown: true);

    /// <summary>Turns the content a quarter clockwise (+1) or counter-clockwise (-1).</summary>
    public void Rotate(int quarterTurns)
    {
        if (PdfiumShown)
        {
            _pdfSession.Rotate(quarterTurns);
            return;
        }

        Orient(quarterTurns > 0 ? _orientation.RotateClockwise() : _orientation.RotateCounterClockwise());
    }

    public void FlipHorizontal() => Orient(_orientation.FlipHorizontal());

    public void FlipVertical() => Orient(_orientation.FlipVertical());

    /// <summary>Moves zoomed content by keyboard; false when it does not move (not zoomed, or at the edge).</summary>
    public bool Scroll(double dx, double dy) => PdfiumShown ? _pdfSession.Scroll(dx, dy) : _surface.ScrollBy(dx, dy);

    /// <summary>Stops playback, releases the file and clears the surface.</summary>
    public void Close()
    {
        _loading?.Cancel();
        _pages.Reset();
        ClearContent();
        ClosePdf();
        ClosePdfium();
        _surface.SetStatus(string.Empty);
        Publish(ViewerState.Empty);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Close();
        _disposed = true;
        _areaTimer.Stop();
        _refineTimer.Stop();
        _surface.AreaChanged -= OnAreaChanged;
        _loading?.Dispose();
        _loading = null;
    }

    private DispatcherQueueTimer CreateTimer(int milliseconds, Action tick)
    {
        DispatcherQueueTimer timer = _dispatcher.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(milliseconds);
        timer.IsRepeating = false;
        timer.Tick += (_, _) => tick();
        return timer;
    }

    private CancellationToken BeginLoad()
    {
        _loading?.Cancel();
        _loading?.Dispose();
        _loading = new CancellationTokenSource();
        return _loading.Token;
    }

    private Task LoadAsync(string path, CancellationToken cancellation) => GuardAsync(
        async () =>
        {
            FileFacts facts = await Task.Run(() => FileFacts.Read(path), cancellation);
            cancellation.ThrowIfCancellationRequested();
            Publish(State with { Facts = facts });
            if (facts.Access == PreviewAccess.NotAFile)
            {
                ShowMessage(ViewerMessage.NotAFile);
                return;
            }

            // Unlike Quick View, opening a cloud-only file here is an explicit request: reading it downloads it.
            if (FileFormatKinds.IsMedia(facts.Format))
            {
                await StartMediaAsync(path, facts, cancellation);
            }
            else if (facts.Format == FileFormat.Pdf && PdfiumLibrary.IsAvailable)
            {
                await ShowPdfiumAsync(path, facts, cancellation);
            }
            else if (facts.Format == FileFormat.Pdf)
            {
                await ShowPdfPageAsync(path, facts, scrollToTop: true, cancellation); // Windows.Data.Pdf fallback
            }
            else if (facts.Format == FileFormat.Svg)
            {
                await ShowSvgAsync(path, facts, cancellation);
            }
            else
            {
                await ShowImageAsync(path, facts, cancellation);
            }
        },
        cancellation);

    /// <summary>Runs one load step; cancellation is silent, any other failure shows a message (never crashes the app).</summary>
    private async Task GuardAsync(Func<Task> work, CancellationToken cancellation)
    {
        try
        {
            await work();
        }
        catch (OperationCanceledException)
        {
            // Another file or page was requested, or the viewer closed.
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Malformed or hostile files must never take the application down.
            Trace?.Invoke("error", new Dictionary<string, object?> { ["type"] = ex.GetType().Name, ["hresult"] = ex.HResult });
            if (!cancellation.IsCancellationRequested)
            {
                ShowMessage(ViewerMessage.Failed);
            }
        }
    }

    /// <summary>
    /// If the new file has produced nothing shortly after opening, remove the previous file's image and say "loading",
    /// so a stale picture is never presented under the new file's name (switching between fast files does not flicker).
    /// </summary>
    private async Task ClearStaleImageAsync(CancellationToken cancellation)
    {
        try
        {
            await Task.Delay(150, cancellation);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        // Only the previous picture goes; a video or audio file of the new item may already be opening.
        if (State.Kind == ViewerContentKind.Loading && _media is null)
        {
            _surface.ClearImages();
            ReplaceDisplayedBitmap(null);
            _surface.SetStatus(_text(ViewerMessage.Loading));
        }
    }

    private async Task ShowImageAsync(string path, FileFacts facts, CancellationToken cancellation)
    {
        (uint areaWidth, uint areaHeight) = await AreaAsync(cancellation);
        (uint Width, uint Height)? header = await Task.Run(() => WicPreviewDecoder.TryReadDimensions(path), cancellation);
        ZoomSetting zoom = _zoom;
        ViewOrientation orientation = _orientation;

        // Decode box (upright): the displayed size when reduced, the source size when enlarged, within the budget.
        (uint decodeWidth, uint decodeHeight) = (areaWidth, areaHeight);
        if (header is { } source)
        {
            double factor = Factor(zoom, source.Width, source.Height, orientation, areaWidth, areaHeight, allowEnlarge: false);
            (uint w, uint h) = ViewerZoom.DisplaySize(source.Width, source.Height, factor);
            (decodeWidth, decodeHeight) = ViewerZoom.DecodeSize(source.Width, source.Height, w, h, DisplaySizing.MaxActualSizePixels);
        }
        else if (orientation.SwapsAxes)
        {
            (decodeWidth, decodeHeight) = (areaHeight, areaWidth);
        }

        DecodedImage decoded = await ImageDecoding.DecodeAsync(
            path, decodeWidth, decodeHeight, BitmapInterpolationMode.Fant, preferWic: facts.Format == FileFormat.Jpeg, cancellation);
        if (decoded.Bitmap is not { } bitmap)
        {
            ShowDecodeFailure(decoded.Reason);
            return;
        }

        bitmap = await OrientAsync(bitmap, orientation, cancellation);
        double shownFactor = Factor(zoom, decoded.SourceWidth, decoded.SourceHeight, orientation, areaWidth, areaHeight, allowEnlarge: false);
        var shown = new Shown(ShownKind.Raster, decoded.SourceWidth, decoded.SourceHeight);
        if (!await ShowBitmapAsync(bitmap, shown, shownFactor, cancellation))
        {
            return;
        }

        Publish(State with
        {
            Kind = ViewerContentKind.Image,
            SourceWidth = decoded.SourceWidth,
            SourceHeight = decoded.SourceHeight,
            Zoom = shownFactor,
            ZoomMode = zoom.Mode,
            Orientation = orientation,
            CanRotate = true,
            IsPaused = false,
            Frame = -1,
            FrameCount = 0,
        });
        if (facts.Format == FileFormat.Gif && _gif is null)
        {
            await StartGifAsync(path, shownFactor, cancellation);
        }
    }

    private async Task ShowPdfPageAsync(string path, FileFacts facts, bool scrollToTop, CancellationToken cancellation)
    {
        (uint areaWidth, uint areaHeight) = await AreaAsync(cancellation);
        if (_pdf is null || !string.Equals(_pdf.Path, path, StringComparison.OrdinalIgnoreCase))
        {
            ClosePdf();
            PdfDocumentSession? opened = await PdfDocumentSession.OpenAsync(path, cancellation);
            if (cancellation.IsCancellationRequested)
            {
                opened?.Dispose();
                cancellation.ThrowIfCancellationRequested();
            }

            if (opened is null)
            {
                ShowMessage(ViewerMessage.PdfPassword);
                return;
            }

            _pdf = opened;
        }

        PdfDocumentSession pdf = _pdf;
        _pages.SetCount(pdf.PageCount);
        int pageIndex = _pages.Index;
        ZoomSetting zoom = _zoom;
        ViewOrientation orientation = _orientation;

        // 100 % is the page at 96 dpi on this monitor; a page is vector, so "fit" may enlarge it.
        (double pageWidth, double pageHeight) = await pdf.PageSizeAsync(pageIndex, cancellation);
        double scale = _surface.PixelScale;
        uint sourceWidth = (uint)Math.Max(1, Math.Round(pageWidth * scale));
        uint sourceHeight = (uint)Math.Max(1, Math.Round(pageHeight * scale));
        double factor = Factor(zoom, sourceWidth, sourceHeight, orientation, areaWidth, areaHeight, allowEnlarge: true);
        (uint displayWidth, uint displayHeight) = ViewerZoom.DisplaySize(sourceWidth, sourceHeight, factor);
        (uint renderWidth, uint renderHeight) = ViewerZoom.DecodeSize(displayWidth, displayHeight, displayWidth, displayHeight, DisplaySizing.MaxActualSizePixels);
        DecodedImage page = await pdf.RenderAsync(pageIndex, renderWidth, renderHeight, orientation, cancellation);
        if (page.Bitmap is not { } bitmap)
        {
            ShowDecodeFailure(page.Reason);
            return;
        }

        if (!await ShowBitmapAsync(bitmap, new Shown(ShownKind.Pdf, sourceWidth, sourceHeight), factor, cancellation))
        {
            return;
        }

        if (scrollToTop)
        {
            _surface.ScrollToTop();
        }

        Publish(State with
        {
            Facts = facts,
            Kind = ViewerContentKind.Pdf,
            SourceWidth = (uint)Math.Round(pageWidth),
            SourceHeight = (uint)Math.Round(pageHeight),
            PageIndex = page.PageIndex,
            PageCount = page.PageCount,
            Zoom = factor,
            ZoomMode = zoom.Mode,
            Orientation = orientation,
            CanRotate = true,
        });
    }

    /// <summary>
    /// SVG through XAML's SvgImageSource (Direct2D): drawn at the size of the element, so zooming stays sharp without
    /// decoding again. 100 % is the drawing's own size at 96 dpi on this monitor. No scripts and no external files are
    /// loaded. Turning is not offered (there are no pixels to turn).
    /// </summary>
    private async Task ShowSvgAsync(string path, FileFacts facts, CancellationToken cancellation)
    {
        (uint areaWidth, uint areaHeight) = await AreaAsync(cancellation);
        if (facts.Length > MaxSvgBytes)
        {
            ShowMessage(ViewerMessage.TooLarge);
            return;
        }

        (double Width, double Height)? intrinsic = await Task.Run(() => SvgDimensions.TryRead(path), cancellation);
        if (intrinsic is not { } size)
        {
            ShowMessage(ViewerMessage.Failed);
            return;
        }

        byte[] content = await File.ReadAllBytesAsync(path, cancellation);
        var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(content.AsBuffer());
        stream.Seek(0);
        var svg = new SvgImageSource();
        SvgImageSourceLoadStatus status = await svg.SetSourceAsync(stream);
        if (cancellation.IsCancellationRequested)
        {
            stream.Dispose();
            cancellation.ThrowIfCancellationRequested();
        }

        if (status != SvgImageSourceLoadStatus.Success)
        {
            stream.Dispose();
            Trace?.Invoke("svg-failed", new Dictionary<string, object?> { ["status"] = status.ToString() });
            ShowMessage(ViewerMessage.Failed);
            return;
        }

        double scale = _surface.PixelScale;
        uint sourceWidth = (uint)Math.Max(1, Math.Round(size.Width * scale));
        uint sourceHeight = (uint)Math.Max(1, Math.Round(size.Height * scale));
        _orientation = ViewOrientation.Identity;
        double factor = Factor(_zoom, sourceWidth, sourceHeight, ViewOrientation.Identity, areaWidth, areaHeight, allowEnlarge: false);
        StopGif();
        _shown = new Shown(ShownKind.Svg, sourceWidth, sourceHeight);
        PresentBox(factor);
        _surface.SetFullImage(svg);
        ReplaceDisplayedBitmap(null);
        ReplaceSvgStream(stream);
        Publish(State with
        {
            Kind = ViewerContentKind.Image,
            SourceWidth = (uint)Math.Round(size.Width),
            SourceHeight = (uint)Math.Round(size.Height),
            Zoom = factor,
            ZoomMode = _zoom.Mode,
            Orientation = ViewOrientation.Identity,
            CanRotate = false,
        });
    }

    /// <summary>A PDF with PDFium: the shared session opens it in the background and shows it in its document view.</summary>
    private async Task ShowPdfiumAsync(string path, FileFacts facts, CancellationToken cancellation)
    {
        await AreaAsync(cancellation); // the view needs a laid-out surface
        StopPlayback();
        _surface.ClearMedia();
        _surface.ClearImages();
        ReplaceDisplayedBitmap(null);
        ReplaceSvgStream(null);
        _shown = null;
        _surface.ShowDocument(_pdfSession.View);
        _surface.SetStatus(_text(ViewerMessage.Loading));
        PdfSessionOpenResult result = await _pdfSession.OpenAsync(path, _zoom, _pdfMode, cancellation);
        bool wrong = false;
        while (result == PdfSessionOpenResult.Password && PasswordProvider is { } provider)
        {
            _surface.SetStatus(_text(ViewerMessage.PdfPassword));
            string? password = await provider(Path.GetFileName(path), wrong);
            cancellation.ThrowIfCancellationRequested();
            if (password is null)
            {
                Trace?.Invoke("pdf-password", new Dictionary<string, object?> { ["result"] = "cancelled" });
                break;
            }

            _surface.SetStatus(_text(ViewerMessage.Loading));
            result = await _pdfSession.OpenAsync(path, _zoom, _pdfMode, cancellation, password);
            wrong = result == PdfSessionOpenResult.Password;
            Trace?.Invoke("pdf-password", new Dictionary<string, object?> { ["result"] = wrong ? "wrong" : result.ToString() });
        }

        switch (result)
        {
            case PdfSessionOpenResult.Opened:
                _surface.SetStatus(string.Empty);
                Publish(State with { Facts = facts });
                PublishPdf();
                break;
            case PdfSessionOpenResult.Cancelled:
                cancellation.ThrowIfCancellationRequested();
                break;
            default:
                ShowMessage(result == PdfSessionOpenResult.Password ? ViewerMessage.PdfPassword : ViewerMessage.Failed);
                break;
        }
    }

    /// <summary>Publishes the PDF view's page, zoom and orientation.</summary>
    private void PublishPdf()
    {
        PdfSession session = _pdfSession;
        if (session.PageCount == 0)
        {
            return;
        }

        int page = Math.Clamp(session.CurrentPage, 0, session.PageCount - 1);
        (double width, double height) = session.PageSizeDips(page);
        _pages.SetCount(session.PageCount);
        Publish(State with
        {
            Kind = ViewerContentKind.Pdf,
            SourceWidth = (uint)Math.Round(width),
            SourceHeight = (uint)Math.Round(height),
            PageIndex = page,
            PageCount = session.PageCount,
            Zoom = session.Factor,
            ZoomMode = session.Zoom.Mode,
            Orientation = new ViewOrientation(session.QuarterTurns, false),
            CanRotate = true,
            PdfLayout = session.Layout,
            Message = null,
        });
    }

    /// <summary>Closes the PDFium document; the view lets go first and the file is released in the background.</summary>
    private void ClosePdfium()
    {
        _pdfSession.Close();
        _surface.ClearDocument();
    }

    private void ShowDecodeFailure(string? reason) => ShowMessage(reason switch
    {
        "no-codec" => ViewerMessage.Unsupported,
        "too-large" => ViewerMessage.TooLarge,
        "password" => ViewerMessage.PdfPassword,
        _ => ViewerMessage.Failed,
    });

    /// <summary>Puts a decoded (already turned) bitmap on screen at <paramref name="factor"/> of <paramref name="shown"/>'s source.</summary>
    private async Task<bool> ShowBitmapAsync(SoftwareBitmap bitmap, Shown shown, double factor, CancellationToken cancellation)
    {
        var source = new SoftwareBitmapSource();
        try
        {
            await source.SetBitmapAsync(bitmap);
            cancellation.ThrowIfCancellationRequested();
        }
        catch
        {
            source.Dispose();
            bitmap.Dispose();
            throw;
        }

        StopGif();
        ReplaceSvgStream(null);
        _shown = shown with { BitmapWidth = (uint)bitmap.PixelWidth, BitmapHeight = (uint)bitmap.PixelHeight };
        PresentBox(factor);
        _surface.SetFullImage(source);
        ReplaceDisplayedBitmap(bitmap);
        Trace?.Invoke("shown", new Dictionary<string, object?> { ["content"] = shown.Kind.ToString(), ["bitmapWidth"] = bitmap.PixelWidth, ["bitmapHeight"] = bitmap.PixelHeight, ["factor"] = Math.Round(factor, 4) });
        return true;
    }

    /// <summary>Sizes the image element for <see cref="_shown"/> at <paramref name="factor"/> (scrolling when larger than the area).</summary>
    private void PresentBox(double factor)
    {
        if (_shown is not { } shown)
        {
            return;
        }

        (uint width, uint height) = DisplayPixels(shown, factor);
        (int areaWidth, int areaHeight) = _surface.LayoutAreaPixels();
        bool scrollable = width > areaWidth + 1 || height > areaHeight + 1;
        _surface.SetImageBox(DisplaySizing.ElementSize(width, height, _surface.PixelScale), scrollable);
    }

    /// <summary>On-screen size (turned) of the shown content at <paramref name="factor"/>.</summary>
    private (uint Width, uint Height) DisplayPixels(Shown shown, double factor)
    {
        (uint width, uint height) = ViewerZoom.DisplaySize(shown.SourceWidth, shown.SourceHeight, factor);
        return (shown.Kind == ShownKind.Svg ? ViewOrientation.Identity : _orientation).Oriented(width, height);
    }

    /// <summary>After a GIF's first frame is on screen: if it has several frames, replace it with the animation.</summary>
    private async Task StartGifAsync(string path, double factor, CancellationToken cancellation)
    {
        GifAnimationReader? reader = await GifAnimationReader.OpenAsync(path, cancellation);
        if (reader is null)
        {
            return;
        }

        if (reader.FrameCount < 2 || (long)reader.Width * reader.Height > MaxAnimatedGifPixels || cancellation.IsCancellationRequested)
        {
            reader.Dispose();
            return;
        }

        StopGif();
        var player = new GifPlayer(reader, Trace, _orientation);
        _gif = player;
        player.PlaybackChanged += OnGifPlaybackChanged;

        // The animation is shown from the full canvas (the logical screen); zoom only resizes the element.
        _shown = new Shown(ShownKind.Gif, (uint)reader.Width, (uint)reader.Height);
        PresentBox(factor);
        _surface.SetFullImage(player.Source);
        ReplaceDisplayedBitmap(null); // the still frame's source was just replaced; free its pixels too
        player.Start();
        Publish(State with { Kind = ViewerContentKind.AnimatedImage, FrameCount = reader.FrameCount, Frame = 0, IsPaused = false });
    }

    private void OnGifPlaybackChanged(GifPlayer player)
    {
        if (ReferenceEquals(player, _gif))
        {
            Publish(State with { IsPaused = player.IsPaused, Frame = player.CurrentFrame });
        }
    }

    private async Task StartMediaAsync(string path, FileFacts facts, CancellationToken cancellation)
    {
        bool audio = FileFormatKinds.IsAudio(facts.Format);
        MediaSession session = await MediaSession.OpenAsync(path, audio, _dispatcher, cancellation);
        if (cancellation.IsCancellationRequested)
        {
            session.Dispose();
            return;
        }

        StopPlayback();
        _media = session;
        session.Opened += OnMediaOpened;
        session.Failed += OnMediaFailed;
        session.StateChanged += OnMediaStateChanged;
        Trace?.Invoke("media-open", new Dictionary<string, object?> { ["kind"] = audio ? "audio" : "video", ["player"] = session.Id });
        _surface.ShowMedia(session.Player);
        ReplaceDisplayedBitmap(null); // the previous file's image was just removed from the surface
        if (audio)
        {
            _surface.SetAudioLayout();
        }

        _surface.SetStatus(_text(ViewerMessage.Loading));
        session.Start();
    }

    private void OnMediaOpened(MediaSession session)
    {
        if (!ReferenceEquals(session, _media))
        {
            return;
        }

        _surface.SetStatus(string.Empty);
        if (session.HasVideo)
        {
            UpdateVideoLayout();
        }
        else
        {
            _surface.SetAudioLayout();
        }

        Trace?.Invoke("media-opened", new Dictionary<string, object?>
        {
            ["kind"] = session.HasVideo ? "video" : "audio",
            ["player"] = session.Id,
            ["width"] = session.NaturalSize.Width,
            ["height"] = session.NaturalSize.Height,
            ["durationMs"] = Math.Round(session.Duration.TotalMilliseconds),
        });
        Publish(State with
        {
            Kind = session.HasVideo ? ViewerContentKind.Video : ViewerContentKind.Audio,
            SourceWidth = session.NaturalSize.Width,
            SourceHeight = session.NaturalSize.Height,
            Duration = session.Duration,
        });
    }

    private void OnMediaFailed(MediaSession session, string error, int hresult)
    {
        if (!ReferenceEquals(session, _media))
        {
            return;
        }

        // Missing codec, unsupported container or a damaged file: say so; the application carries on.
        Trace?.Invoke("media-failed", new Dictionary<string, object?> { ["error"] = error, ["hresult"] = hresult, ["player"] = session.Id });
        StopMedia();
        ShowMessage(ViewerMessage.MediaFailed);
    }

    private void OnMediaStateChanged(MediaSession session, string state)
    {
        if (!ReferenceEquals(session, _media))
        {
            return;
        }

        ViewerMessage? text = state switch
        {
            "Playing" => ViewerMessage.MediaPlaying,
            "Paused" => ViewerMessage.MediaPaused,
            MediaStates.Ended => ViewerMessage.MediaEnded,
            _ => null,
        };
        if (text is { } message)
        {
            _surface.SetAudioState(_text(message));
        }

        Trace?.Invoke("media-state", new Dictionary<string, object?> { ["state"] = state, ["player"] = session.Id });
    }

    /// <summary>Video fits the area and is never enlarged (same rule as images).</summary>
    private void UpdateVideoLayout()
    {
        if (_media is not { HasVideo: true } media)
        {
            return;
        }

        (int areaWidth, int areaHeight) = _surface.LayoutAreaPixels();
        (uint width, uint height) = DisplaySizing.ExpectedDisplayPixels(
            ImageScaleMode.FitNoUpscale, (uint)Math.Max(1, areaWidth), (uint)Math.Max(1, areaHeight), media.NaturalSize.Width, media.NaturalSize.Height);
        (double w, double h) = DisplaySizing.ElementSize(width, height, _surface.PixelScale);
        _surface.SetVideoLayout(w, h);
        Trace?.Invoke("video-layout", new Dictionary<string, object?> { ["area"] = $"{areaWidth}x{areaHeight}", ["width"] = w, ["height"] = h });
    }

    private void ShowMessage(ViewerMessage message)
    {
        ClearContent();
        _surface.SetStatus(_text(message));
        Publish(State with { Kind = ViewerContentKind.Message, Message = message, Zoom = 0, CanRotate = false, IsPaused = false, Frame = -1, FrameCount = 0 });
    }

    /// <summary>
    /// Shows the zoom at once by resizing the element, then (after a pause in zooming) decodes or renders again at the
    /// new size so the result is sharp.
    /// </summary>
    /// <param name="keepWhenNotShown">Nothing zoomable is shown yet: remember the setting for the file being opened.</param>
    private void ApplyZoom(ZoomSetting setting, Point? anchor, bool keepWhenNotShown = false)
    {
        if (PdfiumShown)
        {
            _zoom = setting;
            _pdfSession.SetZoom(setting.Mode == ZoomMode.Custom ? setting with { Factor = ViewerZoom.Clamp(setting.Factor, 10_000, 10_000) } : setting, anchor);
            return;
        }

        if (_shown is not { } shown || !State.CanZoom)
        {
            if (keepWhenNotShown)
            {
                _zoom = setting; // a relative step (zoom in/out) means nothing before the content has a size
            }

            return;
        }

        (int areaWidth, int areaHeight) = _surface.LayoutAreaPixels();
        ViewOrientation orientation = shown.Kind == ShownKind.Svg ? ViewOrientation.Identity : _orientation;
        double factor = Factor(setting, shown.SourceWidth, shown.SourceHeight, orientation, (uint)Math.Max(1, areaWidth), (uint)Math.Max(1, areaHeight), allowEnlarge: shown.Kind == ShownKind.Pdf);
        _zoom = setting.Mode == ZoomMode.Custom ? setting with { Factor = factor } : setting;
        (uint width, uint height) = DisplayPixels(shown, factor);
        _surface.ZoomImage(DisplaySizing.ElementSize(width, height, _surface.PixelScale), anchor ?? _surface.ViewportCenter);
        Trace?.Invoke("zoom", new Dictionary<string, object?> { ["mode"] = _zoom.Mode.ToString(), ["factor"] = Math.Round(factor, 4), ["width"] = width, ["height"] = height });
        Publish(State with { Zoom = factor, ZoomMode = _zoom.Mode });
        _refineTimer.Stop();
        _refineTimer.Start();
    }

    /// <summary>After zooming stopped: a sharper bitmap when the shown one has too few (or needlessly many) pixels.</summary>
    private void Refine()
    {
        if (_shown is not { } shown || State.Path is not { } path || State.Facts is not { } facts)
        {
            return;
        }

        switch (shown.Kind)
        {
            case ShownKind.Raster:
                (uint width, uint height) = DisplayPixels(shown, State.Zoom);
                (uint sw, uint sh) = _orientation.Oriented(shown.SourceWidth, shown.SourceHeight);
                (uint wantWidth, _) = ViewerZoom.DecodeSize(sw, sh, width, height, DisplaySizing.MaxActualSizePixels);
                if (Math.Abs((long)wantWidth - shown.BitmapWidth) > 1)
                {
                    CancellationToken cancellation = BeginLoad();
                    _ = GuardAsync(() => ShowImageAsync(path, facts, cancellation), cancellation);
                }

                break;
            case ShownKind.Pdf:
                (uint pageWidth, _) = DisplayPixels(shown, State.Zoom);
                if (Math.Abs((long)pageWidth - shown.BitmapWidth) > 1)
                {
                    CancellationToken cancellation = BeginLoad();
                    _ = GuardAsync(() => ShowPdfPageAsync(path, facts, scrollToTop: false, cancellation), cancellation);
                }

                break;
        }
    }

    /// <summary>Turns or mirrors what is shown and draws it again (the file is not changed).</summary>
    private void Orient(ViewOrientation orientation)
    {
        if (!State.CanRotate)
        {
            return;
        }

        _orientation = orientation;
        Trace?.Invoke("orientation", new Dictionary<string, object?> { ["quarterTurns"] = orientation.QuarterTurns, ["mirrored"] = orientation.Mirrored });
        StopGif(); // the animation starts again with the new orientation
        Reload(scrollToTop: true);
    }

    private static async Task<SoftwareBitmap> OrientAsync(SoftwareBitmap bitmap, ViewOrientation orientation, CancellationToken cancellation)
    {
        if (orientation.IsIdentity)
        {
            return bitmap;
        }

        try
        {
            return await Task.Run(() => SoftwareBitmapPixels.Orient(bitmap, orientation), cancellation);
        }
        finally
        {
            bitmap.Dispose();
        }
    }

    /// <summary>The factor <paramref name="zoom"/> gives for a source of the given (upright) size turned by <paramref name="orientation"/>.</summary>
    private static double Factor(ZoomSetting zoom, uint sourceWidth, uint sourceHeight, ViewOrientation orientation, uint areaWidth, uint areaHeight, bool allowEnlarge)
    {
        (uint width, uint height) = orientation.Oriented(sourceWidth, sourceHeight);
        return ViewerZoom.FactorFor(zoom, width, height, areaWidth, areaHeight, allowEnlarge);
    }

    /// <summary>Stops the GIF and media players (the last picture stays until it is replaced or cleared).</summary>
    private void StopPlayback()
    {
        StopGif();
        StopMedia();
    }

    /// <summary>Stops playback, removes everything from the surface and frees the displayed bitmap.</summary>
    private void ClearContent()
    {
        ClosePdfium();
        StopPlayback();
        _surface.ClearMedia();
        _surface.ClearImages();
        ReplaceDisplayedBitmap(null);
        ReplaceSvgStream(null);
        _shown = null;
        _refineTimer.Stop();
    }

    private void StopGif()
    {
        if (_gif is { } gif)
        {
            gif.PlaybackChanged -= OnGifPlaybackChanged;
            gif.Dispose();
        }

        _gif = null;
    }

    private void StopMedia()
    {
        if (_media is not { } media)
        {
            return;
        }

        _media = null;
        media.Opened -= OnMediaOpened;
        media.Failed -= OnMediaFailed;
        media.StateChanged -= OnMediaStateChanged;
        _surface.ClearMedia();
        media.Dispose();
        Trace?.Invoke("media-stop", new Dictionary<string, object?> { ["player"] = media.Id });
    }

    private void ClosePdf()
    {
        _pdf?.Dispose();
        _pdf = null;
    }

    private void ReplaceDisplayedBitmap(SoftwareBitmap? bitmap)
    {
        SoftwareBitmap? previous = _displayedBitmap;
        _displayedBitmap = bitmap;
        if (!ReferenceEquals(previous, bitmap))
        {
            previous?.Dispose();
        }
    }

    private void ReplaceSvgStream(IRandomAccessStream? stream)
    {
        IRandomAccessStream? previous = _svgStream;
        _svgStream = stream;
        if (!ReferenceEquals(previous, stream))
        {
            previous?.Dispose();
        }
    }

    /// <summary>The content area in physical pixels; waits for the first layout when the window was just created.</summary>
    private async Task<(uint Width, uint Height)> AreaAsync(CancellationToken cancellation)
    {
        for (int frame = 0; frame < 60; frame++)
        {
            (int width, int height) = _surface.LayoutAreaPixels();
            if (width > 0 && height > 0)
            {
                _plannedArea = ((uint)width, (uint)height);
                return _plannedArea;
            }

            await NextFrameAsync();
            cancellation.ThrowIfCancellationRequested();
        }

        _plannedArea = (1, 1);
        return _plannedArea;
    }

    private void OnAreaChanged()
    {
        if (State.Path is null)
        {
            return;
        }

        _areaTimer.Stop();
        _areaTimer.Start(); // debounce: a drag-resize raises many changes
    }

    /// <summary>The window was resized or changed monitor: re-plan the current image or page if its area changed.</summary>
    private void OnAreaSettled()
    {
        if (PdfiumShown)
        {
            return; // the document view lays itself out
        }

        if (_media is not null)
        {
            UpdateVideoLayout(); // the player scales by itself; only the no-enlarge limit changes
            return;
        }

        if (State.Kind == ViewerContentKind.Loading)
        {
            // The window's first layout (or a resize) while a file is still opening: look again once it is shown.
            // Re-planning now would start a second load of a file whose kind is not known yet.
            _areaTimer.Start();
            return;
        }

        (int width, int height) = _surface.LayoutAreaPixels();
        if (Math.Abs(width - (long)_plannedArea.Width) <= 2 && Math.Abs(height - (long)_plannedArea.Height) <= 2)
        {
            return;
        }

        _plannedArea = ((uint)Math.Max(0, width), (uint)Math.Max(0, height));
        if (_zoom.Mode == ZoomMode.Custom || _shown?.Kind == ShownKind.Svg)
        {
            PresentBox(State.Zoom); // same size; only whether it scrolls may change (SVG redraws itself)
            if (_shown?.Kind == ShownKind.Svg && _zoom.Mode != ZoomMode.Custom)
            {
                ApplyZoom(_zoom, null, keepWhenNotShown: true);
            }

            return;
        }

        Reload(scrollToTop: false);
    }

    /// <summary>Shows the current image or page again for a new area, zoom or orientation (media is left playing).</summary>
    private void Reload(bool scrollToTop)
    {
        // Only a shown image or page is re-planned; media scales by itself and messages have nothing to plan.
        if (State.Path is not { } path || State.Facts is not { } facts || FileFormatKinds.IsMedia(facts.Format) ||
            State.Kind is not (ViewerContentKind.Image or ViewerContentKind.AnimatedImage or ViewerContentKind.Pdf))
        {
            return;
        }

        CancellationToken cancellation = BeginLoad();
        _ = State.Kind == ViewerContentKind.Pdf
            ? GuardAsync(() => ShowPdfPageAsync(path, facts, scrollToTop, cancellation), cancellation)
            : facts.Format == FileFormat.Svg
                ? GuardAsync(() => ShowSvgAsync(path, facts, cancellation), cancellation)
                : GuardAsync(() => ShowImageAsync(path, facts, cancellation), cancellation);
    }

    private void Publish(ViewerState state)
    {
        State = state;
        StateChanged?.Invoke(state);
    }

    private static Task NextFrameAsync()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<object>? handler = null;
        handler = (_, _) =>
        {
            CompositionTarget.Rendering -= handler;
            completion.TrySetResult();
        };
        CompositionTarget.Rendering += handler;
        return completion.Task;
    }

    /// <summary>The zoomable content on screen: its size at 100 % (upright, physical pixels) and the bitmap's size.</summary>
    private sealed record Shown(ShownKind Kind, uint SourceWidth, uint SourceHeight, uint BitmapWidth = 0, uint BitmapHeight = 0);
}
