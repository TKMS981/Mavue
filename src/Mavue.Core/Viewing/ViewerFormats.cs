using Mavue.Core.Formats;

namespace Mavue.Core.Viewing;

/// <summary>Formats the viewer opens (images through Windows Imaging Component, PDF, video and audio).</summary>
public static class ViewerFormats
{
    /// <summary>
    /// Image formats with a decoder in Windows (some through Store extensions: HEIF, AVIF, WebP on older systems,
    /// JPEG XL). JPEG 2000 and SVG have no decoder yet, so they are not offered.
    /// </summary>
    public static readonly IReadOnlyList<FileFormat> Images =
    [
        FileFormat.Jpeg, FileFormat.Png, FileFormat.Gif, FileFormat.Bmp, FileFormat.Tiff,
        FileFormat.WebP, FileFormat.Ico, FileFormat.Heif, FileFormat.Avif, FileFormat.JpegXl,
    ];

    /// <summary>Everything the viewer can open.</summary>
    public static readonly IReadOnlyList<FileFormat> All =
    [
        .. Images,
        FileFormat.Pdf,
        .. Enum.GetValues<FileFormat>().Where(FileFormatKinds.IsMedia),
    ];

    /// <summary>Extensions (".jpg", …) of <see cref="All"/>, sorted.</summary>
    public static IReadOnlyList<string> Extensions { get; } =
        All.SelectMany(FileFormatDetector.ExtensionsOf).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToArray();

    private static readonly HashSet<string> ExtensionSet = new(Extensions, StringComparer.OrdinalIgnoreCase);

    /// <summary>True when the file name has an extension the viewer opens (no file access).</summary>
    public static bool HasViewableExtension(string path) => ExtensionSet.Contains(Path.GetExtension(path));
}
