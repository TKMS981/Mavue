using System.Text;

namespace Mavue.Core.Formats;

/// <summary>
/// Identifies file formats from their leading bytes ("magic numbers"), falling back to the
/// file extension only when the content is inconclusive. Reads at most <see cref="HeaderLength"/>
/// bytes so it is safe to call on very large files and on Quick View's hot path.
/// </summary>
public static class FileFormatDetector
{
    /// <summary>Number of leading bytes inspected.</summary>
    public const int HeaderLength = 1024;

    // ISO BMFF (HEIF family) brands. AVIF is checked first because AVIF files also carry "mif1".
    private static readonly string[] AvifBrands = ["avif", "avis"];
    private static readonly string[] HeifBrands = ["heic", "heix", "heim", "heis", "hevc", "hevx", "mif1", "msf1"];

    private static readonly Dictionary<string, FileFormat> ExtensionMap = new(StringComparer.OrdinalIgnoreCase)
    {
        [".pdf"] = FileFormat.Pdf,
        [".jpg"] = FileFormat.Jpeg,
        [".jpeg"] = FileFormat.Jpeg,
        [".jpe"] = FileFormat.Jpeg,
        [".jfif"] = FileFormat.Jpeg,
        [".png"] = FileFormat.Png,
        [".gif"] = FileFormat.Gif,
        [".bmp"] = FileFormat.Bmp,
        [".dib"] = FileFormat.Bmp,
        [".tif"] = FileFormat.Tiff,
        [".tiff"] = FileFormat.Tiff,
        [".webp"] = FileFormat.WebP,
        [".ico"] = FileFormat.Ico,
        [".heic"] = FileFormat.Heif,
        [".heif"] = FileFormat.Heif,
        [".hif"] = FileFormat.Heif,
        [".avif"] = FileFormat.Avif,
        [".jp2"] = FileFormat.Jpeg2000,
        [".j2k"] = FileFormat.Jpeg2000,
        [".j2c"] = FileFormat.Jpeg2000,
        [".jpf"] = FileFormat.Jpeg2000,
        [".jpx"] = FileFormat.Jpeg2000,
        [".jxl"] = FileFormat.JpegXl,
        [".svg"] = FileFormat.Svg,
    };

