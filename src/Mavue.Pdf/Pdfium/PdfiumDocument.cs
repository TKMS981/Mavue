using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Mavue.Pdf.Pdfium;

/// <summary>
/// One open PDF document (PDFium). The file is read on demand through a callback (never loaded whole) and opened
/// with read, write and delete sharing, so other programs can still change, rename or delete it. Pages and their
/// text are loaded when needed and only the last few are kept (<see cref="PageCacheSize"/>), so memory does not
/// grow with the number of pages. All members are thread-safe (PDFium itself is serialized by
/// <see cref="PdfiumLibrary.Gate"/>); a disposed document throws <see cref="ObjectDisposedException"/>.
/// <para>
/// Positions on a page are "display points": points from the top-left corner of the page as shown, after the
/// page's own /Rotate and the viewer's quarter turns (0–3, clockwise). PDFium converts them, so crop boxes and
/// rotated pages are handled the way it renders them.
/// </para>
/// </summary>
public sealed unsafe class PdfiumDocument : IPdfDocument
{
    /// <summary>Pages (with their text) kept loaded.</summary>
    public const int PageCacheSize = 4;

    /// <summary>Largest file PDFium's file access interface addresses (32-bit length on Windows).</summary>
    public const long MaxFileLength = uint.MaxValue;

    /// <summary>Outline entries read at most (a malformed outline can be very long or cyclic).</summary>
    public const int MaxOutlineItems = 20_000;

    // Device units per point when converting positions (PDFium converts through integer device coordinates).
    private const int DeviceScale = 16;

    private readonly IBlockReader _reader;
    private readonly GCHandle _readerHandle;
    private readonly PdfiumNative.FileAccess* _fileAccess;
    private readonly List<CachedPage> _pages = [];
    private nint _document;

    private PdfiumDocument(nint document, IBlockReader reader, GCHandle readerHandle, PdfiumNative.FileAccess* fileAccess, string? path)
    {
        _document = document;
        _reader = reader;
        _readerHandle = readerHandle;
        _fileAccess = fileAccess;
        Path = path;
        PageCount = Math.Max(0, PdfiumNative.FPDF_GetPageCount(document));
    }

    /// <summary>Reads part of the file for PDFium.</summary>
    private interface IBlockReader : IDisposable
    {
        long Length { get; }

        bool Read(long position, Span<byte> buffer);
    }

    /// <summary>The file, when opened from a path.</summary>
    public string? Path { get; }

    public int PageCount { get; }

    /// <summary>Opens <paramref name="path"/>; throws <see cref="PdfOpenException"/> (e.g. <see cref="PdfOpenError.Password"/>).</summary>
    public static PdfiumDocument Open(string path, string? password = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        PdfiumLibrary.EnsureInitialized();
        SafeFileHandle file;
        try
        {
            file = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, FileOptions.RandomAccess);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new PdfOpenException(PdfOpenError.File);
        }

