using System.Diagnostics;
using System.Runtime.InteropServices;
using Mavue.QuickView.Interop;

namespace Mavue.QuickView.Trigger;

/// <summary>
/// System-wide low-level keyboard hook (WH_KEYBOARD_LL) that turns Space presses in shell file views
/// into <see cref="SpaceTrigger"/>s (docs/ARCHITECTURE.md §4.3).
/// <para>
/// The callback runs on a dedicated thread with its own message loop and must return quickly:
/// Windows silently removes low-level hooks that exceed LowLevelHooksTimeout. It therefore only
/// inspects window classes and posts work; selection lookup and UI happen elsewhere.
/// Only Space and Escape are acted on. No key content is stored or logged.
/// </para>
/// </summary>
public sealed class KeyboardHook : IDisposable
{
    private const int VkSpace = 0x20;
    private const int VkEscape = 0x1B;
    private const int VkShift = 0x10;
    private const int VkControl = 0x11;
    private const int VkMenu = 0x12;
    private const int VkLWin = 0x5B;
    private const int VkRWin = 0x5C;
    private const uint LlkhfInjected = 0x10;
    private const uint ImeQueryTimeoutMs = 30;
    private const int VkLeft = 0x25;
    private const int VkDown = 0x28;
    private const uint WmReinstall = 0x8000 + 1; // WM_APP + 1, posted to the hook thread

    private static KeyboardHook? s_instance;

    private readonly SpaceKeyClassifier _classifier;
    private readonly Action<SpaceTrigger> _onTrigger;
    private readonly TypingTracker _typing = new(Stopwatch.Frequency);
    private readonly ManualResetEventSlim _started = new();
    private Thread? _thread;
    private uint _threadId;
    private nint _hook;
    private bool _spaceDown;
    private bool _swallowingSpace;
    private int _swallowedArrow;
    private int _reinstallCount;

    /// <param name="classifier">Decides whether a Space press opens Quick View.</param>
    /// <param name="onTrigger">Called on the hook thread; must not block (enqueue and return).</param>
    public KeyboardHook(SpaceKeyClassifier classifier, Action<SpaceTrigger> onTrigger)
    {
        ArgumentNullException.ThrowIfNull(classifier);
        ArgumentNullException.ThrowIfNull(onTrigger);
        _classifier = classifier;
        _onTrigger = onTrigger;
    }

    /// <summary>
    /// Optional Escape handler, called on the hook thread while Quick View is shown without activation.
    /// Return true to swallow the key. Must not block.
    /// </summary>
    public Func<nint, bool>? EscapeHandler { get; set; }

    /// <summary>
    /// Optional probe run synchronously inside the hook callback when a Space press is accepted, while
    /// the input event is still being processed (e.g. to test whether foreground rights are held at that
    /// moment). Must be fast; its result is carried in <see cref="SpaceTrigger.HookProbeResult"/>.
    /// </summary>
    public Func<bool>? AcceptedProbe { get; set; }

    /// <summary>
    /// Optional handler for unmodified arrow keys (←↑→↓) pressed while a shell item view has the focus.
    /// Called on the hook thread with the virtual-key code and the foreground window; return true to
    /// swallow the key (Quick View steps through a multi-selection itself). Must not block.
    /// </summary>
    public Func<int, nint, bool>? NavigationKeyHandler { get; set; }

    /// <summary>When true, Space is never taken (another Quick Look tool was given priority by the user).</summary>
    public bool SuspendSpace { get; set; }

    /// <summary>How many times the hook was re-installed (see <see cref="Reinstall"/>).</summary>
    public int ReinstallCount => Volatile.Read(ref _reinstallCount);

    /// <summary>
    /// Re-installs the hook at the head of the low-level hook chain (Windows calls the most recently
    /// installed hook first). Restores priority over tools that hooked the keyboard later (e.g. QuickLook,
    /// which then sees Space first and lets it through, causing a double preview) and recovers a hook
    /// that Windows removed silently after a callback timeout. Non-blocking; runs on the hook thread.
    /// </summary>
    public void Reinstall()
    {
        if (_threadId != 0)
        {
            Win32.PostThreadMessageW(_threadId, WmReinstall, 0, 0);
        }
    }

    /// <summary>Last decision, for diagnostics (classes only, no key content).</summary>
    public event Action<SpaceKeyContext, PassThroughReason>? Classified;

