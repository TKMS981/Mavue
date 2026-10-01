using Mavue.QuickView.Diagnostics;
using Mavue.QuickView.Shell;
using Mavue.QuickView.Trigger;
using Microsoft.UI.Dispatching;

namespace Mavue.QuickView.Host;

/// <summary>
/// Resolves the Explorer selection for each Space trigger on a dedicated STA thread, keeping
/// cross-process COM calls off both the hook thread and the UI thread.
/// </summary>
internal sealed class SelectionWorker : IDisposable
{
    private readonly StaThread _thread = new("Mavue.QuickView.Selection");
    private readonly ExplorerSelectionProvider _provider = new();
    private readonly QuickViewTimeline _timeline;
    private readonly DispatcherQueue _dispatcher;
    private readonly QuickViewController _controller;
    private long _nextRequestId;

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
        long id = Interlocked.Increment(ref _nextRequestId);
        _timeline.Mark(id, "hook", trigger.Timestamp, new Dictionary<string, object?> { ["injected"] = trigger.Injected });
        _thread.Post(() => Resolve(id, trigger));
    }

    public void Dispose()
    {
        _thread.Post(_provider.Dispose);
        _thread.Dispose();
    }

    private void Resolve(long id, SpaceTrigger trigger)
    {
        ExplorerSelection? selection = null;
        string? error = null;
        try
        {
            selection = _provider.GetSelection(trigger.ForegroundWindow, trigger.FocusParentWindow);
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
            ["error"] = error,
        });

        _dispatcher.TryEnqueue(DispatcherQueuePriority.High, () => _controller.OnTrigger(id, trigger, selection));
    }
}
