using System.Globalization;
using System.Runtime.InteropServices;
using Mavue.Core.Formats;
using Mavue.QuickView.Diagnostics;
using Mavue.QuickView.Preview;
using Mavue.QuickView.Shell;
using Mavue.QuickView.Trigger;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Windows.ApplicationModel.Resources;
using Windows.Data.Pdf;
using Windows.Graphics;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Mavue.QuickView.Host;

/// <summary>
/// Shows, fills and hides the Quick View window. All members run on the UI thread except
/// <see cref="HandleEscapeFromHook"/>, which is called on the keyboard hook thread.
/// </summary>
internal sealed class QuickViewController : IDisposable
{
    // Windows that briefly take the foreground while the user switches windows (Alt+Tab, Win+Tab,
    // taskbar). Hiding on these would close Quick View in the middle of a switch back to Explorer.
    private static readonly HashSet<string> TransientForegroundClasses = new(StringComparer.Ordinal)
    {
        "XamlExplorerHostIslandWindow",
        "MultitaskingViewFrame",
        "ForegroundStaging",
        "TaskSwitcherWnd",
        "Shell_TrayWnd",
        "Shell_SecondaryTrayWnd",
    };

    private static readonly ResourceLoader Strings = new();

    private const uint NoActivateFlags = NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE;

    private readonly QuickViewWindow _window;
    private readonly QuickViewTimeline _timeline;
    private readonly HostOptions _options;
    private readonly DispatcherQueue _dispatcher;
    private readonly StaThread _shellThread = new("Mavue.QuickView.ShellItems");
    private CancellationTokenSource? _loading;
    private string? _currentPath;
    private long _currentRequest;
    private bool _visible;
    private nint _owner;
    private volatile bool _visibleWithoutActivation;
    private bool _topmost;
    private bool? _lastHookProbe;
    private nint _ownerForHook;

    public QuickViewController(QuickViewWindow window, QuickViewTimeline timeline, HostOptions options)
    {
        _window = window;
        _timeline = timeline;
        _options = options;
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _window.CloseRequested += () => Hide(_currentRequest, "window-key-or-close", restoreOwner: true);
    }

    private AppWindow AppWindow => _window.AppWindow;

    public void Dispose()
    {
        _loading?.Cancel();
        _loading?.Dispose();
        _loading = null;
        _shellThread.Dispose();
    }

    /// <summary>
    /// Renders the hidden window once (cloaked, so nothing is visible) so the first Space press does
    /// not pay for XAML tree creation, swap chain setup and DWM registration.
    /// </summary>
    public async Task PrewarmAsync()
    {
        long start = QuickViewTimeline.Now;
        PlaceWindow(0);
        int cloak = 1;
        NativeMethods.DwmSetWindowAttribute(_window.Handle, NativeMethods.DWMWA_CLOAK, ref cloak, sizeof(int));
        AppWindow.Show(false);
        await NextFrameAsync();
        AppWindow.Hide();
        cloak = 0;
        NativeMethods.DwmSetWindowAttribute(_window.Handle, NativeMethods.DWMWA_CLOAK, ref cloak, sizeof(int));

        // The first thumbnail-cache lookup in a process costs ~60 ms of shell initialization (measured).
        string self = System.Environment.ProcessPath ?? string.Empty;
        await _shellThread.InvokeAsync(() => File.Exists(self) ? ShellThumbnail.TryGetCached(self, 32) : null);
        _timeline.Mark(0, "prewarmed", QuickViewTimeline.Now, new Dictionary<string, object?> { ["startQpc"] = start });
    }