        return Open(new FileReader(file), password, path);
    }

    /// <summary>Opens a PDF from a seekable stream, which must stay open until the document is disposed.</summary>
    public static PdfiumDocument Open(Stream stream, string? password = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanSeek || !stream.CanRead)
        {
            throw new ArgumentException("The stream must be readable and seekable.", nameof(stream));
        }

        PdfiumLibrary.EnsureInitialized();
        return Open(new StreamReader(stream), password, null);
    }

    private static PdfiumDocument Open(IBlockReader reader, string? password, string? path)
    {
        if (reader.Length > MaxFileLength || reader.Length <= 0)
        {
            reader.Dispose();
            throw new PdfOpenException(reader.Length <= 0 ? PdfOpenError.Format : PdfOpenError.TooLarge);
        }

        GCHandle handle = GCHandle.Alloc(reader);
        var access = (PdfiumNative.FileAccess*)NativeMemory.AllocZeroed((nuint)sizeof(PdfiumNative.FileAccess));
        access->FileLength = (uint)reader.Length;
        access->GetBlock = &GetBlock;
        access->Param = GCHandle.ToIntPtr(handle);
        byte[]? passwordBytes = password is null ? null : Encoding.UTF8.GetBytes(password + "\0");
        nint document;
        uint error;
        lock (PdfiumLibrary.Gate)
        {
            fixed (byte* passwordPointer = passwordBytes)
            {
                document = PdfiumNative.FPDF_LoadCustomDocument(access, passwordPointer);
            }

            error = document == 0 ? PdfiumNative.FPDF_GetLastError() : 0;
        }

        if (document == 0)
        {
            NativeMemory.Free(access);
            handle.Free();
            reader.Dispose();
            throw new PdfOpenException(error switch
            {
                PdfiumNative.FPDF_ERR_PASSWORD => PdfOpenError.Password,
                PdfiumNative.FPDF_ERR_FORMAT => PdfOpenError.Format,
                PdfiumNative.FPDF_ERR_FILE => PdfOpenError.File,
                PdfiumNative.FPDF_ERR_SECURITY => PdfOpenError.Security,
                _ => PdfOpenError.Unknown,
            });
        }

        return new PdfiumDocument(document, reader, handle, access, path);
    }

    /// <summary>Page size in points with the page's /Rotate applied (as it is shown with no extra rotation).</summary>
    public PdfSize GetPageSize(int pageIndex)
    {
        CheckPage(pageIndex);
        lock (PdfiumLibrary.Gate)
        {
            ThrowIfDisposed();
            PdfiumNative.SizeF size;
            return PdfiumNative.FPDF_GetPageSizeByIndexF(_document, pageIndex, &size) != 0
                ? new PdfSize(Math.Max(1, size.Width), Math.Max(1, size.Height))
                : new PdfSize(612, 792);
        }
    }

    /// <summary>
    /// Draws page <paramref name="pageIndex"/> on a white background into <paramref name="buffer"/> (BGRA, top-down,
    /// <paramref name="stride"/> bytes per row), scaled to <paramref name="width"/> × <paramref name="height"/>
    /// pixels and turned by <paramref name="quarterTurns"/>. The caller keeps the page's aspect ratio.
    /// </summary>
    public void RenderPage(int pageIndex, nint buffer, int width, int height, int stride, int quarterTurns = 0)
    {
        CheckPage(pageIndex);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfLessThan(stride, width * 4);
        ArgumentOutOfRangeException.ThrowIfZero(buffer);
        lock (PdfiumLibrary.Gate)
        {
            ThrowIfDisposed();
            nint page = Page(pageIndex).Handle;
            nint bitmap = PdfiumNative.FPDFBitmap_CreateEx(width, height, PdfiumNative.FPDFBitmap_BGRA, (void*)buffer, stride);
            if (bitmap == 0)
            {
                throw new InvalidOperationException("PDFium could not create the bitmap.");
            }

            try
            {
                _ = PdfiumNative.FPDFBitmap_FillRect(bitmap, 0, 0, width, height, 0xFFFFFFFF); // returns void in older builds
                PdfiumNative.FPDF_RenderPageBitmap(bitmap, page, 0, 0, width, height, Normalize(quarterTurns), PdfiumNative.FPDF_ANNOT | PdfiumNative.FPDF_RENDER_LIMITEDIMAGECACHE);
            }
            finally
            {
                PdfiumNative.FPDFBitmap_Destroy(bitmap);
            }
        }
    }

    /// <summary>Number of characters in the page's text (0 for a scanned page without text).</summary>
    public int GetCharCount(int pageIndex)
    {
        CheckPage(pageIndex);
        lock (PdfiumLibrary.Gate)
        {
            ThrowIfDisposed();
            return Math.Max(0, PdfiumNative.FPDFText_CountChars(Page(pageIndex).Text));
        }
    }

    /// <summary>
    /// The character at (<paramref name="x"/>, <paramref name="y"/>) display points, or the nearest one within
    /// <paramref name="tolerance"/> points; -1 when there is none.
    /// </summary>
    public int CharIndexAt(int pageIndex, int quarterTurns, double x, double y, double tolerance)
    {
        CheckPage(pageIndex);
        lock (PdfiumLibrary.Gate)
        {
            ThrowIfDisposed();
            CachedPage page = Page(pageIndex);
            (double px, double py) = ToPageSpace(page.Handle, pageIndex, quarterTurns, x, y);
            int index = PdfiumNative.FPDFText_GetCharIndexAtPos(page.Text, px, py, tolerance, tolerance);
            return index >= 0 ? index : -1;
        }
    }

    /// <summary>Text of characters <paramref name="start"/> .. + <paramref name="count"/> (line breaks as CR LF).</summary>
    public string GetText(int pageIndex, int start, int count)
    {
        CheckPage(pageIndex);
        lock (PdfiumLibrary.Gate)
        {
            ThrowIfDisposed();
            nint text = Page(pageIndex).Text;
            int total = Math.Max(0, PdfiumNative.FPDFText_CountChars(text));
            start = Math.Clamp(start, 0, total);
            count = Math.Clamp(count, 0, total - start);
            if (count == 0)
            {
                return string.Empty;
            }

            ushort[] buffer = new ushort[count + 1];
            int written;
            fixed (ushort* pointer = buffer)
            {
                written = PdfiumNative.FPDFText_GetText(text, start, count, pointer);
            }

            int length = Math.Clamp(written - 1, 0, count); // the result includes the terminating NUL
            return new string(MemoryMarshal.Cast<ushort, char>(buffer.AsSpan(0, length)));
        }
    }

    /// <summary>Rectangles (display points) covering characters <paramref name="start"/> .. + <paramref name="count"/>, one per run of text.</summary>
    public IReadOnlyList<PdfRect> GetTextBounds(int pageIndex, int quarterTurns, int start, int count)
    {
        CheckPage(pageIndex);
        lock (PdfiumLibrary.Gate)
        {
            ThrowIfDisposed();
            CachedPage page = Page(pageIndex);
            int rects = PdfiumNative.FPDFText_CountRects(page.Text, start, count);
            var result = new List<PdfRect>(Math.Max(0, rects));
            for (int i = 0; i < rects; i++)
            {
                double left, top, right, bottom;
                if (PdfiumNative.FPDFText_GetRect(page.Text, i, &left, &top, &right, &bottom) != 0)
                {
                    result.Add(ToDisplayRect(page.Handle, pageIndex, quarterTurns, left, top, right, bottom));
                }
            }

            return result;
        }
    }

    /// <summary>
    /// Occurrences of <paramref name="query"/> on page <paramref name="pageIndex"/> (case-insensitive unless
    /// <paramref name="matchCase"/>), at most <paramref name="limit"/>.
    /// </summary>
    public IReadOnlyList<PdfTextMatch> Find(int pageIndex, string query, bool matchCase = false, bool wholeWord = false, int limit = 1000)
    {
        CheckPage(pageIndex);
        ArgumentNullException.ThrowIfNull(query);
        if (query.Length == 0)
        {
            return [];
        }

        uint flags = (matchCase ? PdfiumNative.FPDF_MATCHCASE : 0) | (wholeWord ? PdfiumNative.FPDF_MATCHWHOLEWORD : 0);
        string terminated = query + "\0";
        lock (PdfiumLibrary.Gate)
        {
            ThrowIfDisposed();
            nint text = Page(pageIndex).Text;
            var result = new List<PdfTextMatch>();
            fixed (char* what = terminated)
            {
                nint search = PdfiumNative.FPDFText_FindStart(text, (ushort*)what, flags, 0);
                if (search == 0)
                {
                    return result;
                }

                try
                {
                    while (result.Count < limit && PdfiumNative.FPDFText_FindNext(search) != 0)
                    {
                        result.Add(new PdfTextMatch(pageIndex, PdfiumNative.FPDFText_GetSchResultIndex(search), PdfiumNative.FPDFText_GetSchCount(search)));
                    }
                }
                finally
                {
                    PdfiumNative.FPDFText_FindClose(search);
                }
            }

            return result;
        }
    }

    /// <summary>
    /// Links on the page: link annotations (to a page of this document or to a web address) and web addresses written
    /// in the text. Launch actions and links to other files are not reported (Mavue never runs them).
    /// </summary>
    public IReadOnlyList<PdfLink> GetLinks(int pageIndex, int quarterTurns)
    {
        CheckPage(pageIndex);
        lock (PdfiumLibrary.Gate)
        {
            ThrowIfDisposed();
            CachedPage page = Page(pageIndex);
            var links = new List<PdfLink>();
            int position = 0;
            nint link;
            while (links.Count < 10_000 && PdfiumNative.FPDFLink_Enumerate(page.Handle, &position, &link) != 0)
            {
                PdfiumNative.RectF rect;
                if (link == 0 || PdfiumNative.FPDFLink_GetAnnotRect(link, &rect) == 0 || TargetOf(link) is not { } target)
                {
                    continue;
                }

                links.Add(new PdfLink(ToDisplayRect(page.Handle, pageIndex, quarterTurns, rect.Left, rect.Top, rect.Right, rect.Bottom), target));
            }

            nint web = PdfiumNative.FPDFLink_LoadWebLinks(page.Text);
            if (web != 0)
            {
                try
                {
                    int count = Math.Min(PdfiumNative.FPDFLink_CountWebLinks(web), 10_000);
                    for (int i = 0; i < count; i++)
                    {
                        if (WebUrl(web, i) is not { } url)
                        {
                            continue;
                        }

                        int rects = PdfiumNative.FPDFLink_CountRects(web, i);
                        for (int r = 0; r < rects; r++)
                        {
                            double left, top, right, bottom;
                            if (PdfiumNative.FPDFLink_GetRect(web, i, r, &left, &top, &right, &bottom) != 0)
                            {
                                links.Add(new PdfLink(ToDisplayRect(page.Handle, pageIndex, quarterTurns, left, top, right, bottom), new PdfUriTarget(url)));
                            }
                        }
                    }
                }
                finally
                {
                    PdfiumNative.FPDFLink_CloseWebLinks(web);
                }
            }

            return links;
        }
    }

    /// <summary>The document outline (bookmarks). Empty when the document has none.</summary>
    public IReadOnlyList<PdfOutlineItem> GetOutline()
    {
        lock (PdfiumLibrary.Gate)
        {
            ThrowIfDisposed();
            int budget = MaxOutlineItems;
            return ReadOutline(0, depth: 0, ref budget, new HashSet<nint>());
        }
    }

    /// <summary>
    /// Where <paramref name="destination"/> is on its page as shown (display points from the page's top-left), or
    /// null when the destination names only the page.
    /// </summary>
    public (double X, double Y)? DestinationPoint(PdfDestination destination, int quarterTurns)
    {
        ArgumentNullException.ThrowIfNull(destination);
        CheckPage(destination.PageIndex);
        if (destination.PageY is null && destination.PageX is null)
        {
            return null;
        }

        lock (PdfiumLibrary.Gate)
        {
            ThrowIfDisposed();
            CachedPage page = Page(destination.PageIndex);
            PdfSize size = OrientedSize(destination.PageIndex, quarterTurns);
            double pageX = destination.PageX ?? 0;
            double pageY = destination.PageY ?? 0;
            (double x, double y) = ToDisplay(page.Handle, destination.PageIndex, quarterTurns, pageX, pageY);
            return (destination.PageX is null ? 0 : Math.Clamp(x, 0, size.Width), destination.PageY is null ? 0 : Math.Clamp(y, 0, size.Height));
        }
    }

    public void Dispose()
    {
        lock (PdfiumLibrary.Gate)
        {
            if (_document == 0)
            {
                return;
            }

            foreach (CachedPage page in _pages)
            {
                page.Close();
            }

            _pages.Clear();
            PdfiumNative.FPDF_CloseDocument(_document);
            _document = 0;
        }

        NativeMemory.Free(_fileAccess);
        _readerHandle.Free();
        _reader.Dispose();
    }

    /// <summary>PDFium asks for a block of the file (called inside a PDFium call, on the calling thread).</summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int GetBlock(nint param, uint position, byte* buffer, uint size)
    {
        try
        {
            var reader = (IBlockReader)GCHandle.FromIntPtr(param).Target!;
            return reader.Read(position, new Span<byte>(buffer, checked((int)size))) ? 1 : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException or OverflowException or ArgumentException)
        {
            return 0; // PDFium treats the document as damaged
        }
    }

    private static int Normalize(int quarterTurns) => ((quarterTurns % 4) + 4) % 4;

    private static string? Utf16(ReadOnlySpan<byte> bytes)
    {
        string text = Encoding.Unicode.GetString(bytes).TrimEnd('\0');
        return text.Length == 0 ? null : text;
    }

    /// <summary>Only web and mail addresses are followed; javascript:, file: and other schemes are dropped.</summary>
    private static string? SafeUri(string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri))
        {
            return null;
        }

        uri = uri.Trim();
        if (uri.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
        {
            uri = "http://" + uri;
        }

        return Uri.TryCreate(uri, UriKind.Absolute, out Uri? parsed) && parsed.Scheme is "http" or "https" or "mailto" ? parsed.AbsoluteUri : null;
    }

    private static string? WebUrl(nint web, int index)
    {
        int length = PdfiumNative.FPDFLink_GetURL(web, index, null, 0);
        if (length <= 1 || length > 8192)
        {
            return null;
        }

        char[] buffer = new char[length];
        fixed (char* pointer = buffer)
        {
            if (PdfiumNative.FPDFLink_GetURL(web, index, (ushort*)pointer, length) != length)
            {
                return null;
            }
        }

        return SafeUri(new string(buffer, 0, length - 1));
    }

    private void CheckPage(int pageIndex)
    {
        if ((uint)pageIndex >= (uint)PageCount)
        {
            throw new ArgumentOutOfRangeException(nameof(pageIndex), pageIndex, "No such page.");
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_document == 0, this);

    /// <summary>The loaded page (most recently used kept, at most <see cref="PageCacheSize"/>). Caller holds the gate.</summary>
    private CachedPage Page(int pageIndex)
    {
        int found = _pages.FindIndex(p => p.Index == pageIndex);
        if (found >= 0)
        {
            CachedPage hit = _pages[found];
            _pages.RemoveAt(found);
            _pages.Insert(0, hit);
            return hit;
        }

        nint handle = PdfiumNative.FPDF_LoadPage(_document, pageIndex);
        if (handle == 0)
        {
            throw new InvalidDataException($"Page {pageIndex + 1} could not be loaded.");
        }

        nint text = PdfiumNative.FPDFText_LoadPage(handle);
        if (text == 0)
        {
            PdfiumNative.FPDF_ClosePage(handle);
            throw new InvalidDataException($"The text of page {pageIndex + 1} could not be read.");
        }

        var page = new CachedPage(pageIndex, handle, text);
        _pages.Insert(0, page);
        while (_pages.Count > PageCacheSize)
        {
            _pages[^1].Close();
            _pages.RemoveAt(_pages.Count - 1);
        }

        return page;
    }

    private PdfSize OrientedSize(int pageIndex, int quarterTurns)
    {
        PdfiumNative.SizeF size;
        PdfSize upright = PdfiumNative.FPDF_GetPageSizeByIndexF(_document, pageIndex, &size) != 0
            ? new PdfSize(Math.Max(1, size.Width), Math.Max(1, size.Height))
            : new PdfSize(612, 792);
        return (Normalize(quarterTurns) & 1) == 1 ? new PdfSize(upright.Height, upright.Width) : upright;
    }

    private (double X, double Y) ToDisplay(nint page, int pageIndex, int quarterTurns, double pageX, double pageY)
    {
        PdfSize size = OrientedSize(pageIndex, quarterTurns);
        int deviceX, deviceY;
        if (PdfiumNative.FPDF_PageToDevice(page, 0, 0, (int)Math.Ceiling(size.Width * DeviceScale), (int)Math.Ceiling(size.Height * DeviceScale), Normalize(quarterTurns), pageX, pageY, &deviceX, &deviceY) == 0)
        {
            return (pageX, size.Height - pageY); // PDFium refused (degenerate page): plain page space
        }

        return (deviceX / (double)DeviceScale, deviceY / (double)DeviceScale);
    }

    private (double X, double Y) ToPageSpace(nint page, int pageIndex, int quarterTurns, double x, double y)
    {
        PdfSize size = OrientedSize(pageIndex, quarterTurns);
        double pageX, pageY;
        if (PdfiumNative.FPDF_DeviceToPage(page, 0, 0, (int)Math.Ceiling(size.Width * DeviceScale), (int)Math.Ceiling(size.Height * DeviceScale), Normalize(quarterTurns), (int)Math.Round(x * DeviceScale), (int)Math.Round(y * DeviceScale), &pageX, &pageY) == 0)
        {
            return (x, size.Height - y);
        }

        return (pageX, pageY);
    }

    private PdfRect ToDisplayRect(nint page, int pageIndex, int quarterTurns, double left, double top, double right, double bottom)
    {
        (double x1, double y1) = ToDisplay(page, pageIndex, quarterTurns, left, top);
        (double x2, double y2) = ToDisplay(page, pageIndex, quarterTurns, right, bottom);
        return new PdfRect(Math.Min(x1, x2), Math.Min(y1, y2), Math.Max(x1, x2), Math.Max(y1, y2));
    }

    /// <summary>Target of a link annotation (caller holds the gate).</summary>
    private PdfLinkTarget? TargetOf(nint link)
    {
        nint destination = PdfiumNative.FPDFLink_GetDest(_document, link);
        if (destination != 0)
        {
            return Destination(destination) is { } d ? new PdfPageTarget(d) : null;
        }

        nint action = PdfiumNative.FPDFLink_GetAction(link);
        return action == 0 ? null : ActionTarget(action);
    }

    private PdfLinkTarget? ActionTarget(nint action)
    {
        switch (PdfiumNative.FPDFAction_GetType(action))
        {
            case PdfiumNative.PDFACTION_GOTO:
                nint destination = PdfiumNative.FPDFAction_GetDest(_document, action);
                return destination != 0 && Destination(destination) is { } d ? new PdfPageTarget(d) : null;
            case PdfiumNative.PDFACTION_URI:
                uint length = PdfiumNative.FPDFAction_GetURIPath(_document, action, null, 0);
                if (length <= 1 || length > 8192)
                {
                    return null;
                }

                byte[] buffer = new byte[length];
                fixed (byte* pointer = buffer)
                {
                    if (PdfiumNative.FPDFAction_GetURIPath(_document, action, pointer, length) != length)
                    {
                        return null;
                    }
                }

                return SafeUri(Encoding.ASCII.GetString(buffer, 0, (int)length - 1)) is { } uri ? new PdfUriTarget(uri) : null;
            default:
                return null; // launch, remote go-to, JavaScript…: never followed
        }
    }

    private PdfDestination? Destination(nint destination)
    {
        int page = PdfiumNative.FPDFDest_GetDestPageIndex(_document, destination);
        if (page < 0 || page >= PageCount)
        {
            return null;
        }

        int hasX, hasY, hasZoom;
        float x, y, zoom;
        if (PdfiumNative.FPDFDest_GetLocationInPage(destination, &hasX, &hasY, &hasZoom, &x, &y, &zoom) == 0)
        {
            return new PdfDestination(page);
        }

        return new PdfDestination(page, hasX != 0 ? x : null, hasY != 0 ? y : null);
    }

    private List<PdfOutlineItem> ReadOutline(nint parent, int depth, ref int budget, HashSet<nint> seen)
    {
        var items = new List<PdfOutlineItem>();
        if (depth > 64)
        {
            return items;
        }

        for (nint item = PdfiumNative.FPDFBookmark_GetFirstChild(_document, parent);
             item != 0 && budget > 0 && seen.Add(item);
             item = PdfiumNative.FPDFBookmark_GetNextSibling(_document, item))
        {
            budget--;
            uint bytes = PdfiumNative.FPDFBookmark_GetTitle(item, null, 0);
            string title = string.Empty;
            if (bytes > 2 && bytes < 64 * 1024)
            {
                byte[] buffer = new byte[bytes];
                fixed (byte* pointer = buffer)
                {
                    title = PdfiumNative.FPDFBookmark_GetTitle(item, pointer, bytes) == bytes ? Utf16(buffer) ?? string.Empty : string.Empty;
                }
            }

            PdfDestination? destination = null;
            nint dest = PdfiumNative.FPDFBookmark_GetDest(_document, item);
            if (dest != 0)
            {
                destination = Destination(dest);
            }
            else if (PdfiumNative.FPDFBookmark_GetAction(item) is not 0 and var action && ActionTarget(action) is PdfPageTarget target)
            {
                destination = target.Destination;
            }

            items.Add(new PdfOutlineItem(title, destination, ReadOutline(item, depth + 1, ref budget, seen)));
        }

        return items;
    }

    private sealed class CachedPage(int index, nint handle, nint text)
    {
        public int Index { get; } = index;

        public nint Handle { get; } = handle;

        public nint Text { get; } = text;

        public void Close()
        {
            PdfiumNative.FPDFText_ClosePage(Text);
            PdfiumNative.FPDF_ClosePage(Handle);
        }
    }

    private sealed class FileReader(SafeFileHandle file) : IBlockReader
    {
        public long Length { get; } = RandomAccess.GetLength(file);

        public bool Read(long position, Span<byte> buffer)
        {
            while (buffer.Length > 0)
            {
                int read = RandomAccess.Read(file, buffer, position);
                if (read <= 0)
                {
                    return false;
                }

                position += read;
                buffer = buffer[read..];
            }

            return true;
        }

        public void Dispose() => file.Dispose();
    }

    private sealed class StreamReader(Stream stream) : IBlockReader
    {
        public long Length { get; } = stream.Length;

        public bool Read(long position, Span<byte> buffer)
        {
            // Reads come from PDFium calls, which are serialized by the gate: the stream is used by one thread at a time.
            stream.Position = position;
            stream.ReadExactly(buffer);
            return true;
        }

        public void Dispose()
        {
            // The caller owns the stream.
        }
    }
}
