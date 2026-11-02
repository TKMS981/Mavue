using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Mavue.Image.Wic;
using Mavue.Metadata;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Mavue.Image.Export;

/// <summary>The file type of a clean copy.</summary>
public enum ExportFormat
{
    /// <summary>The original's type (JPEG, PNG, WebP and GIF only: copied without re-encoding).</summary>
    Original,

    /// <summary>JPEG (converted: HEIF, AVIF, raw, TIFF…).</summary>
    Jpeg,

    /// <summary>PNG (converted, lossless for 8-bit images).</summary>
    Png,
}

/// <summary>What to remove and how to write.</summary>
/// <param name="MaxPixels">Larger images are not converted (memory: 4 bytes per pixel).</param>
public sealed record CleanExportOptions(StripOptions Strip, ExportFormat Format = ExportFormat.Original, int JpegQuality = 92, long MaxPixels = 250_000_000);

/// <summary>How a file can be exported without metadata.</summary>
/// <param name="CanKeepFormat">Its metadata can be removed without re-encoding (JPEG, PNG, WebP, GIF).</param>
/// <param name="OriginalExtension">The original's extension (".heic").</param>
public sealed record CleanExportPlan(MetadataContainer Container, bool CanKeepFormat, string OriginalExtension)
{
    /// <summary>Keep the type when possible; otherwise JPEG (photos: HEIF, raw).</summary>
    public ExportFormat DefaultFormat => CanKeepFormat ? ExportFormat.Original : ExportFormat.Jpeg;

    /// <summary>The image is decoded and encoded again for <paramref name="format"/>.</summary>
    public bool Converts(ExportFormat format) => format switch
    {
        ExportFormat.Original => !CanKeepFormat,
        ExportFormat.Jpeg => Container != MetadataContainer.Jpeg,
        ExportFormat.Png => Container != MetadataContainer.Png,
        _ => true,
    };

    public string ExtensionFor(ExportFormat format) => format switch
    {
        ExportFormat.Jpeg when Converts(format) => ".jpg",
        ExportFormat.Png when Converts(format) => ".png",
        ExportFormat.Original when !CanKeepFormat => ".jpg",
        _ => OriginalExtension,
    };
}

/// <summary>A finished export.</summary>
/// <param name="Converted">The image was decoded and encoded again (pixels upright, colors in sRGB).</param>
public sealed record CleanExportResult(StripResult Strip, bool Converted, ExportFormat Format);

/// <summary>
/// Exports an image without its private metadata (ARCHITECTURE §4.7.13 and §4.7.15). JPEG, PNG, WebP and GIF keep
/// their type and compressed data (<see cref="MetadataStripper"/>). HEIF, AVIF, camera raw, TIFF and other types
/// Windows can decode are converted to JPEG or PNG: decoded with the EXIF orientation applied and colors converted
/// to sRGB, encoded without any metadata, and then the values the user chose to keep (from the original's EXIF) are
/// written by the same stripper. The source is only read. Used by the Quick View worker process (memory-limited) and
/// by the main application's export.
/// </summary>
public static class CleanExport
{
    public static CleanExportPlan Plan(string source)
    {
        ArgumentException.ThrowIfNullOrEmpty(source);
        ImageMetadata metadata = ImageMetadataReader.Read(source);
        return new CleanExportPlan(metadata.Container, MetadataStripper.CanStrip(metadata.Container), Path.GetExtension(source).ToLowerInvariant());
    }

    /// <summary>Writes the clean image of <paramref name="source"/> to <paramref name="output"/>.</summary>
    /// <exception cref="ImageTooLargeException">The image has more than <see cref="CleanExportOptions.MaxPixels"/> pixels.</exception>
    /// <exception cref="NotSupportedException">Windows cannot decode the image (a codec is missing).</exception>
    public static async Task<CleanExportResult> WriteAsync(string source, Stream output, CleanExportOptions options, CancellationToken cancellation)
    {
        ArgumentException.ThrowIfNullOrEmpty(source);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(options);
        ImageMetadata metadata = await Task.Run(() => ImageMetadataReader.Read(source), cancellation);
        var plan = new CleanExportPlan(metadata.Container, MetadataStripper.CanStrip(metadata.Container), Path.GetExtension(source).ToLowerInvariant());
        if (!plan.Converts(options.Format))
        {
            StripResult kept = await Task.Run(
                () =>
                {
                    using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
                    return MetadataStripper.Strip(input, output, options.Strip);
                },
                cancellation);
            return new CleanExportResult(kept, false, options.Format);
        }

        ExportFormat format = options.Format == ExportFormat.Png ? ExportFormat.Png : ExportFormat.Jpeg;
        using MemoryStream encoded = await EncodeAsync(source, format, options.JpegQuality, options.MaxPixels, cancellation);
        cancellation.ThrowIfCancellationRequested();

        // The pixels are upright now: the orientation must not be written again.
        StripOptions strip = options.Strip with { KeepOrientation = false };
        StripResult result = MetadataStripper.Strip(encoded, output, strip, metadata.ExifStructure);

        // What was left out is the original's metadata (the size difference also reflects the new encoding).
        return new CleanExportResult(result with { RemovedBytes = Math.Max(0, metadata.MetadataBytes), Container = metadata.Container }, true, format);
    }

