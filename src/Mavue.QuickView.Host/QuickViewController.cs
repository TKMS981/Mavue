using System.Globalization;
using Mavue.Core.Formats;
using Mavue.Core.Settings;
using Mavue.Core.Viewing;
using Mavue.QuickView.Diagnostics;
using Mavue.QuickView.Host.Content;
using Mavue.QuickView.Host.Presentation;
using Mavue.QuickView.Host.Performance;
using Mavue.QuickView.Preview;
using Mavue.QuickView.Shell;
using Mavue.QuickView.Trigger;
using Mavue.Shell;
using Mavue.Viewer;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.Windows.ApplicationModel.Resources;
using Windows.Graphics;

namespace Mavue.QuickView.Host;

/// <summary>
/// Coordinates one Quick View session (docs/ARCHITECTURE.md §4.7): Space and context-menu requests, following the
/// Explorer selection (single and multi-selection, tabs), the keys taken by the hook, and the life cycle of the shown
/// item (staged loading, re-planning on resize, closing). What an item looks like is decided by the content its file
/// routes to (<see cref="PreviewContentRouting"/>, <see cref="IPreviewContent"/>); how the window appears is
/// <see cref="QuickViewPresence"/>; resource limits and idle memory are <see cref="ResidentResources"/>.
/// The controller never branches on file types.
/// All members run on the UI thread except <see cref="HandleEscapeFromHook"/> and
/// <see cref="HandleNavigationKeyFromHook"/>, which are called on the keyboard hook thread.
/// </summary>
internal sealed class QuickViewController : IPreviewHost, IDisposable
{
    /// <summary>How long the previous item's image may stay while the next one loads (avoids flicker).</summary>
    private static readonly TimeSpan StaleImageGrace = TimeSpan.FromMilliseconds(120);

    private static readonly long ExternalMergeWindowTicks = System.Diagnostics.Stopwatch.Frequency * 2;

    private readonly QuickViewWindow _window;
    private readonly QuickViewTimeline _timeline;
    private readonly HostOptions _options;
    private readonly DispatcherQueue _dispatcher;
    private readonly ResourceLoader _strings = new();
    private readonly StaThread _shellThread = new("Mavue.QuickView.ShellItems");
    private readonly QuickViewPresence _presence;
    private readonly QuickViewSettingsStore _settings;
    private readonly ResidentResources _resources;
    private readonly PreviewContentSet _contents;
    private readonly NeighborPrefetcher _prefetcher;
    private readonly QuickViewFileActions _fileActions;
    private readonly PixelInspector _inspector;
    private readonly FilmstripPresenter _filmstrip;
    private readonly Metadata.ExifCard _exif;
    private readonly Metadata.ShareCheck _shareCheck;
    private readonly Similar.SimilarPanel _similar;
    private readonly PreviewNavigator _navigator = new();
    private readonly WinEventWatcher _focusWatcher = new();
    private readonly bool _pdfDocumentView;
    private CancellationTokenSource? _loading;
    private SelectionWorker? _worker;
    private KeyboardHook? _hook;
    private string? _currentPath;
    private long _currentRequest;
    private bool _visible;
    private nint _owner;
    private nint _ownerForHook;
    private bool? _lastHookProbe;
    private nint _followedView;
    private FileFacts? _currentFacts;
    private IPreviewContent? _active;

    // What the current item has put on screen (its own content or message, as opposed to the previous item's).
    private readonly ItemDisplayState _display = new();
    private (uint Width, uint Height) _plannedArea;
    private DispatcherQueueTimer? _areaTimer;

    // Context-menu requests without an Explorer view: the window is placed near this window.
    private nint _placeNear;
    private long _lastExternalShowQpc = long.MinValue / 2;

    // Keys the hook takes while Quick View is shown passively: PageUp/PageDown for paged content, ←/→ in a
    // multi-selection. Read on the hook thread.
    private volatile bool _paging;
    private volatile bool _multiSelectionKeys;
    private volatile bool _pinned; // the navigator's pin state, for the hook thread

