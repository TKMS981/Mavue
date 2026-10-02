using Mavue.Core.Formats;
using Mavue.Core.Viewing;
using Mavue.Image.Gif;
using Mavue.Image.Wic;
using Mavue.Viewer.Controls;
using Mavue.Viewer.Playback;
using Mavue.Viewer.Rendering;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;

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

/// <summary>Snapshot for the host's title and information line.</summary>
/// <param name="Path">The open file, or null.</param>
/// <param name="Facts">File metadata (null before it is read).</param>
/// <param name="Kind">What is on screen.</param>
/// <param name="SourceWidth">Image size (PDF: page size in points; video: picture size).</param>
/// <param name="SourceHeight">Image size (PDF: page size in points; video: picture size).</param>
/// <param name="PageIndex">Zero-based PDF page.</param>
/// <param name="PageCount">PDF pages, 0 otherwise.</param>
/// <param name="Duration">Video/audio length.</param>
/// <param name="Message">Set when <paramref name="Kind"/> is <see cref="ViewerContentKind.Message"/>.</param>
public sealed record ViewerState(
    string? Path,
    FileFacts? Facts,
    ViewerContentKind Kind,
    uint SourceWidth = 0,
    uint SourceHeight = 0,
    int PageIndex = 0,
    int PageCount = 0,
    TimeSpan Duration = default,
    ViewerMessage? Message = null)
{
    public static readonly ViewerState Empty = new(null, null, ViewerContentKind.None);
}

/// <summary>
/// Opens one file at a time on a <see cref="ViewerSurface"/> (Mavue.App): images decoded for the area (never
/// enlarged by default), animated GIFs, PDF pages, video and audio. Opening another file, closing, or disposing
/// cancels any decoding in flight, stops GIF and media playback, and releases bitmaps, players and files.
/// All members are used on the UI thread.
/// <para>
/// Content is planned from the surface's area and <see cref="ScaleMode"/>; a resize or a monitor change re-plans the
/// current content. Zoom, thumbnails and page prefetch are meant to extend this class (the surface already scrolls).
/// </para>
/// </summary>
public sealed class DocumentViewer : IDisposable
{
    /// <summary>GIFs above this size keep their first frame (copying every frame would load the UI thread).</summary>
    private const long MaxAnimatedGifPixels = 8_000_000;

    private readonly ViewerSurface _surface;
    private readonly Func<ViewerMessage, string> _text;
    private readonly DispatcherQueue _dispatcher;
    private readonly PdfPageCursor _pages = new();
    private readonly DispatcherQueueTimer _areaTimer;

    private CancellationTokenSource? _loading;
    private GifPlayer? _gif;
    private MediaSession? _media;
    private SoftwareBitmap? _displayedBitmap;
    private (uint Width, uint Height) _plannedArea;
    private ImageScaleMode _scaleMode = ImageScaleMode.FitNoUpscale;
    private bool _disposed;

    /// <param name="surface">Where content is shown.</param>
    /// <param name="text">Localized texts for loading, errors and media states.</param>
    public DocumentViewer(ViewerSurface surface, Func<ViewerMessage, string> text)
    {
        ArgumentNullException.ThrowIfNull(surface);
        ArgumentNullException.ThrowIfNull(text);
        _surface = surface;
        _text = text;
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _areaTimer = _dispatcher.CreateTimer();
        _areaTimer.Interval = TimeSpan.FromMilliseconds(150);
        _areaTimer.IsRepeating = false;
        _areaTimer.Tick += (_, _) => OnAreaSettled();
        _surface.AreaChanged += OnAreaChanged;
    }

    /// <summary>Raised on the UI thread whenever <see cref="State"/> changes.</summary>
    public event Action<ViewerState>? StateChanged;

    public ViewerState State { get; private set; } = ViewerState.Empty;

    /// <summary>Optional diagnostics for GIF and media playback.</summary>
    public PlaybackTrace? Trace { get; set; }