    /// <summary>
    /// The written copy has nothing of what was to be removed, no other metadata blocks, and Windows decodes it with the
    /// expected (upright) size.
    /// </summary>
    public static bool Verify(string written, StripOptions options, (uint Width, uint Height)? expectedSize)
    {
        ArgumentException.ThrowIfNullOrEmpty(written);
        ArgumentNullException.ThrowIfNull(options);
        ImageMetadata after = ImageMetadataReader.Read(written, findTrailer: true);
        if (!MetadataStripper.CanStrip(after.Container) || PrivacyCheck.Leftovers(after, options).Count > 0)
        {
            return false;
        }

        if (after.Blocks.Any(b => b.Kind is MetadataBlockKind.Xmp or MetadataBlockKind.Iptc or MetadataBlockKind.Comment or MetadataBlockKind.Text
            or MetadataBlockKind.EmbeddedImage or MetadataBlockKind.Trailer or MetadataBlockKind.Other))
        {
            return false;
        }

        (uint Width, uint Height)? size = WicPreviewDecoder.TryReadDimensions(written);
        return size is not null && (expectedSize is null || size == expectedSize);
    }

    private static async Task<MemoryStream> EncodeAsync(string source, ExportFormat format, int quality, long maxPixels, CancellationToken cancellation)
    {
        IRandomAccessStream input;
        try
        {
            input = await FileRandomAccessStream.OpenAsync(source, FileAccessMode.Read, StorageOpenOptions.AllowReadersAndWriters, FileOpenDisposition.OpenExisting);
        }
        catch (Exception ex) when (ex is FileNotFoundException or UnauthorizedAccessException or COMException)
        {
            throw new IOException("The image could not be opened.", ex);
        }

        using (input)
        {
            BitmapDecoder decoder;
            try
            {
                decoder = await BitmapDecoder.CreateAsync(input);
            }
            catch (Exception ex) when (ex is COMException or ArgumentException)
            {
                throw new NotSupportedException("Windows cannot decode this image (a codec may be missing).", ex);
            }

            if ((long)decoder.OrientedPixelWidth * decoder.OrientedPixelHeight > maxPixels)
            {
                throw new ImageTooLargeException(decoder.OrientedPixelWidth, decoder.OrientedPixelHeight);
            }

            cancellation.ThrowIfCancellationRequested();
            using SoftwareBitmap bitmap = await decoder.GetSoftwareBitmapAsync(
                BitmapPixelFormat.Bgra8,
                format == ExportFormat.Png ? BitmapAlphaMode.Premultiplied : BitmapAlphaMode.Ignore,
                new BitmapTransform(),
                ExifOrientationMode.RespectExifOrientation,
                ColorManagementMode.ColorManageToSRgb);
            cancellation.ThrowIfCancellationRequested();
            using var encoded = new InMemoryRandomAccessStream();
            BitmapEncoder encoder = format == ExportFormat.Png
                ? await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, encoded)
                : await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, encoded, new BitmapPropertySet
                {
                    ["ImageQuality"] = new BitmapTypedValue(Math.Clamp(quality, 1, 100) / 100f, Windows.Foundation.PropertyType.Single),
                });
            encoder.SetSoftwareBitmap(bitmap);
            await encoder.FlushAsync();
            var memory = new MemoryStream((int)Math.Min(int.MaxValue, encoded.Size));
            encoded.Seek(0);
            await encoded.AsStreamForRead().CopyToAsync(memory, cancellation);
            memory.Position = 0;
            return memory;
        }
    }
}
