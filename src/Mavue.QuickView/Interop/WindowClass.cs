namespace Mavue.QuickView.Interop;

/// <summary>Public access to window class names for callers outside this assembly.</summary>
public static class WindowClass
{
    /// <summary>Returns the class name of <paramref name="hwnd"/>, or an empty string.</summary>
    public static string Of(nint hwnd) => Win32.GetClassName(hwnd);
}