    public QuickViewController(QuickViewWindow window, QuickViewTimeline timeline, HostOptions options)
    {
        _window = window;
        _timeline = timeline;
        _options = options;
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _presence = new QuickViewPresence(window, timeline, options);
        _settings = new QuickViewSettingsStore(options.SettingsPath ?? QuickViewSettings.DefaultPath, timeline, _dispatcher);
        _settings.ReloadIfChanged(force: true);
        _resources = new ResidentResources(timeline, options.IdleTrim);
        _resources.Refresh();
        _window.SetBackdrop(UseBackdrop(_resources.Policy)); // decided before the first Space (prewarm draws it)
        _pdfDocumentView = PdfSession.IsAvailable && options.Decoder == DecoderMode.WinRt;

        var services = new PreviewServices(window, timeline, _dispatcher, options, _strings, _shellThread, _settings, _resources, new SurfaceImageView(window, timeline));
        _contents = new PreviewContentSet(services, this);
        _prefetcher = new NeighborPrefetcher(_contents, _resources, timeline, ReadNeighborsAsync, () => _active, _pdfDocumentView);
        _fileActions = new QuickViewFileActions(window, timeline, this, () => _active, () => _currentFacts?.Format ?? FileFormat.Unknown, reason => Hide(_currentRequest, reason, restoreOwner: false));

        // Phase 1 (docs/ARCHITECTURE.md §4.7.8): inspector, filmstrip, pin and drag out. Each works only while Quick
        // View is shown and its feature is on.
        _inspector = new PixelInspector(window, timeline, services.ImageView, () => _active, () => CurrentRequest);
        _filmstrip = new FilmstripPresenter(window, timeline, _resources, () => _worker);
        _filmstrip.ItemInvoked += OnFilmstripInvoked;
        _window.FilmstripToggled += on =>
        {
            _filmstrip.SetEnabled(on);
            _settings.FilmstripVisible = on;
            _settings.Save(s => s with { ShowFilmstrip = on });
            if (on && _visible && _currentPath is { } path)
            {
                _filmstrip.OnItemShowing(path, _navigator);
                _filmstrip.OnItemShown();
            }
        };
        _window.InspectorToggled += on =>
        {
            _inspector.SetEnabled(on);
            _settings.PixelInspectorOn = on;
            _settings.Save(s => s with { ShowPixelInspector = on });
        };
        _window.PinToggled += SetPinned;

        // Phase 5 (docs/ARCHITECTURE.md §4.7.13–14): EXIF card, check before sharing, similar images. Each works only
        // while Quick View is shown and only when asked; nothing runs while it is hidden.
        _exif = new Metadata.ExifCard(window, _strings, timeline, () => CurrentRequest);
        _shareCheck = new Metadata.ShareCheck(window, _strings, timeline, () => CurrentRequest, _dispatcher, () => _resources.Policy.Tier);
        _similar = new Similar.SimilarPanel(window, _strings, timeline, _dispatcher, () => _resources.Policy, DisplayScale, _settings);
        _window.ExifToggled += on =>
        {
            _exif.SetEnabled(on);
            _settings.ExifPanelOn = on;
            _settings.Save(s => s with { ShowExifPanel = on });
        };
        _window.ShareCheckRequested += anchor => OpenShareCheck(anchor);
        _exif.ShareCheckRequested += () => OpenShareCheck(_window.ShareCheckAnchor);
        _window.SimilarRequested += () =>
        {
            if (_visible && _currentPath is { } path && _currentFacts is { } facts && Metadata.ExifCard.IsImage(facts))
            {
                _similar.Start(path, CurrentRequest);
            }
        };
        _similar.ItemInvoked += ShowSimilarResult;
        _window.TransparencyGridToggled += on =>
        {
            _window.SetTransparencyGridOn(on);
            _settings.TransparencyGridOn = on;
            _settings.Save(s => s with { ShowTransparencyGrid = on });
        };
        _resources.PolicyChanged += policy => _window.SetBackdrop(UseBackdrop(policy));
        _window.FileDragStarting += args => _fileActions.PrepareDrag(args, _currentFacts);
        _window.ImagePointerMoved += pointer =>
        {
            if (_visible)
            {
                _inspector.OnPointer(pointer);
            }
        };

        // Generic window commands go to the content showing the current item; content-specific ones (PDF tools, media
        // keys) are subscribed by that content itself.
        _window.CloseRequested += () => Hide(_currentRequest, "window-key-or-close", restoreOwner: true);
        _window.NavigateRequested += delta => Step(delta, "window-arrow");
        _window.PageRequested += (delta, source) => StepPage(delta, source);
        _window.ZoomRequested += (command, wheelDelta, anchor) =>
        {
            if (_visible)
            {
                (_active as IViewTransformContent)?.Zoom(command, wheelDelta, anchor);
            }
        };
        _window.RotateRequested += quarterTurns =>
        {
            if (_visible)
            {
                (_active as IViewTransformContent)?.Rotate(quarterTurns);
            }
        };
        _window.FileCommandRequested += _fileActions.Execute;
        _window.KeyReceived += key => _timeline.Mark(_currentRequest, "window-key", QuickViewTimeline.Now, new Dictionary<string, object?>
        {
            ["key"] = key.ToString(),
            ["focus"] = _window.FocusDescription(),
        });
        _window.ImageAreaChanged += OnImageAreaChanged;
        _focusWatcher.EventRaised += (_, hwnd, _, _) => OnOwnerFocusChanged(hwnd);
    }