    /// <summary>Handles a Space trigger once its selection is known.</summary>
    public void OnTrigger(long requestId, SpaceTrigger trigger, ExplorerSelection? selection)
    {
        _timeline.Mark(requestId, "dispatched", QuickViewTimeline.Now);
        if (selection is null || selection.Paths.Count == 0)
        {
            _timeline.Mark(requestId, "no-selection", QuickViewTimeline.Now);
            return;
        }

        string path = selection.Paths[0];
        if (_visible && string.Equals(path, _currentPath, StringComparison.OrdinalIgnoreCase))
        {
            // Space in Explorer while Quick View shows the same item closes it (Quick Look behavior).
            Hide(requestId, "space-toggle", restoreOwner: false);
            return;
        }

        _owner = trigger.ForegroundWindow;
        Volatile.Write(ref _ownerForHook, _owner);
        if (trigger.HookProbeResult is { } probe)
        {
            _timeline.Mark(requestId, "hook-probe", QuickViewTimeline.Now, new Dictionary<string, object?> { ["allowSetForegroundInHook"] = probe });
        }

        _lastHookProbe = trigger.HookProbeResult;
        Show(requestId, path);
    }

    /// <summary>Called on the hook thread for Escape. Only used when the window is shown without activation.</summary>
    public bool HandleEscapeFromHook(nint foreground)
    {
        if (!_visibleWithoutActivation || foreground != Volatile.Read(ref _ownerForHook))
        {
            return false;
        }

        long request = Interlocked.Read(ref _currentRequest);
        _dispatcher.TryEnqueue(DispatcherQueuePriority.High, () => Hide(request, "escape-hook", restoreOwner: false));
        return true;
    }

    /// <summary>Hides Quick View when the user switches to an unrelated application.</summary>
    public void OnForegroundChanged(nint foreground)
    {
        if (!_visible || foreground == 0)
        {
            return;
        }

        if (foreground == _window.Handle)
        {
            if (_visibleWithoutActivation)
            {
                _visibleWithoutActivation = false; // user clicked into Quick View: normal window behavior from now on
                _window.FocusContent();
            }

            return;
        }

        nint root = NativeMethods.GetAncestor(foreground, NativeMethods.GA_ROOT);
        if (root == _owner || foreground == _owner)
        {
            return; // back to the Explorer window Quick View belongs to: stay open
        }

        string windowClass = Interop.WindowClass.Of(root);
        if (TransientForegroundClasses.Contains(windowClass))
        {
            _timeline.Mark(_currentRequest, "foreground-transient", QuickViewTimeline.Now, new Dictionary<string, object?> { ["class"] = windowClass });
            return;
        }

        Hide(_currentRequest, "foreground-other-app", restoreOwner: false);
    }

    private void Show(long requestId, string path)
    {
        _loading?.Cancel();
        _loading?.Dispose();
        _loading = new CancellationTokenSource();
        _currentPath = path;
        Interlocked.Exchange(ref _currentRequest, requestId);

        string fileName = Path.GetFileName(path);
        _window.ResetContent(fileName);
        _window.Title = string.Format(CultureInfo.CurrentCulture, Strings.GetString("QuickView_WindowTitle"), fileName);
        if (!_visible)
        {
            PlaceWindow(_owner);
        }

        _timeline.Mark(requestId, "show-call", QuickViewTimeline.Now);
        Activate(requestId);
        _visible = true;
        if (!_visibleWithoutActivation)
        {
            _window.FocusContent();
        }

        _ = MarkNextFrameAsync(requestId, "first-frame");
        _ = CheckForegroundLaterAsync(requestId);
        _ = LoadAsync(requestId, path, _loading.Token);
    }

