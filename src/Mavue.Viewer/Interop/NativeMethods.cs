using System.Runtime.InteropServices;

namespace Mavue.Viewer.Interop;

internal static partial class NativeMethods
{
    [LibraryImport("user32.dll")]
    public static partial uint GetDpiForSystem();
}
