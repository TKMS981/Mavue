using System.Globalization;
using Mavue.Core.Archives;
using Mavue.Core.Formats;
using Mavue.Core.Viewing;
using Mavue.QuickView.Diagnostics;
using Mavue.QuickView.Host.Presentation;
using Mavue.QuickView.Preview;
using Mavue.Viewer.Rendering;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using VirtualKey = Windows.System.VirtualKey;

namespace Mavue.QuickView.Host.Content;

/// <summary>
/// ZIP archives without extraction (SPEC §3.1, docs/ARCHITECTURE.md §4.7.10). The listing comes from the central
/// directory only (<see cref="ZipDirectory"/>, bounded by <see cref="ArchiveLimits"/>) and is shown one folder at a
/// time. Images inside are read one entry at a time, into memory, through <see cref="ZipEntryReader"/> (decompressed
/// bytes counted, ratio and CRC checked) and decoded at the displayed size; nothing is written to disk, nothing is
/// executed, archives inside are not opened. In image mode the images of the archive are stepped like pages
/// (PageUp/PageDown from Explorer, wheel, page buttons, arrows while Quick View has the keyboard), with zoom and
/// rotation as for other images and the neighbors decoded ahead within the performance tier's limits.
/// Comic book archives (.cbz) open in image mode.
/// </summary>
internal sealed class ArchiveContent : IPreviewContent, IViewTransformContent, IPagedContent
{
    /// <summary>The last read entry is kept for zoom, rotation and resizing if it is at most this large.</summary>
    private const long MaxKeptEntryBytes = 64L * 1024 * 1024;

    private readonly PreviewServices _s;
    private readonly IPreviewHost _host;
    private readonly SurfaceImageView _view;
    private readonly PreviewCache<DecodedImage> _cache;
    private readonly Func<VirtualKey, bool> _keys;
    private Session? _session;
    private ListingView? _listing;
    private ImageBar? _bar;
    private CancellationTokenSource? _prefetch;
    private DispatcherQueueTimer? _zoomTimer;
    private (ZipEntry Entry, ReadOnlyMemory<byte> Data)? _kept;

    public ArchiveContent(PreviewServices services, IPreviewHost host)
    {
        _s = services;
        _host = host;
        _view = services.ImageView;
        _keys = OnKey;
        _cache = new PreviewCache<DecodedImage>(
            services.Resources.Policy.CacheBudgetBytes / 2,
            d => d.Bitmap is { } b ? (long)b.PixelWidth * b.PixelHeight * 4 : 0,
            d => d.Bitmap?.Dispose());
        services.Resources.PolicyChanged += policy => _cache.BudgetBytes = policy.CacheBudgetBytes / 2;
        services.Settings.ScaleModeChanged += _cache.Clear;
    }

    public PreviewContentKind Kind => PreviewContentKind.Archive;

    private static ArchiveLimits Limits => ArchiveLimits.Default;

    private bool InImageMode => _session is { ImageIndex: >= 0 };

    public async Task ShowAsync(PreviewItem item, CancellationToken cancellation)
    {
        Session? session = _session is { } open && open.Matches(item) ? open : null;
        if (session is null)
        {
            DropSession();
            _s.Window.Surface.ShowStatusOnly(_s.Strings.GetString("QuickView_Loading"));
            _view.ReleaseDisplayedBitmap();
            _view.RecordShown(SurfaceImageKind.None, default, 0, _host.DisplayScale());
            session = await OpenAsync(item, cancellation);
            if (session is null)
            {
                return; // the information card says why
            }

            _session = session;
        }

        _s.Window.ContentKeyHandler = _keys;
        if (session.ImageIndex >= 0)
        {
            await ShowImageAsync(item, session, cancellation);
        }
        else
        {
            await ShowListAsync(item, session, cancellation);
        }
    }

    public void Reset(ContentReset scope, string reason)
    {
        CancelPrefetch();
        if (scope == ContentReset.Reload)
        {
            return; // the same archive again: keep the listing, the folder and the image position
        }

        _zoomTimer?.Stop();
        DropSession();
        if (ReferenceEquals(_s.Window.ContentKeyHandler, _keys))
        {
            _s.Window.ContentKeyHandler = null;
        }

        if (scope == ContentReset.Hidden)
        {
            _cache.Clear();
        }
    }

    /// <summary>The listing lays itself out; an image is decoded again for the new area.</summary>
    public AreaResponse OnAreaChanged() => _session is not null && !InImageMode ? AreaResponse.Handled : AreaResponse.ReloadIfChanged;

    public void Dispose()
    {
        CancelPrefetch();
        _zoomTimer?.Stop();
        _cache.Dispose();
    }