    private void Activate(long requestId)
    {
        nint hwnd = _window.Handle;
        bool? setForegroundResult = null;
        int lastError = 0;
        bool attached = false;

        bool fellBack = false;
        switch (_options.Activation)
        {
            case ActivationMode.Auto:
                if (_lastHookProbe == true)
                {
                    // The hook callback reserved foreground rights while the Space event was processed.
                    AppWindow.Show(true);
                    if (NativeMethods.GetForegroundWindow() != hwnd)
                    {
                        setForegroundResult = NativeMethods.SetForegroundWindow(hwnd);
                        lastError = Marshal.GetLastPInvokeError();
                    }
                }

                if (NativeMethods.GetForegroundWindow() != hwnd)
                {
                    // No rights: show passively. Attempting activation here would only flash the taskbar.
                    NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOP, 0, 0, 0, 0, NoActivateFlags | NativeMethods.SWP_SHOWWINDOW);
                    _visibleWithoutActivation = true;
                    fellBack = true;
                }

                break;

            case ActivationMode.AppWindowShow:
                AppWindow.Show(true);
                break;

            case ActivationMode.SetForeground:
            case ActivationMode.HookGrant:
                AppWindow.Show(true);
                if (NativeMethods.GetForegroundWindow() != hwnd)
                {
                    setForegroundResult = NativeMethods.SetForegroundWindow(hwnd);
                    lastError = Marshal.GetLastPInvokeError();
                }

                break;

            case ActivationMode.AttachThreadInput:
                AppWindow.Show(true);
                if (NativeMethods.GetForegroundWindow() != hwnd)
                {
                    uint foregroundThread = NativeMethods.GetWindowThreadProcessId(NativeMethods.GetForegroundWindow(), out _);
                    uint thisThread = NativeMethods.GetCurrentThreadId();
                    attached = foregroundThread != thisThread && NativeMethods.AttachThreadInput(thisThread, foregroundThread, true);
                    setForegroundResult = NativeMethods.SetForegroundWindow(hwnd);
                    lastError = Marshal.GetLastPInvokeError();
                    if (attached)
                    {
                        NativeMethods.AttachThreadInput(thisThread, foregroundThread, false);
                    }
                }

                break;

            case ActivationMode.NoActivate:
                // Shown and raised to the top of the (non-topmost) z-order without activation.
                setForegroundResult = NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOP, 0, 0, 0, 0, NoActivateFlags | NativeMethods.SWP_SHOWWINDOW);
                _visibleWithoutActivation = true;
                break;

