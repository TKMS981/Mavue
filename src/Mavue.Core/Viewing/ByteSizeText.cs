using System.Globalization;

namespace Mavue.Core.Viewing;

/// <summary>File sizes as shown in the viewer's information line ("624.9 KB").</summary>
public static class ByteSizeText
{
    public static string Format(long bytes) => bytes switch
    {
        < 1024 => string.Format(CultureInfo.CurrentCulture, "{0} B", bytes),
        < 1024 * 1024 => string.Format(CultureInfo.CurrentCulture, "{0:0.#} KB", bytes / 1024.0),
        < 1024L * 1024 * 1024 => string.Format(CultureInfo.CurrentCulture, "{0:0.#} MB", bytes / (1024.0 * 1024)),
        _ => string.Format(CultureInfo.CurrentCulture, "{0:0.##} GB", bytes / (1024.0 * 1024 * 1024)),
    };
}
