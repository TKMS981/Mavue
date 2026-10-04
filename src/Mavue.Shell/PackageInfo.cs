using System.Runtime.InteropServices;

namespace Mavue.Shell;

/// <summary>Whether this process runs with package identity (installed from the MSIX package).</summary>
public static partial class PackageInfo
{
    private const int AppModelErrorNoPackage = 15700;

    /// <summary>
    /// True inside the MSIX package. Then Windows integration (file types, preview/thumbnails, context menu, start at
    /// sign-in) comes from the package manifest, and HKCU registration must not be written (it would be virtualized
    /// or duplicate the package's registrations).
    /// </summary>
    public static bool IsPackaged { get; } = Detect();

    private static bool Detect()
    {
        uint length = 0;
        int result = GetCurrentPackageFullName(ref length, 0); // only the length: no buffer needed
        return result != AppModelErrorNoPackage;
    }

    [LibraryImport("kernel32.dll")]
    private static partial int GetCurrentPackageFullName(ref uint length, nint name);
}
