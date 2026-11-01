using System.Runtime.InteropServices.WindowsRuntime;
using Mavue.Core.Formats;
using Mavue.Core.Viewing;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Mavue.QuickView.Host.Content.Markdown;

/// <summary>Where an image of a Markdown document comes from.</summary>
internal enum MarkdownImageOrigin
{
    /// <summary>Not loadable (empty, malformed, a network share, a device path, not an image).</summary>
    Invalid,

    /// <summary>A file on this computer (relative to the document, or an absolute local path).</summary>
    Local,

    /// <summary>A data: URI inside the document.</summary>
    Data,

    /// <summary>On the network (http, https…): never fetched — Quick View does not access the network.</summary>
    Remote,
}

/// <summary>
/// Images referenced by a Markdown document (docs/ARCHITECTURE.md §4.7.12). Local files next to the document and
/// data: URIs are shown; remote images are not downloaded (a placeholder with the address is shown), network shares
/// (\\server\…, which could send credentials) and device paths are refused, cloud-only files are not read (that would
/// download them). Bounded: the number of images, each file's size and the total read per document.
/// </summary>
internal sealed class MarkdownImages
{
    private const int MaxImages = 64;
    private const long MaxImageBytes = 32L * 1024 * 1024;
    private const long MaxTotalBytes = 160L * 1024 * 1024;

    private readonly string? _folder;
    private int _count;
    private long _total;

    public MarkdownImages(string? folder) => _folder = folder;

    /// <summary>Classifies <paramref name="url"/>; for local files also returns the full path.</summary>
    public (MarkdownImageOrigin Origin, string? Path) Classify(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return (MarkdownImageOrigin.Invalid, null);
        }

        string trimmed = url.Trim();
        if (trimmed.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return (MarkdownImageOrigin.Data, null);
        }

        if (Uri.TryCreate(trimmed, UriKind.Absolute, out Uri? absolute) && absolute.Scheme.Length > 1 && !absolute.IsFile)
        {
            return (MarkdownImageOrigin.Remote, null);
        }

        string path;
        try
        {
            if (absolute is { IsFile: true })
            {
                if (absolute.IsUnc)
                {
                    return (MarkdownImageOrigin.Invalid, null);
                }

                path = absolute.LocalPath;
            }
            else
            {
                if (_folder is null)
                {
                    return (MarkdownImageOrigin.Invalid, null);
                }

                string relative = Uri.UnescapeDataString(StripQuery(trimmed)).Replace('/', '\\');
                path = System.IO.Path.GetFullPath(System.IO.Path.IsPathRooted(relative) && !relative.StartsWith('\\') ? relative : System.IO.Path.Combine(_folder, relative.TrimStart('\\')));
            }
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or UriFormatException)
        {
            return (MarkdownImageOrigin.Invalid, null);
        }

        if (path.StartsWith(@"\\", StringComparison.Ordinal) || PreviewSafetyPolicy.IsDevicePath(path))
        {
            return (MarkdownImageOrigin.Invalid, null); // network shares and devices are never opened
        }

        return (MarkdownImageOrigin.Local, path);
    }

    /// <summary>
    /// Loads an image for display at most <paramref name="maxWidthPixels"/> wide. Null when it cannot or may not be shown.
    /// </summary>
    public async Task<(ImageSource Source, double Width, double Height)?> LoadAsync(string url, double maxWidthPixels, CancellationToken cancellation)
    {
        (MarkdownImageOrigin origin, string? path) = Classify(url);
        byte[]? bytes = origin switch
        {
            MarkdownImageOrigin.Local => await Task.Run(() => ReadLocal(path!), cancellation),
            MarkdownImageOrigin.Data => DecodeDataUri(url.Trim()),
            _ => null,
        };
        if (bytes is null || cancellation.IsCancellationRequested || !Admit(bytes.Length))
        {
            return null;
        }

        try
        {
            using var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(bytes.AsBuffer());
            if (LooksLikeSvg(bytes))
            {
                stream.Seek(0);
                var svg = new SvgImageSource();
                SvgImageSourceLoadStatus status = await svg.SetSourceAsync(stream);
                return status == SvgImageSourceLoadStatus.Success ? (svg, Math.Max(16, svg.RasterizePixelWidth), Math.Max(16, svg.RasterizePixelHeight)) : null;
            }

            stream.Seek(0);
            BitmapDecoder decoder = await BitmapDecoder.CreateAsync(stream);
            uint width = decoder.OrientedPixelWidth;
            uint height = decoder.OrientedPixelHeight;
            if (width == 0 || height == 0)
            {
                return null;
            }

            stream.Seek(0);
            var bitmap = new BitmapImage();
            if (width > maxWidthPixels && maxWidthPixels > 0)
            {
                bitmap.DecodePixelWidth = (int)maxWidthPixels;
            }

            await bitmap.SetSourceAsync(stream);
            return (bitmap, width, height);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null; // not an image, or one Windows cannot decode
        }
    }

    private bool Admit(long bytes)
    {
        if (_count >= MaxImages || _total + bytes > MaxTotalBytes)
        {
            return false;
        }

        _count++;
        _total += bytes;
        return true;
    }

    private static byte[]? ReadLocal(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length == 0 || info.Length > MaxImageBytes ||
                PreviewSafetyPolicy.CheckAttributes(info.Attributes) != PreviewAccess.Allowed)
            {
                return null; // missing, too large, or cloud-only (reading would download it)
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            Span<byte> header = stackalloc byte[FileFormatDetector.HeaderLength];
            int read = stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
            FileFormat format = FileFormatDetector.Detect(header[..read], path);
            if (!FileFormatKinds.IsImage(format) && format != FileFormat.Unknown)
            {
                return null; // e.g. a PDF or a program named .png
            }

            stream.Seek(0, SeekOrigin.Begin);
            byte[] bytes = new byte[info.Length];
            stream.ReadExactly(bytes);
            return bytes;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static byte[]? DecodeDataUri(string url)
    {
        int comma = url.IndexOf(',', StringComparison.Ordinal);
        if (comma < 0 || url.Length - comma > MaxImageBytes * 4 / 3)
        {
            return null;
        }

        string header = url[..comma];
        if (!header.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string payload = url[(comma + 1)..];
        try
        {
            return header.EndsWith(";base64", StringComparison.OrdinalIgnoreCase)
                ? Convert.FromBase64String(payload)
                : System.Text.Encoding.UTF8.GetBytes(Uri.UnescapeDataString(payload));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static string StripQuery(string url)
    {
        int cut = url.IndexOfAny(['?', '#']);
        return cut < 0 ? url : url[..cut];
    }

    private static bool LooksLikeSvg(byte[] bytes) =>
        FileFormatDetector.Detect(bytes.AsSpan(0, Math.Min(bytes.Length, FileFormatDetector.HeaderLength)), ".svg") == FileFormat.Svg;
}
