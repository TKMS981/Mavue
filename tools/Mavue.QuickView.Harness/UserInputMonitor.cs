using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Mavue.QuickView.Harness;

/// <summary>
/// Counts real (non-injected) keyboard and mouse input while the harness runs, so samples taken while
/// someone was using the PC can be flagged: physical input changes Windows' foreground-rights state.
/// Only counts and timestamps are kept; no key codes or positions.
/// </summary>
internal sealed partial class UserInputMonitor : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WH_MOUSE_LL = 14;
    private const uint LLKHF_INJECTED = 0x10;
    private const uint LLMHF_INJECTED = 0x01;
    private const uint WM_QUIT = 0x0012;

    private static UserInputMonitor? s_instance;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new();
    private uint _threadId;
    private long _lastRealInput;
    private int _realInputCount;

    public UserInputMonitor()
    {
        s_instance = this;
        _thread = new Thread(Run) { IsBackground = true, Name = "UserInputMonitor" };
        _thread.Start();
        _ready.Wait();
    }

    /// <summary>Number of real input events since the monitor started.</summary>
    public int RealInputCount => Volatile.Read(ref _realInputCount);

    /// <summary>True if real input happened at or after <paramref name="qpc"/>.</summary>
    public bool RealInputSince(long qpc) => Interlocked.Read(ref _lastRealInput) >= qpc;

    public void Dispose()
    {
        PostThreadMessageW(_threadId, WM_QUIT, 0, 0);
        _thread.Join(1000);
        s_instance = null;
        _ready.Dispose();
    }

    private unsafe void Run()
    {
        _threadId = GetCurrentThreadId();
        nint module = GetModuleHandleW(null);
        nint keyboard = SetWindowsHookExW(WH_KEYBOARD_LL, &KeyboardProc, module, 0);
        nint mouse = SetWindowsHookExW(WH_MOUSE_LL, &MouseProc, module, 0);
        _ready.Set();
        while (GetMessageW(out _, 0, 0, 0) > 0)
        {
        }

        UnhookWindowsHookEx(keyboard);
        UnhookWindowsHookEx(mouse);
    }

    private void Record()
    {
        Interlocked.Exchange(ref _lastRealInput, Stopwatch.GetTimestamp());
        Interlocked.Increment(ref _realInputCount);
    }

    [UnmanagedCallersOnly]
    private static unsafe nint KeyboardProc(int code, nuint wParam, nint lParam)
    {
        if (code == 0 && (((uint*)lParam)[2] & LLKHF_INJECTED) == 0)
        {
            s_instance?.Record();
        }

        return CallNextHookEx(0, code, wParam, lParam);
    }

    [UnmanagedCallersOnly]
    private static unsafe nint MouseProc(int code, nuint wParam, nint lParam)
    {
        // MSLLHOOKSTRUCT: POINT pt (8 bytes), mouseData (4), flags (4) ...
        if (code == 0 && (((uint*)lParam)[3] & LLMHF_INJECTED) == 0)
        {
            s_instance?.Record();
        }

        return CallNextHookEx(0, code, wParam, lParam);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public nint hwnd;
        public uint message;
        public nuint wParam;
        public nint lParam;
        public uint time;
        public int x, y;
    }

    [LibraryImport("user32.dll")]
    private static unsafe partial nint SetWindowsHookExW(int id, delegate* unmanaged<int, nuint, nint, nint> proc, nint module, uint thread);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnhookWindowsHookEx(nint hook);

    [LibraryImport("user32.dll")]
    private static partial nint CallNextHookEx(nint hook, int code, nuint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    private static partial int GetMessageW(out MSG msg, nint hwnd, uint min, uint max);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostThreadMessageW(uint thread, uint msg, nuint wParam, nint lParam);

    [LibraryImport("kernel32.dll")]
    private static partial uint GetCurrentThreadId();

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint GetModuleHandleW(string? name);
}