    /// <summary>Fit without enlarging (default) or actual size; changing it re-plans the current image or page.</summary>
    public ImageScaleMode ScaleMode
    {
        get => _scaleMode;
        set
        {
            if (_scaleMode != value)
            {
                _scaleMode = value;
                Reload();
            }
        }
    }

    /// <summary>True when a multi-page PDF is shown and a step by <paramref name="delta"/> pages is possible.</summary>
    public bool CanStepPage(int delta) => State.Kind == ViewerContentKind.Pdf && _pages.CanStep(delta);

    /// <summary>True when a video or audio file is open.</summary>
    public bool HasMedia => _media is not null;

    /// <summary>Opens <paramref name="path"/>, replacing whatever was shown (first page for a PDF).</summary>
    public Task OpenAsync(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ObjectDisposedException.ThrowIf(_disposed, this);
        _pages.Reset();
        _surface.ResetWheel();
        StopPlayback();
        _surface.ClearMedia();
        _surface.SetStatus(string.Empty);
        Publish(new ViewerState(path, null, ViewerContentKind.Loading));
        CancellationToken cancellation = BeginLoad();
        _ = ClearStaleImageAsync(cancellation);
        return LoadAsync(path, cancellation);
    }

    /// <summary>Turns the PDF page by <paramref name="delta"/>; returns false when there is no such page.</summary>
    public async Task<bool> StepPageAsync(int delta)
    {
        if (!CanStepPage(delta) || State.Path is not { } path || State.Facts is not { } facts)
        {
            return false;
        }

        _pages.TryStep(delta);
        CancellationToken cancellation = BeginLoad();
        await GuardAsync(() => ShowPdfPageAsync(path, facts, scrollToTop: true, cancellation), cancellation);
        return true;
    }

