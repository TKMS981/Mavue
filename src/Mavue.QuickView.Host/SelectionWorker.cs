using Mavue.QuickView.Diagnostics;
using Mavue.QuickView.Shell;
using Mavue.QuickView.Trigger;
using Microsoft.UI.Dispatching;

namespace Mavue.QuickView.Host;

/// <summary>
/// Owns all Shell COM work on one STA thread: resolves the Explorer selection for each Space trigger,
/// keeps an event subscription (<see cref="ExplorerViewSession"/>) on that view while Quick View is open,
/// and reports selection changes to the UI. Selection changes are event-driven (no polling); bursts of
/// events (e.g. a held arrow key) are coalesced into one read.
/// </summary>
internal sealed class SelectionWorker : IDisposable
{
    private static long s_nextRequestId;

    private readonly StaThread _thread = new("Mavue.QuickView.Selection");
    private readonly ExplorerSelectionProvider _provider = new();
    private readonly QuickViewTimeline _timeline;
    private readonly DispatcherQueue _dispatcher;
    private readonly QuickViewController _controller;

    // STA-thread state.
    private ExplorerViewSession? _session;
    private bool _readQueued;
    private long _firstEventQpc;

    public SelectionWorker(QuickViewTimeline timeline, DispatcherQueue dispatcher, QuickViewController controller, bool traceShellCalls)
    {
        _timeline = timeline;
        _dispatcher = dispatcher;
        _controller = controller;
        if (traceShellCalls)
        {
            // HRESULTs, window handles and class names only; never paths.
            _provider.Trace = message => _timeline.Mark(0, "shell-trace", QuickViewTimeline.Now, new Dictionary<string, object?> { ["message"] = message });
        }
    }

    /// <summary>Request ids are shared by Space triggers and navigation so every shown item is measurable.</summary>
    public static long NextRequestId() => Interlocked.Increment(ref s_nextRequestId);

    /// <summary>Connects to ShellWindows before the first Space press.</summary>
    public Task WarmAsync() => _thread.InvokeAsync(() =>
    {
        try
        {
            _provider.Warm();
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // Explorer not running yet (e.g. very early logon); the first request will connect.
        }

        return true;
    });

    /// <summary>Called on the hook thread: record and enqueue only.</summary>
    public void Enqueue(SpaceTrigger trigger)
    {
        long id = NextRequestId();
        _timeline.Mark(id, "hook", trigger.Timestamp, new Dictionary<string, object?> { ["injected"] = trigger.Injected });
        _thread.Post(() => Resolve(id, trigger));
    }

    /// <summary>
    /// Follows another view of the same Explorer window (e.g. the user switched tabs): re-subscribes and
    /// reports that view's current selection as a selection change.
    /// </summary>
    public void Rebind(nint topLevelWindow, nint shellViewWindow) => _thread.Post(() =>
    {
        CloseSessionCore();
        long eventQpc = QuickViewTimeline.Now;
        ViewSelection selection = ViewSelection.Empty;
        try
        {
            _session = _provider.OpenSession(topLevelWindow, shellViewWindow, OnViewEvent);
            selection = _session?.ReadSelection() ?? ViewSelection.Empty;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidCastException or InvalidOperationException)
        {
            // Treated as an empty selection: Quick View closes rather than showing a stale item.
        }

        long readQpc = QuickViewTimeline.Now;
        _dispatcher.TryEnqueue(DispatcherQueuePriority.High, () => _controller.OnExplorerSelectionChanged(selection, eventQpc, readQpc));
    });

    /// <summary>Previous/next items around Explorer's focused item, for prefetching.</summary>
    public Task<(string? Previous, string? Next)> ReadNeighborsAsync() => _thread.InvokeAsync(() =>
    {
        try
        {
            return _session?.ReadNeighbors() ?? (null, null);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            return ((string?)null, (string?)null);
        }
    });

    /// <summary>Stops following Explorer (Quick View closed).</summary>
    public void CloseSession() => _thread.Post(CloseSessionCore);

    /// <summary>Asks Explorer to move its selection (Quick View has keyboard focus, single-item mode).</summary>
    public void MoveExplorerSelection(int delta) => _thread.Post(() =>
    {
        try
        {
            _session?.MoveSelection(delta);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // The Explorer window went away; the Closed event or the next trigger handles it.
        }
    });

    public void Dispose()
    {
        _thread.Post(() =>
        {
            CloseSessionCore();
            _provider.Dispose();
        });
        _thread.Dispose();
    }

    private void Resolve(long id, SpaceTrigger trigger)
    {
        ViewSelection? selection = null;
        string? error = null;
        try
        {
            // A new Space starts a new subscription on the view that has the focus now.
            CloseSessionCore();
            _session = _provider.OpenSession(trigger.ForegroundWindow, trigger.FocusParentWindow, OnViewEvent);
            selection = _session?.ReadSelection();
            if (selection is null)
            {
                // No event subscription possible (e.g. the desktop): read once without following.
                ExplorerSelection? plain = _provider.GetSelection(trigger.ForegroundWindow, trigger.FocusParentWindow);
                selection = plain is null ? null : new ViewSelection(plain.Paths, null, plain.NonFileSystemItemCount);
            }
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidCastException or InvalidOperationException)
        {
            error = ex.GetType().Name;
        }

        _timeline.Mark(id, "selection", QuickViewTimeline.Now, new Dictionary<string, object?>
        {
            ["found"] = selection is not null,
            ["count"] = selection?.Paths.Count ?? 0,
            ["nonFileSystem"] = selection?.NonFileSystemItemCount ?? 0,
            ["following"] = _session?.IsListening ?? false,
            ["error"] = error,
        });

        _dispatcher.TryEnqueue(DispatcherQueuePriority.High, () => _controller.OnTrigger(id, trigger, selection));
    }

    /// <summary>Runs on the STA thread, inside the dispatch of Explorer's event call.</summary>
    private void OnViewEvent(ViewEvent viewEvent)
    {
        if (viewEvent == ViewEvent.Closed)
        {
            CloseSessionCore();
            _dispatcher.TryEnqueue(DispatcherQueuePriority.High, _controller.OnExplorerViewClosed);
            return;
        }

        // Coalesce: one read covers every event that arrives before it runs.
        if (!_readQueued)
        {
            _readQueued = true;
            _firstEventQpc = QuickViewTimeline.Now;
            _thread.Post(PublishSelection);
        }
    }

    private void PublishSelection()
    {
        _readQueued = false;
        if (_session is null)
        {
            return;
        }

        ViewSelection selection;
        try
        {
            selection = _session.ReadSelection();
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            return;
        }

        long eventQpc = _firstEventQpc;
        long readQpc = QuickViewTimeline.Now;
        _dispatcher.TryEnqueue(DispatcherQueuePriority.High, () => _controller.OnExplorerSelectionChanged(selection, eventQpc, readQpc));
    }

    private void CloseSessionCore()
    {
        _session?.Dispose();
        _session = null;
        _readQueued = false;
    }
}
