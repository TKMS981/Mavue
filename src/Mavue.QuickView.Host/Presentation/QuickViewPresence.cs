using System.Runtime.InteropServices;
using Mavue.Core.Viewing;
using Mavue.QuickView.Diagnostics;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

namespace Mavue.QuickView.Host.Presentation;

/// <summary>
/// How the single, pre-created Quick View window appears and disappears: placement on the owner's monitor,
/// activation (the passive "panel" by default, docs/QUICKVIEW-POC.md §3.2), leaving the topmost band before hiding,
/// and which foreground changes close it. Knows nothing about what the window shows.
/// </summary>
internal sealed class QuickViewPresence
{
    // Windows that briefly take the foreground while the user switches windows (Alt+Tab, Win+Tab,
    // taskbar). Hiding on these would close Quick View in the middle of a switch back to Explorer.
    private static readonly HashSet<string> TransientForegroundClasses = new(StringComparer.Ordinal)
    {
        "XamlExplorerHostIslandWindow",
        "MultitaskingViewFrame",
        "ForegroundStaging",
        "TaskSwitcherWnd",
        "Shell_TrayWnd",
        "Shell_SecondaryTrayWnd",
    };

    private const uint NoActivateFlags = NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE;

    private readonly QuickViewWindow _window;
    private readonly QuickViewTimeline _timeline;
    private readonly HostOptions _options;
    private volatile bool _passive;
    private bool _topmost;

    public QuickViewPresence(QuickViewWindow window, QuickViewTimeline timeline, HostOptions options)
    {
        _window = window;
        _timeline = timeline;
        _options = options;
    }

    /// <summary>
    /// Shown without activation (Explorer keeps the keyboard; Space/Esc/arrows arrive through the hook). Read on the
    /// hook thread. Cleared when the user clicks into the window.
    /// </summary>
    public bool IsPassive
    {
        get => _passive;
        set => _passive = value;
    }

    /// <summary>
    /// Context menu: the requesting process (started by Explorer from the user's click) called AllowSetForegroundWindow
    /// for us, so the next show activates normally instead of the passive panel.
    /// </summary>
    public bool GrantedActivation { get; set; }

    private AppWindow AppWindow => _window.AppWindow;

    /// <summary>
    /// Renders the hidden window once (cloaked, so nothing is visible) so the first Space press does not pay for XAML
    /// tree creation, swap chain setup and DWM registration.
    /// </summary>
    public async Task PrewarmAsync()
    {
        Place(0, 0);
        int cloak = 1;
        NativeMethods.DwmSetWindowAttribute(_window.Handle, NativeMethods.DWMWA_CLOAK, ref cloak, sizeof(int));
        AppWindow.Show(false);
        await NextFrameAsync();
        AppWindow.Hide();
        cloak = 0;
        NativeMethods.DwmSetWindowAttribute(_window.Handle, NativeMethods.DWMWA_CLOAK, ref cloak, sizeof(int));
    }

    /// <summary>
    /// Centers the window on the owner's monitor (or the primary monitor without an owner), at the size
    /// <see cref="QuickViewWindowSize"/> gives for that monitor's work area and scale.
    /// </summary>
    public void Place(nint owner, long requestId)
    {
        DisplayArea area = owner != 0
            ? DisplayArea.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(owner), DisplayAreaFallback.Nearest)
            : DisplayArea.Primary;
        RectInt32 work = area.WorkArea;
        (int width, int height) = QuickViewWindowSize.For(work.Width, work.Height, MonitorScale(work));
        var target = new RectInt32(work.X + ((work.Width - width) / 2), work.Y + ((work.Height - height) / 2), width, height);
        AppWindow.MoveAndResize(target);
        if (AppWindow.Size.Width != width || AppWindow.Size.Height != height)
        {
            // Moving to a monitor with another scale makes Windows resize the window for the new DPI during
            // the move (WM_DPICHANGED), overriding the requested size; on the new monitor it sticks.
            AppWindow.MoveAndResize(target);
        }