            case ActivationMode.Panel:
                setForegroundResult = NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0, NoActivateFlags | NativeMethods.SWP_SHOWWINDOW);
                _topmost = true;
                _visibleWithoutActivation = true;
                break;

            case ActivationMode.NoActivateTopmost:
                // Comparison: momentary topmost, then back to the normal band (still above Explorer).
                NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0, NoActivateFlags | NativeMethods.SWP_SHOWWINDOW);
                setForegroundResult = NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_NOTOPMOST, 0, 0, 0, 0, NoActivateFlags);
                _visibleWithoutActivation = true;
                break;
        }

        _timeline.Mark(requestId, "shown", QuickViewTimeline.Now, new Dictionary<string, object?>
        {
            ["mode"] = _options.Activation.ToString(),
            ["aboveOwner"] = IsAbove(hwnd, _owner),
            ["isForeground"] = NativeMethods.GetForegroundWindow() == hwnd,
            ["setForegroundResult"] = setForegroundResult,
            ["lastError"] = lastError,
            ["attached"] = attached,
            ["fellBackToNoActivate"] = fellBack,
        });
    }

    private void Hide(long requestId, string reason, bool restoreOwner)
    {
        if (!_visible)
        {
            return;
        }

        _loading?.Cancel();
        nint hwnd = _window.Handle;
        bool wasForeground = NativeMethods.GetForegroundWindow() == hwnd;
        bool? ownerRestored = null;

        // While still foreground we may hand activation back explicitly; afterwards we could not.
        if (restoreOwner && wasForeground && NativeMethods.IsWindow(_owner))
        {
            ownerRestored = NativeMethods.SetForegroundWindow(_owner);
        }

        if (_topmost)
        {
            // Leave the topmost band before hiding so the window can never linger above other apps.
            NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_NOTOPMOST, 0, 0, 0, 0, NoActivateFlags);
            _topmost = false;
        }

        AppWindow.Hide();
        _visible = false;
        _visibleWithoutActivation = false;
        _currentPath = null;
        _window.ClearImages();

        _timeline.Mark(requestId, "hidden", QuickViewTimeline.Now, new Dictionary<string, object?>
        {
            ["reason"] = reason,
            ["wasForeground"] = wasForeground,
            ["ownerRestored"] = ownerRestored,
            ["ownerIsForegroundAfter"] = NativeMethods.GetForegroundWindow() == _owner,
        });
    }

    /// <summary>True if <paramref name="window"/> is above <paramref name="other"/> in the z-order.</summary>
    private static bool IsAbove(nint window, nint other)
    {
        if (other == 0)
        {
            return true;
        }

        // Walk down from the window; finding the other window below means we are above it.
        for (nint next = NativeMethods.GetWindow(window, NativeMethods.GW_HWNDNEXT); next != 0; next = NativeMethods.GetWindow(next, NativeMethods.GW_HWNDNEXT))
        {
            if (next == other)
            {
                return true;
            }
        }

        return false;
    }

    private void PlaceWindow(nint owner)
    {
        DisplayArea area = owner != 0
            ? DisplayArea.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(owner), DisplayAreaFallback.Nearest)
            : DisplayArea.Primary;
        RectInt32 work = area.WorkArea;
        int width = (int)(work.Width * 0.7);
        int height = (int)(work.Height * 0.75);
        AppWindow.MoveAndResize(new RectInt32(work.X + ((work.Width - width) / 2), work.Y + ((work.Height - height) / 2), width, height));
    }

    private async Task LoadAsync(long requestId, string path, CancellationToken cancellation)
    {
        try
        {
            SizeInt32 client = AppWindow.ClientSize;
            uint viewportWidth = (uint)Math.Max(1, client.Width);
            uint viewportHeight = (uint)Math.Max(1, client.Height);

            // 1. File facts (attributes, size, format) without decoding.
            FileFacts facts = await Task.Run(() => FileFacts.Read(path), cancellation);
            _timeline.Mark(requestId, "info", QuickViewTimeline.Now, new Dictionary<string, object?>
            {
                ["format"] = facts.Format.ToString(),
                ["extension"] = Path.GetExtension(path).ToLowerInvariant(),
                ["bytes"] = facts.Length,
                ["access"] = facts.Access.ToString(),
            });
            _window.SetInfo(FormatInfo(facts, null));

            if (facts.Access == PreviewAccess.NotAFile)
            {
                _window.SetStatus(Strings.GetString("QuickView_Error_NotFile"));
                return;
            }

            // 2. Cached shell thumbnail (never extracts, never hydrates cloud files).
            int thumbnailSize = (int)Math.Min(1024, Math.Max(viewportWidth, viewportHeight));
            Task<BgraImage?> thumbnailTask = _shellThread.InvokeAsync(() => ShellThumbnail.TryGetCached(path, thumbnailSize));

            if (facts.Access == PreviewAccess.CloudPlaceholder)
            {
                _window.SetStatus(Strings.GetString("QuickView_Error_CloudPlaceholder"));
                await ShowThumbnailAsync(requestId, thumbnailTask, cancellation);
                return;
            }

            if (_options.Decoder == DecoderMode.Xaml && facts.Format != FileFormat.Pdf)
            {
                Task xamlThumbnail = ShowThumbnailAsync(requestId, thumbnailTask, cancellation, () => _window.HasFullImage);
                await LoadWithXamlDecoderAsync(requestId, path, facts, viewportWidth, viewportHeight, cancellation);
                await xamlThumbnail;
                return;
            }

            // 3. Full quality in the background; 4. swap in when ready.
            Task<DecodedPreview> fullTask = facts.Format == FileFormat.Pdf
                ? RenderPdfAsync(path, viewportWidth, viewportHeight, cancellation)
                : DecodeImageAsync(path, viewportWidth, viewportHeight, Interpolation(_options.Interpolation), cancellation);

            Task thumbnailShown = ShowThumbnailAsync(requestId, thumbnailTask, cancellation, () => _window.HasFullImage);
            DecodedPreview full = await fullTask;
            cancellation.ThrowIfCancellationRequested();

            if (full.Reason is { } reason)
            {
                _window.SetStatus(Strings.GetString(ReasonToResource(reason)));
                _timeline.Mark(requestId, "full-skipped", QuickViewTimeline.Now, new Dictionary<string, object?> { ["reason"] = full.Reason });
                await thumbnailShown;
                return;
            }

            ImageSource source = await CreateSourceAsync(full.Bitmap!);
            cancellation.ThrowIfCancellationRequested();
            _window.SetFullImage(source);
            _window.SetInfo(FormatInfo(facts, full));
            _timeline.Mark(requestId, "full-set", QuickViewTimeline.Now, new Dictionary<string, object?>
            {
                ["sourceWidth"] = full.SourceWidth,
                ["sourceHeight"] = full.SourceHeight,
                ["decodedWidth"] = full.Bitmap!.PixelWidth,
                ["decodedHeight"] = full.Bitmap.PixelHeight,
                ["decodeMs"] = full.DecodeMilliseconds,
                ["decoder"] = full.Decoder,
            });
            await NextFrameAsync();
            if (!cancellation.IsCancellationRequested)
            {
                _timeline.Mark(requestId, "full-visible", QuickViewTimeline.Now);
            }
        }
        catch (OperationCanceledException)
        {
            // Navigation or hide superseded this request.
        }
        catch (Exception ex)
        {
            // Malformed or hostile files must never take the resident process down.
            _timeline.Mark(requestId, "error", QuickViewTimeline.Now, new Dictionary<string, object?> { ["type"] = ex.GetType().Name, ["hresult"] = ex.HResult });
            if (!cancellation.IsCancellationRequested)
            {
                _window.SetStatus(Strings.GetString("QuickView_Error_Failed"));
            }
        }
    }

    private async Task ShowThumbnailAsync(long requestId, Task<BgraImage?> thumbnailTask, CancellationToken cancellation, Func<bool>? superseded = null)
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

        _timeline.Mark(requestId, "thumbnail", QuickViewTimeline.Now, new Dictionary<string, object?>
        {
            ["cached"] = thumbnail is not null,
            ["width"] = thumbnail?.Width ?? 0,
            ["height"] = thumbnail?.Height ?? 0,
        });
        if (thumbnail is null)
        {
            _window.SetStatus(Strings.GetString("QuickView_Loading"));
            return;
        }

        _window.SetThumbnail(thumbnail);
        await NextFrameAsync();
        if (!cancellation.IsCancellationRequested && superseded?.Invoke() != true)
        {
            _timeline.Mark(requestId, "thumbnail-visible", QuickViewTimeline.Now);
        }
    }

    private static async Task<ImageSource> CreateSourceAsync(SoftwareBitmap bitmap)
    {
        var source = new SoftwareBitmapSource();
        await source.SetBitmapAsync(bitmap);
        return source;
    }

    /// <summary>
    /// Comparison path: XAML BitmapImage with DecodePixelWidth/Height. Decoding happens inside the XAML
    /// image pipeline once the source is in the live tree; completion is signalled by ImageOpened.
    /// </summary>
    private async Task LoadWithXamlDecoderAsync(long requestId, string path, FileFacts facts, uint viewportWidth, uint viewportHeight, CancellationToken cancellation)
    {
        long start = QuickViewTimeline.Now;
        IRandomAccessStream stream = await OpenReadAsync(path);
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
                _window.SetStatus(Strings.GetString("QuickView_Error_TooLarge"));
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
            _window.SetFullImagePending(image);
            bool ok = await opened.Task;
            cancellation.ThrowIfCancellationRequested();
            if (!ok)
            {
                _window.SetStatus(Strings.GetString("QuickView_Error_Failed"));
                return;
            }

            _window.CommitFullImage();
            _window.SetInfo(FormatInfo(facts, new DecodedPreview(null, width, height, 0, "xaml-bitmapimage", 0, null)));
            _timeline.Mark(requestId, "full-set", QuickViewTimeline.Now, new Dictionary<string, object?>
            {
                ["sourceWidth"] = width,
                ["sourceHeight"] = height,
                ["decodedWidth"] = fitWidth,
                ["decodedHeight"] = fitHeight,
                ["decodeMs"] = (QuickViewTimeline.Now - start) * 1000.0 / System.Diagnostics.Stopwatch.Frequency,
                ["decoder"] = "xaml-bitmapimage",
            });
            await NextFrameAsync();
            if (!cancellation.IsCancellationRequested)
            {
                _timeline.Mark(requestId, "full-visible", QuickViewTimeline.Now);
            }
        }
        finally
        {
            stream.Dispose();
        }
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

    private static Task<DecodedPreview> DecodeImageAsync(string path, uint viewportWidth, uint viewportHeight, BitmapInterpolationMode interpolation, CancellationToken cancellation) =>
        Task.Run(
            async () =>
            {
                long start = QuickViewTimeline.Now;
                using IRandomAccessStream stream = await OpenReadAsync(path);
                BitmapDecoder decoder;
                try
                {
                    decoder = await BitmapDecoder.CreateAsync(stream);
                }
                catch (Exception ex) when (ex.HResult == unchecked((int)0x88982F50))
                {
                    // WINCODEC_ERR_COMPONENTNOTFOUND: no WIC codec for this file (e.g. missing Store extension).
                    return DecodedPreview.Skipped("no-codec");
                }

                uint width = decoder.OrientedPixelWidth;
                uint height = decoder.OrientedPixelHeight;
                if (PreviewSafetyPolicy.CheckDimensions(width, height) != PreviewAccess.Allowed)
                {
                    return DecodedPreview.Skipped("too-large");
                }

                (uint fitWidth, uint fitHeight) = PreviewSizing.FitWithin(width, height, viewportWidth, viewportHeight);
                bool swapped = width != decoder.PixelWidth;
                var transform = new BitmapTransform
                {
                    // Scaling applies before EXIF rotation, so use the stored (unrotated) orientation.
                    ScaledWidth = swapped ? fitHeight : fitWidth,
                    ScaledHeight = swapped ? fitWidth : fitHeight,
                    InterpolationMode = interpolation,
                };
                cancellation.ThrowIfCancellationRequested();
                SoftwareBitmap bitmap = await decoder.GetSoftwareBitmapAsync(
                    BitmapPixelFormat.Bgra8,
                    BitmapAlphaMode.Premultiplied,
                    transform,
                    ExifOrientationMode.RespectExifOrientation,
                    ColorManagementMode.ColorManageToSRgb);
                return DecodedPreview.Decoded(bitmap, width, height, QuickViewTimeline.Now - start, "winrt-bitmapdecoder-" + interpolation);
            },
            cancellation);

    private static Task<DecodedPreview> RenderPdfAsync(string path, uint viewportWidth, uint viewportHeight, CancellationToken cancellation) =>
        Task.Run(
            async () =>
            {
                long start = QuickViewTimeline.Now;
                using IRandomAccessStream file = await OpenReadAsync(path);
                PdfDocument document;
                try
                {
                    document = await PdfDocument.LoadFromStreamAsync(file);
                }
                catch (Exception ex) when (ex.HResult == unchecked((int)0x8007052B))
                {
                    return DecodedPreview.Skipped("password");
                }

                using PdfPage page = document.GetPage(0);
                double scale = Math.Min(viewportWidth / page.Size.Width, viewportHeight / page.Size.Height);
                var options = new PdfPageRenderOptions
                {
                    DestinationWidth = (uint)Math.Max(1, page.Size.Width * scale),
                    DestinationHeight = (uint)Math.Max(1, page.Size.Height * scale),
                    BitmapEncoderId = BitmapEncoder.BmpEncoderId,
                };
                cancellation.ThrowIfCancellationRequested();
                using var rendered = new InMemoryRandomAccessStream();
                await page.RenderToStreamAsync(rendered, options);
                rendered.Seek(0);
                BitmapDecoder decoder = await BitmapDecoder.CreateAsync(rendered);
                SoftwareBitmap bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
                return DecodedPreview.Decoded(bitmap, (uint)page.Size.Width, (uint)page.Size.Height, QuickViewTimeline.Now - start, "windows-data-pdf", (int)document.PageCount);
            },
            cancellation);

    private static async Task<IRandomAccessStream> OpenReadAsync(string path) =>
        await FileRandomAccessStream.OpenAsync(path, FileAccessMode.Read, StorageOpenOptions.AllowReadersAndWriters, FileOpenDisposition.OpenExisting);

    private async Task MarkNextFrameAsync(long requestId, string mark)
    {
        await NextFrameAsync();
        _timeline.Mark(requestId, mark, QuickViewTimeline.Now);
    }

    private async Task CheckForegroundLaterAsync(long requestId)
    {
        await Task.Delay(150);
        if (_visible && Interlocked.Read(ref _currentRequest) == requestId)
        {
            _timeline.Mark(requestId, "foreground-check", QuickViewTimeline.Now, new Dictionary<string, object?>
            {
                ["isForeground"] = NativeMethods.GetForegroundWindow() == _window.Handle,
            });
        }
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

    private static string FormatInfo(FileFacts facts, DecodedPreview? decoded)
    {
        string size = FormatBytes(facts.Length);
        if (decoded is { PageCount: > 0 } pdf)
        {
            return string.Format(CultureInfo.CurrentCulture, Strings.GetString("QuickView_InfoPdf"), pdf.PageCount, size);
        }

        return decoded is { SourceWidth: > 0 } image
            ? string.Format(CultureInfo.CurrentCulture, Strings.GetString("QuickView_InfoImage"), facts.Format, image.SourceWidth, image.SourceHeight, size)
            : string.Format(CultureInfo.CurrentCulture, Strings.GetString("QuickView_InfoBasic"), facts.Format, size);
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 => string.Format(CultureInfo.CurrentCulture, "{0} B", bytes),
        < 1024 * 1024 => string.Format(CultureInfo.CurrentCulture, "{0:0.#} KB", bytes / 1024.0),
        < 1024L * 1024 * 1024 => string.Format(CultureInfo.CurrentCulture, "{0:0.#} MB", bytes / (1024.0 * 1024)),
        _ => string.Format(CultureInfo.CurrentCulture, "{0:0.##} GB", bytes / (1024.0 * 1024 * 1024)),
    };
}

