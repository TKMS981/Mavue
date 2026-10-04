using System.Runtime.InteropServices;

namespace Mavue.Pdf.Pdfium;

/// <summary>
/// The PDFium C API used by Mavue (fpdfview.h, fpdf_text.h, fpdf_doc.h of the bblanchon/pdfium-binaries build,
/// docs/DEPENDENCIES.md §3). PDFium is not thread-safe: every call must hold <see cref="PdfiumLibrary.Gate"/>.
/// On Windows "unsigned long" is 32 bits.
/// </summary>
internal static unsafe partial class PdfiumNative
{
    private const string Library = "pdfium";

    public const int FPDF_ERR_SUCCESS = 0;
    public const int FPDF_ERR_FILE = 2;
    public const int FPDF_ERR_FORMAT = 3;
    public const int FPDF_ERR_PASSWORD = 4;
    public const int FPDF_ERR_SECURITY = 5;

    public const int FPDFBitmap_BGRA = 4;

    /// <summary>Render annotations (links, widgets…) with their appearance streams.</summary>
    public const int FPDF_ANNOT = 0x01;

    /// <summary>Limit the image cache (large documents).</summary>
    public const int FPDF_RENDER_LIMITEDIMAGECACHE = 0x200;

    public const uint FPDF_MATCHCASE = 0x1;
    public const uint FPDF_MATCHWHOLEWORD = 0x2;

    public const uint PDFACTION_GOTO = 1;
    public const uint PDFACTION_URI = 3;

