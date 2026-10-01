using System.Runtime.InteropServices;
using Mavue.QuickView.Interop;

namespace Mavue.QuickView.Trigger;

/// <summary>
/// Raises <see cref="ForegroundChanged"/> when the foreground window changes anywhere on the desktop
/// (SetWinEventHook EVENT_SYSTEM_FOREGROUND, out-of-context). Quick View uses it to hide itself when
/// the user switches to an unrelated application, without polling.
/// Must be started on a thread that pumps messages (the UI thread); events arrive on that thread.
/// </summary>
public sealed class ForegroundWatcher : IDisposable
{
    private static ForegroundWatcher? s_instance;
    private nint _hook;

    /// <summary>Raised with the new foreground window handle.</summary>
    public event Action<nint>? ForegroundChanged;

    public unsafe void Start()
    {
        if (Interlocked.CompareExchange(ref s_instance, this, null) is not null)
        {
            throw new InvalidOperationException("Only one foreground watcher per process is supported.");
        }

        _hook = Win32.SetWinEventHook(
            Win32.EVENT_SYSTEM_FOREGROUND,
            Win32.EVENT_SYSTEM_FOREGROUND,
            0,
            &OnWinEvent,
            0,
            0,
            Win32.WINEVENT_OUTOFCONTEXT);
        if (_hook == 0)
        {
            Interlocked.Exchange(ref s_instance, null);
            throw new InvalidOperationException("SetWinEventHook failed.");
        }
    }

    public void Dispose()
    {
        if (_hook != 0)
        {
            Win32.UnhookWinEvent(_hook);
            _hook = 0;
        }

        Interlocked.CompareExchange(ref s_instance, null, this);
    }

    [UnmanagedCallersOnly]
    private static void OnWinEvent(nint hook, uint eventType, nint hwnd, int idObject, int idChild, uint thread, uint time)
    {
        try
        {
            s_instance?.ForegroundChanged?.Invoke(hwnd);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Mavue foreground watcher error: {ex.GetType().Name}");
        }
    }
}
