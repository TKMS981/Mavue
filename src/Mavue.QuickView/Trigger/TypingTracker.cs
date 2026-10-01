namespace Mavue.QuickView.Trigger;

/// <summary>
/// Tracks recent character input so the Space classifier can tell "Space as part of typing" from
/// "Space as a Quick View command" (docs/WINDOWS-INTEGRATION.md §1):
/// <list type="bullet">
/// <item>Explorer type-ahead ("my file") — measured by <see cref="SinceLastCharacterKey"/>.</item>
/// <item>IME composition — a low-level hook cannot read another process's composition state, so
/// <see cref="PendingCharactersSinceCommit"/> counts characters typed since the last commit-like key
/// (Enter/Esc/Tab) or focus change; combined with the target's IME open status this approximates
/// "composition in progress" (Space converts instead of opening Quick View).</item>
/// </list>
/// Pure logic: virtual-key codes and timestamps in, state out. Not thread-safe (owned by the hook thread).
/// </summary>
public sealed class TypingTracker
{
    private const int VkBack = 0x08;
    private const int VkTab = 0x09;
    private const int VkReturn = 0x0D;
    private const int VkEscape = 0x1B;

    private readonly long _ticksPerSecond;
    private long _lastCharacterTimestamp;
    private bool _hasCharacter;
    private nint _lastFocus;

    /// <param name="timestampFrequency">Ticks per second of the timestamps passed in (e.g. <see cref="System.Diagnostics.Stopwatch.Frequency"/>).</param>
    public TypingTracker(long timestampFrequency)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(timestampFrequency);
        _ticksPerSecond = timestampFrequency;
    }

    /// <summary>Characters typed since the last commit key or focus change.</summary>
    public int PendingCharactersSinceCommit { get; private set; }

    /// <summary>Records a key-down event.</summary>
    /// <param name="virtualKey">Virtual-key code.</param>
    /// <param name="ctrlOrAltHeld">Ctrl or Alt is held (shortcuts are not typing).</param>
    /// <param name="timestamp">Event time in ticks of the configured frequency.</param>
    /// <param name="focusWindow">Window with keyboard focus when the key was pressed.</param>
    public void OnKeyDown(int virtualKey, bool ctrlOrAltHeld, long timestamp, nint focusWindow)
    {
        if (focusWindow != _lastFocus)
        {
            _lastFocus = focusWindow;
            PendingCharactersSinceCommit = 0;
        }

        if (virtualKey is VkReturn or VkEscape or VkTab)
        {
            PendingCharactersSinceCommit = 0;
            return;
        }

        if (ctrlOrAltHeld)
        {
            return;
        }

        if (IsCharacterKey(virtualKey) || virtualKey == VkBack)
        {
            _lastCharacterTimestamp = timestamp;
            _hasCharacter = true;
            if (virtualKey != VkBack)
            {
                PendingCharactersSinceCommit++;
            }
        }
    }

    /// <summary>Time since the last character key, or null if none has been seen.</summary>
    public TimeSpan? SinceLastCharacterKey(long now)
    {
        if (!_hasCharacter)
        {
            return null;
        }

        long elapsed = Math.Max(0, now - _lastCharacterTimestamp);
        return TimeSpan.FromSeconds((double)elapsed / _ticksPerSecond);
    }

    /// <summary>Keys that produce text in Explorer's type-ahead or an IME composition.</summary>
    public static bool IsCharacterKey(int vk) =>
        vk is >= 0x30 and <= 0x39      // 0-9
            or >= 0x41 and <= 0x5A     // A-Z
            or >= 0x60 and <= 0x6F     // numpad digits and operators
            or >= 0xBA and <= 0xC0     // OEM ; = , - . / `
            or >= 0xDB and <= 0xDF     // OEM [ \ ] ' and OEM_8
            or 0xE2                    // OEM_102 (JIS backslash/underscore)
            or 0xE5;                   // VK_PROCESSKEY (IME processing)
}