        _timeline.Mark(requestId, "placed", QuickViewTimeline.Now, new Dictionary<string, object?>
        {
            ["width"] = AppWindow.Size.Width,
            ["height"] = AppWindow.Size.Height,
            ["dpi"] = NativeMethods.GetDpiForWindow(_window.Handle),
            ["workWidth"] = work.Width,
            ["workHeight"] = work.Height,
        });
    }

    /// <summary>DPI / 96 of the monitor that holds <paramref name="work"/> (the window may still be on another monitor).</summary>
    private double MonitorScale(RectInt32 work)
    {
        var center = new NativeMethods.POINT { X = work.X + (work.Width / 2), Y = work.Y + (work.Height / 2) };
        nint monitor = NativeMethods.MonitorFromPoint(center, NativeMethods.MONITOR_DEFAULTTONEAREST);
        if (monitor != 0 && NativeMethods.GetDpiForMonitor(monitor, NativeMethods.MDT_EFFECTIVE_DPI, out uint dpi, out _) == 0 && dpi > 0)
        {
            return dpi / 96.0;
        }

        uint windowDpi = NativeMethods.GetDpiForWindow(_window.Handle);
        return windowDpi > 0 ? windowDpi / 96.0 : 1.0;
    }

    /// <summary>Shows the window as the activation mode says (see <see cref="ActivationMode"/>).</summary>
    /// <param name="owner">Explorer window Quick View belongs to (0 for none).</param>
    /// <param name="hookProbe">Whether the hook callback could reserve foreground rights (Auto mode).</param>
    public void Activate(long requestId, nint owner, bool? hookProbe)
    {
        nint hwnd = _window.Handle;
        bool? setForegroundResult = null;
        int lastError = 0;
        bool attached = false;

        bool fellBack = false;
        string mode = _options.Activation.ToString();
        if (GrantedActivation)
        {
            GrantedActivation = false;
            mode = "Granted";
            AppWindow.Show(true);
            if (NativeMethods.GetForegroundWindow() != hwnd)
            {
                setForegroundResult = NativeMethods.SetForegroundWindow(hwnd);
                lastError = Marshal.GetLastPInvokeError();
            }

            if (NativeMethods.GetForegroundWindow() != hwnd)
            {
                // The right was not honored: fall back to the passive panel so the window is at least visible on top.
                NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0, NoActivateFlags | NativeMethods.SWP_SHOWWINDOW);
                _topmost = true;
                _passive = true;
                fellBack = true;
            }
        }
        else
        {
            switch (_options.Activation)
            {
                case ActivationMode.Auto:
                    if (hookProbe == true)
                    {
                        // The hook callback reserved foreground rights while the Space event was processed.
                        AppWindow.Show(true);
                        if (NativeMethods.GetForegroundWindow() != hwnd)
                        {
                            setForegroundResult = NativeMethods.SetForegroundWindow(hwnd);
                            lastError = Marshal.GetLastPInvokeError();
                        }
                    }

                    if (NativeMethods.GetForegroundWindow() != hwnd)
                    {
                        // No rights: show passively. Attempting activation here would only flash the taskbar.
                        NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOP, 0, 0, 0, 0, NoActivateFlags | NativeMethods.SWP_SHOWWINDOW);
                        _passive = true;
                        fellBack = true;
                    }

                    break;

                case ActivationMode.AppWindowShow:
                    AppWindow.Show(true);
                    break;

                case ActivationMode.SetForeground:
                case ActivationMode.HookGrant:
                    AppWindow.Show(true);
                    if (NativeMethods.GetForegroundWindow() != hwnd)
                    {
                        setForegroundResult = NativeMethods.SetForegroundWindow(hwnd);
                        lastError = Marshal.GetLastPInvokeError();
                    }

                    break;

                case ActivationMode.AttachThreadInput:
                    AppWindow.Show(true);
                    if (NativeMethods.GetForegroundWindow() != hwnd)
                    {
                        uint foregroundThread = NativeMethods.GetWindowThreadProcessId(NativeMethods.GetForegroundWindow(), out _);
                        uint thisThread = NativeMethods.GetCurrentThreadId();
                        attached = foregroundThread != thisThread && NativeMethods.AttachThreadInput(thisThread, foregroundThread, true);
                        setForegroundResult = NativeMethods.SetForegroundWindow(hwnd);
                        lastError = Marshal.GetLastPInvokeError();
                        if (attached)
                        {
                            NativeMethods.AttachThreadInput(thisThread, foregroundThread, false);
                        }
                    }

                    break;

                case ActivationMode.NoActivate:
                    // Shown and raised to the top of the (non-topmost) z-order without activation.
                    setForegroundResult = NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOP, 0, 0, 0, 0, NoActivateFlags | NativeMethods.SWP_SHOWWINDOW);
                    _passive = true;
                    break;

                case ActivationMode.Panel:
                    setForegroundResult = NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0, NoActivateFlags | NativeMethods.SWP_SHOWWINDOW);
                    _topmost = true;
                    _passive = true;
                    break;

                case ActivationMode.NoActivateTopmost:
                    // Comparison: momentary topmost, then back to the normal band (still above Explorer).
                    NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0, NoActivateFlags | NativeMethods.SWP_SHOWWINDOW);
                    setForegroundResult = NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_NOTOPMOST, 0, 0, 0, 0, NoActivateFlags);
                    _passive = true;
                    break;
            }
        }

        _timeline.Mark(requestId, "shown", QuickViewTimeline.Now, new Dictionary<string, object?>
        {
            ["mode"] = mode,
            ["aboveOwner"] = IsAbove(hwnd, owner),
            ["isForeground"] = NativeMethods.GetForegroundWindow() == hwnd,
            ["setForegroundResult"] = setForegroundResult,
            ["lastError"] = lastError,
            ["attached"] = attached,
            ["fellBackToNoActivate"] = fellBack,
        });
    }

    /// <summary>
    /// Hides the window: hands activation back to the owner while still foreground (if asked), leaves the topmost band
    /// first so the window can never linger above other apps.
    /// </summary>
    public (bool WasForeground, bool? OwnerRestored) Hide(nint owner, bool restoreOwner)
    {
        nint hwnd = _window.Handle;
        bool wasForeground = NativeMethods.GetForegroundWindow() == hwnd;
        bool? ownerRestored = null;

        // While still foreground we may hand activation back explicitly; afterwards we could not.
        if (restoreOwner && wasForeground && NativeMethods.IsWindow(owner))
        {
            ownerRestored = NativeMethods.SetForegroundWindow(owner);
        }

        if (_topmost)
        {
            NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_NOTOPMOST, 0, 0, 0, 0, NoActivateFlags);
            _topmost = false;
        }

        AppWindow.Hide();
        _passive = false;
        return (wasForeground, ownerRestored);
    }

    /// <summary>True if the window that became foreground is a transient system window (Alt+Tab, taskbar).</summary>
    public static bool IsTransient(string windowClass) => TransientForegroundClasses.Contains(windowClass);

    public static int ProcessIdOf(nint window)
    {
        _ = NativeMethods.GetWindowThreadProcessId(window, out uint pid);
        return (int)pid;
    }

    /// <summary>Diagnostics only (which application took the foreground).</summary>
    public static string ProcessNameOf(nint window)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(ProcessIdOf(window));
            return process.ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return "?";
        }
    }

    /// <summary>Completes when XAML renders the next frame.</summary>
    public static Task NextFrameAsync()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<object>? handler = null;
        handler = (_, _) =>
        {
            CompositionTarget.Rendering -= handler;
            completion.TrySetResult();
        };
        CompositionTarget.Rendering += handler;
        return completion.Task;
    }

    /// <summary>True if <paramref name="window"/> is above <paramref name="other"/> in the z-order.</summary>
    private static bool IsAbove(nint window, nint other)
    {
        if (other == 0)
        {
            return true;
        }

        // Walk down from the window; finding the other window below means we are above it.
        for (nint next = NativeMethods.GetWindow(window, NativeMethods.GW_HWNDNEXT); next != 0; next = NativeMethods.GetWindow(next, NativeMethods.GW_HWNDNEXT))
        {
            if (next == other)
            {
                return true;
            }
        }

        return false;
    }
}
