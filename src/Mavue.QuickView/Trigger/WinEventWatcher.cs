using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Mavue.QuickView.Interop;

namespace Mavue.QuickView.Trigger;

/// <summary>
/// Out-of-context accessibility event subscription (SetWinEventHook). Events are delivered through the
/// message loop of the thread that called <see cref="Start"/> (the UI thread), so no polling is needed.
/// Several watchers can coexist; each is scoped to an event range and optionally to one process.
/// </summary>
public sealed class WinEventWatcher : IDisposable
{
    public const uint EventSystemForeground = 0x0003;
    public const uint EventObjectFocus = 0x8005;

    private static readonly ConcurrentDictionary<nint, WinEventWatcher> s_byHook = new();
    private nint _hook;

    /// <summary>Raised with (event, window, objectId, childId).</summary>
    public event Action<uint, nint, int, int>? EventRaised;

    public bool IsRunning => _hook != 0;

    /// <summary>Starts receiving events in [<paramref name="eventMin"/>, <paramref name="eventMax"/>].</summary>
    /// <param name="processId">Only events from this process (0 = all processes).</param>
    public unsafe void Start(uint eventMin, uint eventMax, uint processId = 0)
    {
        if (_hook != 0)
        {
            throw new InvalidOperationException("Already started.");
        }

        _hook = Win32.SetWinEventHook(eventMin, eventMax, 0, &OnWinEvent, processId, 0, Win32.WINEVENT_OUTOFCONTEXT);
        if (_hook == 0)
        {
            throw new InvalidOperationException("SetWinEventHook failed.");
        }

        s_byHook[_hook] = this;
    }

    /// <summary>Stops receiving events. The watcher can be started again.</summary>
    public void Stop()
    {
        if (_hook == 0)
        {
            return;
        }

        Win32.UnhookWinEvent(_hook);
        s_byHook.TryRemove(_hook, out _);
        _hook = 0;
    }

    public void Dispose() => Stop();

    [UnmanagedCallersOnly]
    private static void OnWinEvent(nint hook, uint eventType, nint hwnd, int idObject, int idChild, uint thread, uint time)
    {
        try
        {
            if (s_byHook.TryGetValue(hook, out WinEventWatcher? watcher))
            {
                watcher.EventRaised?.Invoke(eventType, hwnd, idObject, idChild);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Mavue WinEvent watcher error: {ex.GetType().Name}");
        }
    }
}