    /// <summary>Detects the format of the file at <paramref name="path"/>.</summary>
    public static FileFormat Detect(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 1);
        return Detect(stream, Path.GetExtension(path));
    }

    /// <summary>
    /// Detects the format from a readable stream positioned at the start of the content.
    /// The stream position is advanced by at most <see cref="HeaderLength"/> bytes.
    /// </summary>
    public static FileFormat Detect(Stream stream, string? extensionHint = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        Span<byte> buffer = stackalloc byte[HeaderLength];
        int read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
        return Detect(buffer[..read], extensionHint);
    }

    /// <summary>Detects the format from the leading bytes of a file.</summary>
    public static FileFormat Detect(ReadOnlySpan<byte> header, string? extensionHint = null)
    {
        FileFormat byContent = DetectByContent(header);
        return byContent != FileFormat.Unknown ? byContent : FromExtension(extensionHint);
    }

    /// <summary>Extensions (lower case, with the leading dot) recognized for <paramref name="format"/>.</summary>
    public static IReadOnlyList<string> ExtensionsOf(FileFormat format) =>
        ExtensionMap.Where(p => p.Value == format).Select(p => p.Key).Order(StringComparer.Ordinal).ToArray();

    /// <summary>Maps a file extension (with or without the leading dot) to a format.</summary>
    public static FileFormat FromExtension(string? extension)
    {
        if (string.IsNullOrEmpty(extension))
        {
            return FileFormat.Unknown;
        }

        string normalized = extension.StartsWith('.') ? extension : "." + extension;
        return ExtensionMap.TryGetValue(normalized, out FileFormat format) ? format : FileFormat.Unknown;
    }

    private static FileFormat DetectByContent(ReadOnlySpan<byte> h)
    {
        if (h.StartsWith("%PDF-"u8))
        {
            return FileFormat.Pdf;
        }

        if (h.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8, 0xFF]))
        {
            return FileFormat.Jpeg;
        }

        if (h.StartsWith((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]))
        {
            return FileFormat.Png;
        }

        if (h.StartsWith("GIF87a"u8) || h.StartsWith("GIF89a"u8))
        {
            return FileFormat.Gif;
        }

        if (h.StartsWith("II*\0"u8) || h.StartsWith("MM\0*"u8) || h.StartsWith("II+\0"u8) || h.StartsWith("MM\0+"u8))
        {
            return FileFormat.Tiff;
        }

        if (h.Length >= 12 && h.StartsWith("RIFF"u8) && h.Slice(8, 4).SequenceEqual("WEBP"u8))
        {
            return FileFormat.WebP;
        }

        if (h.Length >= 14 && h.StartsWith("BM"u8))
        {
            return FileFormat.Bmp;
        }

        // ICONDIR: reserved = 0, type = 1 (icon), count > 0.
        if (h.Length >= 6 && h[0] == 0 && h[1] == 0 && h[2] == 1 && h[3] == 0 && (h[4] | h[5]) != 0)
        {
            return FileFormat.Ico;
        }

        // JPEG 2000: JP2 signature box, or a raw J2K codestream (SOC + SIZ markers).
        if (h.StartsWith((ReadOnlySpan<byte>)[0x00, 0x00, 0x00, 0x0C, 0x6A, 0x50, 0x20, 0x20, 0x0D, 0x0A, 0x87, 0x0A]) ||
            h.StartsWith((ReadOnlySpan<byte>)[0xFF, 0x4F, 0xFF, 0x51]))
        {
            return FileFormat.Jpeg2000;
        }

        // JPEG XL: bare codestream, or ISO BMFF-style container ("JXL " signature box).
        if (h.StartsWith((ReadOnlySpan<byte>)[0xFF, 0x0A]) ||
            h.StartsWith((ReadOnlySpan<byte>)[0x00, 0x00, 0x00, 0x0C, 0x4A, 0x58, 0x4C, 0x20, 0x0D, 0x0A, 0x87, 0x0A]))
        {
            return FileFormat.JpegXl;
        }

        FileFormat isoBmff = DetectIsoBmff(h);
        if (isoBmff != FileFormat.Unknown)
        {
            return isoBmff;
        }

        if (LooksLikeSvg(h))
        {
            return FileFormat.Svg;
        }

        // PDF readers accept the header anywhere in the first 1024 bytes (junk may precede it).
        // Checked last so that strings embedded in other formats (e.g. EXIF) cannot win over real signatures.
        return h.IndexOf("%PDF-"u8) > 0 ? FileFormat.Pdf : FileFormat.Unknown;
    }

    private static FileFormat DetectIsoBmff(ReadOnlySpan<byte> h)
    {
        if (h.Length < 16 || !h.Slice(4, 4).SequenceEqual("ftyp"u8))
        {
            return FileFormat.Unknown;
        }

        uint boxSize = (uint)(h[0] << 24 | h[1] << 16 | h[2] << 8 | h[3]);
        int end = (int)Math.Min(boxSize, (uint)h.Length);

        // Major brand at offset 8; minor version at 12 is skipped; compatible brands from offset 16.
        bool heif = false;
        for (int offset = 8; offset + 4 <= end; offset += offset == 8 ? 8 : 4)
        {
            string brand = Encoding.ASCII.GetString(h.Slice(offset, 4));
            if (Array.IndexOf(AvifBrands, brand) >= 0)
            {
                return FileFormat.Avif;
            }

            heif |= Array.IndexOf(HeifBrands, brand) >= 0;
        }

        return heif ? FileFormat.Heif : FileFormat.Unknown;
    }

    private static bool LooksLikeSvg(ReadOnlySpan<byte> h)
    {
        // Tolerate a UTF-8 BOM, XML declaration, comments and DOCTYPE before the root element.
        if (h.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
        {
            h = h[3..];
        }

        string text = Encoding.UTF8.GetString(h).TrimStart();
        return text.StartsWith('<') && text.Contains("<svg", StringComparison.Ordinal);
    }
}