    public bool IsInstalled => _hook != 0;

    /// <summary>Installs the hook on a dedicated thread. Throws if installation fails.</summary>
    public void Start()
    {
        if (Interlocked.CompareExchange(ref s_instance, this, null) is not null)
        {
            throw new InvalidOperationException("Only one keyboard hook per process is supported.");
        }

        Exception? failure = null;
        _thread = new Thread(() =>
        {
            try
            {
                Run();
            }
            catch (Exception ex) when (!_started.IsSet)
            {
                failure = ex;
                _started.Set();
            }
        })
        {
            IsBackground = true,
            Name = "Mavue.QuickView.KeyboardHook",
            Priority = ThreadPriority.Highest,
        };
        _thread.Start();
        _started.Wait();
        if (failure is not null)
        {
            Interlocked.Exchange(ref s_instance, null);
            throw new InvalidOperationException("Failed to install the keyboard hook.", failure);
        }
    }

    public void Dispose()
    {
        if (_threadId != 0)
        {
            Win32.PostThreadMessageW(_threadId, Win32.WM_QUIT, 0, 0);
            _thread?.Join(TimeSpan.FromSeconds(2));
        }

        Interlocked.CompareExchange(ref s_instance, null, this);
        _started.Dispose();
    }

    private unsafe void Run()
    {
        _threadId = Win32.GetCurrentThreadId();
        _hook = Win32.SetWindowsHookExW(Win32.WH_KEYBOARD_LL, &HookProc, Win32.GetModuleHandleW(null), 0);
        if (_hook == 0)
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
        }

