namespace Mavue.QuickView.Preview;

/// <summary>What Quick View may do with a file before reading its content.</summary>
public enum PreviewAccess
{
    /// <summary>Read and decode normally.</summary>
    Allowed = 0,

    /// <summary>Not a regular file (directory, device path, missing).</summary>
    NotAFile,

    /// <summary>Cloud placeholder: reading would download (hydrate) it. Show cached thumbnail and ask first.</summary>
    CloudPlaceholder,

    /// <summary>Decoded size would exceed the memory budget even with scaled decoding.</summary>
    TooLarge,
}

/// <summary>
/// Rules that keep Quick View from crashing, hanging or causing side effects on hostile or unusual files
/// (docs/ARCHITECTURE.md §12). Quick View never writes to the previewed file.
/// </summary>
public static class PreviewSafetyPolicy
{
    // FILE_ATTRIBUTE_* values not exposed by System.IO.FileAttributes.
    private const FileAttributes RecallOnOpen = (FileAttributes)0x00040000;
    private const FileAttributes RecallOnDataAccess = (FileAttributes)0x00400000;

    /// <summary>
    /// Upper bound on source pixels Quick View will hand to a decoder. Scaled decoding keeps output
    /// small, but some codecs still allocate per source row/tile; 1 gigapixel bounds that work.
    /// </summary>
    public const long MaxSourcePixels = 1_000_000_000;

    /// <summary>Classifies a file from its attributes, without opening it.</summary>
    public static PreviewAccess CheckAttributes(FileAttributes attributes)
    {
        if (attributes.HasFlag(FileAttributes.Directory) || attributes.HasFlag(FileAttributes.Device))
        {
            return PreviewAccess.NotAFile;
        }

        // Offline / recall-on-access files are cloud placeholders (OneDrive, etc.). Reading their content
        // triggers a download, which must be the user's explicit choice, never a side effect of Space.
        if ((attributes & (FileAttributes.Offline | RecallOnOpen | RecallOnDataAccess)) != 0)
        {
            return PreviewAccess.CloudPlaceholder;
        }

        return PreviewAccess.Allowed;
    }

    /// <summary>Checks decoded dimensions reported by the codec header before pixel decoding.</summary>
    public static PreviewAccess CheckDimensions(uint width, uint height) =>
        width == 0 || height == 0 || (long)width * height > MaxSourcePixels
            ? PreviewAccess.TooLarge
            : PreviewAccess.Allowed;

    /// <summary>
    /// True for UNC/network paths. These are allowed but must use timeouts and never block the UI thread.
    /// </summary>
    public static bool IsNetworkPath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return path.StartsWith(@"\\", StringComparison.Ordinal) && !IsDevicePath(path) && !path.StartsWith(@"\\?\", StringComparison.Ordinal);
    }

    /// <summary>
    /// True for Win32 device-namespace paths (\\.\PhysicalDrive0, \\.\pipe\x, \\?\GLOBALROOT\...).
    /// Quick View never opens these: reading a raw device or pipe is not a file preview.
    /// </summary>
    public static bool IsDevicePath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return path.StartsWith(@"\\.\", StringComparison.Ordinal)
            || path.StartsWith(@"\\?\GLOBALROOT", StringComparison.OrdinalIgnoreCase);
    }
}
