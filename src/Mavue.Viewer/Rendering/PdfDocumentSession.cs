using System.Diagnostics;
using Mavue.Core.Viewing;
using Windows.Data.Pdf;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Mavue.Viewer.Rendering;

/// <summary>
/// One PDF kept open while it is shown in the main window, so turning pages and drawing page thumbnails do not parse
/// the file again for every page. The file is opened with read, write and delete sharing: other programs may still
/// change, rename or delete it while it is shown. Renders run one at a time, off the UI thread.
/// </summary>
public sealed class PdfDocumentSession : IDisposable
{
    private readonly FileStream _file;
    private readonly IRandomAccessStream _stream;
    private readonly PdfDocument _document;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;
    private bool _closing;

    private PdfDocumentSession(string path, FileStream file, IRandomAccessStream stream, PdfDocument document)
    {
        Path = path;
        _file = file;
        _stream = stream;
        _document = document;
        PageCount = (int)document.PageCount;
    }

    public string Path { get; }

    public int PageCount { get; }

    /// <summary>Opens <paramref name="path"/>; returns null for a password-protected PDF.</summary>
    public static Task<PdfDocumentSession?> OpenAsync(string path, CancellationToken cancellation) => Task.Run(
        async () =>
        {
            var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 1, FileOptions.RandomAccess | FileOptions.Asynchronous);
            IRandomAccessStream stream = file.AsRandomAccessStream();
            try
            {
                PdfDocument document = await PdfDocument.LoadFromStreamAsync(stream).AsTask(cancellation);
                return new PdfDocumentSession(path, file, stream, document);
            }
            catch (Exception ex) when (ex.HResult == unchecked((int)0x8007052B))
            {
                stream.Dispose();
                await file.DisposeAsync();
                return null; // ERROR_WRONG_PASSWORD: Windows.Data.Pdf asks for a password
            }
            catch
            {
                stream.Dispose();
                await file.DisposeAsync();
                throw;
            }
        },
        cancellation);

    /// <summary>Page size in points (1/72 inch), as Windows.Data.Pdf reports it (page rotation applied).</summary>
    public async Task<(double Width, double Height)> PageSizeAsync(int pageIndex, CancellationToken cancellation)
    {
        await _gate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            using PdfPage page = _document.GetPage((uint)Math.Clamp(pageIndex, 0, PageCount - 1));
            return (page.Size.Width, page.Size.Height);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Renders page <paramref name="pageIndex"/> to <paramref name="width"/> × <paramref name="height"/> pixels
    /// (the caller keeps the page's aspect ratio), then turns it as <paramref name="orientation"/> says.
    /// </summary>
    public Task<DecodedImage> RenderAsync(int pageIndex, uint width, uint height, ViewOrientation orientation, CancellationToken cancellation) => Task.Run(
        async () =>
        {
            await _gate.WaitAsync(cancellation).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                long start = Stopwatch.GetTimestamp();
                uint index = (uint)Math.Clamp(pageIndex, 0, PageCount - 1);
                using PdfPage page = _document.GetPage(index);
                SoftwareBitmap bitmap = await PdfRendering.RenderPageAsync(page, width, height, cancellation).ConfigureAwait(false);
                if (!orientation.IsIdentity)
                {
                    using SoftwareBitmap upright = bitmap;
                    bitmap = SoftwareBitmapPixels.Orient(upright, orientation);
                }

                return DecodedImage.Decoded(bitmap, (uint)page.Size.Width, (uint)page.Size.Height, Stopwatch.GetTimestamp() - start, "windows-data-pdf-session", PageCount, (int)index);
            }
            finally
            {
                _gate.Release();
            }
        },
        cancellation);

    /// <summary>Releases the document and the file once no render is running.</summary>
    public void Dispose()
    {
        if (_closing)
        {
            return;
        }

        _closing = true;
        _ = Task.Run(async () =>
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                _disposed = true;
                _stream.Dispose();
                await _file.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        });
    }
}
