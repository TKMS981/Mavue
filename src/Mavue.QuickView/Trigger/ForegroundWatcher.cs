namespace Mavue.QuickView.Trigger;

/// <summary>
/// Raises <see cref="ForegroundChanged"/> when the foreground window changes anywhere on the desktop
/// (EVENT_SYSTEM_FOREGROUND). Quick View uses it to hide itself when the user switches to an unrelated
/// application, without polling. Must be started on a thread that pumps messages (the UI thread).
/// </summary>
public sealed class ForegroundWatcher : IDisposable
{
    private readonly WinEventWatcher _watcher = new();

    public ForegroundWatcher()
    {
        _watcher.EventRaised += (_, hwnd, _, _) => ForegroundChanged?.Invoke(hwnd);
    }

    /// <summary>Raised with the new foreground window handle.</summary>
    public event Action<nint>? ForegroundChanged;

    public void Start() => _watcher.Start(WinEventWatcher.EventSystemForeground, WinEventWatcher.EventSystemForeground);

    public void Dispose() => _watcher.Dispose();
}
