using System.Diagnostics;
using Mavue.Core.Viewing;
using Mavue.Viewer.Interop;
using Windows.Data.Pdf;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Mavue.Viewer.Rendering;

/// <summary>Renders one PDF page with Windows.Data.Pdf, off the UI thread, at the size it will be shown at.</summary>
public static class PdfRendering
{
    /// <summary>
    /// Bitmap pixels Windows.Data.Pdf produces per unit of <see cref="PdfPageRenderOptions.DestinationWidth"/>.
    /// Measured: 1.5 on a PC whose primary monitor is at 150 %, also for a window on a 100 % monitor, so the
    /// planned size came out 1.5 times too large (the page was cut off in the fit mode). Starts from the system
    /// DPI and is corrected from the first render.
    /// </summary>
    private static double s_outputScale = Math.Max(1, NativeMethods.GetDpiForSystem()) / 96.0;

    /// <param name="actualScale">Render at 100 % (page size × this rasterization scale) instead of fitting.</param>
    /// <param name="pageIndex">Zero-based page; clamped to the document.</param>
    public static Task<DecodedImage> RenderAsync(string path, uint viewportWidth, uint viewportHeight, CancellationToken cancellation, double? actualScale = null, int pageIndex = 0) =>
        Task.Run(
            async () =>
            {
                long start = Stopwatch.GetTimestamp();
                using IRandomAccessStream file = await ImageDecoding.OpenReadAsync(path);
                PdfDocument document;
                try
                {
                    document = await PdfDocument.LoadFromStreamAsync(file);
                }
                catch (Exception ex) when (ex.HResult == unchecked((int)0x8007052B))
                {
                    return DecodedImage.Skipped("password");
                }

                uint index = (uint)Math.Clamp(pageIndex, 0, Math.Max(0, (int)document.PageCount - 1));
                using PdfPage page = document.GetPage(index);
                double scale = actualScale is { } s && page.Size.Width * page.Size.Height * s * s <= DisplaySizing.MaxActualSizePixels
                    ? s
                    : Math.Min(viewportWidth / page.Size.Width, viewportHeight / page.Size.Height);
                double wantWidth = Math.Max(1, Math.Floor(page.Size.Width * scale));
                double wantHeight = Math.Max(1, Math.Floor(page.Size.Height * scale));
                for (int attempt = 0; ; attempt++)
                {
                    double outputScale = Volatile.Read(ref s_outputScale);
                    var options = new PdfPageRenderOptions
                    {
                        DestinationWidth = (uint)Math.Max(1, Math.Floor(wantWidth / outputScale)),
                        DestinationHeight = (uint)Math.Max(1, Math.Floor(wantHeight / outputScale)),
                        BitmapEncoderId = BitmapEncoder.BmpEncoderId,
                    };
                    cancellation.ThrowIfCancellationRequested();
                    using var rendered = new InMemoryRandomAccessStream();
                    await page.RenderToStreamAsync(rendered, options);
                    rendered.Seek(0);
                    BitmapDecoder decoder = await BitmapDecoder.CreateAsync(rendered);
                    if (Math.Abs(decoder.PixelWidth - wantWidth) > 2 && attempt == 0)
                    {
                        Volatile.Write(ref s_outputScale, decoder.PixelWidth / (double)options.DestinationWidth);
                        continue; // learned the real factor: render once more at the planned size
                    }

                    SoftwareBitmap bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
                    return DecodedImage.Decoded(bitmap, (uint)page.Size.Width, (uint)page.Size.Height, Stopwatch.GetTimestamp() - start, "windows-data-pdf", (int)document.PageCount, (int)index);
                }
            },
            cancellation);
}