/// <summary>Result of the full-quality stage.</summary>
/// <param name="Reason">Set when the full-quality stage was skipped: "no-codec", "too-large", "password".</param>
internal sealed record DecodedPreview(SoftwareBitmap? Bitmap, uint SourceWidth, uint SourceHeight, double DecodeMilliseconds, string Decoder, int PageCount, string? Reason)
{
    public static DecodedPreview Decoded(SoftwareBitmap bitmap, uint sourceWidth, uint sourceHeight, long elapsedTicks, string decoder, int pageCount = 0) =>
        new(bitmap, sourceWidth, sourceHeight, elapsedTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency, decoder, pageCount, null);

    public static DecodedPreview Skipped(string reason) => new(null, 0, 0, 0, "none", 0, reason);
}

/// <summary>File metadata gathered before any decoding.</summary>
internal sealed record FileFacts(System.IO.FileAttributes Attributes, long Length, FileFormat Format, PreviewAccess Access)
{
    public static FileFacts Read(string path)
    {
        if (PreviewSafetyPolicy.IsDevicePath(path))
        {
            return new FileFacts(0, 0, FileFormat.Unknown, PreviewAccess.NotAFile);
        }

        var info = new FileInfo(path);
        if (!info.Exists)
        {
            return new FileFacts(0, 0, FileFormat.Unknown, PreviewAccess.NotAFile);
        }

        PreviewAccess access = PreviewSafetyPolicy.CheckAttributes(info.Attributes);

        // Reading the header of a cloud placeholder would download it; rely on the extension instead.
        FileFormat format = access == PreviewAccess.Allowed
            ? FileFormatDetector.Detect(path)
            : FileFormatDetector.FromExtension(info.Extension);
        return new FileFacts(info.Attributes, info.Length, format, access);
    }
}
