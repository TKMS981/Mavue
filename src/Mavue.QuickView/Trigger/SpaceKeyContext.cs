namespace Mavue.QuickView.Trigger;

/// <summary>
/// Snapshot of the desktop state at the moment Space is pressed, gathered by the low-level
/// keyboard hook (GetForegroundWindow / GetGUIThreadInfo / GetClassName). Kept free of Win32
/// types so the decision logic can be tested without a live Explorer (docs/ARCHITECTURE.md §4.3).
/// </summary>
/// <param name="ForegroundWindowClass">Window class of the foreground top-level window.</param>
/// <param name="FocusWindowClass">Window class of the keyboard-focus window in the foreground thread.</param>
/// <param name="FocusParentWindowClass">Window class of the focus window's parent.</param>
/// <param name="HasModifiers">Ctrl, Alt, Shift or Win is held.</param>
/// <param name="ImeComposing">The focused thread's IME has an active composition.</param>
/// <param name="SinceLastCharacterKey">Time since the previous character key; null if none was observed.</param>
public readonly record struct SpaceKeyContext(
    string ForegroundWindowClass,
    string FocusWindowClass,
    string? FocusParentWindowClass,
    bool HasModifiers,
    bool ImeComposing,
    TimeSpan? SinceLastCharacterKey);