    /// <summary>
    /// The mouse wheel over the surface: turns the PDF page once a notch has accumulated (at the edges when the page
    /// scrolls). Returns true when the wheel was used for a page turn.
    /// </summary>
    public bool HandleWheel(int wheelDelta)
    {
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

    /// <summary>Stops playback, releases the file and clears the surface.</summary>
    public void Close()
    {
        _loading?.Cancel();
        _pages.Reset();
        ClearContent();
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
        _surface.AreaChanged -= OnAreaChanged;
        _loading?.Dispose();
        _loading = null;
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
            else if (facts.Format == FileFormat.Pdf)
            {
                await ShowPdfPageAsync(path, facts, scrollToTop: true, cancellation);
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
        (uint Width, uint Height)? source = await Task.Run(() => WicPreviewDecoder.TryReadDimensions(path), cancellation);
        DecodeBox box = source is { } size
            ? DisplaySizing.DecodeBoxFor(_scaleMode, areaWidth, areaHeight, size.Width, size.Height)
            : new DecodeBox(areaWidth, areaHeight);
        bool scrollable = _scaleMode == ImageScaleMode.ActualSize && source is not null && !box.ActualSizeRefused;
        DecodedImage decoded = await ImageDecoding.DecodeAsync(
            path, box.Width, box.Height, BitmapInterpolationMode.Fant, preferWic: facts.Format == FileFormat.Jpeg, cancellation);
        if (!await ShowDecodedAsync(decoded, scrollable, cancellation))
        {
            return;
        }

        Publish(State with { Kind = ViewerContentKind.Image, SourceWidth = decoded.SourceWidth, SourceHeight = decoded.SourceHeight });
        if (facts.Format == FileFormat.Gif)
        {
            await StartGifAsync(path, areaWidth, areaHeight, scrollable, cancellation);
        }
    }

    private async Task ShowPdfPageAsync(string path, FileFacts facts, bool scrollToTop, CancellationToken cancellation)
    {
        (uint areaWidth, uint areaHeight) = await AreaAsync(cancellation);
        bool actual = _scaleMode == ImageScaleMode.ActualSize;
        DecodedImage page = await PdfRendering.RenderAsync(path, areaWidth, areaHeight, cancellation, actual ? _surface.PixelScale : null, _pages.Index);
        if (!await ShowDecodedAsync(page, actual, cancellation))
        {
            return;
        }

        _pages.SetCount(page.PageCount);
        if (scrollToTop)
        {
            _surface.ScrollToTop();
        }

        Publish(State with
        {
            Facts = facts,
            Kind = ViewerContentKind.Pdf,
            SourceWidth = page.SourceWidth,
            SourceHeight = page.SourceHeight,
            PageIndex = page.PageIndex,
            PageCount = page.PageCount,
        });
    }

    /// <summary>Puts a decoded bitmap on screen 1:1; shows the reason instead when nothing was decoded.</summary>
    private async Task<bool> ShowDecodedAsync(DecodedImage decoded, bool scrollable, CancellationToken cancellation)
    {
        if (decoded.Bitmap is not { } bitmap)
        {
            ShowMessage(decoded.Reason switch
            {
                "no-codec" => ViewerMessage.Unsupported,
                "too-large" => ViewerMessage.TooLarge,
                "password" => ViewerMessage.PdfPassword,
                _ => ViewerMessage.Failed,
            });
            return false;
        }

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
        _surface.SetImageBox(DisplaySizing.ElementSize((uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, _surface.PixelScale), scrollable);
        _surface.SetFullImage(source);
        ReplaceDisplayedBitmap(bitmap);
        return true;
    }

    /// <summary>After a GIF's first frame is on screen: if it has several frames, replace it with the animation.</summary>
    private async Task StartGifAsync(string path, uint areaWidth, uint areaHeight, bool scrollable, CancellationToken cancellation)
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
        var player = new GifPlayer(reader, Trace);
        _gif = player;

        // The animation is sized from the logical screen (the first frame may cover only part of it).
        (uint width, uint height) = DisplaySizing.ExpectedDisplayPixels(_scaleMode, areaWidth, areaHeight, (uint)reader.Width, (uint)reader.Height);
        _surface.SetImageBox(DisplaySizing.ElementSize(width, height, _surface.PixelScale), scrollable);
        _surface.SetFullImage(player.Source);
        ReplaceDisplayedBitmap(null); // the still frame's source was just replaced; free its pixels too
        player.Start();
        Publish(State with { Kind = ViewerContentKind.AnimatedImage });
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
        Publish(State with { Kind = ViewerContentKind.Message, Message = message });
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
        StopPlayback();
        _surface.ClearMedia();
        _surface.ClearImages();
        ReplaceDisplayedBitmap(null);
    }

    private void StopGif()
    {
        _gif?.Dispose();
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

    private void ReplaceDisplayedBitmap(SoftwareBitmap? bitmap)
    {
        SoftwareBitmap? previous = _displayedBitmap;
        _displayedBitmap = bitmap;
        if (!ReferenceEquals(previous, bitmap))
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
        if (Math.Abs(width - (long)_plannedArea.Width) > 2 || Math.Abs(height - (long)_plannedArea.Height) > 2)
        {
            Reload();
        }
    }

    /// <summary>Shows the current image or page again for a new area or scale mode (media is left playing).</summary>
    private void Reload()
    {
        // Only a shown image or page is re-planned; media scales by itself and messages have nothing to plan.
        if (State.Path is not { } path || State.Facts is not { } facts || FileFormatKinds.IsMedia(facts.Format) ||
            State.Kind is not (ViewerContentKind.Image or ViewerContentKind.AnimatedImage or ViewerContentKind.Pdf))
        {
            return;
        }

        CancellationToken cancellation = BeginLoad();
        _ = State.Kind == ViewerContentKind.Pdf
            ? GuardAsync(() => ShowPdfPageAsync(path, facts, scrollToTop: false, cancellation), cancellation)
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
}