    public void Zoom(QuickViewZoomCommand command, int wheelDelta, Point? anchor)
    {
        if (!_host.IsVisible || !InImageMode)
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

    public void Rotate(int quarterTurns)
    {
        if (!_host.IsVisible || !InImageMode || !SurfaceImageView.IsRotatable(_view.Shown))
        {
            return;
        }

        _view.Orientation = quarterTurns > 0 ? _view.Orientation.RotateClockwise() : _view.Orientation.RotateCounterClockwise();
        long id = SelectionWorker.NextRequestId();
        _s.Timeline.Mark(id, "rotate", QuickViewTimeline.Now, new Dictionary<string, object?> { ["quarterTurns"] = _view.Orientation.QuarterTurns });
        _host.MarkNavigation(id, "rotate");
        _host.ShowCurrentAgain(id);
    }

    /// <summary>The previous or next image of the archive (image mode only). Past the first or last image nothing happens.</summary>
    public void StepPage(int delta, string source, long? inputQpc)
    {
        if (!_host.IsVisible || _session is not { ImageIndex: >= 0 } session)
        {
            return;
        }

        int target = Math.Clamp(session.ImageIndex + delta, 0, session.Tree.Images.Count - 1);
        if (target == session.ImageIndex)
        {
            _s.Timeline.Mark(_host.CurrentRequest, "archive-image-edge", QuickViewTimeline.Now, new Dictionary<string, object?> { ["index"] = target, ["source"] = source });
            return;
        }

        GoToImage(session, target, source, inputQpc);
    }

    // ---------------------------------------------------------------- listing

    private async Task<Session?> OpenAsync(PreviewItem item, CancellationToken cancellation)
    {
        long start = QuickViewTimeline.Now;
        string? failure;
        string reason;
        try
        {
            (ZipDirectory directory, ArchiveTree tree, MarkOfTheWeb? origin) = await Task.Run(
                () =>
                {
                    ZipDirectory read = ZipDirectory.Read(item.Path, Limits, cancellation);
                    return (read, ArchiveTree.Build(read.Entries, Limits, cancellation), UniversalFileInfo.Read(item.Path, FileFormat.Zip).Origin);
                },
                cancellation);
            cancellation.ThrowIfCancellationRequested();
            var session = new Session(item.Path, item.Facts, directory, tree) { Origin = origin };
            if (IsComicArchive(item.Path) && tree.Images.Count > 0)
            {
                session.ImageIndex = 0; // a comic book opens at its first page
            }

            ArchiveSummary summary = tree.Summary;
            _s.Timeline.Mark(item.RequestId, "archive-listed", QuickViewTimeline.Now, new Dictionary<string, object?>
            {
                ["entries"] = directory.Entries.Count,
                ["files"] = summary.Files,
                ["folders"] = summary.Folders,
                ["images"] = summary.Images,
                ["encrypted"] = summary.Encrypted,
                ["unsupported"] = summary.UnsupportedMethod,
                ["unsafeNames"] = summary.UnsafeNames,
                ["suspiciousRatio"] = summary.SuspiciousRatio,
                ["nested"] = summary.NestedArchives,
                ["nameEncoding"] = directory.LegacyNameEncoding,
                ["motw"] = origin?.ZoneId,
                ["listMs"] = Math.Round((QuickViewTimeline.Now - start) * 1000.0 / System.Diagnostics.Stopwatch.Frequency, 1),
            });
            return session;
        }
        catch (ArchiveFormatException ex)
        {
            reason = "archive-" + ex.Problem;
            failure = ex.Problem switch
            {
                ArchiveProblem.MultiVolume => "QuickView_Archive_Error_MultiVolume",
                ArchiveProblem.Damaged => "QuickView_Archive_Error_Damaged",
                _ => "QuickView_Archive_Error_NotArchive",
            };
        }
        catch (ArchiveLimitExceededException ex)
        {
            reason = "archive-limit-" + ex.Limit;
            failure = ex.Limit == nameof(ArchiveLimits.MaxEntries) ? "QuickView_Archive_Error_TooManyEntries" : "QuickView_Archive_Error_DirectoryTooLarge";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            reason = "archive-io";
            failure = "QuickView_Error_Failed";
        }

        _s.Window.Surface.ShowStatusOnly(_s.Strings.GetString(failure));
        _host.Display.MarkMessage(item.RequestId);
        _s.Timeline.Mark(item.RequestId, "full-skipped", QuickViewTimeline.Now, new Dictionary<string, object?> { ["reason"] = reason });
        await _host.ShowInfoCardAsync(item, failure, cancellation);
        return null;
    }

    private async Task ShowListAsync(PreviewItem item, Session session, CancellationToken cancellation)
    {
        _kept = null; // the list does not need the last image's data
        _s.Window.Surface.ShowStatusOnly(string.Empty);
        _view.ReleaseDisplayedBitmap();
        _view.RecordShown(SurfaceImageKind.None, default, 0, _host.DisplayScale());
        _host.SetPaging(false, false, false);
        _listing ??= new ListingView(this, session);
        _listing.ShowFolder(session.Folder, session.SelectPath);
        _s.Window.ShowCard(_listing.Root);
        _host.Display.MarkContent(item.RequestId);
        _host.SetInfo(string.Format(CultureInfo.CurrentCulture, _s.Strings.GetString("QuickView_Archive_InfoLine"), session.Tree.Summary.Files.ToString("N0", CultureInfo.CurrentCulture), ByteSizeText.Format(item.Facts.Length)));
        _s.Timeline.Mark(item.RequestId, "archive-list", QuickViewTimeline.Now, new Dictionary<string, object?>
        {
            ["depth"] = session.Folder.Length == 0 ? 0 : session.Folder.Count(c => c == '/') + 1,
            ["items"] = session.Tree.Children(session.Folder).Count,
        });
        await QuickViewPresence.NextFrameAsync();
        if (!cancellation.IsCancellationRequested)
        {
            _s.Timeline.Mark(item.RequestId, "full-visible", QuickViewTimeline.Now);
        }
    }

    /// <summary>Opens a folder or an image of the listing; anything else says why it is only listed.</summary>
    private void Open(ArchiveNode node)
    {
        if (_session is not { } session || _listing is null)
        {
            return;
        }

        if (node.IsFolder)
        {
            session.Folder = node.Path;
            session.SelectPath = null;
            _listing.ShowFolder(session.Folder, null);
            _s.Timeline.Mark(_host.CurrentRequest, "archive-folder", QuickViewTimeline.Now, new Dictionary<string, object?> { ["items"] = session.Tree.Children(session.Folder).Count });
            return;
        }

        int image = node.IsImage && node.Entry is { } e ? IndexOfImage(session, e) : -1;
        if (image >= 0)
        {
            GoToImage(session, image, "archive-open", null);
            return;
        }

        ZipEntry? entry = node.Entry;
        string message = entry switch
        {
            { NameCheck: not ArchiveNameCheck.Safe } => _s.Strings.GetString("QuickView_Archive_Unsafe") + " — " + _s.Strings.GetString("QuickView_Archive_Entry_NotPreviewable"),
            { IsEncrypted: true } => _s.Strings.GetString("QuickView_Archive_Entry_Encrypted"),
            { IsSupportedMethod: false } => string.Format(CultureInfo.CurrentCulture, _s.Strings.GetString("QuickView_Archive_Entry_Unsupported"), MethodName(entry.Method)),
            _ when ArchiveTree.IsArchiveName(node.Path) => _s.Strings.GetString("QuickView_Archive_Entry_Nested"),
            _ => _s.Strings.GetString("QuickView_Archive_Entry_NotPreviewable"),
        };
        _listing.SetStatus(message);
    }

    private void GoUp()
    {
        if (_session is not { } session || _listing is null || session.Folder.Length == 0)
        {
            return;
        }

        string previous = session.Folder;
        session.Folder = ArchiveTree.ParentOf(previous);
        session.SelectPath = previous;
        _listing.ShowFolder(session.Folder, previous);
    }

    /// <summary>The first image in the shown folder (or below it), else the first of the archive.</summary>
    private void ShowImagesFromFolder()
    {
        if (_session is not { } session || session.Tree.Images.Count == 0)
        {
            return;
        }

        string prefix = session.Folder.Length == 0 ? string.Empty : session.Folder + "/";
        int index = 0;
        for (int i = 0; i < session.Tree.Images.Count; i++)
        {
            if (session.Tree.Images[i].Path.StartsWith(prefix, StringComparison.Ordinal))
            {
                index = i;
                break;
            }
        }

        GoToImage(session, index, "archive-show-images", null);
    }

    private void BackToList()
    {
        if (_session is not { ImageIndex: >= 0 } session)
        {
            return;
        }

        ZipEntry entry = session.Tree.Images[session.ImageIndex];
        session.Folder = ArchiveTree.ParentOf(entry.Path);
        session.SelectPath = entry.Path;
        session.ImageIndex = -1;
        _view.ViewZoom = null;
        _view.Orientation = ViewOrientation.Identity;
        long id = SelectionWorker.NextRequestId();
        _host.MarkNavigation(id, "archive-list");
        _host.ShowCurrentAgain(id);
    }

    // ---------------------------------------------------------------- images

    private void GoToImage(Session session, int index, string source, long? inputQpc)
    {
        session.ImageIndex = index;
        _view.ViewZoom = null; // every image starts at the size from the settings
        _view.Orientation = ViewOrientation.Identity;
        _zoomTimer?.Stop();
        long id = SelectionWorker.NextRequestId();
        if (inputQpc is { } input)
        {
            _s.Timeline.Mark(id, "nav-input", input);
        }

        _host.MarkNavigation(id, source);
        _s.Window.Surface.ScrollToTop();
        _host.ShowCurrentAgain(id);
    }

    private async Task ShowImageAsync(PreviewItem item, Session session, CancellationToken cancellation)
    {
        long requestId = item.RequestId;
        int count = session.Tree.Images.Count;
        int index = Math.Clamp(session.ImageIndex, 0, count - 1);
        session.ImageIndex = index;
        ZipEntry entry = session.Tree.Images[index];
        _bar ??= new ImageBar(this);
        _bar.Set(ArchiveEntryName.ForDisplay(entry.Path), string.Format(CultureInfo.CurrentCulture, _s.Strings.GetString("QuickView_Archive_Position"), index + 1, count));
        _s.Window.ShowCard(_bar.Root);
        _host.SetPaging(true, index > 0, index < count - 1);
        _s.Window.Surface.ImageMayHaveTransparency = FileFormatKinds.MayHaveTransparency(FileFormatDetector.FromExtension(Path.GetExtension(entry.Path)));
        _s.Timeline.Mark(requestId, "archive-image", QuickViewTimeline.Now, new Dictionary<string, object?>
        {
            ["index"] = index,
            ["count"] = count,
            ["method"] = entry.Method,
            ["compressed"] = entry.CompressedSize,
            ["size"] = entry.Size,
        });

        (uint areaWidth, uint areaHeight) = (item.AreaWidth, item.AreaHeight);
        bool cacheable = _view.ViewZoom is null && _view.Orientation.IsIdentity;

        // Stepping back to an image decoded before is instant (no reading, no decoding).
        if (cacheable && session.Dimensions.TryGetValue(entry.Index, out (uint Width, uint Height) known))
        {
            Plan knownPlan = PlanFor(known, item.ScaleMode, areaWidth, areaHeight);
            if (_cache.TryGet(Key(session, entry, knownPlan), out DecodedImage? cached) && cached?.Bitmap is not null)
            {
                await ShowFullAsync(requestId, session, index, cached, "cache", knownPlan, cancellation);
                StartPrefetch(session, index, item.ScaleMode, areaWidth, areaHeight);
                return;
            }
        }

        ReadOnlyMemory<byte> data;
        try
        {
            data = await ReadEntryAsync(session, entry, cancellation);
        }
        catch (ArchiveEntryException ex)
        {
            ShowEntryMessage(requestId, session, index, ex.Problem switch
            {
                ArchiveEntryProblem.Encrypted => _s.Strings.GetString("QuickView_Archive_Entry_Encrypted"),
                ArchiveEntryProblem.UnsupportedMethod => string.Format(CultureInfo.CurrentCulture, _s.Strings.GetString("QuickView_Archive_Entry_Unsupported"), MethodName(entry.Method)),
                _ => _s.Strings.GetString("QuickView_Archive_Entry_Damaged"),
            }, "entry-" + ex.Problem);
            return;
        }
        catch (ArchiveLimitExceededException ex)
        {
            ShowEntryMessage(requestId, session, index, ex.Limit == nameof(ArchiveLimits.MaxEntryBytes)
                ? string.Format(CultureInfo.CurrentCulture, _s.Strings.GetString("QuickView_Archive_Entry_TooLarge"), ByteSizeText.Format(Limits.MaxEntryBytes))
                : _s.Strings.GetString("QuickView_Archive_Entry_Ratio"), "entry-limit-" + ex.Limit);
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowEntryMessage(requestId, session, index, _s.Strings.GetString("QuickView_Error_Failed"), "entry-io");
            return;
        }

        cancellation.ThrowIfCancellationRequested();
        (uint Width, uint Height)? size = await ImageDecoding.ReadDimensionsAsync(data, cancellation);
        cancellation.ThrowIfCancellationRequested();
        if (size is not { } dimensions)
        {
            ShowEntryMessage(requestId, session, index, _s.Strings.GetString("QuickView_Error_Unsupported"), "entry-no-codec");
            return;
        }

        session.Dimensions[entry.Index] = dimensions;
        Plan plan = PlanFor(dimensions, item.ScaleMode, areaWidth, areaHeight);
        DecodedImage full = await ImageDecoding.DecodeAsync(data, plan.Box.Width, plan.Box.Height, Interpolation(_s.Options.Interpolation), cancellation);
        if (full.Reason is null && cacheable)
        {
            _cache.Add(Key(session, entry, plan), full);
        }

        cancellation.ThrowIfCancellationRequested();
        if (full.Reason is { } reason)
        {
            ShowEntryMessage(requestId, session, index, _s.Strings.GetString(reason == "too-large" ? "QuickView_Error_TooLarge" : reason == "no-codec" ? "QuickView_Error_Unsupported" : "QuickView_Error_Failed"), "entry-" + reason);
            return;
        }

        await ShowFullAsync(requestId, session, index, full, full.Decoder, plan, cancellation);
        if (!cacheable)
        {
            full.Bitmap?.Dispose(); // shown through a private copy; not kept for a zoomed or turned view
        }

        StartPrefetch(session, index, item.ScaleMode, areaWidth, areaHeight);
    }

    private async Task<ReadOnlyMemory<byte>> ReadEntryAsync(Session session, ZipEntry entry, CancellationToken cancellation)
    {
        if (_kept is { } kept && ReferenceEquals(kept.Entry, entry))
        {
            return kept.Data;
        }

        // Each image the user opens is bounded by the entry limit and the ratio check; the archive total bounds what
        // Quick View reads ahead by itself.
        ReadOnlyMemory<byte> data = await Task.Run(() => ZipEntryReader.ReadAll(session.Path, entry, Limits, new DecompressionTotals(), cancellation), cancellation);
        _kept = data.Length <= MaxKeptEntryBytes ? (entry, data) : null;
        return data;
    }

    private void ShowEntryMessage(long requestId, Session session, int index, string message, string reason)
    {
        _s.Window.Surface.ShowStatusOnly(message);
        _view.ReleaseDisplayedBitmap();
        _view.RecordShown(SurfaceImageKind.None, default, 0, _host.DisplayScale());
        _host.Display.MarkMessage(requestId);
        ZipEntry entry = session.Tree.Images[index];
        _host.SetInfo(ArchiveEntryName.ForDisplay(entry.FileName) + " · " + string.Format(CultureInfo.CurrentCulture, _s.Strings.GetString("QuickView_Archive_Position"), index + 1, session.Tree.Images.Count));
        _s.Timeline.Mark(requestId, "full-skipped", QuickViewTimeline.Now, new Dictionary<string, object?> { ["reason"] = reason });
    }

    private async Task ShowFullAsync(long requestId, Session session, int index, DecodedImage full, string decoder, Plan plan, CancellationToken cancellation)
    {
        SoftwareBitmap display = SoftwareBitmapPixels.Orient(full.Bitmap!, _view.Orientation); // a private copy (see SurfaceImageView)
        ImageSource source;
        try
        {
            var bitmapSource = new SoftwareBitmapSource();
            await bitmapSource.SetBitmapAsync(display);
            source = bitmapSource;
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
        _view.RecordShown(SurfaceImageKind.Raster, (full.SourceWidth, full.SourceHeight), element.Width, scale);
        _view.SetDisplayedBitmap(display);
        _host.Display.MarkContent(requestId);
        ZipEntry entry = session.Tree.Images[index];
        _host.SetInfo(string.Format(
            CultureInfo.CurrentCulture,
            _s.Strings.GetString("QuickView_Archive_ImageInfo"),
            ArchiveEntryName.ForDisplay(entry.FileName),
            index + 1,
            session.Tree.Images.Count,
            full.SourceWidth,
            full.SourceHeight) + (plan.Box.ActualSizeRefused ? " · " + _s.Strings.GetString("QuickView_ActualSizeTooLarge") : string.Empty));
        _s.Timeline.Mark(requestId, "full-set", QuickViewTimeline.Now, new Dictionary<string, object?>
        {
            ["sourceWidth"] = full.SourceWidth,
            ["sourceHeight"] = full.SourceHeight,
            ["decodedWidth"] = full.Bitmap!.PixelWidth,
            ["decodedHeight"] = full.Bitmap.PixelHeight,
            ["decodeMs"] = decoder == "cache" ? 0 : full.DecodeMilliseconds,
            ["decoder"] = decoder,
            ["archiveIndex"] = index,
        });
        await QuickViewPresence.NextFrameAsync();
        if (!cancellation.IsCancellationRequested)
        {
            _s.Timeline.Mark(requestId, "full-visible", QuickViewTimeline.Now);
        }
    }

    /// <summary>Decode box and element size for an image of <paramref name="size"/> (upright), as for files (RasterContent).</summary>
    private Plan PlanFor((uint Width, uint Height) size, ImageScaleMode mode, uint areaWidth, uint areaHeight)
    {
        if (_view.ViewZoom is null && _view.Orientation.IsIdentity)
        {
            DecodeBox box = DisplaySizing.DecodeBoxFor(mode, areaWidth, areaHeight, size.Width, size.Height);
            return new Plan(box, mode == ImageScaleMode.ActualSize && !box.ActualSizeRefused, null);
        }

        ZoomSetting zoom = _view.ViewZoom ?? ZoomSetting.For(mode);
        ViewOrientation orientation = _view.Orientation;
        (uint sw, uint sh) = orientation.Oriented(size.Width, size.Height);
        double factor = ViewerZoom.FactorFor(zoom, sw, sh, areaWidth, areaHeight);
        (uint dw, uint dh) = ViewerZoom.DisplaySize(size.Width, size.Height, factor);
        (uint decodeWidth, uint decodeHeight) = ViewerZoom.DecodeSize(size.Width, size.Height, dw, dh, DisplaySizing.MaxActualSizePixels);
        (uint displayWidth, uint displayHeight) = orientation.Oriented(dw, dh);
        (double, double) element = DisplaySizing.ElementSize(displayWidth, displayHeight, _host.DisplayScale());
        return new Plan(new DecodeBox(decodeWidth, decodeHeight), displayWidth > areaWidth + 1 || displayHeight > areaHeight + 1, element);
    }

    private static PreviewKey Key(Session session, ZipEntry entry, Plan plan) =>
        PreviewKey.Create(session.Path, session.Length, session.LastWriteUtc, plan.Box.Width, plan.Box.Height, entry.Index + 1);

    /// <summary>The next (and, on capable machines, the previous) image, read and decoded ahead within the tier's limits.</summary>
    private void StartPrefetch(Session session, int index, ImageScaleMode mode, uint areaWidth, uint areaHeight)
    {
        CancelPrefetch();
        PreviewResourcePolicy policy = _s.Resources.Policy;
        if (policy.PrefetchNeighbors <= 0 || _view.ViewZoom is not null || !_view.Orientation.IsIdentity)
        {
            return;
        }

        var work = new CancellationTokenSource();
        _prefetch = work;
        int[] order = policy.PrefetchNeighbors >= 2 ? [index + 1, index - 1] : [index + 1];
        _ = PrefetchAsync(session, order, mode, areaWidth, areaHeight, policy, work.Token);
    }

    private async Task PrefetchAsync(Session session, int[] order, ImageScaleMode mode, uint areaWidth, uint areaHeight, PreviewResourcePolicy policy, CancellationToken cancellation)
    {
        try
        {
            foreach (int i in order)
            {
                if (i < 0 || i >= session.Tree.Images.Count || cancellation.IsCancellationRequested)
                {
                    continue;
                }

                ZipEntry entry = session.Tree.Images[i];
                if (entry.Size > policy.MaxPrefetchFileBytes || session.PrefetchTotals.Bytes > Limits.MaxTotalBytes)
                {
                    continue;
                }

                if (session.Dimensions.TryGetValue(entry.Index, out (uint, uint) known) && _cache.Contains(Key(session, entry, PlanFor(known, mode, areaWidth, areaHeight))))
                {
                    continue;
                }

                ReadOnlyMemory<byte> data = await Task.Run(() => ZipEntryReader.ReadAll(session.Path, entry, Limits, session.PrefetchTotals, cancellation), cancellation);
                (uint Width, uint Height)? size = await ImageDecoding.ReadDimensionsAsync(data, cancellation);
                if (size is not { } dimensions || cancellation.IsCancellationRequested)
                {
                    continue;
                }

                session.Dimensions[entry.Index] = dimensions;
                if ((long)dimensions.Width * dimensions.Height > (mode == ImageScaleMode.ActualSize ? policy.MaxActualSizePrefetchPixels : policy.MaxPrefetchSourcePixels))
                {
                    continue;
                }

                Plan plan = PlanFor(dimensions, mode, areaWidth, areaHeight);
                PreviewKey key = Key(session, entry, plan);
                if (_cache.Contains(key))
                {
                    continue;
                }

                DecodedImage decoded = await ImageDecoding.DecodeAsync(data, plan.Box.Width, plan.Box.Height, Interpolation(_s.Options.Interpolation), cancellation);
                if (decoded.Reason is null && !cancellation.IsCancellationRequested && ReferenceEquals(_session, session) && _host.IsVisible)
                {
                    _cache.Add(key, decoded);
                    _s.Timeline.Mark(0, "prefetched", QuickViewTimeline.Now, new Dictionary<string, object?>
                    {
                        ["decodeMs"] = decoded.DecodeMilliseconds,
                        ["format"] = "archive-entry",
                        ["cacheBytes"] = _cache.Bytes,
                        ["cacheCount"] = _cache.Count,
                    });
                }
                else
                {
                    decoded.Bitmap?.Dispose();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Another image or item.
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException or ArgumentException)
        {
            // Reading ahead is optional: the image reports its own problem when it is shown.
        }
    }

    private void CancelPrefetch()
    {
        _prefetch?.Cancel();
        _prefetch?.Dispose();
        _prefetch = null;
    }

    private void DropSession()
    {
        if (_session is null && _listing is null && _bar is null)
        {
            return;
        }

        UIElement? card = _s.Window.CardContent;
        if (card is not null && (ReferenceEquals(card, _listing?.Root) || ReferenceEquals(card, _bar?.Root)))
        {
            _s.Window.ShowCard(null);
        }

        if (_session is not null)
        {
            _cache.Clear(); // another archive: its images are of no use now
        }

        _session = null;
        _listing = null;
        _kept = null;
    }

    private DispatcherQueueTimer CreateZoomTimer()
    {
        DispatcherQueueTimer timer = _s.Dispatcher.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(180);
        timer.IsRepeating = false;
        timer.Tick += (_, _) =>
        {
            if (_host.IsVisible && InImageMode && _view.Shown == SurfaceImageKind.Raster)
            {
                long id = SelectionWorker.NextRequestId();
                _host.MarkNavigation(id, "zoom-refine");
                _host.ShowCurrentAgain(id);
            }
        };
        return timer;
    }

    /// <summary>
    /// Keys while Quick View has the keyboard: in image mode the arrows, Home and End step through the archive's
    /// images and Backspace returns to the list; in the list Backspace goes up a folder.
    /// </summary>
    private bool OnKey(VirtualKey key)
    {
        if (_session is not { } session)
        {
            return false;
        }

        if (session.ImageIndex < 0)
        {
            if (key == VirtualKey.Back && session.Folder.Length > 0)
            {
                GoUp();
                return true;
            }

            return false;
        }

        switch (key)
        {
            case VirtualKey.Left or VirtualKey.Up:
                StepPage(-1, "window-arrow", QuickViewTimeline.Now);
                return true;
            case VirtualKey.Right or VirtualKey.Down:
                StepPage(+1, "window-arrow", QuickViewTimeline.Now);
                return true;
            case VirtualKey.Home:
                StepPage(-session.ImageIndex, "window-home", QuickViewTimeline.Now);
                return true;
            case VirtualKey.End:
                StepPage(session.Tree.Images.Count - 1 - session.ImageIndex, "window-end", QuickViewTimeline.Now);
                return true;
            case VirtualKey.Back:
                BackToList();
                return true;
            default:
                return false;
        }
    }

    private static int IndexOfImage(Session session, ZipEntry entry)
    {
        IReadOnlyList<ZipEntry> images = session.Tree.Images;
        for (int i = 0; i < images.Count; i++)
        {
            if (ReferenceEquals(images[i], entry))
            {
                return i;
            }
        }

        return -1;
    }

    private static bool IsComicArchive(string path) => string.Equals(Path.GetExtension(path), ".cbz", StringComparison.OrdinalIgnoreCase);

    private static string MethodName(ushort method) => method switch
    {
        9 => "Deflate64",
        12 => "bzip2",
        14 => "LZMA",
        93 => "Zstandard",
        95 => "XZ",
        96 => "JPEG",
        97 => "WavPack",
        98 => "PPMd",
        99 => "AES",
        _ => method.ToString(CultureInfo.InvariantCulture),
    };

    private static BitmapInterpolationMode Interpolation(string name) => name switch
    {
        "linear" => BitmapInterpolationMode.Linear,
        "cubic" => BitmapInterpolationMode.Cubic,
        "nearest" => BitmapInterpolationMode.NearestNeighbor,
        _ => BitmapInterpolationMode.Fant,
    };

    private readonly record struct Plan(DecodeBox Box, bool Scrollable, (double Width, double Height)? DisplayElementSize);

    /// <summary>One archive while it is previewed (kept across reloads of the same file).</summary>
    private sealed class Session(string path, FileFacts facts, ZipDirectory directory, ArchiveTree tree)
    {
        public string Path { get; } = path;

        public long Length { get; } = facts.Length;

        public DateTime LastWriteUtc { get; } = facts.LastWriteUtc;

        public ZipDirectory Directory { get; } = directory;

        public ArchiveTree Tree { get; } = tree;

        /// <summary>Mark-of-the-Web of the archive (shown in the listing; Quick View never removes it).</summary>
        public MarkOfTheWeb? Origin { get; init; }

        /// <summary>The folder shown in the listing ("" for the root).</summary>
        public string Folder { get; set; } = string.Empty;

        /// <summary>The item to select when the listing is shown (the image or folder just left).</summary>
        public string? SelectPath { get; set; }

        /// <summary>Index in <see cref="ArchiveTree.Images"/> in image mode; -1 shows the listing.</summary>
        public int ImageIndex { get; set; } = -1;

        /// <summary>Upright sizes of images already read (by entry index), so a cached image shows without reading.</summary>
        public Dictionary<int, (uint Width, uint Height)> Dimensions { get; } = [];

        /// <summary>What Quick View decompressed ahead by itself for this archive (bounded by <see cref="ArchiveLimits.MaxTotalBytes"/>).</summary>
        public DecompressionTotals PrefetchTotals { get; } = new();

        public bool Matches(PreviewItem item) =>
            string.Equals(item.Path, Path, StringComparison.OrdinalIgnoreCase) && item.Facts.Length == Length && item.Facts.LastWriteUtc == LastWriteUtc;
    }

    /// <summary>The listing: summary and warnings, the folder path with "up", and one folder's items (virtualized).</summary>
    private sealed class ListingView
    {
        private const string RowTemplate =
            "<DataTemplate xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\">" +
            "<Grid Height=\"32\" ColumnSpacing=\"12\">" +
            "<Grid.ColumnDefinitions><ColumnDefinition Width=\"20\"/><ColumnDefinition Width=\"*\"/><ColumnDefinition Width=\"Auto\"/><ColumnDefinition Width=\"Auto\"/></Grid.ColumnDefinitions>" +
            "<FontIcon FontFamily=\"Segoe Fluent Icons,Segoe MDL2 Assets\" FontSize=\"16\" VerticalAlignment=\"Center\"/>" +
            "<TextBlock Grid.Column=\"1\" VerticalAlignment=\"Center\" TextTrimming=\"CharacterEllipsis\"/>" +
            "<TextBlock Grid.Column=\"2\" VerticalAlignment=\"Center\" MinWidth=\"72\" HorizontalTextAlignment=\"Right\" Opacity=\"0.7\"/>" +
            "<TextBlock Grid.Column=\"3\" VerticalAlignment=\"Center\" MinWidth=\"120\" Opacity=\"0.7\"/>" +
            "</Grid></DataTemplate>";

        private readonly ArchiveContent _owner;
        private readonly Session _session;
        private readonly Microsoft.Windows.ApplicationModel.Resources.ResourceLoader _strings;
        private readonly TextBlock _path;
        private readonly Button _up;
        private readonly ListView _list;
        private readonly TextBlock _status;
        private readonly TextBlock _empty;

        public ListingView(ArchiveContent owner, Session session)
        {
            _owner = owner;
            _session = session;
            _strings = owner._s.Strings;
            QuickViewWindow window = owner._s.Window;
            ArchiveSummary summary = session.Tree.Summary;
            Brush secondary = window.ThemeBrush("QvSecondaryTextBrush");

            var root = new Grid { MaxWidth = 960, Margin = new Thickness(20, 16, 20, 12), RowSpacing = 8 };
            for (int i = 0; i < 5; i++)
            {
                root.RowDefinitions.Add(new RowDefinition { Height = i == 3 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
            }

            // Header: name, counts and sizes, the "show images" action.
            var header = new Grid { ColumnSpacing = 12 };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.Children.Add(new FontIcon { Glyph = UniversalContent.IconGlyph(FileKindCategory.Archive), FontSize = 32, VerticalAlignment = VerticalAlignment.Center });
            var titles = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
            var title = new TextBlock { Text = System.IO.Path.GetFileName(session.Path), FontSize = 16, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
            title.SetValue(AutomationProperties.HeadingLevelProperty, Microsoft.UI.Xaml.Automation.Peers.AutomationHeadingLevel.Level1);
            titles.Children.Add(title);
            titles.Children.Add(new TextBlock
            {
                Text = string.Format(
                    CultureInfo.CurrentCulture,
                    _strings.GetString("QuickView_Archive_Summary"),
                    summary.Files.ToString("N0", CultureInfo.CurrentCulture),
                    summary.Folders.ToString("N0", CultureInfo.CurrentCulture),
                    ByteSizeText.Format(summary.DeclaredSize),
                    ByteSizeText.Format(summary.CompressedSize)),
                Foreground = secondary,
                TextWrapping = TextWrapping.Wrap,
            });
            Grid.SetColumn(titles, 1);
            header.Children.Add(titles);
            if (summary.Images > 0)
            {
                var showImages = new Button
                {
                    Content = string.Format(CultureInfo.CurrentCulture, _strings.GetString("QuickView_Archive_ShowImages"), summary.Images.ToString("N0", CultureInfo.CurrentCulture)),
                    VerticalAlignment = VerticalAlignment.Center,
                    Style = (Style)Application.Current.Resources["AccentButtonStyle"],
                };
                AutomationProperties.SetAutomationId(showImages, "QuickViewArchiveShowImages");
                showImages.Click += (_, _) => owner.ShowImagesFromFolder();
                Grid.SetColumn(showImages, 2);
                header.Children.Add(showImages);
            }

            root.Children.Add(header);

            // Where it came from, and warnings (declared values; nothing was decompressed to find them).
            var warnings = new StackPanel { Spacing = 2 };
            if (session.Origin is { IsFromInternet: true } origin)
            {
                AddLine(warnings, "\uE774", origin.SourceHost is { } host
                    ? string.Format(CultureInfo.CurrentCulture, _strings.GetString("QuickView_Card_FromInternet"), host)
                    : _strings.GetString("QuickView_Card_FromInternetNoHost"));
            }

            AddWarning(warnings, summary.UnsafeNames, "QuickView_Archive_Warn_UnsafeNames");
            AddWarning(warnings, summary.SuspiciousRatio, "QuickView_Archive_Warn_Ratio");
            AddWarning(warnings, summary.Encrypted, "QuickView_Archive_Warn_Encrypted");
            AddWarning(warnings, summary.UnsupportedMethod, "QuickView_Archive_Warn_Unsupported");
            AddWarning(warnings, summary.NestedArchives, "QuickView_Archive_Warn_Nested");
            Grid.SetRow(warnings, 1);
            root.Children.Add(warnings);

            // Where we are, and up one level.
            var bar = new Grid { ColumnSpacing = 8 };
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            _up = new Button { Content = new FontIcon { Glyph = "\uE74A", FontSize = 14 }, Padding = new Thickness(8, 5, 8, 5) };
            string upText = _strings.GetString("QuickView_Archive_Up");
            AutomationProperties.SetName(_up, upText);
            AutomationProperties.SetAutomationId(_up, "QuickViewArchiveUp");
            ToolTipService.SetToolTip(_up, upText);
            _up.Click += (_, _) => owner.GoUp();
            bar.Children.Add(_up);
            _path = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = secondary };
            AutomationProperties.SetAutomationId(_path, "QuickViewArchivePath");
            Grid.SetColumn(_path, 1);
            bar.Children.Add(_path);
            Grid.SetRow(bar, 2);
            root.Children.Add(bar);

            // The items of one folder (virtualized: only visible rows are built).
            _list = new ListView
            {
                SelectionMode = ListViewSelectionMode.Single,
                ItemTemplate = (DataTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load(RowTemplate),
                IsTabStop = true,
            };
            AutomationProperties.SetName(_list, _strings.GetString("QuickView_Archive_ListName"));
            AutomationProperties.SetAutomationId(_list, "QuickViewArchiveList");
            ToolTipService.SetToolTip(_list, _strings.GetString("QuickView_Archive_Hint"));
            _list.ContainerContentChanging += OnContainerContentChanging;
            _list.DoubleTapped += (_, _) => OpenSelected();
            _list.PreviewKeyDown += OnListPreviewKeyDown;
            Grid.SetRow(_list, 3);
            root.Children.Add(_list);
            _empty = new TextBlock { Text = _strings.GetString("QuickView_Archive_Empty"), Visibility = Visibility.Collapsed, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Foreground = secondary };
            Grid.SetRow(_empty, 3);
            root.Children.Add(_empty);

            _status = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = secondary };
            AutomationProperties.SetAutomationId(_status, "QuickViewArchiveStatus");
            AutomationProperties.SetLiveSetting(_status, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
            Grid.SetRow(_status, 4);
            root.Children.Add(_status);

            Root = root;
            AutomationProperties.SetAutomationId(Root, "QuickViewArchive");
        }

        public FrameworkElement Root { get; }

        public void ShowFolder(string folder, string? select)
        {
            IReadOnlyList<ArchiveNode> children = _session.Tree.Children(folder);
            _list.ItemsSource = children as List<ArchiveNode> ?? children.ToList();
            _empty.Visibility = children.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            _up.IsEnabled = folder.Length > 0;
            string name = System.IO.Path.GetFileName(_session.Path);
            _path.Text = folder.Length == 0 ? name : name + " › " + string.Join(" › ", folder.Split('/').Select(ArchiveEntryName.ForDisplay));
            _status.Text = string.Empty;
            if (select is not null)
            {
                for (int i = 0; i < children.Count; i++)
                {
                    if (string.Equals(children[i].Path, select, StringComparison.Ordinal))
                    {
                        _list.SelectedIndex = i;
                        _list.ScrollIntoView(children[i]);
                        break;
                    }
                }
            }
        }

        public void SetStatus(string text) => _status.Text = text;

        private void AddWarning(StackPanel warnings, int count, string resource)
        {
            if (count <= 0)
            {
                return;
            }

            AddLine(warnings, "\uE7BA", string.Format(CultureInfo.CurrentCulture, _strings.GetString(resource), count.ToString("N0", CultureInfo.CurrentCulture)));
        }

        private static void AddLine(StackPanel lines, string glyph, string text)
        {
            var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            line.Children.Add(new FontIcon { Glyph = glyph, FontSize = 14, VerticalAlignment = VerticalAlignment.Center });
            var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
            line.Children.Add(block);
            AutomationProperties.SetName(line, text);
            lines.Children.Add(line);
        }

        private void OpenSelected()
        {
            if (_list.SelectedItem is ArchiveNode node)
            {
                _owner.Open(node);
            }
        }

        /// <summary>Enter opens, Backspace goes up, Space closes Quick View (the list would otherwise take it).</summary>
        private void OnListPreviewKeyDown(object sender, KeyRoutedEventArgs e)
        {
            switch (e.Key)
            {
                case VirtualKey.Enter when !e.KeyStatus.WasKeyDown:
                    e.Handled = true;
                    OpenSelected();
                    break;
                case VirtualKey.Back:
                    e.Handled = true;
                    _owner.GoUp();
                    break;
                case VirtualKey.Space when !e.KeyStatus.WasKeyDown:
                    e.Handled = true;
                    _owner._s.Window.RequestClose();
                    break;
            }
        }

        private void OnContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
            if (args.InRecycleQueue || args.Item is not ArchiveNode node || args.ItemContainer.ContentTemplateRoot is not Grid row)
            {
                return;
            }

            ZipEntry? entry = node.Entry;
            string glyph = node.IsFolder
                ? UniversalContent.IconGlyph(FileKindCategory.Folder)
                : node.IsUnsafe ? "\uE7BA"
                : entry is { IsEncrypted: true } ? "\uE72E"
                : UniversalContent.IconGlyph(UniversalFileInfo.CategoryFor(System.IO.Path.GetExtension(node.Path)));
            string size = node.IsFolder
                ? string.Format(CultureInfo.CurrentCulture, _strings.GetString("QuickView_Archive_FolderItems"), node.ChildCount.ToString("N0", CultureInfo.CurrentCulture))
                : ByteSizeText.Format(entry?.Size ?? 0);
            string date = entry?.Modified is { } modified ? modified.ToString("g", CultureInfo.CurrentCulture) : string.Empty;
            ((FontIcon)row.Children[0]).Glyph = glyph;
            ((TextBlock)row.Children[1]).Text = node.Name;
            ((TextBlock)row.Children[2]).Text = size;
            ((TextBlock)row.Children[3]).Text = date;

            var name = new List<string> { node.Name };
            if (node.IsFolder)
            {
                name.Add(_strings.GetString("QuickView_Archive_Folder"));
            }

            name.Add(size);
            if (node.IsUnsafe)
            {
                name.Add(_strings.GetString("QuickView_Archive_Unsafe"));
            }

            if (entry is { IsEncrypted: true })
            {
                name.Add(_strings.GetString("QuickView_Archive_Encrypted"));
            }

            AutomationProperties.SetName(args.ItemContainer, string.Join(", ", name));
            args.Handled = true;
        }
    }

    /// <summary>Image mode: a small bar over the image's corner with "back to the list", the entry and its position.</summary>
    private sealed class ImageBar
    {
        private readonly ArchiveContent _owner;
        private readonly Border _pill;
        private readonly TextBlock _name;
        private readonly TextBlock _position;

        public ImageBar(ArchiveContent owner)
        {
            _owner = owner;
            var back = new Button
            {
                Content = new FontIcon { Glyph = "\uE72B", FontSize = 14 },
                Padding = new Thickness(8, 5, 8, 5),
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                BorderThickness = new Thickness(0),
            };
            string backText = owner._s.Strings.GetString("QuickView_Archive_BackToList");
            AutomationProperties.SetName(back, backText);
            AutomationProperties.SetAutomationId(back, "QuickViewArchiveBack");
            ToolTipService.SetToolTip(back, backText);
            back.Click += (_, _) => owner.BackToList();
            _name = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 520 };
            _position = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            content.Children.Add(back);
            content.Children.Add(_name);
            content.Children.Add(_position);
            _pill = new Border
            {
                Child = content,
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(2, 2, 12, 2),
                Margin = new Thickness(12),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
            };
            AutomationProperties.SetAutomationId(_pill, "QuickViewArchiveImageBar");

            // No background on the layer itself: the image keeps the pointer (zoom, pan, drag) outside the bar.
            var layer = new Grid();
            layer.Children.Add(_pill);
            Root = layer;
        }

        public FrameworkElement Root { get; }

        public void Set(string name, string position)
        {
            QuickViewWindow window = _owner._s.Window;
            _pill.Background = window.ThemeBrush("QvOverlayBrush"); // follows a theme change
            _pill.BorderBrush = window.ThemeBrush("QvOverlayBorderBrush");
            _position.Foreground = window.ThemeBrush("QvSecondaryTextBrush");
            _name.Text = name;
            _position.Text = position;
            AutomationProperties.SetName(_pill, name + ", " + position);
        }
    }
}
