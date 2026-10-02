using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Mavue.Core.Formats;
using Mavue.Core.Viewing;
using Mavue.Image.Wic;
using Mavue.QuickView.Diagnostics;
using Mavue.QuickView.Preview;
using Mavue.QuickView.Settings;
using Mavue.QuickView.Shell;
using Mavue.QuickView.Trigger;
using Mavue.Viewer.Playback;
using Mavue.Viewer.Rendering;
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
    private readonly PreviewNavigator _navigator = new();
    private SelectionWorker? _worker;
    private KeyboardHook? _hook;
    private string? _currentPath;
    private long _currentRequest;
    private bool _visible;
    private nint _owner;
    private volatile bool _visibleWithoutActivation;
    private bool _topmost;
    private bool? _lastHookProbe;
    private nint _followedView;
    private readonly WinEventWatcher _focusWatcher = new();

    // Decoded neighbors for instant stepping. Cleared when Quick View closes to keep idle memory low.
    private const long CacheBudgetBytes = 192L * 1024 * 1024;
    private const long MaxPrefetchFileBytes = 64L * 1024 * 1024;

    // Background decoding of very large neighbors raised the peak working set to ~800 MB (measured);
    // only modest images are prefetched. The pixel count is checked from the header before decoding.
    private const long MaxPrefetchSourcePixels = 40_000_000;
    private readonly PreviewCache<DecodedImage> _cache = new(
        CacheBudgetBytes,
        d => d.Bitmap is { } b ? (long)b.PixelWidth * b.PixelHeight * 4 : 0,
        d => d.Bitmap?.Dispose());
    private CancellationTokenSource? _prefetch;

    // The bitmap behind the displayed full image. It is a private copy: cached bitmaps are never handed
    // to a SoftwareBitmapSource, because disposing a replaced source also closed the cached bitmap and a
    // later cache hit then failed with E_INVALIDARG (measured with fast arrow-key navigation).
    private SoftwareBitmap? _displayedBitmap;

    // Request whose own thumbnail or image is on screen (as opposed to the previous item's).
    private long _contentRequest;

    /// <summary>How long the previous item's image may stay while the next one loads (avoids flicker).</summary>
    private static readonly TimeSpan StaleImageGrace = TimeSpan.FromMilliseconds(120);

    // Actual-size mode decodes full resolution; only small neighbors are prefetched then.
    private const long MaxActualSizePrefetchPixels = 16_000_000;

    private readonly string _settingsPath;
    private ImageScaleMode _scaleMode;
    private DateTime _settingsStamp;
    private (uint Width, uint Height) _plannedArea;
    private DispatcherQueueTimer? _areaTimer;
    private nint _ownerForHook;

    // Context-menu requests: the --quickview process allowed us to take the foreground, so the next show
    // activates normally instead of the passive panel. Without an owner, the window is placed near this.
    private bool _grantedActivation;
    private nint _placeNear;

    // PDF pages: the current page of the shown PDF. PageUp/PageDown (hook while Explorer has the keyboard,
    // or the window when Quick View is active), the mouse wheel and the page buttons step through it; the
    // arrow keys keep moving between files. Read on the hook thread through the volatile flags.
    private readonly PdfPageCursor _pdfPages = new();
    private volatile bool _pdfPaging;
    private volatile bool _multiSelectionKeys;

    // Animated GIF: at most one player, stopped whenever the shown content changes.
    // Larger canvases keep the static first frame (copying a bigger canvas every frame would load the UI thread).
    private const long MaxAnimatedGifPixels = 8_000_000;
    private GifPlayer? _gif;

    // Video/audio: one media session at a time, streaming the file (Media Foundation reads what it plays).
    // Every content change (another file, a page, closing) stops and disposes it; events of a replaced session are ignored.
    private MediaSession? _media;
    private long _lastExternalShowQpc = long.MinValue / 2;
    private static readonly long ExternalMergeWindowTicks = System.Diagnostics.Stopwatch.Frequency * 2;

    public QuickViewController(QuickViewWindow window, QuickViewTimeline timeline, HostOptions options)
    {
        _window = window;
        _timeline = timeline;
        _options = options;
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _window.CloseRequested += () => Hide(_currentRequest, "window-key-or-close", restoreOwner: true);
        _window.NavigateRequested += delta => Step(delta, "window-arrow");
        _window.PageRequested += (delta, source) => StepPage(delta, source);
        _window.MediaCommandRequested += OnMediaCommand;
        _window.KeyReceived += key => _timeline.Mark(_currentRequest, "window-key", QuickViewTimeline.Now, new Dictionary<string, object?>
        {
            ["key"] = key.ToString(),
            ["focus"] = _window.FocusDescription(),
        });
        _settingsPath = options.SettingsPath ?? QuickViewSettings.DefaultPath;
        ReloadSettingsIfChanged(force: true);
        _window.ImageAreaChanged += OnImageAreaChanged;
        _focusWatcher.EventRaised += (_, hwnd, _, _) => OnOwnerFocusChanged(hwnd);
    }

    private AppWindow AppWindow => _window.AppWindow;

    /// <summary>Connects the Shell worker and the keyboard hook (created after the controller).</summary>
    public void Attach(SelectionWorker worker, KeyboardHook hook)
    {
        _worker = worker;
        _hook = hook;
    }

    public void Dispose()
    {
        StopGif();
        StopMedia("exit");
        _prefetch?.Cancel();
        _prefetch?.Dispose();
        _cache.Dispose();
        _focusWatcher.Dispose();
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
    public void OnTrigger(long requestId, SpaceTrigger trigger, ViewSelection? selection)
    {
        _timeline.Mark(requestId, "dispatched", QuickViewTimeline.Now);
        if (_visible)
        {
            // Space in Explorer while Quick View is open closes it (Quick Look behavior). Quick View
            // already follows the selection, so there is no "switch to another item" case here.
            Hide(requestId, "space-toggle", restoreOwner: false);
            return;
        }

        if (selection is null || selection.Paths.Count == 0)
        {
            _timeline.Mark(requestId, "no-selection", QuickViewTimeline.Now);
            _worker?.CloseSession();
            return;
        }

        NavigationResult start = _navigator.Start(selection.Paths, selection.FocusedPath);
        string path = start.Path!;

        _owner = trigger.ForegroundWindow;
        Volatile.Write(ref _ownerForHook, _owner);
        if (trigger.HookProbeResult is { } probe)
        {
            _timeline.Mark(requestId, "hook-probe", QuickViewTimeline.Now, new Dictionary<string, object?> { ["allowSetForegroundInHook"] = probe });
        }

        _lastHookProbe = trigger.HookProbeResult;
        _followedView = trigger.FocusParentWindow;
        MarkNavigation(requestId, "space");
        Show(requestId, path);
        UpdateNavigationKeys();
        WatchOwnerFocus();
    }

    /// <summary>
    /// Handles a context-menu request (<c>--quickview</c>) once its selection is known. Unlike Space it never
    /// toggles: if Quick View already shows this view's selection (another file of the same multi-selection
    /// arriving late), nothing changes; otherwise it shows the new selection.
    /// </summary>
    /// <param name="owner">Explorer window whose selection is followed, or 0 when showing the given files.</param>
    /// <param name="view">That window's SHELLDLL_DefView.</param>
    /// <param name="foreground">Foreground window when the command was invoked (placement when there is no owner).</param>
    public void OnExternalRequest(long requestId, nint owner, nint view, nint foreground, ViewSelection selection)
    {
        _timeline.Mark(requestId, "dispatched", QuickViewTimeline.Now);
        if (selection.Paths.Count == 0)
        {
            _timeline.Mark(requestId, "no-selection", QuickViewTimeline.Now);
            return;
        }

        if (_visible)
        {
            if (owner != 0 && owner == _owner && selection.Paths.All(p => _navigator.Items.Contains(p, StringComparer.OrdinalIgnoreCase)))
            {
                // Another file of the multi-selection already being shown (Explorer starts one process per file).
                _timeline.Mark(requestId, "external-already-shown", QuickViewTimeline.Now);
                return;
            }

            if (owner == 0 && _owner == 0 && _currentPath is { } current &&
                QuickViewTimeline.Now - _lastExternalShowQpc < ExternalMergeWindowTicks)
            {
                // No Explorer view to read: the files of one command arrive one by one; add them to the list.
                List<string> merged = [.. _navigator.Items];
                merged.AddRange(selection.Paths.Where(p => !merged.Contains(p, StringComparer.OrdinalIgnoreCase)));
                _navigator.Start(merged, current);
                _timeline.Mark(requestId, "external-merged", QuickViewTimeline.Now, new Dictionary<string, object?> { ["count"] = merged.Count });
                ShowNext(requestId, current, sameItem: true); // refreshes "i / N"
                return;
            }

            // The worker already subscribed to the new view; keep that session.
            Hide(requestId, "external-replace", restoreOwner: false, closeSession: false);
        }

        NavigationResult start = _navigator.Start(selection.Paths, selection.FocusedPath);
        _owner = owner;
        Volatile.Write(ref _ownerForHook, owner);
        _lastHookProbe = null;
        _followedView = view;
        _placeNear = owner != 0 ? owner : foreground;
        _grantedActivation = true;
        _lastExternalShowQpc = QuickViewTimeline.Now;
        MarkNavigation(requestId, "context-menu");
        Show(requestId, start.Path!);
        UpdateNavigationKeys();
        if (owner != 0)
        {
            WatchOwnerFocus();
        }
    }

    /// <summary>
    /// While Quick View is open, focus changes inside the owning Explorer process are observed
    /// (EVENT_OBJECT_FOCUS, scoped to that process) to notice when the user switches to another tab.
    /// </summary>
    private void WatchOwnerFocus()
    {
        _focusWatcher.Stop();
        NativeMethods.GetWindowThreadProcessId(_owner, out uint explorerProcess);
        if (explorerProcess == 0)
        {
            return;
        }

        try
        {
            _focusWatcher.Start(WinEventWatcher.EventObjectFocus, WinEventWatcher.EventObjectFocus, explorerProcess);
        }
        catch (InvalidOperationException)
        {
            // Following tab switches is best effort; selection events still work.
        }
    }

    private void OnOwnerFocusChanged(nint focus)
    {
        if (!_visible || focus == 0)
        {
            return;
        }

        nint parent = NativeMethods.GetAncestor(focus, NativeMethods.GA_PARENT);
        if (!ShellViewFocus.IsOtherItemView(
            Interop.WindowClass.Of(focus),
            Interop.WindowClass.Of(parent),
            parent,
            NativeMethods.GetAncestor(focus, NativeMethods.GA_ROOT),
            _owner,
            _followedView))
        {
            return;
        }

        // Another tab (or another view in the same window) took the focus: follow its selection.
        _followedView = parent;
        _timeline.Mark(_currentRequest, "view-switched", QuickViewTimeline.Now);
        _worker?.Rebind(_owner, parent);
    }

    /// <summary>Explorer's selection changed while Quick View is open (DShellFolderViewEvents).</summary>
    public void OnExplorerSelectionChanged(ViewSelection selection, long eventQpc, long readQpc)
    {
        if (!_visible)
        {
            return;
        }

        Apply(_navigator.OnSelectionChanged(selection.Paths, selection.FocusedPath), "explorer-selection", eventQpc, readQpc);
    }

    /// <summary>The Explorer window or tab Quick View follows was closed.</summary>
    public void OnExplorerViewClosed() => Hide(_currentRequest, "explorer-closed", restoreOwner: false);

    /// <summary>
    /// Hook thread: ←/→ while a multi-selection is previewed passively. Steps inside the selection
    /// and swallows the key so Explorer keeps the selection intact.
    /// </summary>
    public bool HandleNavigationKeyFromHook(int virtualKey, nint foreground)
    {
        const int VkPageUp = 0x21;
        const int VkPageDown = 0x22;
        const int VkLeft = 0x25;
        const int VkRight = 0x27;
        if (foreground != Volatile.Read(ref _ownerForHook) || !_visibleWithoutActivation)
        {
            return false;
        }

        long qpc = QuickViewTimeline.Now;
        if (virtualKey is VkPageUp or VkPageDown)
        {
            if (!_pdfPaging)
            {
                return false; // not a multi-page PDF: Explorer keeps PageUp/PageDown
            }

            // Swallowed even at the first/last page: otherwise Explorer would move its selection by a screen.
            int pageDelta = virtualKey == VkPageUp ? -1 : 1;
            _dispatcher.TryEnqueue(DispatcherQueuePriority.High, () => StepPage(pageDelta, "hook-page", qpc));
            return true;
        }

        if (virtualKey is not (VkLeft or VkRight) || !_multiSelectionKeys)
        {
            return false;
        }

        int delta = virtualKey == VkLeft ? -1 : 1;
        _dispatcher.TryEnqueue(DispatcherQueuePriority.High, () => Step(delta, "hook-arrow", qpc));
        return true;
    }

    /// <summary>Previous/next page of the shown PDF. Past the first or last page nothing happens.</summary>
    private void StepPage(int delta, string source, long? inputQpc = null)
    {
        if (!_visible || _currentPath is not { } path || !_pdfPages.IsMultiPage)
        {
            return;
        }

        if (!_pdfPages.TryStep(delta))
        {
            _timeline.Mark(_currentRequest, "pdf-page-edge", QuickViewTimeline.Now, new Dictionary<string, object?>
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
            _timeline.Mark(id, "nav-input", input);
        }

        _timeline.Mark(id, "pdf-page", QuickViewTimeline.Now, new Dictionary<string, object?>
        {
            ["index"] = _pdfPages.Index,
            ["count"] = _pdfPages.Count,
            ["source"] = source,
        });
        MarkNavigation(id, "pdf-page");
        UpdatePageControls();
        _window.Surface.ScrollToTop();
        ShowNext(id, path, sameItem: true);
    }

    private void UpdatePageControls()
    {
        _pdfPaging = _visible && _pdfPages.IsMultiPage;
        _window.SetPageControls(_pdfPaging, _pdfPages.CanStep(-1), _pdfPages.CanStep(+1));
        UpdateNavigationKeys();
    }

    private void Step(int delta, string source, long? inputQpc = null)
    {
        if (!_visible)
        {
            return;
        }

        Apply(_navigator.Step(delta), source, inputQpc, null);
    }

    private void Apply(NavigationResult result, string source, long? inputQpc, long? readQpc)
    {
        switch (result.Action)
        {
            case NavigationAction.Show:
                long id = SelectionWorker.NextRequestId();
                if (inputQpc is { } input)
                {
                    _timeline.Mark(id, "nav-input", input);
                }

                if (readQpc is { } read)
                {
                    _timeline.Mark(id, "selection-read", read);
                }

                MarkNavigation(id, source);
                ShowNext(id, result.Path!);
                break;

            case NavigationAction.Hide:
                Hide(_currentRequest, "selection-empty", restoreOwner: false);
                break;

            case NavigationAction.MoveExplorerSelection:
                _worker?.MoveExplorerSelection(result.Delta);
                break;
        }

        UpdateNavigationKeys();
    }

    private void MarkNavigation(long requestId, string source) =>
        _timeline.Mark(requestId, "nav", QuickViewTimeline.Now, new Dictionary<string, object?>
        {
            ["source"] = source,
            ["mode"] = _navigator.Mode.ToString(),
            ["index"] = _navigator.Index,
            ["count"] = _navigator.Items.Count,
        });

    /// <summary>Arms ←/→ interception only while a multi-selection is shown and Explorer has the keyboard.</summary>
    private void UpdateNavigationKeys()
    {
        if (_hook is null)
        {
            return;
        }

        bool passive = _visible && _visibleWithoutActivation;
        _multiSelectionKeys = passive && _navigator.Mode == NavigationMode.MultipleItems;
        _hook.NavigationKeyHandler = _multiSelectionKeys || (passive && _pdfPaging) ? HandleNavigationKeyFromHook : null;
    }

    /// <summary>Shows another item in the already visible window, keeping the previous image until the new one is ready.</summary>
    /// <param name="sameItem">Re-showing the current file (e.g. new window size): keep its image until the new one is ready.</param>
    private void ShowNext(long requestId, string path, bool sameItem = false)
    {
        StopGif();
        StopMedia("next-item");
        if (!sameItem)
        {
            _pdfPages.Reset(); // another file starts at its first page
            UpdatePageControls();
        }

        _prefetch?.Cancel();
        _loading?.Cancel();
        _loading?.Dispose();
        _loading = new CancellationTokenSource();
        _currentPath = path;
        Interlocked.Exchange(ref _currentRequest, requestId);

        string fileName = Path.GetFileName(path);
        _window.BeginNextItem(fileName);
        if (!sameItem)
        {
            _ = ClearStaleImageAsync(requestId);
        }

        _window.Title = string.Format(CultureInfo.CurrentCulture, Strings.GetString("QuickView_WindowTitle"), fileName);
        _timeline.Mark(requestId, "show-call", QuickViewTimeline.Now);
        _ = MarkNextFrameAsync(requestId, "first-frame");
        _ = LoadAsync(requestId, path, _loading.Token);
    }

    /// <summary>
    /// If the next item has produced nothing to show shortly after navigation, remove the previous item's
    /// image and show "loading" so a stale picture is never presented under the new file name.
    /// </summary>
    private async Task ClearStaleImageAsync(long requestId)
    {
        await Task.Delay(StaleImageGrace);
        if (_visible && Interlocked.Read(ref _currentRequest) == requestId && _contentRequest != requestId)
        {
            _window.Surface.ShowStatusOnly(Strings.GetString("QuickView_Loading"));
            _timeline.Mark(requestId, "stale-cleared", QuickViewTimeline.Now);
        }
    }

    /// <summary>Decode/display plan for one item (see <see cref="DisplaySizing"/>).</summary>
    private readonly record struct Plan(DecodeBox Box, (double Width, double Height)? ExpectedElementSize, bool Scrollable, double? PdfActualScale)
    {
        public static Plan Unknown => new(new DecodeBox(1, 1), null, false, null);
    }

    /// <summary>Physical pixels per device-independent pixel for the window's current monitor.</summary>
    private double DisplayScale()
    {
        uint dpi = NativeMethods.GetDpiForWindow(_window.Handle);
        return dpi > 0 ? dpi / 96.0 : _window.RasterizationScale;
    }

    /// <summary>
    /// Image area in physical pixels, from the window's real client size and DPI. The XAML layout is not
    /// used: right after the window moves to a monitor with another scale it still describes the old
    /// size, which made images decode for the wrong area (measured with three monitors).
    /// </summary>
    private (uint Width, uint Height) ImageArea()
    {
        SizeInt32 client = AppWindow.ClientSize;
        double scale = DisplayScale();
        (double chromeWidth, double chromeHeight) = _window.ImageChromeDip();
        return ((uint)Math.Max(1, Math.Floor(client.Width - (chromeWidth * scale))), (uint)Math.Max(1, Math.Floor(client.Height - (chromeHeight * scale))));
    }

    /// <summary>The window was resized or changed monitor while visible: re-plan the current item if its area changed.</summary>
    private void OnImageAreaChanged()
    {
        if (!_visible || _currentPath is null)
        {
            return;
        }

        _areaTimer ??= CreateAreaTimer();
        _areaTimer.Stop();
        _areaTimer.Start(); // debounce: a drag-resize raises many changes
    }

    private DispatcherQueueTimer CreateAreaTimer()
    {
        DispatcherQueueTimer timer = _dispatcher.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(150);
        timer.IsRepeating = false;
        timer.Tick += (_, _) =>
        {
            if (_media is not null)
            {
                UpdateVideoLayout(); // the player scales by itself; only the no-enlarge limit changes
                return;
            }

            (uint width, uint height) = ImageArea();
            if (_visible && _currentPath is { } path &&
                (Math.Abs((long)width - _plannedArea.Width) > 2 || Math.Abs((long)height - _plannedArea.Height) > 2))
            {
                long id = SelectionWorker.NextRequestId();
                MarkNavigation(id, "area-changed");
                ShowNext(id, path, sameItem: true);
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
        double rasterization = DisplayScale();
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
    /// The image scale is chosen in settings (written by a settings UI or by hand), not in the Quick View
    /// window. The resident process re-reads the file when Quick View opens and the file has changed,
    /// so a change applies from the next Space without restarting (one timestamp check per open).
    /// </summary>
    private void ReloadSettingsIfChanged(bool force = false)
    {
        DateTime stamp;
        try
        {
            stamp = File.GetLastWriteTimeUtc(_settingsPath); // 1601-01-01 when the file does not exist
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return;
        }

        if (!force && stamp == _settingsStamp)
        {
            return;
        }

        _settingsStamp = stamp;
        ImageScaleMode previous = _scaleMode;
        _scaleMode = QuickViewSettings.Load(_settingsPath).ImageScale;
        if (!force && previous != _scaleMode)
        {
            _cache.Clear(); // decoded for the other mode's box
        }

        _timeline.Mark(0, "settings-loaded", QuickViewTimeline.Now, new Dictionary<string, object?> { ["imageScale"] = _scaleMode.ToString() });
    }

    private void SetInfo(string text)
    {
        string position = _navigator.Mode == NavigationMode.MultipleItems
            ? string.Format(CultureInfo.CurrentCulture, Strings.GetString("QuickView_Position"), _navigator.Index + 1, _navigator.Items.Count) + " · "
            : string.Empty;
        _window.SetInfo(position + text);
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
            _timeline.Mark(_currentRequest, "window-activated", QuickViewTimeline.Now, new Dictionary<string, object?>
            {
                ["focus"] = _window.FocusDescription(),
                ["firstActivation"] = _visibleWithoutActivation,
            });
            if (_visibleWithoutActivation)
            {
                _visibleWithoutActivation = false; // user clicked into Quick View: normal window behavior from now on
                _window.FocusContent();
                UpdateNavigationKeys();
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

        _timeline.Mark(_currentRequest, "foreground-other", QuickViewTimeline.Now, new Dictionary<string, object?>
        {
            ["class"] = windowClass,
            ["process"] = ProcessNameOf(root),
            ["ownProcess"] = ProcessIdOf(root) == Environment.ProcessId,
        });
        Hide(_currentRequest, "foreground-other-app", restoreOwner: false);
    }

    private static int ProcessIdOf(nint window)
    {
        _ = NativeMethods.GetWindowThreadProcessId(window, out uint pid);
        return (int)pid;
    }

    /// <summary>Diagnostics only (which application took the foreground).</summary>
    private static string ProcessNameOf(nint window)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(ProcessIdOf(window));
            return process.ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return "?";
        }
    }

    private void Show(long requestId, string path)
    {
        StopGif();
        StopMedia("show");
        _pdfPages.Reset();
        _loading?.Cancel();
        _loading?.Dispose();
        _loading = new CancellationTokenSource();
        _currentPath = path;
        Interlocked.Exchange(ref _currentRequest, requestId);

        if (!_visible)
        {
            ReloadSettingsIfChanged();
        }

        string fileName = Path.GetFileName(path);
        _window.ResetContent(fileName);
        _window.Title = string.Format(CultureInfo.CurrentCulture, Strings.GetString("QuickView_WindowTitle"), fileName);
        if (!_visible)
        {
            PlaceWindow(_owner != 0 ? _owner : _placeNear);
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
        string mode = _options.Activation.ToString();
        if (_grantedActivation)
        {
            // Context menu: the requesting process (started by Explorer from the user's click) called
            // AllowSetForegroundWindow for us, so a normal activation is legitimate here.
            _grantedActivation = false;
            mode = "Granted";
            AppWindow.Show(true);
            if (NativeMethods.GetForegroundWindow() != hwnd)
            {
                setForegroundResult = NativeMethods.SetForegroundWindow(hwnd);
                lastError = Marshal.GetLastPInvokeError();
            }

            if (NativeMethods.GetForegroundWindow() != hwnd)
            {
                // The right was not honored: fall back to the passive panel so the window is at least visible on top.
                NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0, NoActivateFlags | NativeMethods.SWP_SHOWWINDOW);
                _topmost = true;
                _visibleWithoutActivation = true;
                fellBack = true;
            }
        }
        else
        {
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
        }

        _timeline.Mark(requestId, "shown", QuickViewTimeline.Now, new Dictionary<string, object?>
        {
            ["mode"] = mode,
            ["aboveOwner"] = IsAbove(hwnd, _owner),
            ["isForeground"] = NativeMethods.GetForegroundWindow() == hwnd,
            ["setForegroundResult"] = setForegroundResult,
            ["lastError"] = lastError,
            ["attached"] = attached,
            ["fellBackToNoActivate"] = fellBack,
        });
    }

    /// <param name="closeSession">False when a new Explorer subscription was already opened for the next show.</param>
    private void Hide(long requestId, string reason, bool restoreOwner, bool closeSession = true)
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
        _window.Surface.ClearImages();
        _displayedBitmap?.Dispose();
        _displayedBitmap = null;
        _contentRequest = 0;
        _navigator.Reset();
        if (closeSession)
        {
            _worker?.CloseSession();
        }

        _placeNear = 0;
        StopGif();
        StopMedia("hidden");
        _pdfPages.Reset();
        UpdatePageControls();
        _focusWatcher.Stop();
        _followedView = 0;
        _prefetch?.Cancel();
        _cache.Clear();
        UpdateNavigationKeys();

        _timeline.Mark(requestId, "hidden", QuickViewTimeline.Now, new Dictionary<string, object?>
        {
            ["reason"] = reason,
            ["wasForeground"] = wasForeground,
            ["ownerRestored"] = ownerRestored,
            ["ownerIsForegroundAfter"] = NativeMethods.GetForegroundWindow() == _owner,
        });
        _ = TrimAfterHideAsync(requestId);
    }

    /// <summary>
    /// Records (and optionally trims) the resident memory once Quick View has been hidden for a moment.
    /// Decoding creates large short-lived buffers; while hidden the process should give that memory back.
    /// </summary>
    private async Task TrimAfterHideAsync(long requestId)
    {
        await Task.Delay(1000);
        if (_visible)
        {
            return;
        }

        Dictionary<string, object?> before = MemorySnapshot();
        if (_options.IdleTrim)
        {
            System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }

        Dictionary<string, object?> after = MemorySnapshot();
        foreach ((string k, object? v) in after)
        {
            before["after_" + k] = v;
        }

        before["trimmed"] = _options.IdleTrim;
        _timeline.Mark(requestId, "idle-memory", QuickViewTimeline.Now, before);
    }

    internal static Dictionary<string, object?> MemorySnapshot()
    {
        GCMemoryInfo gc = GC.GetGCMemoryInfo();
        using var self = System.Diagnostics.Process.GetCurrentProcess();
        return new Dictionary<string, object?>
        {
            ["managedHeapMb"] = Math.Round(gc.HeapSizeBytes / 1048576.0, 1),
            ["managedCommittedMb"] = Math.Round(gc.TotalCommittedBytes / 1048576.0, 1),
            ["workingSetMb"] = Math.Round(self.WorkingSet64 / 1048576.0, 1),
            ["privateMb"] = Math.Round(self.PrivateMemorySize64 / 1048576.0, 1),
        };
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
        var target = new RectInt32(work.X + ((work.Width - width) / 2), work.Y + ((work.Height - height) / 2), width, height);
        AppWindow.MoveAndResize(target);
        if (AppWindow.Size.Width != width || AppWindow.Size.Height != height)
        {
            // Moving to a monitor with another scale makes Windows resize the window for the new DPI during
            // the move (WM_DPICHANGED), overriding the requested size; on the new monitor it sticks.
            AppWindow.MoveAndResize(target);
        }

        _timeline.Mark(_currentRequest, "placed", QuickViewTimeline.Now, new Dictionary<string, object?>
        {
            ["width"] = AppWindow.Size.Width,
            ["height"] = AppWindow.Size.Height,
            ["dpi"] = NativeMethods.GetDpiForWindow(_window.Handle),
        });
    }

    private async Task LoadAsync(long requestId, string path, CancellationToken cancellation)
    {
        try
        {
            (uint areaWidth, uint areaHeight) = ImageArea();
            _plannedArea = (areaWidth, areaHeight);
            ImageScaleMode mode = _scaleMode;

            // 1. File facts (attributes, size, format) without decoding.
            FileFacts facts = await Task.Run(() => FileFacts.Read(path), cancellation);
            _timeline.Mark(requestId, "info", QuickViewTimeline.Now, new Dictionary<string, object?>
            {
                ["format"] = facts.Format.ToString(),
                ["extension"] = Path.GetExtension(path).ToLowerInvariant(),
                ["bytes"] = facts.Length,
                ["access"] = facts.Access.ToString(),
            });
            SetInfo(FormatInfo(facts, null));

            if (facts.Access == PreviewAccess.NotAFile)
            {
                _window.Surface.ShowStatusOnly(Strings.GetString("QuickView_Error_NotFile"));
                return;
            }

            if (FileFormatKinds.IsMedia(facts.Format))
            {
                if (facts.Access == PreviewAccess.CloudPlaceholder)
                {
                    _window.Surface.ShowStatusOnly(Strings.GetString("QuickView_Error_CloudPlaceholder")); // playing would download it
                    return;
                }

                await StartMediaAsync(requestId, path, facts, cancellation);
                return;
            }

            // Display box: decode exactly the pixels that will be shown (no second resample in the UI),
            // never enlarging small images; actual-size mode decodes the source size.
            Plan plan = await PlanAsync(path, facts, mode, areaWidth, areaHeight, cancellation);
            uint viewportWidth = plan.Box.Width;
            uint viewportHeight = plan.Box.Height;

            bool cacheable = facts.Access == PreviewAccess.Allowed && _options.Decoder == DecoderMode.WinRt;
            int page = facts.Format == FileFormat.Pdf ? _pdfPages.Index : 0;
            PreviewKey key = PreviewKey.Create(path, facts.Length, facts.LastWriteUtc, viewportWidth, viewportHeight, page);
            if (cacheable && _cache.TryGet(key, out DecodedImage? cached) && cached?.Bitmap is not null)
            {
                await ShowFullAsync(requestId, facts, cached, "cache", plan, cancellation);
                await StartGifAsync(requestId, path, facts, plan, areaWidth, areaHeight, cancellation);
                StartPrefetch(areaWidth, areaHeight);
                return;
            }

            // 2. Cached shell thumbnail (never extracts, never hydrates cloud files).
            int thumbnailSize = (int)Math.Min(1024, Math.Max(areaWidth, areaHeight));
            // The shell thumbnail of a PDF shows its first page; other pages go straight to the rendered page.
            Task<BgraImage?> thumbnailTask = page > 0
                ? Task.FromResult<BgraImage?>(null)
                : _shellThread.InvokeAsync(() => ShellThumbnail.TryGetCached(path, thumbnailSize));

            if (facts.Access == PreviewAccess.CloudPlaceholder)
            {
                _window.Surface.ShowStatusOnly(Strings.GetString("QuickView_Error_CloudPlaceholder"));
                await ShowThumbnailAsync(requestId, thumbnailTask, plan, null, cancellation);
                return;
            }

            if (_options.Decoder == DecoderMode.Xaml && facts.Format != FileFormat.Pdf)
            {
                _window.Surface.SetImageBox(null, scrollable: false); // comparison path keeps the original "fit" layout
                Task xamlThumbnail = ShowThumbnailAsync(requestId, thumbnailTask, Plan.Unknown, () => _window.Surface.HasFullImage, cancellation);
                await LoadWithXamlDecoderAsync(requestId, path, facts, areaWidth, areaHeight, cancellation);
                await xamlThumbnail;
                return;
            }

            // 3. Full quality in the background; 4. swap in when ready.
            Task<DecodedImage> fullTask = facts.Format == FileFormat.Pdf
                ? PdfRendering.RenderAsync(path, viewportWidth, viewportHeight, cancellation, plan.PdfActualScale, page)
                : ImageDecoding.DecodeAsync(path, viewportWidth, viewportHeight, Interpolation(_options.Interpolation), PreferWic(facts), cancellation);

            Task thumbnailShown = ShowThumbnailAsync(requestId, thumbnailTask, plan, () => _window.Surface.HasFullImage, cancellation);
            DecodedImage full = await fullTask;

            // Keep the work even if the user already moved on: stepping back is then instant.
            if (full.Reason is null && cacheable)
            {
                _cache.Add(key, full);
            }

            cancellation.ThrowIfCancellationRequested();

            if (full.Reason is { } reason)
            {
                _window.Surface.ShowStatusOnly(Strings.GetString(ReasonToResource(reason)));
                _timeline.Mark(requestId, "full-skipped", QuickViewTimeline.Now, new Dictionary<string, object?> { ["reason"] = full.Reason });
                await thumbnailShown;
                return;
            }

            await ShowFullAsync(requestId, facts, full, full.Decoder, plan, cancellation);
            await StartGifAsync(requestId, path, facts, plan, areaWidth, areaHeight, cancellation);
            StartPrefetch(areaWidth, areaHeight);
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
                _window.Surface.ShowStatusOnly(Strings.GetString("QuickView_Error_Failed"));
            }
        }
    }

    private async Task ShowFullAsync(long requestId, FileFacts facts, DecodedImage full, string decoder, Plan plan, CancellationToken cancellation)
    {
        SoftwareBitmap display = SoftwareBitmap.Copy(full.Bitmap!);
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

        _window.Surface.SetImageBox(DisplaySizing.ElementSize((uint)display.PixelWidth, (uint)display.PixelHeight, DisplayScale()), plan.Scrollable);
        _window.Surface.SetFullImage(source);
        SoftwareBitmap? previous = _displayedBitmap;
        _displayedBitmap = display;
        previous?.Dispose();
        _contentRequest = requestId;
        if (full.PageCount > 0)
        {
            _pdfPages.SetCount(full.PageCount);
            UpdatePageControls();
        }

        SetInfo(FormatInfo(facts, full) + (plan.Box.ActualSizeRefused ? " · " + Strings.GetString("QuickView_ActualSizeTooLarge") : string.Empty));
        _timeline.Mark(requestId, "full-set", QuickViewTimeline.Now, new Dictionary<string, object?>
        {
            ["sourceWidth"] = full.SourceWidth,
            ["sourceHeight"] = full.SourceHeight,
            ["decodedWidth"] = full.Bitmap!.PixelWidth,
            ["decodedHeight"] = full.Bitmap.PixelHeight,
            ["decodeMs"] = decoder == "cache" ? 0 : full.DecodeMilliseconds,
            ["decoder"] = decoder,
            ["areaWidth"] = _plannedArea.Width,
            ["areaHeight"] = _plannedArea.Height,
            ["layoutAreaWidth"] = _window.LayoutImageAreaPixels().Width,
            ["layoutAreaHeight"] = _window.LayoutImageAreaPixels().Height,
            ["dpi"] = NativeMethods.GetDpiForWindow(_window.Handle),
            ["pageIndex"] = full.PageIndex,
            ["pageCount"] = full.PageCount,
        });
        await NextFrameAsync();
        if (!cancellation.IsCancellationRequested)
        {
            _timeline.Mark(requestId, "full-visible", QuickViewTimeline.Now);
        }
    }

    /// <summary>
    /// Decodes the neighbors of the shown item in the background (next first), so the next step is
    /// instant. Cancelled by any navigation or by closing; never runs while the current item decodes.
    /// </summary>
    private async void StartPrefetch(uint areaWidth, uint areaHeight)
    {
        _prefetch?.Cancel();
        _prefetch?.Dispose();
        _prefetch = new CancellationTokenSource();
        CancellationToken cancellation = _prefetch.Token;
        try
        {
            // A multi-page PDF: the next and previous pages first (page turns are the likely next step).
            if (_pdfPages.IsMultiPage && _currentPath is { } document)
            {
                int[] pages = [_pdfPages.Index + 1, _pdfPages.Index - 1];
                int count = _pdfPages.Count;
                foreach (int neighborPage in pages.Where(p => p >= 0 && p < count))
                {
                    await PrefetchPdfPageAsync(document, neighborPage, areaWidth, areaHeight, cancellation);
                }
            }

            (string? previous, string? next) = _navigator.Mode == NavigationMode.MultipleItems
                ? (Neighbor(-1), Neighbor(+1))
                : _worker is null ? (null, null) : await _worker.ReadNeighborsAsync();

            foreach (string? path in new[] { next, previous })
            {
                if (path is null || cancellation.IsCancellationRequested)
                {
                    continue;
                }

                FileFacts facts = await Task.Run(() => FileFacts.Read(path), cancellation);
                if (facts.Access != PreviewAccess.Allowed || facts.Length > MaxPrefetchFileBytes || FileFormatKinds.IsMedia(facts.Format))
                {
                    continue;
                }

                Plan plan = await PlanAsync(path, facts, _scaleMode, areaWidth, areaHeight, cancellation);
                PreviewKey key = PreviewKey.Create(path, facts.Length, facts.LastWriteUtc, plan.Box.Width, plan.Box.Height);
                long pixelBudget = _scaleMode == ImageScaleMode.ActualSize ? MaxActualSizePrefetchPixels : MaxPrefetchSourcePixels;
                if (_cache.Contains(key))
                {
                    continue;
                }

                DecodedImage decoded = facts.Format == FileFormat.Pdf
                    ? await PdfRendering.RenderAsync(path, plan.Box.Width, plan.Box.Height, cancellation, plan.PdfActualScale)
                    : await ImageDecoding.DecodeAsync(path, plan.Box.Width, plan.Box.Height, Interpolation(_options.Interpolation), PreferWic(facts), cancellation, pixelBudget);
                if (decoded.Reason is null && _visible)
                {
                    _cache.Add(key, decoded);
                    _timeline.Mark(0, "prefetched", QuickViewTimeline.Now, new Dictionary<string, object?>
                    {
                        ["decodeMs"] = decoded.DecodeMilliseconds,
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
            // Superseded by navigation or closing.
        }
        catch (Exception ex)
        {
            // Prefetch is an optimization; failures (hostile/broken neighbor files) are ignored.
            _timeline.Mark(0, "prefetch-error", QuickViewTimeline.Now, new Dictionary<string, object?> { ["type"] = ex.GetType().Name });
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

        Mavue.Image.Gif.GifAnimationReader? reader = await Mavue.Image.Gif.GifAnimationReader.OpenAsync(path, cancellation);
        if (reader is null)
        {
            return;
        }

        if (reader.FrameCount < 2 || (long)reader.Width * reader.Height > MaxAnimatedGifPixels ||
            cancellation.IsCancellationRequested || !_visible || _contentRequest != requestId)
        {
            reader.Dispose();
            return;
        }

        StopGif();
        var player = new GifPlayer(reader, Trace(requestId));
        _gif = player;

        // The animation is sized from the logical screen (the first frame may cover only part of it).
        (uint displayWidth, uint displayHeight) = DisplaySizing.ExpectedDisplayPixels(_scaleMode, areaWidth, areaHeight, (uint)reader.Width, (uint)reader.Height);
        _window.Surface.SetImageBox(DisplaySizing.ElementSize(displayWidth, displayHeight, DisplayScale()), plan.Scrollable);
        _window.Surface.SetFullImage(player.Source);
        SoftwareBitmap? still = _displayedBitmap;
        _displayedBitmap = null; // the still frame's source was just replaced; free its pixels too
        still?.Dispose();
        player.Start();
    }

    /// <summary>Plays a video or audio file with Windows' media pipeline (MediaPlayerElement + Media Foundation).</summary>
    private async Task StartMediaAsync(long requestId, string path, FileFacts facts, CancellationToken cancellation)
    {
        StopMedia("replace");
        bool audio = FileFormatKinds.IsAudio(facts.Format);
        MediaSession session = await MediaSession.OpenAsync(path, audio, _dispatcher, cancellation);
        if (cancellation.IsCancellationRequested)
        {
            session.Dispose();
            cancellation.ThrowIfCancellationRequested();
        }

        _media = session;
        session.Opened += s => OnMediaOpened(s, requestId, facts);
        session.Failed += (s, error, hresult) => OnMediaFailed(s, requestId, error, hresult);
        session.StateChanged += (s, state) => OnMediaState(s, requestId, state);
        _timeline.Mark(requestId, "media-open", QuickViewTimeline.Now, new Dictionary<string, object?> { ["kind"] = audio ? "audio" : "video", ["player"] = session.Id });
        _window.Surface.ShowMedia(session.Player);
        _contentRequest = requestId; // the previous item's picture is gone; no "stale image" clearing needed
        if (audio)
        {
            _window.Surface.SetAudioLayout();
        }

        _window.Surface.SetStatus(Strings.GetString("QuickView_Loading"));
        session.Start();
    }

    private async void OnMediaOpened(MediaSession session, long requestId, FileFacts facts)
    {
        if (!ReferenceEquals(session, _media))
        {
            return;
        }

        TimeSpan duration = session.Duration;
        _window.Surface.SetStatus(string.Empty);
        if (session.HasVideo)
        {
            UpdateVideoLayout();
            SetInfo(string.Format(CultureInfo.CurrentCulture, Strings.GetString("QuickView_InfoVideo"), facts.Format, session.NaturalSize.Width, session.NaturalSize.Height, MediaControlMath.FormatDuration(duration), ByteSizeText.Format(facts.Length)));
        }
        else
        {
            _window.Surface.SetAudioLayout();
            SetInfo(string.Format(CultureInfo.CurrentCulture, Strings.GetString("QuickView_InfoAudio"), facts.Format, MediaControlMath.FormatDuration(duration), ByteSizeText.Format(facts.Length)));
        }

        _contentRequest = requestId;
        _timeline.Mark(requestId, "media-opened", QuickViewTimeline.Now, new Dictionary<string, object?>
        {
            ["kind"] = session.HasVideo ? "video" : "audio",
            ["player"] = session.Id,
            ["width"] = session.NaturalSize.Width,
            ["height"] = session.NaturalSize.Height,
            ["durationMs"] = Math.Round(duration.TotalMilliseconds),
        });
        await NextFrameAsync();
        if (ReferenceEquals(session, _media))
        {
            _timeline.Mark(requestId, "full-visible", QuickViewTimeline.Now);
        }
    }

    private void OnMediaFailed(MediaSession session, long requestId, string error, int hresult)
    {
        if (!ReferenceEquals(session, _media))
        {
            return;
        }

        // Missing codec, unsupported container or a damaged file: say so; the resident process carries on.
        _timeline.Mark(requestId, "media-failed", QuickViewTimeline.Now, new Dictionary<string, object?> { ["error"] = error, ["hresult"] = hresult, ["player"] = session.Id });
        StopMedia("failed");
        _window.Surface.ShowStatusOnly(Strings.GetString("QuickView_Error_Media"));
        _timeline.Mark(requestId, "full-skipped", QuickViewTimeline.Now, new Dictionary<string, object?> { ["reason"] = "media-failed" });
    }

    private void OnMediaState(MediaSession session, long requestId, string state)
    {
        if (!ReferenceEquals(session, _media))
        {
            return;
        }

        string? text = state switch
        {
            "Playing" => Strings.GetString("QuickView_MediaPlaying"),
            "Paused" => Strings.GetString("QuickView_MediaPaused"),
            MediaStates.Ended => Strings.GetString("QuickView_MediaEnded"),
            _ => null,
        };
        if (text is not null)
        {
            _window.Surface.SetAudioState(text);
        }

        _timeline.Mark(requestId, "media-state", QuickViewTimeline.Now, new Dictionary<string, object?> { ["state"] = state, ["player"] = session.Id });
    }

    /// <summary>Enter, Ctrl+←/→ and Ctrl+↑/↓ while the Quick View window has the keyboard.</summary>
    private void OnMediaCommand(MediaCommand command)
    {
        if (_media is not { } session)
        {
            return;
        }

        (TimeSpan position, double volume) = session.Execute(command);
        _timeline.Mark(_currentRequest, "media-command", QuickViewTimeline.Now, new Dictionary<string, object?>
        {
            ["command"] = command.ToString(),
            ["positionMs"] = Math.Round(position.TotalMilliseconds),
            ["volume"] = Math.Round(volume, 2),
            ["player"] = session.Id,
        });
    }

    /// <summary>Video fits the area and is never enlarged (same rule as images).</summary>
    private void UpdateVideoLayout()
    {
        if (_media is not { HasVideo: true } session)
        {
            return;
        }

        (uint areaWidth, uint areaHeight) = ImageArea();
        (uint width, uint height) = DisplaySizing.ExpectedDisplayPixels(ImageScaleMode.FitNoUpscale, areaWidth, areaHeight, session.NaturalSize.Width, session.NaturalSize.Height);
        (double w, double h) = DisplaySizing.ElementSize(width, height, DisplayScale());
        _window.Surface.SetVideoLayout(w, h);
    }

    private void StopMedia(string reason)
    {
        if (_media is not { } session)
        {
            return;
        }

        _media = null;
        _window.Surface.ClearMedia();
        session.Dispose();
        _timeline.Mark(_currentRequest, "media-stop", QuickViewTimeline.Now, new Dictionary<string, object?> { ["reason"] = reason, ["player"] = session.Id });
    }

    /// <summary>GIF/media playback events go to the timing log under the request that started them.</summary>
    private PlaybackTrace Trace(long requestId) => (name, detail) => _timeline.Mark(requestId, name, QuickViewTimeline.Now, detail);

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

        Plan plan = await PlanAsync(path, facts, _scaleMode, areaWidth, areaHeight, cancellation);
        PreviewKey key = PreviewKey.Create(path, facts.Length, facts.LastWriteUtc, plan.Box.Width, plan.Box.Height, page);
        if (_cache.Contains(key))
        {
            return;
        }

        DecodedImage decoded = await PdfRendering.RenderAsync(path, plan.Box.Width, plan.Box.Height, cancellation, plan.PdfActualScale, page);
        if (decoded.Reason is null && _visible)
        {
            _cache.Add(key, decoded);
            _timeline.Mark(0, "prefetched", QuickViewTimeline.Now, new Dictionary<string, object?>
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

    private string? Neighbor(int delta)
    {
        int index = _navigator.Index + delta;
        return index >= 0 && index < _navigator.Items.Count ? _navigator.Items[index] : null;
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

        _timeline.Mark(requestId, "thumbnail", QuickViewTimeline.Now, new Dictionary<string, object?>
        {
            ["cached"] = thumbnail is not null,
            ["width"] = thumbnail?.Width ?? 0,
            ["height"] = thumbnail?.Height ?? 0,
        });
        if (thumbnail is null)
        {
            _window.Surface.SetStatus(Strings.GetString("QuickView_Loading"));
            return;
        }

        _window.Surface.SetImageBox(plan.ExpectedElementSize, plan.Scrollable);
        _window.SetThumbnail(thumbnail);
        _contentRequest = requestId;
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
                _window.Surface.ShowStatusOnly(Strings.GetString("QuickView_Error_TooLarge"));
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
            _window.Surface.SetFullImagePending(image);
            bool ok = await opened.Task;
            cancellation.ThrowIfCancellationRequested();
            if (!ok)
            {
                _window.Surface.ShowStatusOnly(Strings.GetString("QuickView_Error_Failed"));
                return;
            }

            _window.Surface.CommitFullImage();
            SetInfo(FormatInfo(facts, new DecodedImage(null, width, height, 0, "xaml-bitmapimage", 0, null)));
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

    /// <summary>JPEG goes through WIC directly (measured faster); other formats were as fast or faster via WinRT.</summary>
    private bool PreferWic(FileFacts facts) => _options.UseWicForJpeg && facts.Format == FileFormat.Jpeg;

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

    private static string FormatInfo(FileFacts facts, DecodedImage? decoded)
    {
        string size = ByteSizeText.Format(facts.Length);
        if (decoded is { PageCount: > 1 } pages)
        {
            return string.Format(CultureInfo.CurrentCulture, Strings.GetString("QuickView_InfoPdfPage"), pages.PageIndex + 1, pages.PageCount, size);
        }

        if (decoded is { PageCount: > 0 } pdf)
        {
            return string.Format(CultureInfo.CurrentCulture, Strings.GetString("QuickView_InfoPdf"), pdf.PageCount, size);
        }

        return decoded is { SourceWidth: > 0 } image
            ? string.Format(CultureInfo.CurrentCulture, Strings.GetString("QuickView_InfoImage"), facts.Format, image.SourceWidth, image.SourceHeight, size)
            : string.Format(CultureInfo.CurrentCulture, Strings.GetString("QuickView_InfoBasic"), facts.Format, size);
    }
}
