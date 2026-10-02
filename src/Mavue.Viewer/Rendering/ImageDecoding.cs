using System.Diagnostics;
using System.Runtime.InteropServices;
using Mavue.Core.Viewing;
using Mavue.Image.Wic;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Mavue.Viewer.Rendering;

/// <summary>
/// Decodes an image straight to the size it will be shown at (never enlarged), off the UI thread. JPEG can go
/// through WIC directly into the bitmap's memory (measured faster); everything else, and JPEGs with a color profile,
/// use the color-managed WinRT decoder. Very large sources are refused before any pixel is decoded.
/// </summary>
public static class ImageDecoding
{
    public static Task<DecodedImage> DecodeAsync(
        string path,
        uint viewportWidth,
        uint viewportHeight,
        BitmapInterpolationMode interpolation,
        bool preferWic,
        CancellationToken cancellation,
        long maxSourcePixels = PreviewSafetyPolicy.MaxSourcePixels) =>
        Task.Run(
            async () =>
            {
                long start = Stopwatch.GetTimestamp();
                string fallbackReason = string.Empty;
                if (preferWic)
                {
                    try
                    {
                        using WicPreparedImage? prepared = WicPreviewDecoder.Prepare(path, viewportWidth, viewportHeight, maxSourcePixels, cancellation);
                        if (prepared is not null)
                        {
                            SoftwareBitmap fast = SoftwareBitmapPixels.FromWic(prepared);
                            return DecodedImage.Decoded(fast, prepared.SourceWidth, prepared.SourceHeight, Stopwatch.GetTimestamp() - start, "wic-direct");
                        }

                        fallbackReason = "-color-managed"; // embedded profile / non-sRGB: use the color-managed path
                    }
                    catch (ImageTooLargeException)
                    {
                        return DecodedImage.Skipped("too-large");
                    }
                    catch (Exception ex) when (ex is COMException or ArgumentException or OverflowException)
                    {
                        fallbackReason = "-after-wic-error";
                    }
                }

                using IRandomAccessStream stream = await OpenReadAsync(path);
                BitmapDecoder decoder;
                try
                {
                    decoder = await BitmapDecoder.CreateAsync(stream);
                }
                catch (Exception ex) when (ex.HResult == unchecked((int)0x88982F50))
                {
                    // WINCODEC_ERR_COMPONENTNOTFOUND: no WIC codec for this file (e.g. missing Store extension).
                    return DecodedImage.Skipped("no-codec");
                }

                uint width = decoder.OrientedPixelWidth;
                uint height = decoder.OrientedPixelHeight;
                if (PreviewSafetyPolicy.CheckDimensions(width, height) != PreviewAccess.Allowed || (long)width * height > maxSourcePixels)
                {
                    return DecodedImage.Skipped("too-large");
                }

                (uint fitWidth, uint fitHeight) = PreviewSizing.FitWithin(width, height, viewportWidth, viewportHeight);
                bool swapped = width != decoder.PixelWidth;
                var transform = new BitmapTransform
                {
                    // Scaling applies before EXIF rotation, so use the stored (unrotated) orientation.
                    ScaledWidth = swapped ? fitHeight : fitWidth,
                    ScaledHeight = swapped ? fitWidth : fitHeight,
                    InterpolationMode = interpolation,
                };
                cancellation.ThrowIfCancellationRequested();
                SoftwareBitmap bitmap = await decoder.GetSoftwareBitmapAsync(
                    BitmapPixelFormat.Bgra8,
                    BitmapAlphaMode.Premultiplied,
                    transform,
                    ExifOrientationMode.RespectExifOrientation,
                    ColorManagementMode.ColorManageToSRgb);
                return DecodedImage.Decoded(bitmap, width, height, Stopwatch.GetTimestamp() - start, "winrt-bitmapdecoder-" + interpolation + fallbackReason);
            },
            cancellation);

    /// <summary>Opens a file for reading while other programs may still read and write it.</summary>
    public static async Task<IRandomAccessStream> OpenReadAsync(string path) =>
        await FileRandomAccessStream.OpenAsync(path, FileAccessMode.Read, StorageOpenOptions.AllowReadersAndWriters, FileOpenDisposition.OpenExisting);
}
