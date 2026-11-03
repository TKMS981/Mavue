using System.Runtime.InteropServices;

namespace Mavue.QuickView.Host;

internal static partial class NativeMethods
{
    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_SHOWWINDOW = 0x0040;
    public static readonly nint HWND_TOP = 0;
    public static readonly nint HWND_TOPMOST = -1;
    public static readonly nint HWND_NOTOPMOST = -2;
    public const uint DWMWA_CLOAK = 13;
    public const uint GA_PARENT = 1;
    public const uint GA_ROOT = 2;
    public const uint GW_HWNDNEXT = 2;

    [LibraryImport("user32.dll")]
    public static partial nint GetWindow(nint hWnd, uint uCmd);

    [LibraryImport("user32.dll")]
    public static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    public static partial uint GetDpiForWindow(nint hwnd);

    [LibraryImport("user32.dll")]
    public static partial uint GetDpiForSystem();

    public const uint MONITOR_DEFAULTTONEAREST = 2;
    public const int MDT_EFFECTIVE_DPI = 0;

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    [LibraryImport("user32.dll")]
    public static partial nint MonitorFromPoint(POINT pt, uint dwFlags);

    /// <summary>The DPI of a monitor (Windows 8.1+); 0 (S_OK) on success.</summary>
    [LibraryImport("shcore.dll")]
    public static partial int GetDpiForMonitor(nint hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetForegroundWindow(nint hWnd);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AttachThreadInput(uint idAttach, uint idAttachTo, [MarshalAs(UnmanagedType.Bool)] bool fAttach);

    [LibraryImport("user32.dll")]
    public static partial uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

    [LibraryImport("kernel32.dll")]
    public static partial uint GetCurrentThreadId();

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindow(nint hWnd);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AllowSetForegroundWindow(uint dwProcessId);

    [LibraryImport("user32.dll")]
    public static partial nint GetAncestor(nint hwnd, uint gaFlags);

    [LibraryImport("dwmapi.dll")]
    public static partial int DwmSetWindowAttribute(nint hwnd, uint attribute, ref int value, int size);

    public const int ASSOCSTR_FRIENDLYDOCNAME = 3;
    public const int ASSOCSTR_CONTENTTYPE = 14;
    public const uint ASSOCF_NOTRUNCATE = 0x20;

    [LibraryImport("shlwapi.dll", EntryPoint = "AssocQueryStringW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int AssocQueryString(uint flags, int str, string association, string? extra, [Out] char[] output, ref uint length);

    /// <summary>A string Windows associates with an extension (type name, MIME type), or null. Reads the registry only.</summary>
    public static string? AssociationString(string extension, int what)
    {
        if (string.IsNullOrEmpty(extension))
        {
            return null;
        }

        char[] buffer = new char[260];
        uint length = (uint)buffer.Length;
        return AssocQueryString(ASSOCF_NOTRUNCATE, what, extension, null, buffer, ref length) >= 0 && length > 1
            ? new string(buffer, 0, (int)length - 1)
            : null;
    }

    public const int SM_REMOTESESSION = 0x1000;

    [LibraryImport("user32.dll")]
    public static partial int GetSystemMetrics(int index);

    /// <summary>Restart only after a crash or hang (not after updates or reboots).</summary>
    public const uint RESTART_NO_PATCH = 0x4;
    public const uint RESTART_NO_REBOOT = 0x8;

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int RegisterApplicationRestart(string? commandLineArgs, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    public struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);
}