        _started.Set();
        try
        {
            while (Win32.GetMessageW(out Win32.MSG message, 0, 0, 0) > 0)
            {
                // Low-level hook callbacks are delivered while this thread waits in GetMessage.
                if (message.message == WmReinstall)
                {
                    // Install the new hook before removing the old one so no key press is missed.
                    nint replacement = Win32.SetWindowsHookExW(Win32.WH_KEYBOARD_LL, &HookProc, Win32.GetModuleHandleW(null), 0);
                    if (replacement != 0)
                    {
                        Win32.UnhookWindowsHookEx(_hook);
                        _hook = replacement;
                        Interlocked.Increment(ref _reinstallCount);
                    }
                }
            }
        }
        finally
        {
            Win32.UnhookWindowsHookEx(_hook);
            _hook = 0;
        }
    }

    [UnmanagedCallersOnly]
    private static nint HookProc(int nCode, nuint wParam, nint lParam)
    {
        KeyboardHook? self = s_instance;
        if (nCode == Win32.HC_ACTION && self is not null)
        {
            try
            {
                if (self.Handle(wParam, lParam))
                {
                    return 1;
                }
            }
            catch (Exception ex)
            {
                // Never let an exception cross the native boundary; fail open (pass the key through).
                Debug.WriteLine($"Mavue keyboard hook error: {ex.GetType().Name}");
            }
        }

        return Win32.CallNextHookEx(0, nCode, wParam, lParam);
    }

    /// <returns>True to swallow the key.</returns>
    private unsafe bool Handle(nuint wParam, nint lParam)
    {
        long now = Stopwatch.GetTimestamp();
        var info = (Win32.KBDLLHOOKSTRUCT*)lParam;
        int vk = (int)info->vkCode;
        bool isDown = wParam is Win32.WM_KEYDOWN or Win32.WM_SYSKEYDOWN;

        if (vk == VkSpace)
        {
            return isDown ? OnSpaceDown(now, (info->flags & LlkhfInjected) != 0) : OnSpaceUp();
        }

        if (vk is >= VkLeft and <= VkDown)
        {
            return OnArrow(vk, isDown);
        }

        if (!isDown)
        {
            return false;
        }

        if (vk == VkEscape && EscapeHandler is { } escape && escape(Win32.GetForegroundWindow()))
        {
            return true;
        }

        // Only character-like and commit keys matter for typing state; avoid window queries otherwise.
        if (TypingTracker.IsCharacterKey(vk) || vk is 0x08 or 0x09 or 0x0D or VkEscape)
        {
            nint focus = GetFocus(Win32.GetForegroundWindow(), out _);
            _typing.OnKeyDown(vk, IsDown(VkControl) || IsDown(VkMenu), now, focus);
        }

        return false;
    }

    private bool OnSpaceDown(long now, bool injected)
    {
        if (_spaceDown)
        {
            // Auto-repeat while held: keep swallowing if we took the first press, never re-trigger.
            return _swallowingSpace;
        }

        _spaceDown = true;
        if (SuspendSpace)
        {
            _swallowingSpace = false;
            return false;
        }

        nint foreground = Win32.GetForegroundWindow();
        nint focus = GetFocus(foreground, out nint focusParent);

        var context = new SpaceKeyContext(
            ForegroundWindowClass: Win32.GetClassName(foreground),
            FocusWindowClass: Win32.GetClassName(focus),
            FocusParentWindowClass: Win32.GetClassName(focusParent),
            HasModifiers: IsDown(VkControl) || IsDown(VkMenu) || IsDown(VkShift) || IsDown(VkLWin) || IsDown(VkRWin),
            ImeComposing: IsImeComposing(focus),
            SinceLastCharacterKey: _typing.SinceLastCharacterKey(now));

        SpaceKeyDecision decision = _classifier.Classify(context, out PassThroughReason reason);
        Classified?.Invoke(context, reason);

        _swallowingSpace = decision == SpaceKeyDecision.OpenQuickView;
        if (_swallowingSpace)
        {
            bool? probe = AcceptedProbe?.Invoke();
            _onTrigger(new SpaceTrigger(now, foreground, focus, focusParent, injected, probe));
        }

        return _swallowingSpace;
    }

    private bool OnArrow(int vk, bool isDown)
    {
        if (!isDown)
        {
            // Swallow the key-up of an arrow whose key-down we took, so Explorer sees a consistent pair.
            bool swallowUp = _swallowedArrow == vk;
            _swallowedArrow = swallowUp ? 0 : _swallowedArrow;
            return swallowUp;
        }

        if (NavigationKeyHandler is not { } handler ||
            IsDown(VkControl) || IsDown(VkMenu) || IsDown(VkShift) || IsDown(VkLWin) || IsDown(VkRWin))
        {
            return false;
        }

        nint foreground = Win32.GetForegroundWindow();
        nint focus = GetFocus(foreground, out nint focusParent);
        bool itemViewFocused =
            Win32.GetClassName(focusParent) == SpaceKeyClassifier.ShellViewClass &&
            Win32.GetClassName(focus) is SpaceKeyClassifier.DirectUiViewClass or SpaceKeyClassifier.ListViewClass;
        if (!itemViewFocused || !handler(vk, foreground))
        {
            return false;
        }

        _swallowedArrow = vk;
        return true;
    }

    private bool OnSpaceUp()
    {
        _spaceDown = false;
        bool swallow = _swallowingSpace;
        _swallowingSpace = false;
        return swallow;
    }

    private bool IsImeComposing(nint focus)
    {
        // Querying another process's IME is a cross-process SendMessage; only do it when typing
        // since the last commit makes a composition possible, and bound it with a short timeout.
        if (_typing.PendingCharactersSinceCommit == 0 || focus == 0)
        {
            return false;
        }

        nint imeWindow = Win32.ImmGetDefaultIMEWnd(focus);
        if (imeWindow == 0)
        {
            return false;
        }

        nint ok = Win32.SendMessageTimeoutW(imeWindow, Win32.WM_IME_CONTROL, Win32.IMC_GETOPENSTATUS, 0, Win32.SMTO_ABORTIFHUNG, ImeQueryTimeoutMs, out nuint open);

        // If the query fails, assume composition: passing Space through is the safe failure mode.
        return ok == 0 || open != 0;
    }

    private static unsafe nint GetFocus(nint foreground, out nint focusParent)
    {
        focusParent = 0;
        if (foreground == 0)
        {
            return 0;
        }

        uint thread = Win32.GetWindowThreadProcessId(foreground, out _);
        var gui = new Win32.GUITHREADINFO { cbSize = (uint)sizeof(Win32.GUITHREADINFO) };
        if (!Win32.GetGUIThreadInfo(thread, ref gui) || gui.hwndFocus == 0)
        {
            return 0;
        }

        focusParent = Win32.GetAncestor(gui.hwndFocus, Win32.GA_PARENT);
        return gui.hwndFocus;
    }

    private static bool IsDown(int vk) => (Win32.GetAsyncKeyState(vk) & 0x8000) != 0;
}
