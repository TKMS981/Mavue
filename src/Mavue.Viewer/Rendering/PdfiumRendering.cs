using System.Diagnostics;
using System.Runtime.InteropServices;
using Mavue.Core.Viewing;
using Mavue.Pdf;
using Mavue.Pdf.Pdfium;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using WinRT;

namespace Mavue.Viewer.Rendering;

/// <summary>
/// PDFium pages as <see cref="SoftwareBitmap"/>s: PDFium draws straight into the bitmap's memory (no intermediate
/// copy). Used by the main window's document view, its page thumbnails and Quick View.
/// </summary>
public static unsafe class PdfiumRendering
{
    // IMemoryBufferByteAccess (see SoftwareBitmapPixels).
    private static readonly Guid MemoryBufferByteAccessId = new("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D");

    /// <summary>Device-independent pixels per PDF point (96 / 72).</summary>
    public const double DipsPerPoint = 96.0 / 72.0;

    /// <summary>Draws page <paramref name="pageIndex"/> at <paramref name="width"/> × <paramref name="height"/> pixels (call off the UI thread).</summary>
    public static SoftwareBitmap Render(IPdfDocument document, int pageIndex, int width, int height, int quarterTurns)
    {
        ArgumentNullException.ThrowIfNull(document);
        var bitmap = new SoftwareBitmap(BitmapPixelFormat.Bgra8, Math.Max(1, width), Math.Max(1, height), BitmapAlphaMode.Premultiplied);
        try
        {
            using BitmapBuffer buffer = bitmap.LockBuffer(BitmapBufferAccessMode.Write);
            using IMemoryBufferReference reference = buffer.CreateReference();
            BitmapPlaneDescription plane = buffer.GetPlaneDescription(0);
            nint data = Bytes(reference);
            document.RenderPage(pageIndex, data + plane.StartIndex, bitmap.PixelWidth, bitmap.PixelHeight, plane.Stride, quarterTurns);
            return bitmap;
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Quick View and the fallback-free path of a single page: opens <paramref name="path"/>, draws one page fitted to
    /// the viewport (or at <paramref name="actualScale"/> physical pixels per DIP) and closes the file again, so the
    /// resident process never keeps a document open. Sizes in the result are the page in DIPs, like Windows.Data.Pdf.
    /// </summary>
    public static Task<DecodedImage> RenderPageAsync(string path, uint viewportWidth, uint viewportHeight, double? actualScale, int pageIndex, CancellationToken cancellation) =>
        Task.Run(
            () =>
            {
                long start = Stopwatch.GetTimestamp();
                PdfiumDocument document;
                try
                {
                    document = PdfiumDocument.Open(path);
                }
                catch (PdfOpenException ex) when (ex.Error == PdfOpenError.Password)
                {
                    return DecodedImage.Skipped("password");
                }

                using (document)
                {
                    if (document.PageCount == 0)
                    {
                        return DecodedImage.Skipped("failed");
                    }

                    int index = Math.Clamp(pageIndex, 0, document.PageCount - 1);
                    PdfSize size = document.GetPageSize(index);
                    double width = size.Width * DipsPerPoint;
                    double height = size.Height * DipsPerPoint;
                    double scale = actualScale is { } s && width * height * s * s <= DisplaySizing.MaxActualSizePixels
                        ? s
                        : Math.Min(viewportWidth / width, viewportHeight / height);
                    cancellation.ThrowIfCancellationRequested();
                    SoftwareBitmap bitmap = Render(document, index, (int)Math.Max(1, Math.Floor(width * scale)), (int)Math.Max(1, Math.Floor(height * scale)), 0);
                    return DecodedImage.Decoded(bitmap, (uint)Math.Round(width), (uint)Math.Round(height), Stopwatch.GetTimestamp() - start, "pdfium", document.PageCount, index);
                }
            },
            cancellation);

    private static nint Bytes(IMemoryBufferReference reference)
    {
        nint unknown = ((IWinRTObject)reference).NativeObject.ThisPtr;
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unknown, in MemoryBufferByteAccessId, out nint access));
        try
        {
            byte* data;
            uint capacity;
            var getBuffer = (delegate* unmanaged[Stdcall]<nint, byte**, uint*, int>)(*(void***)access)[3];
            Marshal.ThrowExceptionForHR(getBuffer(access, &data, &capacity));
            return (nint)data;
        }
        finally
        {
            Marshal.Release(access);
        }
    }
}
