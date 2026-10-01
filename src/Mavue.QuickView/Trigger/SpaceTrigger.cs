namespace Mavue.QuickView.Trigger;

/// <summary>A Space press that the classifier accepted as a Quick View command.</summary>
/// <param name="Timestamp">QPC timestamp (<see cref="System.Diagnostics.Stopwatch.GetTimestamp"/>) when the hook saw the key.</param>
/// <param name="ForegroundWindow">Top-level shell window that had the foreground.</param>
/// <param name="FocusWindow">Item view that had keyboard focus.</param>
/// <param name="FocusParentWindow">Parent of the item view (the SHELLDLL_DefView), used to find the matching shell browser.</param>
/// <param name="Injected">The event was injected (SendInput, remote desktop tools, test harness).</param>
/// <param name="HookProbeResult">Result of <see cref="KeyboardHook.AcceptedProbe"/> evaluated inside the hook callback, if set.</param>
public readonly record struct SpaceTrigger(
    long Timestamp,
    nint ForegroundWindow,
    nint FocusWindow,
    nint FocusParentWindow,
    bool Injected,
    bool? HookProbeResult = null);