    // IPreviewHost
    public bool IsVisible => _visible;

    public string? CurrentPath => _currentPath;

    public long CurrentRequest => Interlocked.Read(ref _currentRequest);

    public ItemDisplayState Display => _display;

    public (uint Width, uint Height) PlannedArea
    {
        get => _plannedArea;
        set => _plannedArea = value;
    }

    /// <summary>Limits and memory of the resident process (also asked by the App for diagnostics).</summary>
    public ResidentResources Resources => _resources;

    private AppWindow AppWindow => _window.AppWindow;

    /// <summary>Connects the Shell worker and the keyboard hook (created after the controller).</summary>
    public void Attach(SelectionWorker worker, KeyboardHook hook)
    {
        _worker = worker;
        _hook = hook;
    }

    public void Dispose()
    {
        _prefetcher.Dispose();
        _filmstrip.Dispose();
        _exif.Dispose();
        _shareCheck.Dispose();
        _similar.Dispose();
        _inspector.Dispose();
        _contents.Dispose();
        _resources.Dispose();
        _focusWatcher.Dispose();
        _loading?.Cancel();
        _loading?.Dispose();
        _loading = null;
        _shellThread.Dispose();
    }

    /// <summary>
    /// Renders the hidden window once so the first Space press does not pay for XAML/DWM setup, and initializes the
    /// shell thumbnail path (the first thumbnail-cache lookup in a process costs ~60 ms, measured).
    /// </summary>
    public async Task PrewarmAsync()
    {
        long start = QuickViewTimeline.Now;
        await _presence.PrewarmAsync();
        string self = Environment.ProcessPath ?? string.Empty;
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
        _presence.GrantedActivation = true;
        _lastExternalShowQpc = QuickViewTimeline.Now;
        MarkNavigation(requestId, "context-menu");
        Show(requestId, start.Path!);
        UpdateNavigationKeys();
        if (owner != 0)
        {
            WatchOwnerFocus();
        }
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

    /// <summary>The Explorer window or tab Quick View follows was closed (a pinned preview stays).</summary>
    public void OnExplorerViewClosed()
    {
        if (_visible && _navigator.IsPinned)
        {
            _timeline.Mark(_currentRequest, "explorer-closed-pinned", QuickViewTimeline.Now);
            return;
        }

        Hide(_currentRequest, "explorer-closed", restoreOwner: false);
    }

    /// <summary>
    /// Hook thread: ←/→ while a multi-selection is previewed passively (steps inside the selection and swallows the
    /// key so Explorer keeps the selection intact), and PageUp/PageDown while paged content is shown.
    /// </summary>
    public bool HandleNavigationKeyFromHook(int virtualKey, nint foreground)
    {
        const int VkPageUp = 0x21;
        const int VkPageDown = 0x22;
        const int VkLeft = 0x25;
        const int VkRight = 0x27;
        if (foreground != Volatile.Read(ref _ownerForHook) || !_presence.IsPassive)
        {
            return false;
        }

        long qpc = QuickViewTimeline.Now;
        if (virtualKey is VkPageUp or VkPageDown)
        {
            if (!_paging)
            {
                return false; // nothing paged: Explorer keeps PageUp/PageDown
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

    /// <summary>Called on the hook thread for Escape. Only used when the window is shown without activation.</summary>
    public bool HandleEscapeFromHook(nint foreground)
    {
        if (!_presence.IsPassive || _pinned || foreground != Volatile.Read(ref _ownerForHook))
        {
            return false; // pinned: Esc in Explorer belongs to Explorer
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

        nint root = NativeMethods.GetAncestor(foreground, NativeMethods.GA_ROOT);
        if (foreground == _window.Handle || root == _window.Handle)
        {
            _timeline.Mark(_currentRequest, "window-activated", QuickViewTimeline.Now, new Dictionary<string, object?>
            {
                ["focus"] = _window.FocusDescription(),
                ["firstActivation"] = _presence.IsPassive,
            });
            if (_presence.IsPassive)
            {
                _presence.IsPassive = false; // user clicked into Quick View: normal window behavior from now on
                _window.FocusContent();
                UpdateNavigationKeys();
            }

            return;
        }

        if (root == _owner || foreground == _owner)
        {
            return; // back to the Explorer window Quick View belongs to: stay open
        }

        if (QuickViewPresence.ProcessIdOf(root) == Environment.ProcessId)
        {
            // A window of Quick View's own process: its content (the PDF presentation) or the WinUI windows behind a
            // click on its controls (seen when the filmstrip and the pin button are clicked). Never another app.
            _timeline.Mark(_currentRequest, "foreground-own", QuickViewTimeline.Now, new Dictionary<string, object?>
            {
                ["class"] = Interop.WindowClass.Of(root),
                ["auxiliary"] = _contents.AnyAuxiliaryWindow,
                ["ownedByQuickView"] = NativeMethods.GetAncestor(root, 3 /* GA_ROOTOWNER */) == _window.Handle,
            });
            return;
        }

        string windowClass = Interop.WindowClass.Of(root);
        if (QuickViewPresence.IsTransient(windowClass))
        {
            _timeline.Mark(_currentRequest, "foreground-transient", QuickViewTimeline.Now, new Dictionary<string, object?> { ["class"] = windowClass });
            return;
        }

        _timeline.Mark(_currentRequest, "foreground-other", QuickViewTimeline.Now, new Dictionary<string, object?>
        {
            ["class"] = windowClass,
            ["process"] = QuickViewPresence.ProcessNameOf(root),
            ["ownProcess"] = QuickViewPresence.ProcessIdOf(root) == Environment.ProcessId,
            ["pinned"] = _navigator.IsPinned,
        });
        if (_navigator.IsPinned)
        {
            return; // a pinned preview stays while the user works in another application
        }

        Hide(_currentRequest, "foreground-other-app", restoreOwner: false);
    }

    // IPreviewHost
    public void AdoptRequest(long requestId)
    {
        Interlocked.Exchange(ref _currentRequest, requestId);
        _display.MarkContent(requestId);
    }

    /// <summary>Physical pixels per device-independent pixel for the window's current monitor.</summary>
    public double DisplayScale()
    {
        uint dpi = NativeMethods.GetDpiForWindow(_window.Handle);
        return dpi > 0 ? dpi / 96.0 : _window.RasterizationScale;
    }

    /// <summary>
    /// Image area in physical pixels, from the window's real client size and DPI. The XAML layout is not
    /// used: right after the window moves to a monitor with another scale it still describes the old
    /// size, which made images decode for the wrong area (measured with three monitors).
    /// </summary>
    public (uint Width, uint Height) ImageArea()
    {
        SizeInt32 client = AppWindow.ClientSize;
        double scale = DisplayScale();
        (double chromeWidth, double chromeHeight) = _window.ImageChromeDip();
        return ((uint)Math.Max(1, Math.Floor(client.Width - (chromeWidth * scale))), (uint)Math.Max(1, Math.Floor(client.Height - (chromeHeight * scale))));
    }

    public void SetInfo(string text)
    {
        string position = _navigator.Mode == NavigationMode.MultipleItems
            ? string.Format(CultureInfo.CurrentCulture, _strings.GetString("QuickView_Position"), _navigator.Index + 1, _navigator.Items.Count) + " · "
            : string.Empty;
        _window.SetInfo(position + text);
    }

    public void SetPaging(bool multiPage, bool canPrevious, bool canNext)
    {
        _paging = _visible && multiPage;
        _window.SetPageControls(_paging, canPrevious, canNext);
        UpdateNavigationKeys();
    }

    public void MarkNavigation(long requestId, string source) =>
        _timeline.Mark(requestId, "nav", QuickViewTimeline.Now, new Dictionary<string, object?>
        {
            ["source"] = source,
            ["mode"] = _navigator.Mode.ToString(),
            ["index"] = _navigator.Index,
            ["count"] = _navigator.Items.Count,
        });

    public void ShowCurrentAgain(long requestId)
    {
        if (_currentPath is { } path)
        {
            ShowNext(requestId, path, sameItem: true);
        }
    }

    public void StartPrefetch(uint areaWidth, uint areaHeight) => _prefetcher.Start(areaWidth, areaHeight);

    /// <summary>Mica behind the chrome, except on Low-tier machines (and when switched off for measurement).</summary>
    private bool UseBackdrop(PreviewResourcePolicy policy) => policy.Tier != PerformanceTier.Low && !_options.NoBackdrop;

    public Task ShowInfoCardAsync(PreviewItem item, string messageResource, CancellationToken cancellation)
    {
        if (cancellation.IsCancellationRequested || !_visible || item.RequestId != CurrentRequest)
        {
            return Task.CompletedTask;
        }

        _active = _contents.Universal;
        return _contents.Universal.ShowCardAsync(item, messageResource, cancellation);
    }

    /// <summary>Pin on/off (button or P): a pinned preview does not follow Explorer and stays when another app is used.</summary>
    private void SetPinned(bool pinned)
    {
        if (!_visible || !_navigator.SetPinned(pinned))
        {
            _window.SetPinned(_navigator.IsPinned);
            return;
        }

        _pinned = pinned;
        _window.SetPinned(pinned);
        _filmstrip.SetPinned(pinned);
        UpdateNavigationKeys();
        _timeline.Mark(_currentRequest, "pin", QuickViewTimeline.Now, new Dictionary<string, object?> { ["pinned"] = pinned });
        if (!pinned)
        {
            _worker?.RefreshSelection(); // follow Explorer again from its current selection
        }
    }

    private void OpenShareCheck(Microsoft.UI.Xaml.FrameworkElement anchor)
    {
        if (_visible && _currentPath is { } path && _currentFacts is { } facts && Metadata.ExifCard.IsImage(facts))
        {
            _shareCheck.Open(path, facts, anchor);
        }
    }

    /// <summary>
    /// A similar image was clicked: it is shown inside Quick View, pinned, so Explorer's selection (and the folder
    /// view) stay as they were while the results are looked through.
    /// </summary>
    private void ShowSimilarResult(string path)
    {
        if (!_visible)
        {
            return;
        }

        if (!_navigator.IsPinned)
        {
            SetPinned(true);
        }

        _timeline.Mark(_currentRequest, "similar-open", QuickViewTimeline.Now);
        NavigationResult result = _navigator.GoTo(path);
        if (result.Action == NavigationAction.Show)
        {
            Apply(result, "similar", null, null);
        }
    }

    /// <summary>A file of the filmstrip was clicked.</summary>
    private void OnFilmstripInvoked(FilmstripItem item)
    {
        if (!_visible)
        {
            return;
        }

        NavigationResult result = _navigator.GoTo(item.Path);
        if (result.Action == NavigationAction.SelectInExplorer)
        {
            // Following Explorer: select it there, and Quick View follows the selection as usual.
            _timeline.Mark(_currentRequest, "filmstrip-select", QuickViewTimeline.Now, new Dictionary<string, object?> { ["viewIndex"] = item.ViewIndex });
            _worker?.SelectInView(item.ViewIndex, item.Path);
            return;
        }

        Apply(result, "filmstrip", null, null);
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

    /// <summary>Previous/next page of the shown item (paged content only; otherwise nothing happens).</summary>
    private void StepPage(int delta, string source, long? inputQpc = null)
    {
        if (_visible)
        {
            (_active as IPagedContent)?.StepPage(delta, source, inputQpc);
        }
    }

    private void Step(int delta, string source, long? inputQpc = null)
    {
        if (!_visible)
        {
            return;
        }

        if (_navigator.IsPinned && _navigator.Mode == NavigationMode.SingleItem)
        {
            // Pinned: step through the filmstrip's files inside Quick View; Explorer is left alone.
            if (_filmstrip.Model.Neighbor(Math.Sign(delta)) is { } neighbor)
            {
                Apply(_navigator.GoTo(neighbor.Path), source, inputQpc, null);
            }

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

    /// <summary>Arms hook interception only while Quick View is shown passively and there is something to take.</summary>
    private void UpdateNavigationKeys()
    {
        if (_hook is null)
        {
            return;
        }

        bool passive = _visible && _presence.IsPassive && !_navigator.IsPinned; // pinned: Explorer keeps its keys
        _multiSelectionKeys = passive && _navigator.Mode == NavigationMode.MultipleItems;
        _hook.NavigationKeyHandler = _multiSelectionKeys || (passive && _paging) ? HandleNavigationKeyFromHook : null;
    }

    private Task<(string? Previous, string? Next)> ReadNeighborsAsync()
    {
        if (_navigator.Mode == NavigationMode.MultipleItems)
        {
            return Task.FromResult((Neighbor(-1), Neighbor(+1)));
        }

        if (_navigator.IsPinned)
        {
            // Explorer's focus has moved on; the files around a pinned one are the filmstrip's.
            return Task.FromResult((_filmstrip.Model.Neighbor(-1)?.Path, _filmstrip.Model.Neighbor(+1)?.Path));
        }

        return _worker is null ? Task.FromResult<(string?, string?)>((null, null)) : _worker.ReadNeighborsAsync();
    }

    private string? Neighbor(int delta)
    {
        int index = _navigator.Index + delta;
        return index >= 0 && index < _navigator.Items.Count ? _navigator.Items[index] : null;
    }

    /// <summary>First item of a session: places and shows the window, then loads the item.</summary>
    private void Show(long requestId, string path)
    {
        _contents.Reset(ContentReset.NewItem, "show");
        BeginLoad(requestId, path);

        if (!_visible)
        {
            _settings.ReloadIfChanged();
            _resources.Refresh();

            // Before the image area is measured (the filmstrip takes a row).
            _filmstrip.SetEnabled(_settings.FilmstripVisible);
            _inspector.SetEnabled(_settings.PixelInspectorOn);
            _exif.SetEnabled(_settings.ExifPanelOn);
            _window.SetTransparencyGridOn(_settings.TransparencyGridOn);
            _window.SetBackdrop(UseBackdrop(_resources.Policy)); // solid chrome on modest machines
            _window.SetPinned(false);
        }

        _inspector.OnItemChanged();
        _exif.OnItemChanged();
        _shareCheck.Close();
        _window.SetImageTools(false);
        _filmstrip.OnItemShowing(path, _navigator);
        string fileName = Path.GetFileName(path);
        _window.ResetContent(fileName);
        _window.Title = string.Format(CultureInfo.CurrentCulture, _strings.GetString("QuickView_WindowTitle"), fileName);
        if (!_visible)
        {
            _presence.Place(_owner != 0 ? _owner : _placeNear, requestId);
            _window.SetOpenInMavueAvailable(ShellActions.FindMavueExecutable() is not null);
        }

        _timeline.Mark(requestId, "show-call", QuickViewTimeline.Now);
        _presence.Activate(requestId, _owner, _lastHookProbe);
        _visible = true;
        if (!_presence.IsPassive)
        {
            _window.FocusContent();
        }

        _ = MarkNextFrameAsync(requestId, "first-frame");
        _ = CheckForegroundLaterAsync(requestId);
        _ = LoadAsync(requestId, path, _loading!.Token);
    }

    /// <summary>Shows another item in the already visible window, keeping the previous image until the new one is ready.</summary>
    /// <param name="sameItem">Re-showing the current file (e.g. new window size): keep its image, zoom and page.</param>
    private void ShowNext(long requestId, string path, bool sameItem = false)
    {
        _contents.Reset(sameItem ? ContentReset.Reload : ContentReset.NewItem, "next-item");
        if (!sameItem)
        {
            SetPaging(false, false, false);
            _inspector.OnItemChanged();
            _exif.OnItemChanged();
            _shareCheck.Close();
            _window.SetImageTools(false);
            _filmstrip.OnItemShowing(path, _navigator);
        }

        _prefetcher.Cancel();
        BeginLoad(requestId, path);

        string fileName = Path.GetFileName(path);
        _window.BeginNextItem(fileName);
        if (!sameItem)
        {
            _ = ClearStaleImageAsync(requestId);
        }

        _window.Title = string.Format(CultureInfo.CurrentCulture, _strings.GetString("QuickView_WindowTitle"), fileName);
        _timeline.Mark(requestId, "show-call", QuickViewTimeline.Now);
        _ = MarkNextFrameAsync(requestId, "first-frame");
        _ = LoadAsync(requestId, path, _loading!.Token);
    }

    private void BeginLoad(long requestId, string path)
    {
        _loading?.Cancel();
        _loading?.Dispose();
        _loading = new CancellationTokenSource();
        _currentPath = path;
        Interlocked.Exchange(ref _currentRequest, requestId);
    }

    /// <param name="closeSession">False when a new Explorer subscription was already opened for the next show.</param>
    private void Hide(long requestId, string reason, bool restoreOwner, bool closeSession = true)
    {
        if (!_visible)
        {
            return;
        }

        _loading?.Cancel();
        (bool wasForeground, bool? ownerRestored) = _presence.Hide(_owner, restoreOwner);
        _visible = false;
        _currentPath = null;
        _window.Surface.ClearImages();
        _display.Clear();
        _navigator.Reset();
        if (closeSession)
        {
            _worker?.CloseSession();
        }

        _placeNear = 0;
        _prefetcher.Cancel();
        _filmstrip.OnHidden();
        _inspector.OnHidden();
        _exif.OnHidden();
        _shareCheck.Close();
        _similar.Close(); // the worker ends and the results are released
        _window.SetImageTools(false);
        _pinned = false;
        _window.SetPinned(false);
        _contents.Reset(ContentReset.Hidden, "hidden");
        _active = null;
        SetPaging(false, false, false);
        _focusWatcher.Stop();
        _followedView = 0;
        UpdateNavigationKeys();

        _timeline.Mark(requestId, "hidden", QuickViewTimeline.Now, new Dictionary<string, object?>
        {
            ["reason"] = reason,
            ["wasForeground"] = wasForeground,
            ["ownerRestored"] = ownerRestored,
            ["ownerIsForegroundAfter"] = NativeMethods.GetForegroundWindow() == _owner,
        });
        _ = _resources.TrimAfterHideAsync(requestId, () => _visible);
    }

    /// <summary>
    /// Stage 1 of every item: file facts (attributes, size, format; no decoding), then the content its file routes to
    /// takes over (thumbnail, full quality, playback, document).
    /// </summary>
    private async Task LoadAsync(long requestId, string path, CancellationToken cancellation)
    {
        (uint areaWidth, uint areaHeight) = ImageArea();
        _plannedArea = (areaWidth, areaHeight);
        ImageScaleMode mode = _settings.ScaleMode;
        FileFacts? read = null;
        try
        {
            FileFacts facts = await Task.Run(() => FileFacts.Read(path), cancellation);
            read = facts;
            _timeline.Mark(requestId, "info", QuickViewTimeline.Now, new Dictionary<string, object?>
            {
                ["format"] = facts.Format.ToString(),
                ["extension"] = Path.GetExtension(path).ToLowerInvariant(),
                ["bytes"] = facts.Length,
                ["access"] = facts.Access.ToString(),
            });
            SetInfo(PreviewInfoText.For(_strings, facts, null));
            _currentFacts = facts;
            _window.SetImageTools(Metadata.ExifCard.IsImage(facts));

            IPreviewContent content = _contents[PreviewContentRouting.Choose(facts, _pdfDocumentView, Path.GetExtension(path))];
            _active = content;
            await content.ShowAsync(new PreviewItem(requestId, path, facts, areaWidth, areaHeight, mode), cancellation);
            MarkFinished(requestId, cancellation);
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
                _window.Surface.ShowStatusOnly(_strings.GetString("QuickView_Error_Failed"));
                _display.MarkMessage(requestId);
                await ShowErrorCardAsync(new PreviewItem(requestId, path, read ?? new FileFacts(0, 0, FileFormat.Unknown, PreviewAccess.Allowed), areaWidth, areaHeight, mode), cancellation);
                MarkFinished(requestId, cancellation);
            }
        }
    }

    /// <summary>After a failure: the information card with the "could not be previewed" message (never throws).</summary>
    private async Task ShowErrorCardAsync(PreviewItem item, CancellationToken cancellation)
    {
        try
        {
            await ShowInfoCardAsync(item, "QuickView_Error_Failed", cancellation);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The message is already on screen.
        }
    }

    /// <summary>
    /// The item's own result (content or a message such as "unsupported") is on screen: the stale-image guard must
    /// not replace it with "loading" afterwards.
    /// </summary>
    private void MarkFinished(long requestId, CancellationToken cancellation)
    {
        if (!cancellation.IsCancellationRequested && _visible && Interlocked.Read(ref _currentRequest) == requestId)
        {
            _display.MarkContent(requestId);
            _inspector.OnContentShown();
            if (_currentPath is { } shown && _currentFacts is { } shownFacts)
            {
                _exif.OnItemShown(shown, shownFacts); // after the item is on screen: the preview comes first
            }

            _filmstrip.OnItemShown(); // after the item is on screen: the preview comes first
        }
    }

    /// <summary>
    /// If the next item has produced nothing to show shortly after navigation, remove the previous item's
    /// image and show "loading" so a stale picture is never presented under the new file name.
    /// </summary>
    private async Task ClearStaleImageAsync(long requestId)
    {
        await Task.Delay(StaleImageGrace);
        if (_visible && _display.NeedsStaleClear(Interlocked.Read(ref _currentRequest), requestId))
        {
            _window.Surface.ShowStatusOnly(_strings.GetString("QuickView_Loading"));
            _timeline.Mark(requestId, "stale-cleared", QuickViewTimeline.Now);
        }
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
            if (_active is { } active && active.OnAreaChanged() != AreaResponse.ReloadIfChanged)
            {
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

    private async Task MarkNextFrameAsync(long requestId, string mark)
    {
        await QuickViewPresence.NextFrameAsync();
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
}
