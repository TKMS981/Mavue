using System.Runtime.InteropServices;
using System.Text;

namespace Mavue.QuickView.Harness;

internal static partial class Native
{
    public const ushort VK_SPACE = 0x20;
    public const ushort VK_LEFT = 0x25;
    public const ushort VK_RIGHT = 0x27;
    public const ushort VK_DOWN = 0x28;
    public const ushort VK_ESCAPE = 0x1B;
    public const ushort VK_TAB = 0x09;
    public const ushort VK_MENU = 0x12;
    public const uint KEYEVENTF_KEYUP = 0x0002;
    public const uint WM_CLOSE = 0x0010;
    public const uint GA_ROOT = 2;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct GUITHREADINFO
    {
        public uint cbSize, flags;
        public nint hwndActive, hwndFocus, hwndCapture, hwndMenuOwner, hwndMoveSize, hwndCaret;
        public RECT rcCaret;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public nuint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Explicit, Size = 40)]
    public struct INPUT
    {
        [FieldOffset(0)]
        public uint type;

        [FieldOffset(8)]
        public KEYBDINPUT ki;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial uint SendInput(uint cInputs, [In] INPUT[] pInputs, int cbSize);

    [LibraryImport("user32.dll")]
    public static partial uint MapVirtualKeyW(uint uCode, uint uMapType);

    [LibraryImport("user32.dll")]
    public static partial nint GetForegroundWindow();

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        public nint hwnd;
        public uint message;
        public nuint wParam;
        public nint lParam;
        public uint time;
        public int x, y;
    }

    /// <summary>Called once so this console thread owns a message queue (required by AttachThreadInput).</summary>
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PeekMessageW(out MSG msg, nint hwnd, uint min, uint max, uint remove);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetForegroundWindow(nint hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool BringWindowToTop(nint hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AttachThreadInput(uint idAttach, uint idAttachTo, [MarshalAs(UnmanagedType.Bool)] bool fAttach);

    [LibraryImport("user32.dll")]
    public static partial uint GetWindowThreadProcessId(nint hWnd, out uint pid);

    [LibraryImport("kernel32.dll")]
    public static partial uint GetCurrentThreadId();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetGUIThreadInfo(uint idThread, ref GUITHREADINFO pgui);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static unsafe partial int GetClassNameW(nint hWnd, char* buffer, int max);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetWindowRect(nint hWnd, out RECT rect);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindowVisible(nint hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindow(nint hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PostMessageW(nint hWnd, uint msg, nuint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    public static partial nint GetAncestor(nint hwnd, uint flags);

    [LibraryImport("user32.dll")]
    public static unsafe partial int EnumWindows(delegate* unmanaged<nint, nint, int> callback, nint lParam);

    [LibraryImport("user32.dll")]
    public static partial nint GetDC(nint hWnd);

    [LibraryImport("user32.dll")]
    public static partial int ReleaseDC(nint hWnd, nint hdc);

    [LibraryImport("gdi32.dll")]
    public static partial uint GetPixel(nint hdc, int x, int y);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetProcessDpiAwarenessContext(nint value);

    public static unsafe string ClassName(nint hwnd)
    {
        char* b = stackalloc char[256];
        int n = GetClassNameW(hwnd, b, 256);
        return n > 0 ? new string(b, 0, n) : string.Empty;
    }

    public static (nint Focus, string FocusClass) Focus(nint foreground)
    {
        uint thread = GetWindowThreadProcessId(foreground, out _);
        var gui = new GUITHREADINFO { cbSize = (uint)Marshal.SizeOf<GUITHREADINFO>() };
        return GetGUIThreadInfo(thread, ref gui) ? (gui.hwndFocus, ClassName(gui.hwndFocus)) : (0, string.Empty);
    }

    private static List<nint>? s_enumResult;

    [UnmanagedCallersOnly]
    private static int EnumCallback(nint hwnd, nint lParam)
    {
        s_enumResult!.Add(hwnd);
        return 1;
    }

    public static unsafe List<nint> TopLevelWindows()
    {
        s_enumResult = [];
        EnumWindows(&EnumCallback, 0);
        List<nint> result = s_enumResult;
        s_enumResult = null;
        return result;
    }

    public static string Describe(nint hwnd)
    {
        if (hwnd == 0)
        {
            return "(none)";
        }

        GetWindowThreadProcessId(hwnd, out uint pid);
        string process;
        try
        {
            process = System.Diagnostics.Process.GetProcessById((int)pid).ProcessName;
        }
        catch (ArgumentException)
        {
            process = "?";
        }

        var sb = new StringBuilder();
        sb.Append(ClassName(hwnd)).Append(" [").Append(process).Append(']');
        return sb.ToString();
    }
}