    [StructLayout(LayoutKind.Sequential)]
    public struct FileAccess
    {
        public uint FileLength;
        public delegate* unmanaged[Cdecl]<nint, uint, byte*, uint, int> GetBlock;
        public nint Param;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct LibraryConfig
    {
        public int Version;
        public nint UserFontPaths;
        public nint Isolate;
        public uint V8EmbedderSlot;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SizeF
    {
        public float Width;
        public float Height;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RectF
    {
        public float Left;
        public float Top;
        public float Right;
        public float Bottom;
    }

    // fpdfview.h
    [LibraryImport(Library)]
    public static partial void FPDF_InitLibraryWithConfig(LibraryConfig* config);

    [LibraryImport(Library)]
    public static partial nint FPDF_LoadCustomDocument(FileAccess* fileAccess, byte* password);

    [LibraryImport(Library)]
    public static partial uint FPDF_GetLastError();

    [LibraryImport(Library)]
    public static partial void FPDF_CloseDocument(nint document);

    [LibraryImport(Library)]
    public static partial int FPDF_GetPageCount(nint document);

    [LibraryImport(Library)]
    public static partial int FPDF_GetPageSizeByIndexF(nint document, int pageIndex, SizeF* size);

    [LibraryImport(Library)]
    public static partial nint FPDF_LoadPage(nint document, int pageIndex);

    [LibraryImport(Library)]
    public static partial void FPDF_ClosePage(nint page);

    [LibraryImport(Library)]
    public static partial nint FPDFBitmap_CreateEx(int width, int height, int format, void* firstScan, int stride);

    [LibraryImport(Library)]
    public static partial int FPDFBitmap_FillRect(nint bitmap, int left, int top, int width, int height, uint color);

    [LibraryImport(Library)]
    public static partial void FPDFBitmap_Destroy(nint bitmap);

    [LibraryImport(Library)]
    public static partial void FPDF_RenderPageBitmap(nint bitmap, nint page, int startX, int startY, int sizeX, int sizeY, int rotate, int flags);

    [LibraryImport(Library)]
    public static partial int FPDF_DeviceToPage(nint page, int startX, int startY, int sizeX, int sizeY, int rotate, int deviceX, int deviceY, double* pageX, double* pageY);

    [LibraryImport(Library)]
    public static partial int FPDF_PageToDevice(nint page, int startX, int startY, int sizeX, int sizeY, int rotate, double pageX, double pageY, int* deviceX, int* deviceY);

    // fpdf_text.h
    [LibraryImport(Library)]
    public static partial nint FPDFText_LoadPage(nint page);

    [LibraryImport(Library)]
    public static partial void FPDFText_ClosePage(nint textPage);

    [LibraryImport(Library)]
    public static partial int FPDFText_CountChars(nint textPage);

    [LibraryImport(Library)]
    public static partial int FPDFText_GetCharIndexAtPos(nint textPage, double x, double y, double xTolerance, double yTolerance);

    [LibraryImport(Library)]
    public static partial int FPDFText_GetText(nint textPage, int startIndex, int count, ushort* result);

    [LibraryImport(Library)]
    public static partial int FPDFText_CountRects(nint textPage, int startIndex, int count);

    [LibraryImport(Library)]
    public static partial int FPDFText_GetRect(nint textPage, int rectIndex, double* left, double* top, double* right, double* bottom);

    [LibraryImport(Library)]
    public static partial nint FPDFText_FindStart(nint textPage, ushort* findWhat, uint flags, int startIndex);

    [LibraryImport(Library)]
    public static partial int FPDFText_FindNext(nint handle);

    [LibraryImport(Library)]
    public static partial int FPDFText_GetSchResultIndex(nint handle);

    [LibraryImport(Library)]
    public static partial int FPDFText_GetSchCount(nint handle);

    [LibraryImport(Library)]
    public static partial void FPDFText_FindClose(nint handle);

    [LibraryImport(Library)]
    public static partial nint FPDFLink_LoadWebLinks(nint textPage);

    [LibraryImport(Library)]
    public static partial int FPDFLink_CountWebLinks(nint pageLink);

    [LibraryImport(Library)]
    public static partial int FPDFLink_GetURL(nint pageLink, int linkIndex, ushort* buffer, int bufferLength);

    [LibraryImport(Library)]
    public static partial int FPDFLink_CountRects(nint pageLink, int linkIndex);

    [LibraryImport(Library)]
    public static partial int FPDFLink_GetRect(nint pageLink, int linkIndex, int rectIndex, double* left, double* top, double* right, double* bottom);

    [LibraryImport(Library)]
    public static partial void FPDFLink_CloseWebLinks(nint pageLink);

    // fpdf_doc.h
    [LibraryImport(Library)]
    public static partial nint FPDFBookmark_GetFirstChild(nint document, nint bookmark);

    [LibraryImport(Library)]
    public static partial nint FPDFBookmark_GetNextSibling(nint document, nint bookmark);

    [LibraryImport(Library)]
    public static partial uint FPDFBookmark_GetTitle(nint bookmark, void* buffer, uint bufferLength);

    [LibraryImport(Library)]
    public static partial nint FPDFBookmark_GetDest(nint document, nint bookmark);

    [LibraryImport(Library)]
    public static partial nint FPDFBookmark_GetAction(nint bookmark);

    [LibraryImport(Library)]
    public static partial uint FPDFAction_GetType(nint action);

    [LibraryImport(Library)]
    public static partial nint FPDFAction_GetDest(nint document, nint action);

    [LibraryImport(Library)]
    public static partial uint FPDFAction_GetURIPath(nint document, nint action, void* buffer, uint bufferLength);

    [LibraryImport(Library)]
    public static partial int FPDFDest_GetDestPageIndex(nint document, nint destination);

    [LibraryImport(Library)]
    public static partial int FPDFDest_GetLocationInPage(nint destination, int* hasX, int* hasY, int* hasZoom, float* x, float* y, float* zoom);

    [LibraryImport(Library)]
    public static partial int FPDFLink_Enumerate(nint page, int* startPosition, nint* link);

    [LibraryImport(Library)]
    public static partial int FPDFLink_GetAnnotRect(nint link, RectF* rect);

    [LibraryImport(Library)]
    public static partial nint FPDFLink_GetDest(nint document, nint link);

    [LibraryImport(Library)]
    public static partial nint FPDFLink_GetAction(nint link);
}
